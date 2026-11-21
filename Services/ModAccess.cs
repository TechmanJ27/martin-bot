using Discord.WebSocket;

namespace ShinBreaker.Services;

/// <summary>One moderation command that /configure can hand to specific roles, plus the text /help_mod shows for it.</summary>
public sealed record ModActionInfo(string Action, string Title, string Blurb, string Usage, string PrefixUsage, string DefaultLabel);

/// <summary>
/// Decides who may use a moderation command. If a server owner assigned roles to the command with /configure,
/// only members with one of those roles may use it. If no roles are assigned, the normal Discord permission
/// (Ban Members, Moderate Members, ...) applies. The server owner and Administrators can always use everything.
/// Slash commands and prefix commands share this check.
/// </summary>
public static class ModAccess
{
    public static readonly ModActionInfo[] Actions =
    [
        new("verify", "Verifying members", "Finish a member's verification: gives them the verified role and removes the unverified one (set up with /verify config).",
            "/verify member user:@user note:Passed the check", "verify @user Passed the check", "Manage Roles"),
        new("warn", "Warning users", "Give a formal warning to a member. They get a DM and it is saved as a case.",
            "/warn user:@user reason:Stop spamming", "warn @user Stop spamming", "Moderate Members"),
        new("mute", "Timing out users", "Temporarily stop a member from chatting (1 minute to 28 days).",
            "/mute user:@user minutes:30 reason:Inappropriate language", "mute @user 30 Inappropriate language", "Moderate Members"),
        new("unmute", "Ending a timeout early", "Remove a member's timeout.",
            "/unmute user:@user reason:Cooled down", "unmute @user Cooled down", "Moderate Members"),
        new("kick", "Kicking users", "Remove a member from the server. They can rejoin.",
            "/kick user:@user reason:Repeated spamming", "kick @user Repeated spamming", "Kick Members"),
        new("ban", "Banning users", "Ban a member from the server.",
            "/ban user:@user reason:Repeated spamming", "ban @user Repeated spamming", "Ban Members"),
        new("unban", "Unbanning users", "Unban someone by their Discord ID.",
            "/unban user_id:123456789012345678 reason:Appeal accepted", "unban 123456789012345678 Appeal accepted", "Ban Members"),
        new("warnings", "Checking warnings", "Show a member's active warnings. Older warnings expire.",
            "/warnings user:@user", "warnings @user", "Moderate Members"),
        new("modlogs", "Full history", "Show a member's full moderation history, including expired warnings.",
            "/modlogs user:@user", "modlogs @user", "Moderate Members"),
        new("purge", "Clearing messages", "Delete up to 100 recent messages in this channel.",
            "/purge amount:20", "purge 20", "Manage Messages"),
    ];

    public static ModActionInfo? Find(string action) => Actions.FirstOrDefault(a => a.Action == action);

    public static bool IsOwnerOrAdmin(SocketGuild guild, SocketGuildUser user) =>
        user.Id == guild.OwnerId || user.GuildPermissions.Administrator;

    public static async Task<bool> CanUseAsync(ModerationDatabase database, SocketGuild guild, SocketGuildUser user, string action)
    {
        if (IsOwnerOrAdmin(guild, user)) return true;
        var configured = await database.GetActionRolesAsync(guild.Id);
        if (configured.TryGetValue(action, out var roleIds) && roleIds.Count > 0)
            return user.Roles.Any(r => roleIds.Contains(r.Id));
        return HasDefaultPermission(action, user);
    }

    private static bool HasDefaultPermission(string action, SocketGuildUser user) => action switch
    {
        "ban" or "unban" => user.GuildPermissions.BanMembers,
        "kick" => user.GuildPermissions.KickMembers,
        "purge" => user.GuildPermissions.ManageMessages,
        "verify" => user.GuildPermissions.ManageRoles,
        "warnings" or "modlogs" => user.GuildPermissions.ModerateMembers || user.GuildPermissions.ManageGuild,
        _ => user.GuildPermissions.ModerateMembers
    };

    /// <summary>The "you can't use this" message: names the configured roles, or the default permission.</summary>
    public static async Task<string> DeniedAsync(ModerationDatabase database, SocketGuild guild, string action)
    {
        var configured = await database.GetActionRolesAsync(guild.Id);
        if (configured.TryGetValue(action, out var roleIds) && roleIds.Count > 0)
            return $"You need one of these roles to use this command: {string.Join(", ", roleIds.Select(id => $"<@&{id}>"))}.";
        return $"You need **{Find(action)?.DefaultLabel ?? "more permissions"}** to use this command.";
    }
}
