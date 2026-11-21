using Discord;

namespace ShinBreaker.Services;

/// <summary>What /warnings and /modlogs send: a header line plus pages of case cards (one embed per case).</summary>
public sealed record CaseView(string Header, IReadOnlyList<Embed[]> Pages);

/// <summary>Builds one embed per case (rule, rule details, date, note) for /warnings, /modlogs and their prefix versions.</summary>
public static class CaseCards
{
    /// <summary>Appended to reasons of cases brought over by ExportImporter; stripped again when displaying.</summary>
    public const string ImportedSuffix = " (imported)";

    /// <summary>How long a warning counts as active. Set from Martin_WARN_EXPIRY_DAYS in Program.cs (default 6 weeks).</summary>
    public static TimeSpan WarnExpiry { get; set; } = TimeSpan.FromDays(42);

    // Discord's hard limits: 10 embeds and 6000 characters per message, 1024 characters per embed field.
    private const int MaxEmbedsPerMessage = 10;
    private const int CharacterBudget = 5800;
    private const int FieldLimit = 1024;
    private const int NoteLimit = 4000; // one note can't be shown beyond this: a single embed may hold 6000 characters in total

    public static bool IsExpired(ModerationCase c) =>
        c.Action == "warn" && c.CreatedAt + WarnExpiry <= DateTimeOffset.UtcNow;

    /// <summary>Loads a member's cases and turns them into a header and pages of cards. Nothing is cut off; long cases just use more pages.</summary>
    public static async Task<CaseView> BuildViewAsync(ModerationDatabase database, ulong guildId, ulong targetId,
        string mention, bool warningsOnly)
    {
        IReadOnlyList<ModerationCase> cases = await database.GetCasesAsync(guildId, targetId, 25);
        var expiredHidden = 0;
        if (warningsOnly)
        {
            var warns = cases.Where(c => c.Action == "warn").ToArray();
            cases = warns.Where(c => !IsExpired(c)).ToArray();
            expiredHidden = warns.Length - cases.Count;
        }

        var expiredNote = expiredHidden > 0
            ? $" {expiredHidden} expired warning(s) (older than {WarnExpiry.TotalDays:0} days) hidden; use `/modlogs` to see everything."
            : "";

        if (cases.Count == 0)
        {
            var none = warningsOnly
                ? (expiredHidden > 0 ? $"No active warnings for {mention}." : $"No warnings found for {mention}.")
                : $"No moderation cases found for {mention}.";
            return new CaseView(none + expiredNote, []);
        }

        var header = warningsOnly
            ? $"**Warnings** for {mention} — {cases.Count} active.{expiredNote}"
            : $"**Moderation history** for {mention} — {cases.Count} total.";

        var rules = await database.GetRulesAsync(guildId);
        return new CaseView(header, Paginate(cases.Select(c => Build(c, rules)).ToList()));
    }

    private static Embed Build(ModerationCase c, IReadOnlyDictionary<int, GuildRule> rules)
    {
        var imported = c.Reason.EndsWith(ImportedSuffix, StringComparison.Ordinal);
        var note = imported ? c.Reason[..^ImportedSuffix.Length] : c.Reason;
        var expired = IsExpired(c);

        var title = c.Action == "warn" ? $"Warning #{c.Id:0000}" : $"Case #{c.Id:0000} · {c.Action}";
        var embed = new EmbedBuilder()
            .WithTitle(expired ? $"{title} · expired" : title)
            .WithColor(expired ? Color.LightGrey : c.Action switch
            {
                "ban" => Color.DarkRed,
                "kick" or "timeout" => Color.Orange,
                "warn" => Color.Gold,
                "untimeout" or "unban" => Color.Green,
                _ => Color.Blue
            });

        if (c.RuleNumber is > 0 and var number)
        {
            if (rules.TryGetValue(number, out var rule))
            {
                AddLong(embed, "Rule", $"{rule.Number}. {rule.Title}");
                if (rule.Details.Length > 0) AddLong(embed, "Details", rule.Details);
            }
            else embed.AddField("Rule", $"Rule {number}");
        }

        embed.AddField("Date", $"<t:{c.CreatedAt.ToUnixTimeSeconds()}:F>");
        if (c.Action == "warn" && !expired)
            embed.AddField("Expires", $"<t:{(c.CreatedAt + WarnExpiry).ToUnixTimeSeconds()}:R>");
        if (c.DurationMinutes is { } minutes) embed.AddField("Duration", $"{minutes} minute(s)");
        if (!string.IsNullOrWhiteSpace(note) && note != "No reason recorded") AddLong(embed, "Note", note);
        if (c.ModeratorId != 0) embed.AddField("Moderator", $"<@{c.ModeratorId}>");

        var footer = new List<string>();
        if (expired) footer.Add("Expired");
        if (imported) footer.Add("Imported from the previous bot");
        if (footer.Count > 0) embed.WithFooter(string.Join(" · ", footer));
        return embed.Build();
    }

    /// <summary>Adds a field, splitting values over 1024 characters across several fields instead of cutting them.</summary>
    private static void AddLong(EmbedBuilder embed, string name, string value)
    {
        if (value.Length > NoteLimit) value = value[..(NoteLimit - 1)] + "…";
        var first = true;
        while (value.Length > 0)
        {
            var take = Math.Min(FieldLimit, value.Length);
            if (take < value.Length)
            {
                var cut = value.LastIndexOfAny([' ', '\n'], take - 1, take);
                if (cut > take / 2) take = cut;
            }
            var chunk = value[..take].TrimEnd();
            if (chunk.Length > 0) embed.AddField(first ? name : "​", chunk);
            first = false;
            value = value[take..].TrimStart();
        }
    }

    /// <summary>Groups embeds into messages that respect Discord's 10-embed and 6000-character limits.</summary>
    private static List<Embed[]> Paginate(IReadOnlyList<Embed> embeds)
    {
        var pages = new List<Embed[]>();
        var current = new List<Embed>();
        var used = 0;
        foreach (var embed in embeds)
        {
            var size = Size(embed);
            if (current.Count > 0 && (current.Count >= MaxEmbedsPerMessage || used + size > CharacterBudget))
            {
                pages.Add(current.ToArray());
                current.Clear();
                used = 0;
            }
            current.Add(embed);
            used += size;
        }
        if (current.Count > 0) pages.Add(current.ToArray());
        return pages;
    }

    private static int Size(Embed e) =>
        (e.Title?.Length ?? 0) + (e.Footer?.Text.Length ?? 0) + e.Fields.Sum(f => f.Name.Length + f.Value.Length);
}
