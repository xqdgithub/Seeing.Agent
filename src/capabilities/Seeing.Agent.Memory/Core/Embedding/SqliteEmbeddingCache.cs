using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Embedding;

/// <summary>
/// SQLite Embedding 缓存实现（共享连接须经 <see cref="SqliteConnectionGate"/>）
/// <para>主键是 <c>(text_hash, model_fingerprint)</c>：向量空间随模型而不同，
/// 只按文本哈希命中会把「A 模型的向量」当作「B 模型的向量」返回（spec §7.8.1）。</para>
/// </summary>
public class SqliteEmbeddingCache : IEmbeddingCache
{
    private readonly SqliteConnectionSource _connections;
    private readonly SqliteConnectionGate _gate;
    private readonly ILogger<SqliteEmbeddingCache>? _logger;
    private bool _initialized;
    private int _hits;
    private int _misses;

    private SqliteConnection Connection => _connections.Get();

    public SqliteEmbeddingCache(
        SqliteConnectionOwner owner,
        SqliteConnectionGate gate,
        ILogger<SqliteEmbeddingCache>? logger = null)
    {
        _connections = new SqliteConnectionSource(owner ?? throw new ArgumentNullException(nameof(owner)));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger;
    }

    public SqliteEmbeddingCache(
        SqliteConnection connection,
        SqliteConnectionGate gate,
        ILogger<SqliteEmbeddingCache>? logger = null)
    {
        _connections = new SqliteConnectionSource(connection ?? throw new ArgumentNullException(nameof(connection)));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger;
    }

    private async Task EnsureInitializedCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // 旧表（主键只有 text_hash）无法承载模型维度：它既不能区分同文本的不同模型向量，
        // 也没地方放新列。缓存是纯派生数据（丢了最多重算一次向量），直接重建而不做数据迁移。
        if (await TableExistsCoreAsync(ct) && !await HasColumnCoreAsync("model_fingerprint", ct))
        {
            using (var dropCmd = Connection.CreateCommand())
            {
                dropCmd.CommandText = "DROP TABLE embedding_cache";
                await dropCmd.ExecuteNonQueryAsync(ct);
            }

            _logger?.LogWarning(
                "embedding_cache 表结构为旧版（主键缺模型指纹），已重建为空表；既有向量缓存作废并会按需重算。");
        }

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS embedding_cache (
                text_hash TEXT NOT NULL,
                model_fingerprint TEXT NOT NULL,
                vector BLOB NOT NULL,
                dimensions INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                last_accessed TEXT NOT NULL,
                PRIMARY KEY (text_hash, model_fingerprint)
            );
            CREATE INDEX IF NOT EXISTS idx_cache_created
            ON embedding_cache(created_at)";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("Embedding 缓存表初始化完成（主键：text_hash + model_fingerprint）");
    }

    private async Task<bool> TableExistsCoreAsync(CancellationToken ct)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='embedding_cache'";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    private async Task<bool> HasColumnCoreAsync(string column, CancellationToken ct)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('embedding_cache') WHERE name = @column";
        cmd.Parameters.AddWithValue("@column", column);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    /// <inheritdoc />
    public Task<EmbeddingResult?> GetAsync(
        string textHash,
        string modelFingerprint,
        CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            byte[]? vectorBytes = null;
            var dimensions = 0;

            using (var cmd = Connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT vector, dimensions
                    FROM embedding_cache
                    WHERE text_hash = @hash AND model_fingerprint = @fingerprint";
                cmd.Parameters.AddWithValue("@hash", textHash);
                cmd.Parameters.AddWithValue("@fingerprint", modelFingerprint);

                using var reader = await cmd.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    _misses++;
                    _logger?.LogDebug("缓存未命中: {Hash} / {Fingerprint}",
                        textHash[..Math.Min(8, textHash.Length)], modelFingerprint);
                    return null;
                }

                vectorBytes = (byte[])reader.GetValue(0);
                dimensions = reader.GetInt32(1);
            }

            // 必须在 Reader 关闭后再发 UPDATE（同一连接不允许并发命令）
            using (var updateCmd = Connection.CreateCommand())
            {
                updateCmd.CommandText = @"
                    UPDATE embedding_cache
                    SET last_accessed = @now
                    WHERE text_hash = @hash AND model_fingerprint = @fingerprint";
                updateCmd.Parameters.AddWithValue("@hash", textHash);
                updateCmd.Parameters.AddWithValue("@fingerprint", modelFingerprint);
                updateCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                await updateCmd.ExecuteNonQueryAsync(token);
            }

            var vector = DeserializeVector(vectorBytes!);
            _hits++;
            _logger?.LogDebug("缓存命中: {Hash} / {Fingerprint}",
                textHash[..Math.Min(8, textHash.Length)], modelFingerprint);
            return new EmbeddingResult("", vector, dimensions > 0 ? dimensions : vector.Length, modelFingerprint);
        }, ct);

    /// <inheritdoc />
    public Task SetAsync(
        string textHash,
        string modelFingerprint,
        EmbeddingResult result,
        CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(result);
            await EnsureInitializedCoreAsync(token);

            var vectorBytes = SerializeVector(result.Vector);
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO embedding_cache
                    (text_hash, model_fingerprint, vector, dimensions, created_at, last_accessed)
                VALUES (@hash, @fingerprint, @vector, @dimensions, @createdAt, @lastAccessed)";
            cmd.Parameters.AddWithValue("@hash", textHash);
            cmd.Parameters.AddWithValue("@fingerprint", modelFingerprint);
            cmd.Parameters.AddWithValue("@vector", vectorBytes);
            cmd.Parameters.AddWithValue("@dimensions", result.Vector.Length);
            cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@lastAccessed", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(token);

            _logger?.LogDebug("已缓存 Embedding: {Hash} / {Fingerprint}",
                textHash[..Math.Min(8, textHash.Length)], modelFingerprint);
        }, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmbeddingResult?>> GetBatchAsync(
        IReadOnlyList<EmbeddingCacheKey> keys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var results = new List<EmbeddingResult?>(keys.Count);
        foreach (var key in keys)
            results.Add(await GetAsync(key.TextHash, key.ModelFingerprint, ct));
        return results;
    }

    /// <inheritdoc />
    public Task<int> ClearExpiredAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToString("O");
            using var cmd = Connection.CreateCommand();
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

            using var countCmd = Connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM embedding_cache";
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(token));

            using var sizeCmd = Connection.CreateCommand();
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
