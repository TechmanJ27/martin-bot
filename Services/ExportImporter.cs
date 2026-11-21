using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

/// <summary>
/// One-off importer for a guild export from the previous bot (guild_config, warnings, cases).
/// Safe to run more than once: rows already present (same guild, target, action and timestamp) are skipped.
/// Pass targetGuildId to store the data under a different server (e.g. a test server); log-channel settings are
/// then left alone, because the old server's channel IDs don't exist there.
/// With replace = true, previously imported rows (reason ends in "(imported)") for the source guild are removed first.
/// </summary>
public static class ExportImporter
{
    public static async Task<string> ImportAsync(string jsonPath, string databasePath,
        ModerationDatabase moderationDb, LoggingDatabase loggingDb,
        ulong? targetGuildId = null, bool replace = false)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        var tables = doc.RootElement.GetProperty("tables");

        var added = 0;
        var skipped = 0;

        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(databasePath) }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = connection.BeginTransaction();

        var sourceGuild = doc.RootElement.GetProperty("guild_id").GetString()!;
        var removed = 0;
        if (replace)
        {
            var del = connection.CreateCommand();
            del.Transaction = tx;
            // Both the source guild and the target guild, so re-running with the same target doesn't duplicate rows.
            del.CommandText = "DELETE FROM ModerationCases WHERE GuildId IN ($g, $t) AND Reason LIKE '%(imported)';";
            del.Parameters.AddWithValue("$g", sourceGuild);
            del.Parameters.AddWithValue("$t", targetGuildId?.ToString() ?? sourceGuild);
            removed = await del.ExecuteNonQueryAsync();
        }

        async Task ImportRows(string table, Func<JsonElement, string> action)
        {
            foreach (var row in tables.GetProperty(table).EnumerateArray())
            {
                var guild = targetGuildId?.ToString() ?? row.GetProperty("guild_id").GetString()!;
                var target = row.GetProperty("user_id").GetString()!;
                var act = action(row);
                var created = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("timestamp").GetInt64()).ToString("O");
                var rule = row.GetProperty("rule_number").GetInt32();
                var note = row.GetProperty("note").GetString() ?? "";

                var reason = (note.Length > 0 ? note : "No reason recorded") + CaseCards.ImportedSuffix;
                object ruleValue = rule > 0 ? rule : DBNull.Value;

                var exists = connection.CreateCommand();
                exists.Transaction = tx;
                exists.CommandText = "SELECT 1 FROM ModerationCases WHERE GuildId=$g AND TargetId=$t AND Action=$a AND CreatedAt=$c LIMIT 1;";
                exists.Parameters.AddWithValue("$g", guild);
                exists.Parameters.AddWithValue("$t", target);
                exists.Parameters.AddWithValue("$a", act);
                exists.Parameters.AddWithValue("$c", created);
                if (await exists.ExecuteScalarAsync() is not null) { skipped++; continue; }

                var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO ModerationCases (GuildId, TargetId, ModeratorId, Action, Reason, CreatedAt, DurationMinutes, RuleNumber)
                    VALUES ($g, $t, '0', $a, $r, $c, NULL, $rule);
                    """;
                insert.Parameters.AddWithValue("$rule", ruleValue);
                insert.Parameters.AddWithValue("$g", guild);
                insert.Parameters.AddWithValue("$t", target);
                insert.Parameters.AddWithValue("$a", act);
                insert.Parameters.AddWithValue("$r", reason);
                insert.Parameters.AddWithValue("$c", created);
                await insert.ExecuteNonQueryAsync();
                added++;
            }
        }

        // Old "warnings" table -> Martin "warn" cases; old "cases" keep their type (ban / timeout / unban).
        await ImportRows("warnings", _ => "warn");
        await ImportRows("cases", r => r.GetProperty("case_type").GetString()!);
        await tx.CommitAsync();

        // Server rules: stored so cases can show the rule's title and details.
        var rulesImported = 0;
        foreach (var row in tables.GetProperty("guild_rules").EnumerateArray())
        {
            var guildId = targetGuildId ?? ulong.Parse(row.GetProperty("guild_id").GetString()!);
            var parsed = ParseRules(row.GetProperty("rules").GetString() ?? "");
            await moderationDb.ReplaceRulesAsync(guildId, parsed);
            rulesImported += parsed.Count;
        }

        // Channel settings only make sense in the original guild, since channel IDs are per-guild.
        if (targetGuildId is null)
        foreach (var cfg in tables.GetProperty("guild_config").EnumerateArray())
        {
            var guildId = ulong.Parse(cfg.GetProperty("guild_id").GetString()!);
            ulong? Channel(string key) =>
                cfg.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && ulong.TryParse(v.GetString(), out var id) ? id : null;

            await moderationDb.SetLogChannelAsync(guildId, Channel("mod_channel"));
            await loggingDb.SetLogChannelAsync(guildId, LogKind.Messages, Channel("message_channel"));
            await loggingDb.SetLogChannelAsync(guildId, LogKind.Users, Channel("member_channel"));
        }

        var where = targetGuildId is { } t ? $" under guild {t} (channel settings skipped)" : "";
        return $"Imported {added} cases ({skipped} already present) and {rulesImported} rules{where}." + (replace ? $" Removed {removed} earlier imported rows first." : "");
    }

    /// <summary>
    /// Splits the old bot's single rules string ("1. Title: details;  2. Title: details; ...") into rules.
    /// Entries that don't start with "N." (such as the trailing joke line) are ignored.
    /// </summary>
    public static List<GuildRule> ParseRules(string text)
    {
        var rules = new List<GuildRule>();
        foreach (var part in text.Split(';'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"^(\d+)\.\s*(.+?):\s+(.*)$",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success)
                rules.Add(new GuildRule(int.Parse(match.Groups[1].Value), match.Groups[2].Value.Trim(), match.Groups[3].Value.Trim()));
        }
        return rules;
    }
}
