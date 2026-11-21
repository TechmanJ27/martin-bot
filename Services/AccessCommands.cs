using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

/// <summary>/configure (server owner / admins choose which roles may use which moderation commands) and /help_mod.</summary>
public static class AccessCommands
{
    private const string MenuPrefix = "access:";

    public static bool Handles(string commandName) => commandName is "configure" or "help_mod";

    public static ApplicationCommandProperties[] BuildAll() =>
    [
        new SlashCommandBuilder().WithName("configure")
            .WithDescription("Choose which roles may use which moderation commands")
            .WithDefaultMemberPermissions(GuildPermission.Administrator)
            .AddOption("role", ApplicationCommandOptionType.Role, "Role to set up (leave empty to see the current setup)", isRequired: false)
            .Build(),
        new SlashCommandBuilder().WithName("help_mod")
            .WithDescription("Show the moderation commands you can use").Build()
    ];

    public static async Task HandleAsync(SocketSlashCommand command, ModerationDatabase database, string prefixes)
    {
        if (command.GuildId is null || (command.Channel as SocketGuildChannel)?.Guild is not SocketGuild guild || command.User is not SocketGuildUser user)
        {
            await command.RespondAsync("This command can only be used inside a server.", ephemeral: true);
            return;
        }
        await command.DeferAsync(ephemeral: true);

        if (command.Data.Name == "help_mod")
        {
            await HelpAsync(command, database, guild, user, prefixes);
            return;
        }

        if (!ModAccess.IsOwnerOrAdmin(guild, user))
        {
            await command.FollowupAsync("Only the server owner or an administrator can use `/configure`.", ephemeral: true);
            return;
        }

        var role = command.Data.Options.FirstOrDefault(o => o.Name == "role")?.Value as SocketRole;
        if (role is null)
        {
            await command.FollowupAsync(embed: await OverviewAsync(database, guild), ephemeral: true);
            return;
        }
        if (role.IsEveryone)
        {
            await command.FollowupAsync("Pick a specific role, not @everyone.", ephemeral: true);
            return;
        }

        var current = await database.GetRoleActionsAsync(guild.Id, role.Id);
        await command.FollowupAsync(
            $"Which commands may {role.Mention} use? Pick any number, then close the menu. " +
            "Run `/configure` again with the same role to change it.",
            components: BuildMenu(role.Id, current), allowedMentions: AllowedMentions.None, ephemeral: true);
    }

    /// <summary>Handles the command picker from /configure.</summary>
    public static async Task HandleComponentAsync(SocketMessageComponent component, ModerationDatabase database)
    {
        var id = component.Data.CustomId;
        if (!id.StartsWith(MenuPrefix, StringComparison.Ordinal) || !ulong.TryParse(id[MenuPrefix.Length..], out var roleId)) return;

        if ((component.Channel as SocketGuildChannel)?.Guild is not SocketGuild guild || component.User is not SocketGuildUser user
            || !ModAccess.IsOwnerOrAdmin(guild, user))
        {
            await component.RespondAsync("Only the server owner or an administrator can change this.", ephemeral: true);
            return;
        }

        var selected = component.Data.Values.Where(v => ModAccess.Find(v) is not null).ToHashSet();
        await database.SetRoleActionsAsync(guild.Id, roleId, selected);

        var summary = selected.Count == 0
            ? $"<@&{roleId}> is no longer assigned to any moderation command. Commands with no roles assigned fall back to the normal Discord permission."
            : $"<@&{roleId}> can now use: {string.Join(", ", ModAccess.Actions.Where(a => selected.Contains(a.Action)).Select(a => $"`/{a.Action}`"))}.";
        await component.UpdateAsync(m =>
        {
            m.Content = summary;
            m.Components = BuildMenu(roleId, selected);
            m.AllowedMentions = AllowedMentions.None;
        });
    }

    private static MessageComponent BuildMenu(ulong roleId, IReadOnlySet<string> selected)
    {
        var options = ModAccess.Actions.Select(a => new SelectMenuOptionBuilder
        {
            Label = $"/{a.Action}",
            Value = a.Action,
            Description = Truncate(a.Blurb, 100),
            IsDefault = selected.Contains(a.Action)
        }).ToList();
        var menu = new SelectMenuBuilder()
            .WithCustomId($"{MenuPrefix}{roleId}")
            .WithPlaceholder("Choose commands")
            .WithMinValues(0)
            .WithMaxValues(options.Count)
            .WithOptions(options);
        return new ComponentBuilder().WithSelectMenu(menu).Build();
    }

    private static async Task<Embed> OverviewAsync(ModerationDatabase database, SocketGuild guild)
    {
        var configured = await database.GetActionRolesAsync(guild.Id);
        var lines = ModAccess.Actions.Select(a =>
            configured.TryGetValue(a.Action, out var ids) && ids.Count > 0
                ? $"**/{a.Action}** — {string.Join(", ", ids.Select(r => $"<@&{r}>"))}"
                : $"**/{a.Action}** — default ({a.DefaultLabel})");
        return new EmbedBuilder()
            .WithTitle("Moderation access")
            .WithDescription(string.Join("\n", lines))
            .WithFooter("Run /configure role:@Role to choose what a role may use. The server owner and administrators can always use every command. Text commands follow the same setup.")
            .WithColor(Color.Blue).Build();
    }

    private static async Task HelpAsync(SocketSlashCommand command, ModerationDatabase database, SocketGuild guild, SocketGuildUser user, string prefixes)
    {
        var prefix = prefixes.Length > 0 ? prefixes[0].ToString() : ".";
        var embed = new EmbedBuilder()
            .WithTitle("Moderator Help Guide")
            .WithDescription("Welcome to the moderation team! Here's how to use Martin's moderation features.")
            .WithColor(Color.Blue)
            .WithFooter("Martin Moderation System")
            .WithCurrentTimestamp();

        var number = 0;
        foreach (var info in ModAccess.Actions)
        {
            if (!await ModAccess.CanUseAsync(database, guild, user, info.Action)) continue;
            number++;
            var blurb = info.Action == "warnings"
                ? $"Show a member's active warnings. Warnings expire after {CaseCards.WarnExpiry.TotalDays:0} days; `/modlogs` still shows them."
                : info.Blurb;
            embed.AddField($"{number}. {info.Title} (/{info.Action})",
                $"{blurb}\n**Usage:** `{info.Usage}`\n**Text version:** `{prefix}{info.PrefixUsage}`");
        }

        if (number == 0)
        {
            await command.FollowupAsync("You don't have access to any moderation commands. A server owner can give a role access with `/configure`.", ephemeral: true);
            return;
        }
        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
