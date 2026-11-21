using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

/// <summary>The roles /verify swaps: GiveRoleId is added, RemoveRoleId (optional) is taken away.</summary>
public sealed record VerifyConfig(ulong GiveRoleId, ulong? RemoveRoleId);

/// <summary>
/// Verification: /verify config (server owner / admins) picks the role to give and the role to remove,
/// and /verify member (anyone allowed through ModAccess) applies that swap to a member and records a "verify" case.
/// Slash and text commands share <see cref="VerifyAsync"/>.
/// </summary>
public static class Verification
{
    public static async Task ConfigureAsync(SocketSlashCommand command, IReadOnlyCollection<SocketSlashCommandDataOption> options,
        ModerationDatabase database, SocketGuild guild, SocketGuildUser user)
    {
        if (!ModAccess.IsOwnerOrAdmin(guild, user))
        {
            await command.FollowupAsync("Only the server owner or an administrator can use `/verify config`.", ephemeral: true);
            return;
        }

        var give = options.FirstOrDefault(o => o.Name == "give_role")?.Value as SocketRole;
        var remove = options.FirstOrDefault(o => o.Name == "remove_role")?.Value as SocketRole;
        var reset = options.FirstOrDefault(o => o.Name == "reset")?.Value is true;

        if (reset)
        {
            await database.ClearVerifyConfigAsync(guild.Id);
            await command.FollowupAsync("Verification setup removed. `/verify` won't work until you set it up again.", ephemeral: true);
            return;
        }

        if (give is null && remove is null)
        {
            await command.FollowupAsync(await DescribeAsync(database, guild), allowedMentions: AllowedMentions.None, ephemeral: true);
            return;
        }
        if (give is null)
        {
            await command.FollowupAsync("Choose the role to give with `give_role`. `remove_role` is optional.", ephemeral: true);
            return;
        }
        if (remove is not null && remove.Id == give.Id)
        {
            await command.FollowupAsync("The role to give and the role to remove must be different.", ephemeral: true);
            return;
        }
        foreach (var role in new[] { give, remove }.OfType<SocketRole>())
        {
            if (role.IsEveryone || role.IsManaged)
            {
                await command.FollowupAsync($"{role.Mention} can't be handed out (it's @everyone or managed by an integration). Pick a normal role.",
                    allowedMentions: AllowedMentions.None, ephemeral: true);
                return;
            }
            if (role.Position >= guild.CurrentUser.Hierarchy)
            {
                await command.FollowupAsync($"I can't manage {role.Mention} because it's above (or equal to) my highest role. " +
                    "Drag my role above it in Server Settings → Roles, then run this again.",
                    allowedMentions: AllowedMentions.None, ephemeral: true);
                return;
            }
        }

        await database.SetVerifyConfigAsync(guild.Id, give.Id, remove?.Id);
        await command.FollowupAsync("Saved.\n" + await DescribeAsync(database, guild), allowedMentions: AllowedMentions.None, ephemeral: true);
    }

    private static async Task<string> DescribeAsync(ModerationDatabase database, SocketGuild guild)
    {
        var config = await database.GetVerifyConfigAsync(guild.Id);
        if (config is null)
            return "Verification isn't set up. Run `/verify config give_role:@Member remove_role:@Visitor` (`remove_role` is optional).";
        return $"`/verify` gives <@&{config.GiveRoleId}>" +
               (config.RemoveRoleId is { } r ? $" and removes <@&{r}>." : " and removes nothing.") +
               "\nWho can run `/verify`: administrators, members with Manage Roles, or roles you pick for it with `/configure`. " +
               "Use `reset:True` to remove this setup.";
    }

    /// <summary>Applies the configured role swap. Returns the message to show the moderator (success or the reason it failed).</summary>
    public static async Task<string> VerifyAsync(ModerationDatabase database, ModerationLogger logger,
        SocketGuild guild, SocketGuildUser moderator, SocketGuildUser? target, string note)
    {
        var config = await database.GetVerifyConfigAsync(guild.Id);
        if (config is null) return "Verification isn't set up yet. A server owner can set it up with `/verify config`.";
        if (target is null) return "That user isn't currently a member of this server.";
        if (target.IsBot) return "Bots can't be verified.";
        if (target.Id == moderator.Id) return "You can't verify yourself.";

        var give = guild.GetRole(config.GiveRoleId);
        if (give is null) return "The role to give no longer exists. Run `/verify config` again.";
        var remove = config.RemoveRoleId is { } removeId ? guild.GetRole(removeId) : null;

        var hasGive = target.Roles.Any(r => r.Id == give.Id);
        var hasRemove = remove is not null && target.Roles.Any(r => r.Id == remove.Id);
        if (hasGive && !hasRemove) return $"{target.Mention} is already verified.";

        // Roles are changed by the bot, so the bot must be able to manage them; moderators can't hand out roles above their own.
        var touched = new List<SocketRole>();
        if (!hasGive) touched.Add(give);
        if (hasRemove) touched.Add(remove!);
        if (!guild.CurrentUser.GuildPermissions.ManageRoles)
            return "I need the **Manage Roles** permission to do that.";
        if (touched.Any(r => r.Position >= guild.CurrentUser.Hierarchy))
            return "One of the verification roles is above my highest role, so I can't change it. Move my role higher in Server Settings → Roles.";
        if (!ModAccess.IsOwnerOrAdmin(guild, moderator) && touched.Any(r => r.Position >= moderator.Hierarchy))
            return "You can't hand out or take away roles that are above your own highest role.";

        var reason = string.IsNullOrWhiteSpace(note) ? "Verified" : note.Trim();
        var options = new RequestOptions { AuditLogReason = $"Verification by {moderator.Username}: {reason}" };
        try
        {
            // Give first, then remove, so the member is never left without a role in between.
            if (!hasGive) await target.AddRoleAsync(give.Id, options);
            if (hasRemove) await target.RemoveRoleAsync(remove!.Id, options);
        }
        catch (Discord.Net.HttpException ex)
        {
            return $"Discord refused that action: {ex.Reason ?? ex.Message}";
        }

        var caseId = await database.AddCaseAsync(guild.Id, target.Id, moderator.Id, "verify", reason);
        await logger.LogAsync(guild, caseId, "verify", target.Id, target.Username, moderator, reason);
        return $"Verified {target.Mention}: " + (hasGive ? "" : $"added {give.Mention}") +
               (hasRemove ? (hasGive ? "" : ", ") + $"removed {remove!.Mention}" : "") + $". Case `#{caseId:0000}`.";
    }
}
