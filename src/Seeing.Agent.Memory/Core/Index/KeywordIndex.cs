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
    private readonly SqliteConnectionGate _gate;
    private readonly ILogger<KeywordIndex>? _logger;
    private bool _initialized;

    public KeywordIndex(
        SqliteConnection connection,
        SqliteConnectionGate gate,
        ILogger<KeywordIndex>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _logger = logger;
    }

    /// <summary>须在已持有 <see cref="_gate"/> 时调用。</summary>
    private async Task EnsureInitializedCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE VIRTUAL TABLE IF NOT EXISTS keyword_index USING fts5(
                path,
                title,
                content,
                tags,
                tokenize='porter unicode61'
            )";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("关键词索引表初始化完成");
    }

    /// <inheritdoc />
    public Task IndexAsync(FileNode node, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using (var deleteCmd = _connection.CreateCommand())
            {
                deleteCmd.CommandText = "DELETE FROM keyword_index WHERE path = @path";
                deleteCmd.Parameters.AddWithValue("@path", node.Path);
                await deleteCmd.ExecuteNonQueryAsync(token);
            }

            using var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO keyword_index (path, title, content, tags)
                VALUES (@path, @title, @content, @tags)";
            insertCmd.Parameters.AddWithValue("@path", node.Path);
            insertCmd.Parameters.AddWithValue("@title", node.Metadata.Title ?? "");
            insertCmd.Parameters.AddWithValue("@content", node.Content);
            insertCmd.Parameters.AddWithValue("@tags", string.Join(" ", node.Metadata.Tags));
            await insertCmd.ExecuteNonQueryAsync(token);

            _logger?.LogDebug("已索引文档: {Path}", node.Path);
        }, ct);

    /// <inheritdoc />
    public Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var node in nodes)
                {
                    token.ThrowIfCancellationRequested();

                    using (var deleteCmd = _connection.CreateCommand())
                    {
                        deleteCmd.Transaction = transaction;
                        deleteCmd.CommandText = "DELETE FROM keyword_index WHERE path = @path";
                        deleteCmd.Parameters.AddWithValue("@path", node.Path);
                        await deleteCmd.ExecuteNonQueryAsync(token);
                    }

                    using (var insertCmd = _connection.CreateCommand())
                    {
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = @"
                            INSERT INTO keyword_index (path, title, content, tags)
                            VALUES (@path, @title, @content, @tags)";
                        insertCmd.Parameters.AddWithValue("@path", node.Path);
                        insertCmd.Parameters.AddWithValue("@title", node.Metadata.Title ?? "");
                        insertCmd.Parameters.AddWithValue("@content", node.Content);
                        insertCmd.Parameters.AddWithValue("@tags", string.Join(" ", node.Metadata.Tags));
                        await insertCmd.ExecuteNonQueryAsync(token);
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
        }, ct);

    /// <inheritdoc />
    public Task RemoveAsync(string path, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM keyword_index WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", path);
            var rowsAffected = await cmd.ExecuteNonQueryAsync(token);
            if (rowsAffected > 0)
                _logger?.LogDebug("已删除索引: {Path}", path);
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<KeywordSearchResult>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<KeywordSearchResult>>(Array.Empty<KeywordSearchResult>());

        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
            return Task.FromResult<IReadOnlyList<KeywordSearchResult>>(Array.Empty<KeywordSearchResult>());

        return _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

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

            using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var path = reader.GetString(0);
                var title = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var rawScore = reader.GetDouble(2);
                results.Add(new KeywordSearchResult(path, title, Math.Max(0, -rawScore)));
            }

            _logger?.LogDebug("搜索 '{Query}' 找到 {Count} 个结果", query, results.Count);
            return (IReadOnlyList<KeywordSearchResult>)results;
        }, ct);
    }

    /// <inheritdoc />
    public Task<int> GetDocumentCountAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM keyword_index";
            var result = await cmd.ExecuteScalarAsync(token);
            return Convert.ToInt32(result);
        }, ct);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM keyword_index";
            await cmd.ExecuteNonQueryAsync(token);
            _logger?.LogDebug("已清空关键词索引");
        }, ct);

    private static string BuildFtsQuery(string query)
    {
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
            return "";

        var escapedKeywords = keywords.Select(k =>
        {
            var hasNonAscii = k.Any(c => c > 127);
            return hasNonAscii ? $"\"{k}\"*" : $"\"{k}\"";
        });

        return string.Join(" AND ", escapedKeywords);
    }
}

/// <summary>
/// 关键词索引接口
/// </summary>
public interface IKeywordIndex
{
    Task IndexAsync(FileNode node, CancellationToken ct = default);
    Task IndexBatchAsync(IEnumerable<FileNode> nodes, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<KeywordSearchResult>> SearchAsync(
        string query, int limit = 10, CancellationToken ct = default);
    Task<int> GetDocumentCountAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// 关键词搜索结果
/// </summary>
public record KeywordSearchResult(string Path, string Title, double Score);
