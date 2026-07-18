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
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<VectorIndex>? _logger;
    private bool _initialized;

    /// <summary>
    /// 创建 VectorIndex 实例
    /// </summary>
    /// <param name="connection">SQLite 连接</param>
    /// <param name="embeddingService">Embedding 服务</param>
    /// <param name="logger">日志记录器</param>
    public VectorIndex(
        SqliteConnection connection,
        IEmbeddingService embeddingService,
        ILogger<VectorIndex>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _logger = logger;
    }

    /// <summary>
    /// 确保索引表已初始化
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        // 创建向量存储表
        // 使用 BLOB 存储向量数据（float[] 序列化为 byte[]）
        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS vector_index (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                content_hash TEXT NOT NULL,
                vector BLOB NOT NULL,
                created_at TEXT NOT NULL
            )";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = createTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        // 创建索引以加速路径查找
        var createIndexSql = "CREATE INDEX IF NOT EXISTS idx_vector_path ON vector_index(path)";
        cmd.CommandText = createIndexSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("向量索引表初始化完成，维度: {Dimensions}", _embeddingService.Dimensions);
    }

    /// <inheritdoc />
    public async Task IndexAsync(FileNode node, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // 获取文本的 embedding
        var embedding = await _embeddingService.EmbedAsync(node.Content, ct);

        // 先删除旧记录
        await RemoveAsync(node.Path, ct);

        // 序列化向量为字节数组
        var vectorBytes = SerializeVector(embedding.Vector);
        var contentHash = ComputeHash(node.Content);

        // 插入新记录
        var insertSql = @"
            INSERT INTO vector_index (path, content_hash, vector, created_at)
            VALUES (@path, @contentHash, @vector, @createdAt)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@path", node.Path);
        cmd.Parameters.AddWithValue("@contentHash", contentHash);
        cmd.Parameters.AddWithValue("@vector", vectorBytes);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已索引向量: {Path}, 维度: {Dimensions}", node.Path, embedding.Vector.Length);
    }

    /// <inheritdoc />
    public async Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var nodeList = nodes.ToList();
        var texts = nodeList.Select(n => n.Content).ToList();

        // 批量获取 embedding
        var embeddings = await _embeddingService.EmbedBatchAsync(texts, ct);

        using var transaction = _connection.BeginTransaction();

        try
        {
            for (var i = 0; i < nodeList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var node = nodeList[i];
                var embedding = embeddings[i];

                // 删除旧记录
                using (var deleteCmd = _connection.CreateCommand())
                {
                    deleteCmd.CommandText = "DELETE FROM vector_index WHERE path = @path";
                    deleteCmd.Parameters.AddWithValue("@path", node.Path);
                    await deleteCmd.ExecuteNonQueryAsync(ct);
                }

                // 插入新记录
                var vectorBytes = SerializeVector(embedding.Vector);
                var contentHash = ComputeHash(node.Content);

                using (var insertCmd = _connection.CreateCommand())
                {
                    insertCmd.CommandText = @"
                        INSERT INTO vector_index (path, content_hash, vector, created_at)
                        VALUES (@path, @contentHash, @vector, @createdAt)";
                    insertCmd.Parameters.AddWithValue("@path", node.Path);
                    insertCmd.Parameters.AddWithValue("@contentHash", contentHash);
                    insertCmd.Parameters.AddWithValue("@vector", vectorBytes);
                    insertCmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
                    await insertCmd.ExecuteNonQueryAsync(ct);
                }
            }

            transaction.Commit();
            _logger?.LogDebug("批量索引向量完成: {Count} 个文档", nodeList.Count);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var deleteSql = "DELETE FROM vector_index WHERE path = @path";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = deleteSql;
        cmd.Parameters.AddWithValue("@path", path);

        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        if (rowsAffected > 0)
        {
            _logger?.LogDebug("已删除向量索引: {Path}", path);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<VectorSearchResult>();
        }

        // 获取查询的 embedding
        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);
        var queryVector = queryEmbedding.Vector;

        // 加载所有向量并计算相似度
        // 注意：对于大规模数据，应该使用专门的向量数据库
        var results = new List<(string Path, float[] Vector, double Similarity)>();

        var selectSql = "SELECT path, vector FROM vector_index";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = selectSql;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var path = reader.GetString(0);
                var vectorBytes = (byte[])reader.GetValue(1);
                var vector = DeserializeVector(vectorBytes);

                var similarity = CosineSimilarity(queryVector, vector);
                results.Add((path, vector, similarity));
            }
        }

        // 按相似度排序并取前 N 个结果
        var topResults = results
            .OrderByDescending(r => r.Similarity)
            .Take(limit)
            .Select(r => new VectorSearchResult(
                Path: r.Path,
                Score: r.Similarity
            ))
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
        await EnsureInitializedAsync(ct);

        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<VectorSearchResult>();
        }

        // 获取查询的 embedding
        var queryEmbedding = await _embeddingService.EmbedAsync(query, ct);
        var queryVector = queryEmbedding.Vector;

        // 加载所有向量并计算相似度
        var results = new List<(string Path, double Similarity)>();

        var selectSql = "SELECT path, vector FROM vector_index";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = selectSql;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var path = reader.GetString(0);
                
                // 应用过滤条件
                if (!pathFilter(path))
                {
                    continue;
                }

                var vectorBytes = (byte[])reader.GetValue(1);
                var vector = DeserializeVector(vectorBytes);

                var similarity = CosineSimilarity(queryVector, vector);
                results.Add((path, similarity));
            }
        }

        // 按相似度排序并取前 N 个结果
        var topResults = results
            .OrderByDescending(r => r.Similarity)
            .Take(limit)
            .Select(r => new VectorSearchResult(
                Path: r.Path,
                Score: r.Similarity
            ))
            .ToList();

        _logger?.LogDebug("向量搜索（带过滤） '{Query}' 找到 {Count} 个结果", query, topResults.Count);
        return topResults;
    }

    /// <inheritdoc />
    public async Task<int> GetDocumentCountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var countSql = "SELECT COUNT(*) FROM vector_index";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = countSql;

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var deleteAllSql = "DELETE FROM vector_index";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = deleteAllSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug("已清空向量索引");
    }

    /// <summary>
    /// 序列化向量为字节数组
    /// </summary>
    private static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// 从字节数组反序列化向量
    /// </summary>
    private static float[] DeserializeVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>
    /// 计算 Cosine Similarity
    /// </summary>
    private static double CosineSimilarity(float[] vector1, float[] vector2)
    {
        if (vector1.Length != vector2.Length)
        {
            throw new ArgumentException("Vectors must have the same dimension");
        }

        double dotProduct = 0;
        double magnitude1 = 0;
        double magnitude2 = 0;

        for (var i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }

        magnitude1 = Math.Sqrt(magnitude1);
        magnitude2 = Math.Sqrt(magnitude2);

        if (magnitude1 == 0 || magnitude2 == 0)
        {
            return 0;
        }

        return dotProduct / (magnitude1 * magnitude2);
    }

    /// <summary>
    /// 计算内容的哈希值
    /// </summary>
    private static string ComputeHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes)[..16]; // 取前 16 个字符
    }
}

/// <summary>
/// 向量索引接口
/// </summary>
public interface IVectorIndex
{
    /// <summary>索引单个文档</summary>
    Task IndexAsync(FileNode node, CancellationToken ct = default);

    /// <summary>批量索引文档</summary>
    Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);

    /// <summary>移除索引</summary>
    Task RemoveAsync(string path, CancellationToken ct = default);

    /// <summary>搜索相似文档</summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>带路径过滤的搜索</summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchWithFilterAsync(
        string query,
        Func<string, bool> pathFilter,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>获取文档数量</summary>
    Task<int> GetDocumentCountAsync(CancellationToken ct = default);

    /// <summary>清空索引</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// 向量搜索结果
/// </summary>
public record VectorSearchResult(
    string Path,         // 文档路径
    double Score         // 相似度分数 (0-1, 越高越相似)
);
