using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

public sealed record ModerationCase(
    long Id,
    ulong GuildId,
    ulong TargetId,
    ulong ModeratorId,
    string Action,
    string Reason,
    DateTimeOffset CreatedAt,
    int? DurationMinutes,
    int? RuleNumber = null);

public sealed record GuildRule(int Number, string Title, string Details);

public sealed class ModerationDatabase
{
    private readonly string _connectionString;

    public ModerationDatabase(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS ModerationCases (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GuildId TEXT NOT NULL,
                TargetId TEXT NOT NULL,
                ModeratorId TEXT NOT NULL,
                Action TEXT NOT NULL,
                Reason TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                DurationMinutes INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ModerationCases_GuildTarget
                ON ModerationCases (GuildId, TargetId, Id DESC);
            CREATE TABLE IF NOT EXISTS GuildSettings (
                GuildId TEXT PRIMARY KEY,
                LogChannelId TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS ActionRoles (
                GuildId TEXT NOT NULL,
                Action TEXT NOT NULL,
                RoleId TEXT NOT NULL,
                PRIMARY KEY (GuildId, Action, RoleId)
            );
            CREATE TABLE IF NOT EXISTS VerifyConfig (
                GuildId TEXT PRIMARY KEY,
                GiveRoleId TEXT NOT NULL,
                RemoveRoleId TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS GuildRules (
                GuildId TEXT NOT NULL,
                RuleNumber INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Details TEXT NOT NULL,
                PRIMARY KEY (GuildId, RuleNumber)
            );
            """;
        await command.ExecuteNonQueryAsync();

        // Column added after the first release; older databases get it here.
        var hasRuleNumber = false;
        var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(ModerationCases);";
        await using (var reader = await info.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                if (string.Equals(reader.GetString(1), "RuleNumber", StringComparison.OrdinalIgnoreCase)) hasRuleNumber = true;
        if (!hasRuleNumber)
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE ModerationCases ADD COLUMN RuleNumber INTEGER NULL;";
            await alter.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Roles assigned to each command with /configure. Commands with no entry use the default Discord permission.</summary>
    public async Task<IReadOnlyDictionary<string, HashSet<ulong>>> GetActionRolesAsync(ulong guildId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Action, RoleId FROM ActionRoles WHERE GuildId = $guild;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        var map = new Dictionary<string, HashSet<ulong>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!ulong.TryParse(reader.GetString(1), out var roleId)) continue;
            var action = reader.GetString(0);
            if (!map.TryGetValue(action, out var set)) map[action] = set = [];
            set.Add(roleId);
        }
        return map;
    }

    public async Task<HashSet<string>> GetRoleActionsAsync(ulong guildId, ulong roleId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Action FROM ActionRoles WHERE GuildId = $guild AND RoleId = $role;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$role", roleId.ToString());
        var actions = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actions.Add(reader.GetString(0));
        return actions;
    }

    /// <summary>Sets exactly which commands a role may use (an empty set removes the role from every command).</summary>
    public async Task SetRoleActionsAsync(ulong guildId, ulong roleId, IEnumerable<string> actions)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM ActionRoles WHERE GuildId = $guild AND RoleId = $role;";
        delete.Parameters.AddWithValue("$guild", guildId.ToString());
        delete.Parameters.AddWithValue("$role", roleId.ToString());
        await delete.ExecuteNonQueryAsync();
        foreach (var action in actions)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO ActionRoles (GuildId, Action, RoleId) VALUES ($guild, $action, $role);";
            insert.Parameters.AddWithValue("$guild", guildId.ToString());
            insert.Parameters.AddWithValue("$action", action);
            insert.Parameters.AddWithValue("$role", roleId.ToString());
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<VerifyConfig?> GetVerifyConfigAsync(ulong guildId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT GiveRoleId, RemoveRoleId FROM VerifyConfig WHERE GuildId = $guild;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || !ulong.TryParse(reader.GetString(0), out var give)) return null;
        ulong? remove = !reader.IsDBNull(1) && ulong.TryParse(reader.GetString(1), out var r) ? r : null;
        return new VerifyConfig(give, remove);
    }

    public async Task SetVerifyConfigAsync(ulong guildId, ulong giveRoleId, ulong? removeRoleId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO VerifyConfig (GuildId, GiveRoleId, RemoveRoleId) VALUES ($guild, $give, $remove)
            ON CONFLICT(GuildId) DO UPDATE SET GiveRoleId = $give, RemoveRoleId = $remove;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$give", giveRoleId.ToString());
        command.Parameters.AddWithValue("$remove", removeRoleId?.ToString() ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ClearVerifyConfigAsync(ulong guildId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM VerifyConfig WHERE GuildId = $guild;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyDictionary<int, GuildRule>> GetRulesAsync(ulong guildId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT RuleNumber, Title, Details FROM GuildRules WHERE GuildId = $guild ORDER BY RuleNumber;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        var rules = new Dictionary<int, GuildRule>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rules[reader.GetInt32(0)] = new GuildRule(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
        return rules;
    }

    /// <summary>Replaces the stored rules of a guild with the given list.</summary>
    public async Task ReplaceRulesAsync(ulong guildId, IEnumerable<GuildRule> rules)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM GuildRules WHERE GuildId = $guild;";
        delete.Parameters.AddWithValue("$guild", guildId.ToString());
        await delete.ExecuteNonQueryAsync();
        foreach (var rule in rules)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO GuildRules (GuildId, RuleNumber, Title, Details) VALUES ($guild, $number, $title, $details);";
            insert.Parameters.AddWithValue("$guild", guildId.ToString());
            insert.Parameters.AddWithValue("$number", rule.Number);
            insert.Parameters.AddWithValue("$title", rule.Title);
            insert.Parameters.AddWithValue("$details", rule.Details);
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<long> AddCaseAsync(ulong guildId, ulong targetId, ulong moderatorId,
        string action, string reason, int? durationMinutes = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ModerationCases (GuildId, TargetId, ModeratorId, Action, Reason, CreatedAt, DurationMinutes)
            VALUES ($guild, $target, $moderator, $action, $reason, $created, $duration);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$moderator", moderatorId.ToString());
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$duration", (object?)durationMinutes ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<IReadOnlyList<ModerationCase>> GetCasesAsync(ulong guildId, ulong targetId, int limit = 10)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GuildId, TargetId, ModeratorId, Action, Reason, CreatedAt, DurationMinutes, RuleNumber
            FROM ModerationCases WHERE GuildId = $guild AND TargetId = $target
            ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 25));
        var cases = new List<ModerationCase>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cases.Add(new ModerationCase(
                reader.GetInt64(0), ulong.Parse(reader.GetString(1)), ulong.Parse(reader.GetString(2)),
                ulong.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8)));
        }
        return cases;
    }

    public async Task<ulong?> GetLogChannelAsync(ulong guildId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT LogChannelId FROM GuildSettings WHERE GuildId = $guild;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        var value = await command.ExecuteScalarAsync();
        return value is string text && ulong.TryParse(text, out var id) ? id : null;
    }

    public async Task SetLogChannelAsync(ulong guildId, ulong? channelId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GuildSettings (GuildId, LogChannelId) VALUES ($guild, $channel)
            ON CONFLICT(GuildId) DO UPDATE SET LogChannelId = excluded.LogChannelId;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$channel", channelId?.ToString() is { } id ? id : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
