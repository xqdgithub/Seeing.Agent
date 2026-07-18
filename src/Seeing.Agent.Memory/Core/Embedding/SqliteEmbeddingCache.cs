using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// SQLite Embedding 缓存实现
/// </summary>
public class SqliteEmbeddingCache : IEmbeddingCache, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteEmbeddingCache>? _logger;
    private bool _initialized;
    private int _hits;
    private int _misses;

    public SqliteEmbeddingCache(SqliteConnection connection, ILogger<SqliteEmbeddingCache>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS embedding_cache (
                text_hash TEXT PRIMARY KEY,
                vector BLOB NOT NULL,
                dimensions INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                last_accessed TEXT NOT NULL
            )";

        var createIndexSql = @"
            CREATE INDEX IF NOT EXISTS idx_cache_created 
            ON embedding_cache(created_at)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{createTableSql};{createIndexSql}";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("Embedding 缓存表初始化完成");
    }

    /// <inheritdoc />
    public async Task<EmbeddingResult?> GetAsync(string textHash, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var selectSql = @"
            SELECT vector, dimensions 
            FROM embedding_cache 
            WHERE text_hash = @hash";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@hash", textHash);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            // 更新最后访问时间
            var updateSql = @"
                UPDATE embedding_cache 
                SET last_accessed = @now 
                WHERE text_hash = @hash";

            using var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = updateSql;
            updateCmd.Parameters.AddWithValue("@hash", textHash);
            updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await updateCmd.ExecuteNonQueryAsync(ct);

            var vectorBytes = (byte[])reader.GetValue(0);
            var dimensions = reader.GetInt32(1);
            var vector = DeserializeVector(vectorBytes);

            _hits++;
            _logger?.LogDebug("缓存命中: {Hash}", textHash[..8]);
            return new EmbeddingResult("", vector, vector.Length);
        }

        _misses++;
        _logger?.LogDebug("缓存未命中: {Hash}", textHash[..8]);
        return null;
    }

    /// <inheritdoc />
    public async Task SetAsync(string textHash, EmbeddingResult result, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var insertSql = @"
            INSERT OR REPLACE INTO embedding_cache (text_hash, vector, dimensions, created_at, last_accessed)
            VALUES (@hash, @vector, @dimensions, @createdAt, @lastAccessed)";

        var vectorBytes = SerializeVector(result.Vector);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@hash", textHash);
        cmd.Parameters.AddWithValue("@vector", vectorBytes);
        cmd.Parameters.AddWithValue("@dimensions", result.Vector.Length);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@lastAccessed", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已缓存 Embedding: {Hash}", textHash[..8]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddingResult?>> GetBatchAsync(
        IReadOnlyList<string> textHashes, 
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var results = new List<EmbeddingResult?>();

        foreach (var hash in textHashes)
        {
            var result = await GetAsync(hash, ct);
            results.Add(result);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<int> ClearExpiredAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToString("O");

        var deleteSql = @"
            DELETE FROM embedding_cache 
            WHERE last_accessed < @cutoff";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = deleteSql;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已清除 {Count} 条过期缓存", deleted);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<CacheStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var countSql = "SELECT COUNT(*) FROM embedding_cache";
        using var countCmd = _connection.CreateCommand();
        countCmd.CommandText = countSql;
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        var sizeSql = "SELECT SUM(LENGTH(vector)) FROM embedding_cache";
        using var sizeCmd = _connection.CreateCommand();
        sizeCmd.CommandText = sizeSql;
        var sizeResult = await sizeCmd.ExecuteScalarAsync(ct);
        var sizeBytes = sizeResult == DBNull.Value ? 0L : Convert.ToInt64(sizeResult);

        var total = _hits + _misses;
        var hitRate = total > 0 ? (double)_hits / total : 0;

        return new CacheStats(count, sizeBytes, _hits, _misses, hitRate);
    }

    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
