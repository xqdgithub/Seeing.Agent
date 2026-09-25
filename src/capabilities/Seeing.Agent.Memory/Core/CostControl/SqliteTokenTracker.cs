using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core.CostControl;

/// <summary>
/// SQLite Token 追踪器实现
/// </summary>
public class SqliteTokenTracker : ITokenTracker
{
    private readonly SqliteConnectionSource _connections;
    private readonly SqliteConnectionGate _gate;
    private readonly ILogger<SqliteTokenTracker>? _logger;
    private bool _initialized;

    private SqliteConnection Connection => _connections.Get();

    public SqliteTokenTracker(
        SqliteConnectionOwner owner,
        SqliteConnectionGate gate,
        ILogger<SqliteTokenTracker>? logger = null)
    {
        _connections = new SqliteConnectionSource(owner ?? throw new ArgumentNullException(nameof(owner)));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger;
    }

    public SqliteTokenTracker(
        SqliteConnection connection,
        SqliteConnectionGate gate,
        ILogger<SqliteTokenTracker>? logger = null)
    {
        _connections = new SqliteConnectionSource(connection ?? throw new ArgumentNullException(nameof(connection)));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger;
    }

    /// <summary>须在已持有 <see cref="_gate"/> 时调用。</summary>
    private async Task EnsureInitializedCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS token_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                operation TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                created_at TEXT NOT NULL
            )";

        var createIndexSql = @"
            CREATE INDEX IF NOT EXISTS idx_token_created 
            ON token_usage(created_at);
            CREATE INDEX IF NOT EXISTS idx_token_operation 
            ON token_usage(operation)";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"{createTableSql};{createIndexSql}";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("Token 追踪表初始化完成");
    }

    /// <inheritdoc />
    public Task TrackAsync(TokenUsage usage, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var insertSql = @"
            INSERT INTO token_usage (operation, input_tokens, output_tokens, created_at)
            VALUES (@operation, @inputTokens, @outputTokens, @createdAt)";

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = insertSql;
            cmd.Parameters.AddWithValue("@operation", "embedding");
            cmd.Parameters.AddWithValue("@inputTokens", usage.InputTokens);
            cmd.Parameters.AddWithValue("@outputTokens", usage.OutputTokens);
            cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(token);
            _logger?.LogDebug("已记录 Token 消耗: {Total}", usage.TotalTokens);
        }, ct);

    /// <inheritdoc />
    public Task<TokenUsage> GetUsageAsync(
        DateTimeOffset startTime, 
        DateTimeOffset endTime, 
        CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var selectSql = @"
            SELECT 
                COALESCE(SUM(input_tokens), 0) as input_tokens,
                COALESCE(SUM(output_tokens), 0) as output_tokens,
                COUNT(*) as request_count
            FROM token_usage
            WHERE created_at >= @start AND created_at < @end";

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = selectSql;
            cmd.Parameters.AddWithValue("@start", startTime.ToString("O"));
            cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));

            using var reader = await cmd.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var inputTokens = reader.GetInt64(0);
                var outputTokens = reader.GetInt64(1);
                var requestCount = reader.GetInt32(2);

                return new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens, requestCount);
            }

            return TokenUsage.Empty;
        }, ct);

    /// <inheritdoc />
    public async Task<TokenUsage> GetTodayUsageAsync(CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        return await GetUsageAsync(today, tomorrow, ct);
    }

    /// <inheritdoc />
    public Task<TokenUsage> GetOperationUsageAsync(string operation, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var selectSql = @"
            SELECT 
                COALESCE(SUM(input_tokens), 0) as input_tokens,
                COALESCE(SUM(output_tokens), 0) as output_tokens,
                COUNT(*) as request_count
            FROM token_usage
            WHERE operation = @operation";

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = selectSql;
            cmd.Parameters.AddWithValue("@operation", operation);

            using var reader = await cmd.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var inputTokens = reader.GetInt64(0);
                var outputTokens = reader.GetInt64(1);
                var requestCount = reader.GetInt32(2);

                return new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens, requestCount);
            }

            return TokenUsage.Empty;
        }, ct);
}
