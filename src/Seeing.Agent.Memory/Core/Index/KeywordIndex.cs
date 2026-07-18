using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Index;

/// <summary>
/// 关键词索引 - 使用 SQLite FTS5 实现 BM25 检索
/// </summary>
public class KeywordIndex : IKeywordIndex
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<KeywordIndex>? _logger;
    private bool _initialized;

    /// <summary>
    /// 创建 KeywordIndex 实例
    /// </summary>
    /// <param name="connection">SQLite 连接</param>
    /// <param name="logger">日志记录器</param>
    public KeywordIndex(SqliteConnection connection, ILogger<KeywordIndex>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger;
    }

    /// <summary>
    /// 确保索引表已初始化
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        // 创建 FTS5 虚拟表用于关键词搜索
        // tokenize='porter unicode61' 支持英文词干提取和 Unicode 字符
        var createTableSql = @"
            CREATE VIRTUAL TABLE IF NOT EXISTS keyword_index USING fts5(
                path,
                title,
                content,
                tags,
                tokenize='porter unicode61'
            )";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = createTableSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("关键词索引表初始化完成");
    }

    /// <inheritdoc />
    public async Task IndexAsync(FileNode node, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // 先删除旧记录
        await RemoveAsync(node.Path, ct);

        // 插入新记录
        var insertSql = @"
            INSERT INTO keyword_index (path, title, content, tags)
            VALUES (@path, @title, @content, @tags)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@path", node.Path);
        cmd.Parameters.AddWithValue("@title", node.Metadata.Title ?? "");
        cmd.Parameters.AddWithValue("@content", node.Content);
        cmd.Parameters.AddWithValue("@tags", string.Join(" ", node.Metadata.Tags));

        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已索引文档: {Path}", node.Path);
    }

    /// <inheritdoc />
    public async Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        using var transaction = _connection.BeginTransaction();

        try
        {
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();

                // 删除旧记录
                using (var deleteCmd = _connection.CreateCommand())
                {
                    deleteCmd.CommandText = "DELETE FROM keyword_index WHERE path = @path";
                    deleteCmd.Parameters.AddWithValue("@path", node.Path);
                    await deleteCmd.ExecuteNonQueryAsync(ct);
                }

                // 插入新记录
                using (var insertCmd = _connection.CreateCommand())
                {
                    insertCmd.CommandText = @"
                        INSERT INTO keyword_index (path, title, content, tags)
                        VALUES (@path, @title, @content, @tags)";
                    insertCmd.Parameters.AddWithValue("@path", node.Path);
                    insertCmd.Parameters.AddWithValue("@title", node.Metadata.Title ?? "");
                    insertCmd.Parameters.AddWithValue("@content", node.Content);
                    insertCmd.Parameters.AddWithValue("@tags", string.Join(" ", node.Metadata.Tags));
                    await insertCmd.ExecuteNonQueryAsync(ct);
                }
            }

            transaction.Commit();
            _logger?.LogDebug("批量索引完成");
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

        var deleteSql = "DELETE FROM keyword_index WHERE path = @path";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = deleteSql;
        cmd.Parameters.AddWithValue("@path", path);

        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        if (rowsAffected > 0)
        {
            _logger?.LogDebug("已删除索引: {Path}", path);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeywordSearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<KeywordSearchResult>();
        }

        // 转义 FTS5 特殊字符并构建 AND 查询
        var ftsQuery = BuildFtsQuery(query);

        // 使用 BM25 排序，分数越低越好（BM25 返回负值用于排序）
        var searchSql = @"
            SELECT path, title, bm25(keyword_index) as score
            FROM keyword_index
            WHERE keyword_index MATCH @query
            ORDER BY bm25(keyword_index)
            LIMIT @limit";

        var results = new List<KeywordSearchResult>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = searchSql;
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var path = reader.GetString(0);
            var title = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var rawScore = reader.GetDouble(2);

            // BM25 返回负值，转换为正分数用于显示
            var normalizedScore = -rawScore;

            results.Add(new KeywordSearchResult(
                Path: path,
                Title: title,
                Score: Math.Max(0, normalizedScore)
            ));
        }

        _logger?.LogDebug("搜索 '{Query}' 找到 {Count} 个结果", query, results.Count);
        return results;
    }

    /// <inheritdoc />
    public async Task<int> GetDocumentCountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var countSql = "SELECT COUNT(*) FROM keyword_index";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = countSql;

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var deleteAllSql = "DELETE FROM keyword_index";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = deleteAllSql;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug("已清空关键词索引");
    }

    /// <summary>
    /// 构建 FTS5 查询字符串
    /// 将用户输入转换为安全的 FTS5 AND 查询
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        // FTS5 特殊字符: " ' { } ( ) : * [ ] ^ ~ \
        // 简化处理：移除特殊字符，将空格分隔的关键词用 AND 连接

        var keywords = query
            .Replace("\"", "")
            .Replace("'", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace(":", "")
            .Replace("*", "")
            .Replace("[", "")
            .Replace("]", "")
            .Replace("^", "")
            .Replace("~", "")
            .Replace("\\", "")
            .Replace("{", "")
            .Replace("}", "")
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        if (keywords.Length == 0)
        {
            return "";
        }

        // 对每个关键词添加引号以进行精确匹配
        // 对于非 ASCII 字符（如中文），添加前缀通配符支持
        var escapedKeywords = keywords.Select(k =>
        {
            // 检查是否包含非 ASCII 字符（如中文）
            var hasNonAscii = k.Any(c => c > 127);
            if (hasNonAscii)
            {
                // 使用前缀匹配以支持中文部分匹配
                return $"\"{k}\"*";
            }
            return $"\"{k}\"";
        });

        // 使用 AND 连接所有关键词
        return string.Join(" AND ", escapedKeywords);
    }
}

/// <summary>
/// 关键词索引接口
/// </summary>
public interface IKeywordIndex
{
    /// <summary>索引单个文档</summary>
    Task IndexAsync(FileNode node, CancellationToken ct = default);

    /// <summary>批量索引文档</summary>
    Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);

    /// <summary>移除索引</summary>
    Task RemoveAsync(string path, CancellationToken ct = default);

    /// <summary>搜索关键词</summary>
    Task<IReadOnlyList<KeywordSearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>获取文档数量</summary>
    Task<int> GetDocumentCountAsync(CancellationToken ct = default);

    /// <summary>清空索引</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// 关键词搜索结果
/// </summary>
public record KeywordSearchResult(
    string Path,         // 文档路径
    string Title,        // 文档标题
    double Score         // BM25 分数 (越高越好)
);
