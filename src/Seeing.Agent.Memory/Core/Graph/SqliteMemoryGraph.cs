using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Graph;

/// <summary>
/// SQLite 知识图谱实现 - 基于 Wikilink 的文件关联图
/// </summary>
public class SqliteMemoryGraph : IMemoryGraph, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteMemoryGraph>? _logger;
    private bool _initialized;

    public SqliteMemoryGraph(SqliteConnection connection, ILogger<SqliteMemoryGraph>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger;
    }

    /// <summary>
    /// 确保图谱表已初始化
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var createNodesSql = @"
            CREATE TABLE IF NOT EXISTS graph_nodes (
                path TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                degree INTEGER NOT NULL DEFAULT 0
            )";

        var createEdgesSql = @"
            CREATE TABLE IF NOT EXISTS graph_edges (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                target_path TEXT NOT NULL,
                type TEXT NOT NULL,
                weight REAL NOT NULL DEFAULT 1.0,
                context TEXT,
                UNIQUE(source_path, target_path, type)
            )";

        var createIndexSql = @"
            CREATE INDEX IF NOT EXISTS idx_edges_source ON graph_edges(source_path);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON graph_edges(target_path)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{createNodesSql};{createEdgesSql};{createIndexSql}";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("知识图谱表初始化完成");
    }

    /// <inheritdoc />
    public async Task AddNodeAsync(string path, string title, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var insertSql = @"
            INSERT OR REPLACE INTO graph_nodes (path, title, degree)
            VALUES (@path, @title, 
                    (SELECT COUNT(*) FROM graph_edges 
                     WHERE source_path = @path OR target_path = @path))";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@title", title);
        await cmd.ExecuteNonQueryAsync(ct);

        _logger?.LogDebug("已添加图节点: {Path}", path);
    }

    /// <inheritdoc />
    public async Task AddEdgeAsync(string sourcePath, string targetPath, EdgeType type,
        double weight = 1.0, string? context = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // 确保两个节点都存在
        await EnsureNodeExistsAsync(sourcePath, ct);
        await EnsureNodeExistsAsync(targetPath, ct);

        // 插入边
        var insertSql = @"
            INSERT OR REPLACE INTO graph_edges (source_path, target_path, type, weight, context)
            VALUES (@source, @target, @type, @weight, @context)";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = insertSql;
        cmd.Parameters.AddWithValue("@source", sourcePath);
        cmd.Parameters.AddWithValue("@target", targetPath);
        cmd.Parameters.AddWithValue("@type", type.ToString());
        cmd.Parameters.AddWithValue("@weight", weight);
        cmd.Parameters.AddWithValue("@context", (object?)context ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        // 更新节点度数
        await UpdateNodeDegreeAsync(sourcePath, ct);
        await UpdateNodeDegreeAsync(targetPath, ct);

        _logger?.LogDebug("已添加图边: {Source} -> {Target} ({Type})", sourcePath, targetPath, type);
    }

    /// <inheritdoc />
    public async Task RemoveNodeAsync(string path, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // 删除所有相关边
        var deleteEdgesSql = @"
            DELETE FROM graph_edges 
            WHERE source_path = @path OR target_path = @path";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = deleteEdgesSql;
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 删除节点
        var deleteNodeSql = "DELETE FROM graph_nodes WHERE path = @path";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = deleteNodeSql;
            cmd.Parameters.AddWithValue("@path", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger?.LogDebug("已删除图节点: {Path}", path);
    }

    /// <inheritdoc />
    public async Task RemoveEdgeAsync(string sourcePath, string targetPath, EdgeType type, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var deleteSql = @"
            DELETE FROM graph_edges 
            WHERE source_path = @source AND target_path = @target AND type = @type";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = deleteSql;
        cmd.Parameters.AddWithValue("@source", sourcePath);
        cmd.Parameters.AddWithValue("@target", targetPath);
        cmd.Parameters.AddWithValue("@type", type.ToString());
        await cmd.ExecuteNonQueryAsync(ct);

        // 更新节点度数
        await UpdateNodeDegreeAsync(sourcePath, ct);
        await UpdateNodeDegreeAsync(targetPath, ct);

        _logger?.LogDebug("已删除图边: {Source} -> {Target} ({Type})", sourcePath, targetPath, type);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(string path, int depth = 1, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var visited = new HashSet<string> { path };
        var result = new List<GraphNode>();
        var currentLevel = new List<string> { path };

        for (var d = 0; d < depth; d++)
        {
            var nextLevel = new List<string>();

            foreach (var nodePath in currentLevel)
            {
                // 获取出边邻居
                var outNeighbors = await GetOutNeighborsAsync(nodePath, ct);
                foreach (var neighbor in outNeighbors.Where(n => !visited.Contains(n)))
                {
                    visited.Add(neighbor);
                    nextLevel.Add(neighbor);
                    var node = await GetNodeAsync(neighbor, ct);
                    if (node != null)
                        result.Add(node);
                }

                // 获取入边邻居
                var inNeighbors = await GetInNeighborsAsync(nodePath, ct);
                foreach (var neighbor in inNeighbors.Where(n => !visited.Contains(n)))
                {
                    visited.Add(neighbor);
                    nextLevel.Add(neighbor);
                    var node = await GetNodeAsync(neighbor, ct);
                    if (node != null)
                        result.Add(node);
                }
            }

            currentLevel = nextLevel;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> FindPathAsync(string sourcePath, string targetPath,
        int maxDepth = 5, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // BFS 查找最短路径
        var queue = new Queue<List<string>>();
        var visited = new HashSet<string>();

        queue.Enqueue(new List<string> { sourcePath });
        visited.Add(sourcePath);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var current = path.Last();

            if (current == targetPath)
                return path;

            if (path.Count > maxDepth)
                continue;

            // 获取所有邻居
            var outNeighbors = await GetOutNeighborsAsync(current, ct);
            var inNeighbors = await GetInNeighborsAsync(current, ct);
            var allNeighbors = outNeighbors.Concat(inNeighbors).Distinct();

            foreach (var neighbor in allNeighbors.Where(n => !visited.Contains(n)))
            {
                visited.Add(neighbor);
                var newPath = new List<string>(path) { neighbor };
                queue.Enqueue(newPath);
            }
        }

        return Array.Empty<string>();
    }

    /// <inheritdoc />
    public async Task<GraphStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var nodeCountSql = "SELECT COUNT(*) FROM graph_nodes";
        var edgeCountSql = "SELECT COUNT(*) FROM graph_edges";
        var isolatedSql = @"
            SELECT COUNT(*) FROM graph_nodes 
            WHERE degree = 0";

        using var nodeCmd = _connection.CreateCommand();
        nodeCmd.CommandText = nodeCountSql;
        var nodeCount = Convert.ToInt32(await nodeCmd.ExecuteScalarAsync(ct));

        using var edgeCmd = _connection.CreateCommand();
        edgeCmd.CommandText = edgeCountSql;
        var edgeCount = Convert.ToInt32(await edgeCmd.ExecuteScalarAsync(ct));

        using var isolatedCmd = _connection.CreateCommand();
        isolatedCmd.CommandText = isolatedSql;
        var isolatedNodes = Convert.ToInt32(await isolatedCmd.ExecuteScalarAsync(ct));

        return new GraphStats(nodeCount, edgeCount, isolatedNodes);
    }

    /// <inheritdoc />
    public async Task<GraphQueryResult> QueryAsync(string? startPath = null, int depth = 1,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        List<GraphNode> nodes;
        List<GraphEdge> edges;

        if (startPath != null)
        {
            // 从指定节点开始查询
            var neighbors = await GetNeighborsAsync(startPath, depth, ct);
            nodes = new List<GraphNode>(neighbors);

            // 添加起始节点
            var startNode = await GetNodeAsync(startPath, ct);
            if (startNode != null)
                nodes.Insert(0, startNode);

            // 获取所有相关边
            edges = new List<GraphEdge>();
            foreach (var node in nodes)
            {
                var nodeEdges = await GetEdgesAsync(node.Path, ct);
                edges.AddRange(nodeEdges);
            }
        }
        else
        {
            // 获取整个图
            nodes = await GetAllNodesAsync(ct);
            edges = await GetAllEdgesAsync(ct);
        }

        return new GraphQueryResult(nodes.Distinct().ToList(), edges.Distinct().ToList());
    }

    /// <summary>
    /// 确保节点存在
    /// </summary>
    private async Task EnsureNodeExistsAsync(string path, CancellationToken ct)
    {
        var checkSql = "SELECT COUNT(*) FROM graph_nodes WHERE path = @path";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = checkSql;
        cmd.Parameters.AddWithValue("@path", path);
        var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;

        if (!exists)
        {
            await AddNodeAsync(path, Path.GetFileNameWithoutExtension(path), ct);
        }
    }

    /// <summary>
    /// 更新节点度数
    /// </summary>
    private async Task UpdateNodeDegreeAsync(string path, CancellationToken ct)
    {
        var updateSql = @"
            UPDATE graph_nodes 
            SET degree = (
                SELECT COUNT(*) FROM graph_edges 
                WHERE source_path = @path OR target_path = @path
            )
            WHERE path = @path";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = updateSql;
        cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 获取节点
    /// </summary>
    private async Task<GraphNode?> GetNodeAsync(string path, CancellationToken ct)
    {
        var selectSql = "SELECT path, title, degree FROM graph_nodes WHERE path = @path";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@path", path);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new GraphNode(
                Path: reader.GetString(0),
                Title: reader.GetString(1),
                Degree: reader.GetInt32(2)
            );
        }

        return null;
    }

    /// <summary>
    /// 获取所有节点
    /// </summary>
    private async Task<List<GraphNode>> GetAllNodesAsync(CancellationToken ct)
    {
        var selectSql = "SELECT path, title, degree FROM graph_nodes";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;

        var nodes = new List<GraphNode>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            nodes.Add(new GraphNode(
                Path: reader.GetString(0),
                Title: reader.GetString(1),
                Degree: reader.GetInt32(2)
            ));
        }

        return nodes;
    }

    /// <summary>
    /// 获取所有边
    /// </summary>
    private async Task<List<GraphEdge>> GetAllEdgesAsync(CancellationToken ct)
    {
        var selectSql = "SELECT source_path, target_path, type, weight, context FROM graph_edges";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;

        var edges = new List<GraphEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new GraphEdge(
                SourcePath: reader.GetString(0),
                TargetPath: reader.GetString(1),
                Type: Enum.Parse<EdgeType>(reader.GetString(2)),
                Weight: reader.GetDouble(3),
                Context: reader.IsDBNull(4) ? null : reader.GetString(4)
            ));
        }

        return edges;
    }

    /// <summary>
    /// 获取节点的所有边
    /// </summary>
    private async Task<List<GraphEdge>> GetEdgesAsync(string path, CancellationToken ct)
    {
        var selectSql = @"
            SELECT source_path, target_path, type, weight, context 
            FROM graph_edges 
            WHERE source_path = @path OR target_path = @path";

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@path", path);

        var edges = new List<GraphEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new GraphEdge(
                SourcePath: reader.GetString(0),
                TargetPath: reader.GetString(1),
                Type: Enum.Parse<EdgeType>(reader.GetString(2)),
                Weight: reader.GetDouble(3),
                Context: reader.IsDBNull(4) ? null : reader.GetString(4)
            ));
        }

        return edges;
    }

    /// <summary>
    /// 获取出边邻居
    /// </summary>
    private async Task<List<string>> GetOutNeighborsAsync(string path, CancellationToken ct)
    {
        var selectSql = "SELECT target_path FROM graph_edges WHERE source_path = @path";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@path", path);

        var neighbors = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            neighbors.Add(reader.GetString(0));
        }

        return neighbors;
    }

    /// <summary>
    /// 获取入边邻居
    /// </summary>
    private async Task<List<string>> GetInNeighborsAsync(string path, CancellationToken ct)
    {
        var selectSql = "SELECT source_path FROM graph_edges WHERE target_path = @path";
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.Parameters.AddWithValue("@path", path);

        var neighbors = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            neighbors.Add(reader.GetString(0));
        }

        return neighbors;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
