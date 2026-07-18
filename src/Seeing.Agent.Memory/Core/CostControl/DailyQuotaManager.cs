using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Core.CostControl;

/// <summary>
/// 每日配额管理器实现
/// </summary>
public class DailyQuotaManager : IQuotaManager, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<DailyQuotaManager>? _logger;
    private bool _initialized;

    public DailyQuotaManager(SqliteConnection connection, ILogger<DailyQuotaManager>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS quota_limits (
                quota_type TEXT PRIMARY KEY,
                daily_limit INTEGER NOT NULL DEFAULT 1000000,
                monthly_limit INTEGER NOT NULL DEFAULT 30000000
            )";

        var createUsageTableSql = @"
            CREATE TABLE IF NOT EXISTS quota_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                quota_type TEXT NOT NULL,
                tokens INTEGER NOT NULL,
                date TEXT NOT NULL,
                created_at TEXT NOT NULL
            )";

        var createIndexSql = @"
            CREATE INDEX IF NOT EXISTS idx_quota_date 
            ON quota_usage(quota_type, date)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{createTableSql};{createUsageTableSql};{createIndexSql}";
        await cmd.ExecuteNonQueryAsync(ct);

        // 插入默认配额
        var insertDefaultSql = @"
            INSERT OR IGNORE INTO quota_limits (quota_type, daily_limit, monthly_limit)
            VALUES ('daily', 1000000, 30000000)";

        using var insertCmd = _connection.CreateCommand();
        insertCmd.CommandText = insertDefaultSql;
        await insertCmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("配额管理表初始化完成");
    }

    /// <inheritdoc />
    public async Task<bool> IsQuotaAvailableAsync(string quotaType = "daily", CancellationToken ct = default)
    {
        var usage = await GetUsageAsync(quotaType, ct);
        return usage.Used < usage.Limit;
    }

    /// <inheritdoc />
    public async Task<QuotaUsage> GetUsageAsync(string quotaType = "daily", CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var date = now.ToString("yyyy-MM-dd");

        // 获取限额
        long limit;
        var getLimitSql = quotaType == "monthly"
            ? "SELECT monthly_limit FROM quota_limits WHERE quota_type = @type"
            : "SELECT daily_limit FROM quota_limits WHERE quota_type = @type";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = getLimitSql;
            cmd.Parameters.AddWithValue("@type", quotaType);
            var result = await cmd.ExecuteScalarAsync(ct);
            limit = result == null ? 1000000 : Convert.ToInt64(result);
        }

        // 获取使用量
        long used;
        var getUsageSql = quotaType == "monthly"
            ? @"SELECT COALESCE(SUM(tokens), 0) FROM quota_usage 
                WHERE quota_type = @type AND date LIKE @datePattern"
            : @"SELECT COALESCE(SUM(tokens), 0) FROM quota_usage 
                WHERE quota_type = @type AND date = @date";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = getUsageSql;
            cmd.Parameters.AddWithValue("@type", quotaType);

            if (quotaType == "monthly")
            {
                cmd.Parameters.AddWithValue("@datePattern", now.ToString("yyyy-MM") + "%");
            }
            else
            {
                cmd.Parameters.AddWithValue("@date", date);
            }

            var result = await cmd.ExecuteScalarAsync(ct);
            used = result == null ? 0 : Convert.ToInt64(result);
        }

        // 计算重置时间
        var resetAt = quotaType == "monthly"
            ? new DateTimeOffset(now.Year, now.Month + 1, 1, 0, 0, 0, now.Offset)
            : now.Date.AddDays(1);

        var usageRate = limit > 0 ? (double)used / limit : 1.0;

        return new QuotaUsage(quotaType, used, limit, usageRate, resetAt);
    }

    /// <inheritdoc />
    public async Task SetLimitAsync(string quotaType, long limit, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var upsertSql = @"
            INSERT INTO quota_limits (quota_type, daily_limit, monthly_limit)
            VALUES (@type, @limit, @limit * 30)
            ON CONFLICT(quota_type) DO UPDATE SET 
                daily_limit = @limit,
                monthly_limit = @limit * 30";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = upsertSql;
        cmd.Parameters.AddWithValue("@type", quotaType);
        cmd.Parameters.AddWithValue("@limit", limit);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug("已设置配额限制: {Type} = {Limit}", quotaType, limit);
    }

    /// <inheritdoc />
    public async Task ConsumeAsync(long tokens, string quotaType = "daily", CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var date = now.ToString("yyyy-MM-dd");

        var insertSql = @"
            INSERT INTO quota_usage (quota_type, tokens, date, created_at)
            VALUES (@type, @tokens, @date, @createdAt)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@type", quotaType);
        cmd.Parameters.AddWithValue("@tokens", tokens);
        cmd.Parameters.AddWithValue("@date", date);
        cmd.Parameters.AddWithValue("@createdAt", now.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已消耗配额: {Type} +{Tokens}", quotaType, tokens);
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
