using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

public static class ModerationCommands
{
    public static ApplicationCommandProperties[] BuildAll()
    {
        var commands = new List<ApplicationCommandProperties>
        {
            new SlashCommandBuilder().WithName("verify").WithDescription("Verify members, and choose which roles verifying gives and removes")
                .AddOption(new SlashCommandOptionBuilder().WithName("member")
                    .WithDescription("Verify a member: gives the verified role and removes the unverified one")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("user", ApplicationCommandOptionType.User, "Member to verify", isRequired: true)
                    .AddOption("note", ApplicationCommandOptionType.String, "Optional note saved with the case", isRequired: false))
                .AddOption(new SlashCommandOptionBuilder().WithName("config")
                    .WithDescription("Owner/admin: choose the role to give and the role to remove")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("give_role", ApplicationCommandOptionType.Role, "Role to give when verifying (e.g. Member)", isRequired: false)
                    .AddOption("remove_role", ApplicationCommandOptionType.Role, "Role to remove when verifying (e.g. Visitor)", isRequired: false)
                    .AddOption("reset", ApplicationCommandOptionType.Boolean, "Remove the verification setup", isRequired: false))
                .Build(),
            new SlashCommandBuilder().WithName("ban").WithDescription("Ban a member")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to ban", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the ban", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("kick").WithDescription("Kick a member")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to kick", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the kick", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("warn").WithDescription("Issue a warning to a member")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to warn", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the warning", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("mute").WithDescription("Mute a member")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to mute", isRequired: true)
                .AddOption("minutes", ApplicationCommandOptionType.Integer, "Mute duration in minutes (1-40320)", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the mute", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("unmute").WithDescription("Unmute a member")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to unmute", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("unban").WithDescription("Unban a user by their Discord ID")
                .AddOption("user_id", ApplicationCommandOptionType.String, "Discord user ID", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the unban", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("warnings").WithDescription("View a member's warning history")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to look up", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("modlogs").WithDescription("View a member's recent moderation cases")
                .AddOption("user", ApplicationCommandOptionType.User, "Member to look up", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("modconfig").WithDescription("Configure moderation logging")
                .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
                .AddOption("log_channel", ApplicationCommandOptionType.Channel, "Channel for moderation logs", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("purge").WithDescription("Delete up to 100 recent messages")
                .AddOption("amount", ApplicationCommandOptionType.Integer, "Number of messages to delete (1-100)", isRequired: true).Build()
        };
        return commands.ToArray();
    }

    public static async Task HandleAsync(SocketSlashCommand command, ModerationDatabase database, ModerationLogger logger)
    {
        if (command.GuildId is null || (command.Channel as SocketGuildChannel)?.Guild is not SocketGuild guild || command.User is not SocketGuildUser moderator)
        {
            await command.RespondAsync("Moderation commands can only be used inside a server.", ephemeral: true);
            return;
        }

        // Discord requires a reply within 3 seconds. Acknowledge right away, then send the real
        // answer with FollowupAsync once the database / API calls are done.
        await command.DeferAsync(ephemeral: true);

        var name = command.Data.Name;
        if (name == "modconfig")
        {
            if (!moderator.GuildPermissions.ManageGuild)
            {
                await command.FollowupAsync("You need **Manage Server** to configure moderation logs.", ephemeral: true);
                return;
            }
            var channel = command.Data.Options.First().Value as IChannel;
            if (channel is not SocketGuildChannel guildChannel || guildChannel.Guild.Id != guild.Id || guildChannel is not SocketTextChannel)
            {
                await command.FollowupAsync("Choose a text channel in this server.", ephemeral: true);
                return;
            }
            await database.SetLogChannelAsync(guild.Id, guildChannel.Id);
            await command.FollowupAsync($"Moderation logs will be sent to <#{guildChannel.Id}>.", ephemeral: true);
            await logger.LogSystemAsync(guild, "Moderation logging configured", $"Configured by {moderator.Mention}. Log channel: <#{guildChannel.Id}>.");
            return;
        }

        // Who may use each moderation command is decided by ModAccess (roles chosen with /configure, else Discord permissions).
        if (ModAccess.Find(name) is not null && !await ModAccess.CanUseAsync(database, guild, moderator, name))
        {
            await command.FollowupAsync(await ModAccess.DeniedAsync(database, guild, name), allowedMentions: AllowedMentions.None, ephemeral: true);
            return;
        }

        if (name == "verify")
        {
            var sub = command.Data.Options.First();
            if (sub.Name == "config")
            {
                await Verification.ConfigureAsync(command, sub.Options, database, guild, moderator);
                return;
            }
            var verifyUser = sub.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
            var verifyNote = sub.Options.FirstOrDefault(o => o.Name == "note")?.Value as string ?? "";
            var verifyMember = verifyUser as SocketGuildUser ?? (verifyUser is null ? null : guild.GetUser(verifyUser.Id));
            await command.FollowupAsync(await Verification.VerifyAsync(database, logger, guild, moderator, verifyMember, verifyNote),
                allowedMentions: AllowedMentions.None, ephemeral: true);
            return;
        }

        if (name == "purge")
        {
            if (command.Channel is not SocketTextChannel textChannel)
            {
                await command.FollowupAsync("This command only works in a server text channel.", ephemeral: true);
                return;
            }
            var amount = Convert.ToInt32(command.Data.Options.First().Value);
            if (amount is < 1 or > 100)
            {
                await command.FollowupAsync("Amount must be between 1 and 100.", ephemeral: true);
                return;
            }
            var messages = (await textChannel.GetMessagesAsync(amount).FlattenAsync())
                .Where(m => (DateTimeOffset.UtcNow - m.Timestamp).TotalDays < 14 && m.Id != command.Id)
                .ToArray();
            if (messages.Length > 0) await textChannel.DeleteMessagesAsync(messages);
            var caseId = await database.AddCaseAsync(guild.Id, moderator.Id, moderator.Id, "purge",
                $"Deleted {messages.Length} message(s) in #{textChannel.Name}.");
            await command.FollowupAsync($"Deleted {messages.Length} message(s). Case `#{caseId:0000}`.", ephemeral: true);
            await logger.LogSystemAsync(guild, $"Moderation Case #{caseId:0000} · purge",
                $"**Moderator:** {moderator.Mention}\n**Channel:** {textChannel.Mention}\n**Messages deleted:** {messages.Length}");
            return;
        }

        var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var reason = command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string ?? "No reason provided";

        if (name == "unban")
        {
            var rawId = (string)command.Data.Options.First(o => o.Name == "user_id").Value;
            if (!ulong.TryParse(rawId, out var userId))
            {
                await command.FollowupAsync("That doesn't look like a valid Discord user ID.", ephemeral: true);
                return;
            }
            await guild.RemoveBanAsync(userId, new RequestOptions { AuditLogReason = reason });
            var caseId = await database.AddCaseAsync(guild.Id, userId, moderator.Id, "unban", reason);
            await command.FollowupAsync($"Unbanned user `{userId}`. Case `#{caseId:0000}`.", ephemeral: true);
            await logger.LogAsync(guild, caseId, "unban", userId, userId.ToString(), moderator, reason);
            return;
        }

        if (name is "warnings" or "modlogs")
        {
            if (userOption is null)
            {
                await command.FollowupAsync("I couldn't resolve that user.", ephemeral: true);
                return;
            }
            var view = await CaseCards.BuildViewAsync(database, guild.Id, userOption.Id, userOption.Mention, name == "warnings");
            if (view.Pages.Count == 0)
            {
                await command.FollowupAsync(view.Header, allowedMentions: AllowedMentions.None, ephemeral: true);
                return;
            }
            for (var i = 0; i < view.Pages.Count; i++)
                await command.FollowupAsync(text: i == 0 ? view.Header : null, embeds: view.Pages[i],
                    allowedMentions: AllowedMentions.None, ephemeral: true);
            return;
        }

        if (userOption is null)
        {
            await command.FollowupAsync("I couldn't resolve that user.", ephemeral: true);
            return;
        }
        if (userOption.Id == moderator.Id)
        {
            await command.FollowupAsync("You can't moderate yourself.", ephemeral: true);
            return;
        }
        if (userOption.Id == guild.OwnerId)
        {
            await command.FollowupAsync("You can't moderate the server owner.", ephemeral: true);
            return;
        }

        SocketGuildUser? targetMember = userOption as SocketGuildUser ?? guild.GetUser(userOption.Id);
        if (targetMember is null)
        {
            await command.FollowupAsync("That user isn't currently a member of this server.", ephemeral: true);
            return;
        }
        if (targetMember.Id == guild.CurrentUser.Id || targetMember.Hierarchy >= moderator.Hierarchy || targetMember.Hierarchy >= guild.CurrentUser.Hierarchy)
        {
            await command.FollowupAsync("Your role and the bot's role must both be higher than the target's role, and you can't moderate the bot itself.", ephemeral: true);
            return;
        }

        int? durationMinutes = null;
        if (name == "ban")
        {
            await guild.AddBanAsync(targetMember, 0, reason);
        }
        else if (name == "kick")
        {
            await targetMember.KickAsync(reason);
        }
        else if (name == "warn")
        {
        }
        else if (name == "mute")
        {
            durationMinutes = Convert.ToInt32(command.Data.Options.First(o => o.Name == "minutes").Value);
            if (durationMinutes is < 1 or > 40320)
            {
                await command.FollowupAsync("Mute duration must be between 1 minute and 28 days (40,320 minutes).", ephemeral: true);
                return;
            }
            await targetMember.SetTimeOutAsync(TimeSpan.FromMinutes(durationMinutes.Value), new RequestOptions { AuditLogReason = reason });
        }
        else if (name == "unmute")
        {
            await targetMember.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = reason });
        }
        else
        {
            await command.FollowupAsync("Unknown moderation command.", ephemeral: true);
            return;
        }

        var caseAction = name;
        var caseNumber = await database.AddCaseAsync(guild.Id, targetMember.Id, moderator.Id, caseAction, reason, durationMinutes);
        await command.FollowupAsync($"Done: **{caseAction}** {targetMember.Mention}. Case `#{caseNumber:0000}`.", ephemeral: true);
        await logger.LogAsync(guild, caseNumber, caseAction, targetMember.Id, targetMember.Username, moderator, reason, durationMinutes);

        if (name == "warn")
        {
            try
            {
                await targetMember.SendMessageAsync(embed: new EmbedBuilder().WithTitle($"Warning in {guild.Name}")
                    .WithDescription($"You have received a warning.\n**Reason:** {reason}\n**Case:** #{caseNumber:0000}")
                    .WithColor(Color.Gold).Build());
            }
            catch (Discord.Net.HttpException) { /* DMs may be disabled; warning remains recorded. */ }
        }
    }

    private static Task DenyAsync(SocketSlashCommand command, string permission) =>
        command.FollowupAsync($"You need **{permission}** to use this command.", ephemeral: true);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

}