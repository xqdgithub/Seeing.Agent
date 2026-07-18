using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Index;

/// <summary>
/// 向量索引 - 使用 SQLite 存储向量，支持 Cosine Similarity 检索
/// </summary>
public class VectorIndex : IVectorIndex
{
    private readonly SqliteConnection _connection;
    private readonly SqliteConnectionGate _gate;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<VectorIndex>? _logger;
    private bool _initialized;

    public VectorIndex(
        SqliteConnection connection,
        SqliteConnectionGate gate,
        IEmbeddingService embeddingService,
        ILogger<VectorIndex>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _logger = logger;
    }

    /// <summary>须在已持有 <see cref="_gate"/> 时调用。</summary>
    private async Task EnsureInitializedCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS vector_index (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                content_hash TEXT NOT NULL,
                vector BLOB NOT NULL,
                created_at TEXT NOT NULL
            )";
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_vector_path ON vector_index(path)";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("向量索引表初始化完成，维度: {Dimensions}", _embeddingService.Dimensions);
    }

    /// <inheritdoc />
    public async Task IndexAsync(FileNode node, CancellationToken ct = default)
    {
        // Embedding 可能较慢，放在连接锁外，避免阻塞其他 SQLite 操作
        var embedding = await _embeddingService.EmbedAsync(node.Content, ct);
        var vectorBytes = SerializeVector(embedding.Vector);
        var contentHash = ComputeHash(node.Content);

        await _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using (var deleteCmd = _connection.CreateCommand())
            {
                deleteCmd.CommandText = "DELETE FROM vector_index WHERE path = @path";
                deleteCmd.Parameters.AddWithValue("@path", node.Path);
                await deleteCmd.ExecuteNonQueryAsync(token);
            }

            using var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO vector_index (path, content_hash, vector, created_at)
                VALUES (@path, @contentHash, @vector, @createdAt)";
            insertCmd.Parameters.AddWithValue("@path", node.Path);
            insertCmd.Parameters.AddWithValue("@contentHash", contentHash);
            insertCmd.Parameters.AddWithValue("@vector", vectorBytes);
            insertCmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await insertCmd.ExecuteNonQueryAsync(token);
        }, ct);

        _logger?.LogDebug("已索引向量: {Path}, 维度: {Dimensions}", node.Path, embedding.Vector.Length);
    }

    /// <inheritdoc />
    public async Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default)
    {
        var nodeList = nodes.ToList();
        var texts = nodeList.Select(n => n.Content).ToList();
        var embeddings = await _embeddingService.EmbedBatchAsync(texts, ct);

        await _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var transaction = _connection.BeginTransaction();
            try
            {
                for (var i = 0; i < nodeList.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var node = nodeList[i];
                    var embedding = embeddings[i];

                    using (var deleteCmd = _connection.CreateCommand())
                    {
                        deleteCmd.Transaction = transaction;
                        deleteCmd.CommandText = "DELETE FROM vector_index WHERE path = @path";
                        deleteCmd.Parameters.AddWithValue("@path", node.Path);
                        await deleteCmd.ExecuteNonQueryAsync(token);
                    }

                    var vectorBytes = SerializeVector(embedding.Vector);
                    var contentHash = ComputeHash(node.Content);

                    using (var insertCmd = _connection.CreateCommand())
                    {
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = @"
                            INSERT INTO vector_index (path, content_hash, vector, created_at)
                            VALUES (@path, @contentHash, @vector, @createdAt)";
                        insertCmd.Parameters.AddWithValue("@path", node.Path);
                        insertCmd.Parameters.AddWithValue("@contentHash", contentHash);
                        insertCmd.Parameters.AddWithValue("@vector", vectorBytes);
                        insertCmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
                        await insertCmd.ExecuteNonQueryAsync(token);
                    }
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, ct);

        _logger?.LogDebug("批量索引向量完成: {Count} 个文档", nodeList.Count);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string path, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vector_index WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", path);
            var rowsAffected = await cmd.ExecuteNonQueryAsync(token);
            if (rowsAffected > 0)
                _logger?.LogDebug("已删除向量索引: {Path}", path);
        }, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<VectorSearchResult>();

        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);
        var queryVector = queryEmbedding.Vector;

        var results = await _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            var list = new List<(string Path, double Similarity)>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, vector FROM vector_index";
            using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var path = reader.GetString(0);
                var vectorBytes = (byte[])reader.GetValue(1);
                var vector = DeserializeVector(vectorBytes);
                list.Add((path, CosineSimilarity(queryVector, vector)));
            }

            return list;
        }, ct);

        var topResults = results
            .OrderByDescending(r => r.Similarity)
            .Take(limit)
            .Select(r => new VectorSearchResult(r.Path, r.Similarity))
            .ToList();

        _logger?.LogDebug("向量搜索 '{Query}' 找到 {Count} 个结果", query, topResults.Count);
        return topResults;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchWithFilterAsync(
        string query,
        Func<string, bool> pathFilter,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<VectorSearchResult>();

        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);
        var queryVector = queryEmbedding.Vector;

        var results = await _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            var list = new List<(string Path, double Similarity)>();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, vector FROM vector_index";
            using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var path = reader.GetString(0);
                if (!pathFilter(path))
                    continue;

                var vectorBytes = (byte[])reader.GetValue(1);
                var vector = DeserializeVector(vectorBytes);
                list.Add((path, CosineSimilarity(queryVector, vector)));
            }

            return list;
        }, ct);

        var topResults = results
            .OrderByDescending(r => r.Similarity)
            .Take(limit)
            .Select(r => new VectorSearchResult(r.Path, r.Similarity))
            .ToList();

        _logger?.LogDebug("向量搜索（带过滤） '{Query}' 找到 {Count} 个结果", query, topResults.Count);
        return topResults;
    }

    /// <inheritdoc />
    public Task<int> GetDocumentCountAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM vector_index";
            var result = await cmd.ExecuteScalarAsync(token);
            return Convert.ToInt32(result);
        }, ct);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM vector_index";
            await cmd.ExecuteNonQueryAsync(token);
            _logger?.LogDebug("已清空向量索引");
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

    private static double CosineSimilarity(float[] vector1, float[] vector2)
    {
        if (vector1.Length != vector2.Length)
            throw new ArgumentException("Vectors must have the same dimension");

        double dotProduct = 0, magnitude1 = 0, magnitude2 = 0;
        for (var i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }

        magnitude1 = Math.Sqrt(magnitude1);
        magnitude2 = Math.Sqrt(magnitude2);
        if (magnitude1 == 0 || magnitude2 == 0)
            return 0;

        return dotProduct / (magnitude1 * magnitude2);
    }

    private static string ComputeHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes)[..16];
    }
}

/// <summary>
/// 向量索引接口
/// </summary>
public interface IVectorIndex
{
    Task IndexAsync(FileNode node, CancellationToken ct = default);
    Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query, int limit = 10, CancellationToken ct = default);
    Task<IReadOnlyList<VectorSearchResult>> SearchWithFilterAsync(
        string query, Func<string, bool> pathFilter, int limit = 10, CancellationToken ct = default);
    Task<int> GetDocumentCountAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// 向量搜索结果
/// </summary>
public record VectorSearchResult(string Path, double Score);
