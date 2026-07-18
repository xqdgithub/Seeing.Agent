using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// SQLite Embedding 缓存实现（共享连接须经 <see cref="SqliteConnectionGate"/>）
/// </summary>
public class SqliteEmbeddingCache : IEmbeddingCache
{
    private readonly SqliteConnection _connection;
    private readonly SqliteConnectionGate _gate;
    private readonly ILogger<SqliteEmbeddingCache>? _logger;
    private bool _initialized;
    private int _hits;
    private int _misses;

    public SqliteEmbeddingCache(
        SqliteConnection connection,
        SqliteConnectionGate gate,
        ILogger<SqliteEmbeddingCache>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger;
    }

    private async Task EnsureInitializedCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS embedding_cache (
                text_hash TEXT PRIMARY KEY,
                vector BLOB NOT NULL,
                dimensions INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                last_accessed TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_cache_created
            ON embedding_cache(created_at)";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("Embedding 缓存表初始化完成");
    }

    /// <inheritdoc />
    public Task<EmbeddingResult?> GetAsync(string textHash, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            byte[]? vectorBytes = null;
            var dimensions = 0;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT vector, dimensions
                    FROM embedding_cache
                    WHERE text_hash = @hash";
                cmd.Parameters.AddWithValue("@hash", textHash);

                using var reader = await cmd.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    _misses++;
                    _logger?.LogDebug("缓存未命中: {Hash}", textHash[..Math.Min(8, textHash.Length)]);
                    return null;
                }

                vectorBytes = (byte[])reader.GetValue(0);
                dimensions = reader.GetInt32(1);
            }

            // 必须在 Reader 关闭后再发 UPDATE（同一连接不允许并发命令）
            using (var updateCmd = _connection.CreateCommand())
            {
                updateCmd.CommandText = @"
                    UPDATE embedding_cache
                    SET last_accessed = @now
                    WHERE text_hash = @hash";
                updateCmd.Parameters.AddWithValue("@hash", textHash);
                updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await updateCmd.ExecuteNonQueryAsync(token);
            }

            var vector = DeserializeVector(vectorBytes!);
            _hits++;
            _logger?.LogDebug("缓存命中: {Hash}", textHash[..Math.Min(8, textHash.Length)]);
            return new EmbeddingResult("", vector, dimensions > 0 ? dimensions : vector.Length);
        }, ct);

    /// <inheritdoc />
    public Task SetAsync(string textHash, EmbeddingResult result, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var vectorBytes = SerializeVector(result.Vector);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO embedding_cache (text_hash, vector, dimensions, created_at, last_accessed)
                VALUES (@hash, @vector, @dimensions, @createdAt, @lastAccessed)";
            cmd.Parameters.AddWithValue("@hash", textHash);
            cmd.Parameters.AddWithValue("@vector", vectorBytes);
            cmd.Parameters.AddWithValue("@dimensions", result.Vector.Length);
            cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@lastAccessed", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(token);

            _logger?.LogDebug("已缓存 Embedding: {Hash}", textHash[..Math.Min(8, textHash.Length)]);
        }, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddingResult?>> GetBatchAsync(
        IReadOnlyList<string> textHashes,
        CancellationToken ct = default)
    {
        var results = new List<EmbeddingResult?>(textHashes.Count);
        foreach (var hash in textHashes)
            results.Add(await GetAsync(hash, ct));
        return results;
    }

    /// <inheritdoc />
    public Task<int> ClearExpiredAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToString("O");
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM embedding_cache WHERE last_accessed < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            var deleted = await cmd.ExecuteNonQueryAsync(token);
            _logger?.LogDebug("已清除 {Count} 条过期缓存", deleted);
            return deleted;
        }, ct);

    /// <inheritdoc />
    public Task<CacheStats> GetStatsAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var countCmd = _connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM embedding_cache";
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(token));

            using var sizeCmd = _connection.CreateCommand();
            sizeCmd.CommandText = "SELECT SUM(LENGTH(vector)) FROM embedding_cache";
            var sizeResult = await sizeCmd.ExecuteScalarAsync(token);
            var sizeBytes = sizeResult is null or DBNull ? 0L : Convert.ToInt64(sizeResult);

            var total = _hits + _misses;
            var hitRate = total > 0 ? (double)_hits / total : 0;
            return new CacheStats(count, sizeBytes, _hits, _misses, hitRate);
        }, ct);

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
}
