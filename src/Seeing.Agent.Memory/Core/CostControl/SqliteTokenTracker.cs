using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core.CostControl;

/// <summary>
/// SQLite Token 追踪器实现
/// </summary>
public class SqliteTokenTracker : ITokenTracker, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteTokenTracker>? _logger;
    private bool _initialized;

    public SqliteTokenTracker(SqliteConnection connection, ILogger<SqliteTokenTracker>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
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

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{createTableSql};{createIndexSql}";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("Token 追踪表初始化完成");
    }

    /// <inheritdoc />
    public async Task TrackAsync(TokenUsage usage, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var insertSql = @"
            INSERT INTO token_usage (operation, input_tokens, output_tokens, created_at)
            VALUES (@operation, @inputTokens, @outputTokens, @createdAt)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@operation", "embedding");
        cmd.Parameters.AddWithValue("@inputTokens", usage.InputTokens);
        cmd.Parameters.AddWithValue("@outputTokens", usage.OutputTokens);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已记录 Token 消耗: {Total}", usage.TotalTokens);
    }

    /// <inheritdoc />
    public async Task<TokenUsage> GetUsageAsync(
        DateTimeOffset startTime, 
        DateTimeOffset endTime, 
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var selectSql = @"
            SELECT 
                COALESCE(SUM(input_tokens), 0) as input_tokens,
                COALESCE(SUM(output_tokens), 0) as output_tokens,
                COUNT(*) as request_count
            FROM token_usage
            WHERE created_at >= @start AND created_at < @end";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var inputTokens = reader.GetInt64(0);
            var outputTokens = reader.GetInt64(1);
            var requestCount = reader.GetInt32(2);

            return new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens, requestCount);
        }

        return TokenUsage.Empty;
    }

    /// <inheritdoc />
    public async Task<TokenUsage> GetTodayUsageAsync(CancellationToken ct = default)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var tomorrow = today.AddDays(1);
        return await GetUsageAsync(today, tomorrow, ct);
    }

    /// <inheritdoc />
    public async Task<TokenUsage> GetOperationUsageAsync(string operation, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var selectSql = @"
            SELECT 
                COALESCE(SUM(input_tokens), 0) as input_tokens,
                COALESCE(SUM(output_tokens), 0) as output_tokens,
                COUNT(*) as request_count
            FROM token_usage
            WHERE operation = @operation";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@operation", operation);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var inputTokens = reader.GetInt64(0);
            var outputTokens = reader.GetInt64(1);
            var requestCount = reader.GetInt32(2);

            return new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens, requestCount);
        }

        return TokenUsage.Empty;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
