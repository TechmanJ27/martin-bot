using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using ShinBreaker.Services;

var token = Environment.GetEnvironmentVariable("Martin_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Missing Martin_TOKEN environment variable. Set it to your Discord bot token before starting Martin.");
    return;
}

var databasePath = Environment.GetEnvironmentVariable("Martin_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "data", "Martin.db");
// Warnings older than this stop counting as active (shown only in /modlogs). Default 6 weeks.
if (int.TryParse(Environment.GetEnvironmentVariable("Martin_WARN_EXPIRY_DAYS"), out var warnExpiryDays) && warnExpiryDays > 0)
    CaseCards.WarnExpiry = TimeSpan.FromDays(warnExpiryDays);
var database = new ModerationDatabase(databasePath);
await database.InitializeAsync();

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    // GuildMembers and MessageContent are privileged intents: enable both under
    // Bot > Privileged Gateway Intents in the Discord developer portal.
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMessageReactions
        | GatewayIntents.GuildMembers | GatewayIntents.MessageContent,
    AlwaysDownloadUsers = true,
    LogLevel = LogSeverity.Info
});
var logger = new ModerationLogger(database);

var loggingDatabase = new LoggingDatabase(databasePath);
await loggingDatabase.InitializeAsync();
// One-off import: dotnet run -- --import path/to/export.json [targetGuildId] [--replace]
if (args.Length >= 2 && args[0] == "--import")
{
    var replace = args.Contains("--replace");
    ulong? targetGuild = args.Skip(2).FirstOrDefault(a => ulong.TryParse(a, out _)) is { } g ? ulong.Parse(g) : null;
    Console.WriteLine(await ExportImporter.ImportAsync(args[1], databasePath, database, loggingDatabase, targetGuild, replace));
    return;
}

var activityLogger = new ActivityLogger(client, database, loggingDatabase);
activityLogger.Attach();

// Characters that start a text command (".ban @user reason"). Keep it to characters other bots in your
// servers don't use, or both bots will react. Override with Martin_PREFIXES, e.g. ".,;".
var prefixes = Environment.GetEnvironmentVariable("Martin_PREFIXES") is { Length: > 0 } configured ? configured : ".,";
new PrefixCommands(client, database, logger, loggingDatabase, prefixes).Attach();

// How long message text is kept for edit/delete logs.
var retentionDays = int.TryParse(Environment.GetEnvironmentVariable("Martin_MESSAGE_RETENTION_DAYS"), out var days) && days > 0 ? days : 14;

client.Log += message =>
{
    Console.WriteLine(message.ToString());
    return Task.CompletedTask;
};

client.Ready += async () =>
{
    var commands = ModerationCommands.BuildAll().Concat(LoggingCommands.BuildAll()).Concat(AccessCommands.BuildAll())
        .ToArray();
    try
    {
        await client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
        Console.WriteLine($"Martin is ready. Registered {commands.Length} global slash commands: {string.Join(", ", commands.Select(c => "/" + c.Name.Value))}");
    }
    catch (Discord.Net.HttpException ex)
    {
        // Discord refuses the whole list if any one command definition is invalid, and then no command changes at all.
        Console.Error.WriteLine($"Discord REJECTED the slash command list, so no commands were updated: {ex.Message}");
        foreach (var error in ex.Errors) Console.Error.WriteLine($"  {string.Join(".", error.Path)}: {string.Join("; ", error.Errors.Select(e => e.Message))}");
    }
};

client.SlashCommandExecuted += async command =>
{
    try
    {
        if (AccessCommands.Handles(command.Data.Name))
        {
            await AccessCommands.HandleAsync(command, database, prefixes);
            return;
        }

        if (LoggingCommands.Handles(command.Data.Name))
        {
            await LoggingCommands.HandleAsync(command, loggingDatabase, database);
            return;
        }

        await ModerationCommands.HandleAsync(command, database, logger);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Command /{command.Data.Name} failed: {ex}");
        if (command.HasResponded)
            await command.FollowupAsync("Something went wrong while running that command. Check the bot logs.", ephemeral: true);
        else
            await command.RespondAsync("Something went wrong while running that command. Check the bot logs.", ephemeral: true);
    }
};

client.SelectMenuExecuted += async component =>
{
    try { await AccessCommands.HandleComponentAsync(component, database); }
    catch (Exception ex) { Console.Error.WriteLine($"Menu {component.Data.CustomId} failed: {ex}"); }
};

client.ReactionAdded += ReactionHandler.HandleReactionAdded;
client.ReactionRemoved += ReactionHandler.HandleReactionRemoved;

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
_ = activityLogger.RunRetentionAsync(TimeSpan.FromDays(retentionDays));
await Task.Delay(Timeout.Infinite);
