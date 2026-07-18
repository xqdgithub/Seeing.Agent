using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Graph;

/// <summary>
/// SQLite 知识图谱实现 - 基于 Wikilink 的文件关联图。
/// 共享连接须经 <see cref="SqliteConnectionGate"/> 串行访问。
/// </summary>
public class SqliteMemoryGraph : IMemoryGraph
{
    private readonly SqliteConnection _connection;
    private readonly SqliteConnectionGate _gate;
    private readonly ILogger<SqliteMemoryGraph>? _logger;
    private bool _initialized;

    public SqliteMemoryGraph(SqliteConnection connection, ILogger<SqliteMemoryGraph>? logger = null)
        : this(connection, new SqliteConnectionGate(), logger)
    {
    }

    public SqliteMemoryGraph(
        SqliteConnection connection,
        SqliteConnectionGate gate,
        ILogger<SqliteMemoryGraph>? logger = null)
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
            CREATE TABLE IF NOT EXISTS graph_nodes (
                path TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                degree INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS graph_edges (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                target_path TEXT NOT NULL,
                type TEXT NOT NULL,
                weight REAL NOT NULL DEFAULT 1.0,
                context TEXT,
                UNIQUE(source_path, target_path, type)
            );
            CREATE INDEX IF NOT EXISTS idx_edges_source ON graph_edges(source_path);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON graph_edges(target_path)";
        await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
        _logger?.LogDebug("知识图谱表初始化完成");
    }

    /// <inheritdoc />
    public Task AddNodeAsync(string path, string title, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            await AddNodeCoreAsync(path, title, token);
        }, ct);

    private async Task AddNodeCoreAsync(string path, string title, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO graph_nodes (path, title, degree)
            VALUES (@path, @title,
                    (SELECT COUNT(*) FROM graph_edges
                     WHERE source_path = @path OR target_path = @path))";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@title", title);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger?.LogDebug("已添加图节点: {Path}", path);
    }

    /// <inheritdoc />
    public Task AddEdgeAsync(string sourcePath, string targetPath, EdgeType type,
        double weight = 1.0, string? context = null, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            await EnsureNodeExistsCoreAsync(sourcePath, token);
            await EnsureNodeExistsCoreAsync(targetPath, token);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO graph_edges (source_path, target_path, type, weight, context)
                VALUES (@source, @target, @type, @weight, @context)";
            cmd.Parameters.AddWithValue("@source", sourcePath);
            cmd.Parameters.AddWithValue("@target", targetPath);
            cmd.Parameters.AddWithValue("@type", type.ToString());
            cmd.Parameters.AddWithValue("@weight", weight);
            cmd.Parameters.AddWithValue("@context", (object?)context ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(token);

            await UpdateNodeDegreeCoreAsync(sourcePath, token);
            await UpdateNodeDegreeCoreAsync(targetPath, token);
            _logger?.LogDebug("已添加图边: {Source} -> {Target} ({Type})", sourcePath, targetPath, type);
        }, ct);

    /// <inheritdoc />
    public Task RemoveNodeAsync(string path, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var neighbors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in await GetOutNeighborsCoreAsync(path, token))
                neighbors.Add(n);
            foreach (var n in await GetInNeighborsCoreAsync(path, token))
                neighbors.Add(n);

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    DELETE FROM graph_edges
                    WHERE source_path = @path OR target_path = @path";
                cmd.Parameters.AddWithValue("@path", path);
                await cmd.ExecuteNonQueryAsync(token);
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM graph_nodes WHERE path = @path";
                cmd.Parameters.AddWithValue("@path", path);
                await cmd.ExecuteNonQueryAsync(token);
            }

            foreach (var neighbor in neighbors)
                await UpdateNodeDegreeCoreAsync(neighbor, token);

            _logger?.LogDebug("已删除图节点: {Path}", path);
        }, ct);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM graph_edges; DELETE FROM graph_nodes;";
            await cmd.ExecuteNonQueryAsync(token);
            _logger?.LogInformation("已清空知识图谱");
        }, ct);

    /// <inheritdoc />
    public Task RemoveEdgeAsync(string sourcePath, string targetPath, EdgeType type, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM graph_edges
                WHERE source_path = @source AND target_path = @target AND type = @type";
            cmd.Parameters.AddWithValue("@source", sourcePath);
            cmd.Parameters.AddWithValue("@target", targetPath);
            cmd.Parameters.AddWithValue("@type", type.ToString());
            await cmd.ExecuteNonQueryAsync(token);

            await UpdateNodeDegreeCoreAsync(sourcePath, token);
            await UpdateNodeDegreeCoreAsync(targetPath, token);
            _logger?.LogDebug("已删除图边: {Source} -> {Target} ({Type})", sourcePath, targetPath, type);
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphNode>> GetNeighborsAsync(string path, int depth = 1, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);
            return await GetNeighborsCoreAsync(path, depth, token);
        }, ct);

    private async Task<IReadOnlyList<GraphNode>> GetNeighborsCoreAsync(string path, int depth, CancellationToken ct)
    {
        var visited = new HashSet<string> { path };
        var result = new List<GraphNode>();
        var currentLevel = new List<string> { path };

        for (var d = 0; d < depth; d++)
        {
            var nextLevel = new List<string>();
            foreach (var nodePath in currentLevel)
            {
                foreach (var neighbor in (await GetOutNeighborsCoreAsync(nodePath, ct)).Where(n => !visited.Contains(n)))
                {
                    visited.Add(neighbor);
                    nextLevel.Add(neighbor);
                    var node = await GetNodeCoreAsync(neighbor, ct);
                    if (node != null)
                        result.Add(node);
                }

                foreach (var neighbor in (await GetInNeighborsCoreAsync(nodePath, ct)).Where(n => !visited.Contains(n)))
                {
                    visited.Add(neighbor);
                    nextLevel.Add(neighbor);
                    var node = await GetNodeCoreAsync(neighbor, ct);
                    if (node != null)
                        result.Add(node);
                }
            }

            currentLevel = nextLevel;
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindPathAsync(string sourcePath, string targetPath,
        int maxDepth = 5, CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            var queue = new Queue<List<string>>();
            var visited = new HashSet<string> { sourcePath };
            queue.Enqueue(new List<string> { sourcePath });

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var current = path.Last();
                if (current == targetPath)
                    return (IReadOnlyList<string>)path;
                if (path.Count > maxDepth)
                    continue;

                var allNeighbors = (await GetOutNeighborsCoreAsync(current, token))
                    .Concat(await GetInNeighborsCoreAsync(current, token))
                    .Distinct();

                foreach (var neighbor in allNeighbors.Where(n => !visited.Contains(n)))
                {
                    visited.Add(neighbor);
                    queue.Enqueue(new List<string>(path) { neighbor });
                }
            }

            return Array.Empty<string>();
        }, ct);

    /// <inheritdoc />
    public Task<GraphStats> GetStatsAsync(CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            using var nodeCmd = _connection.CreateCommand();
            nodeCmd.CommandText = "SELECT COUNT(*) FROM graph_nodes";
            var nodeCount = Convert.ToInt32(await nodeCmd.ExecuteScalarAsync(token));

            using var edgeCmd = _connection.CreateCommand();
            edgeCmd.CommandText = "SELECT COUNT(*) FROM graph_edges";
            var edgeCount = Convert.ToInt32(await edgeCmd.ExecuteScalarAsync(token));

            using var isolatedCmd = _connection.CreateCommand();
            isolatedCmd.CommandText = "SELECT COUNT(*) FROM graph_nodes WHERE degree = 0";
            var isolatedNodes = Convert.ToInt32(await isolatedCmd.ExecuteScalarAsync(token));

            return new GraphStats(nodeCount, edgeCount, isolatedNodes);
        }, ct);

    /// <inheritdoc />
    public Task<GraphQueryResult> QueryAsync(string? startPath = null, int depth = 1,
        CancellationToken ct = default) =>
        _gate.RunAsync(async token =>
        {
            await EnsureInitializedCoreAsync(token);

            List<GraphNode> nodes;
            List<GraphEdge> edges;

            if (startPath != null)
            {
                var neighbors = await GetNeighborsCoreAsync(startPath, depth, token);
                nodes = new List<GraphNode>(neighbors);
                var startNode = await GetNodeCoreAsync(startPath, token);
                if (startNode != null)
                    nodes.Insert(0, startNode);

                edges = new List<GraphEdge>();
                foreach (var node in nodes)
                    edges.AddRange(await GetEdgesCoreAsync(node.Path, token));
            }
            else
            {
                nodes = await GetAllNodesCoreAsync(token);
                edges = await GetAllEdgesCoreAsync(token);
            }

            return new GraphQueryResult(nodes.Distinct().ToList(), edges.Distinct().ToList());
        }, ct);

    private async Task EnsureNodeExistsCoreAsync(string path, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM graph_nodes WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
        if (!exists)
            await AddNodeCoreAsync(path, Path.GetFileNameWithoutExtension(path), ct);
    }

    private async Task UpdateNodeDegreeCoreAsync(string path, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE graph_nodes
            SET degree = (
                SELECT COUNT(*) FROM graph_edges
                WHERE source_path = @path OR target_path = @path
            )
            WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<GraphNode?> GetNodeCoreAsync(string path, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, title, degree FROM graph_nodes WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new GraphNode(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    private async Task<List<GraphNode>> GetAllNodesCoreAsync(CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, title, degree FROM graph_nodes";
        var nodes = new List<GraphNode>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            nodes.Add(new GraphNode(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return nodes;
    }

    private async Task<List<GraphEdge>> GetAllEdgesCoreAsync(CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT source_path, target_path, type, weight, context FROM graph_edges";
        var edges = new List<GraphEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new GraphEdge(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<EdgeType>(reader.GetString(2)),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return edges;
    }

    private async Task<List<GraphEdge>> GetEdgesCoreAsync(string path, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT source_path, target_path, type, weight, context
            FROM graph_edges
            WHERE source_path = @path OR target_path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        var edges = new List<GraphEdge>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new GraphEdge(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<EdgeType>(reader.GetString(2)),
                reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return edges;
    }

    private async Task<List<string>> GetOutNeighborsCoreAsync(string path, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT target_path FROM graph_edges WHERE source_path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        var neighbors = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            neighbors.Add(reader.GetString(0));
        return neighbors;
    }

    private async Task<List<string>> GetInNeighborsCoreAsync(string path, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT source_path FROM graph_edges WHERE target_path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        var neighbors = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            neighbors.Add(reader.GetString(0));
        return neighbors;
    }
}
