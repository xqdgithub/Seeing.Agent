using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Core.Graph;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Core;

public class SqliteMemoryGraphTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteMemoryGraph _graph;

    public SqliteMemoryGraphTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _graph = new SqliteMemoryGraph(_connection);
    }

    [Fact]
    public async Task AddNodeAsync_ShouldAddNode()
    {
        // Act
        await _graph.AddNodeAsync("test.md", "Test");

        // Assert
        var stats = await _graph.GetStatsAsync();
        Assert.Equal(1, stats.NodeCount);
    }

    [Fact]
    public async Task AddEdgeAsync_ShouldAddEdgeAndCreateNodes()
    {
        // Act
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);

        // Assert
        var stats = await _graph.GetStatsAsync();
        Assert.Equal(2, stats.NodeCount);
        Assert.Equal(1, stats.EdgeCount);
    }

    [Fact]
    public async Task AddEdgeAsync_SameSourceTarget_ShouldNotDuplicate()
    {
        // Act
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);

        // Assert
        var stats = await _graph.GetStatsAsync();
        Assert.Equal(1, stats.EdgeCount); // UNIQUEness constraint
    }

    [Fact]
    public async Task AddEdgeAsync_DifferentTypes_ShouldAllowSamePair()
    {
        // Act
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.ParentChild);

        // Assert
        var stats = await _graph.GetStatsAsync();
        Assert.Equal(2, stats.EdgeCount);
    }

    [Fact]
    public async Task RemoveNodeAsync_ShouldRemoveAllEdges()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc2.md", "doc3.md", EdgeType.Reference);

        // Act
        await _graph.RemoveNodeAsync("doc2.md");

        // Assert
        var stats = await _graph.GetStatsAsync();
        Assert.Equal(2, stats.NodeCount); // doc1, doc3
        Assert.Equal(0, stats.EdgeCount); // all edges removed
    }

    [Fact]
    public async Task RemoveEdgeAsync_ShouldRemoveOnlyEdge()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc1.md", "doc3.md", EdgeType.Reference);

        // Act
        await _graph.RemoveEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);

        // Assert
        var stats = await _graph.GetStatsAsync();
        Assert.Equal(3, stats.NodeCount);
        Assert.Equal(1, stats.EdgeCount);
    }

    [Fact]
    public async Task GetNeighborsAsync_ShouldReturnConnectedNodes()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc2.md", "doc3.md", EdgeType.Reference);

        // Act
        var neighbors = await _graph.GetNeighborsAsync("doc2.md");

        // Assert
        Assert.Equal(2, neighbors.Count);
        Assert.Contains(neighbors, n => n.Path == "doc1.md");
        Assert.Contains(neighbors, n => n.Path == "doc3.md");
    }

    [Fact]
    public async Task GetNeighborsAsync_WithDepth_ShouldTraverseGraph()
    {
        // Arrange: doc1 -> doc2 -> doc3
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc2.md", "doc3.md", EdgeType.Reference);

        // Act - depth 1 from doc1
        var depth1 = await _graph.GetNeighborsAsync("doc1.md", depth: 1);

        // Act - depth 2 from doc1
        var depth2 = await _graph.GetNeighborsAsync("doc1.md", depth: 2);

        // Assert
        Assert.Single(depth1); // only doc2
        Assert.Equal(2, depth2.Count); // doc2 and doc3
    }

    [Fact]
    public async Task FindPathAsync_ShouldFindShortestPath()
    {
        // Arrange: doc1 -> doc2 -> doc3
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc2.md", "doc3.md", EdgeType.Reference);

        // Act
        var path = await _graph.FindPathAsync("doc1.md", "doc3.md");

        // Assert
        Assert.Equal(3, path.Count);
        Assert.Equal("doc1.md", path[0]);
        Assert.Equal("doc2.md", path[1]);
        Assert.Equal("doc3.md", path[2]);
    }

    [Fact]
    public async Task FindPathAsync_NoPath_ShouldReturnEmpty()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc3.md", "doc4.md", EdgeType.Reference);

        // Act
        var path = await _graph.FindPathAsync("doc1.md", "doc4.md");

        // Assert
        Assert.Empty(path);
    }

    [Fact]
    public async Task GetStatsAsync_ShouldReturnCorrectStats()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc2.md", "doc3.md", EdgeType.Reference);
        await _graph.AddNodeAsync("doc4.md", "Doc4"); // isolated

        // Act
        var stats = await _graph.GetStatsAsync();

        // Assert
        Assert.Equal(4, stats.NodeCount);
        Assert.Equal(2, stats.EdgeCount);
        Assert.Equal(1, stats.IsolatedNodes); // doc4
    }

    [Fact]
    public async Task QueryAsync_WithStartNode_ShouldReturnSubgraph()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc2.md", "doc3.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc3.md", "doc4.md", EdgeType.Reference);

        // Act
        var result = await _graph.QueryAsync("doc2.md", depth: 1);

        // Assert
        Assert.Contains(result.Nodes, n => n.Path == "doc2.md");
        Assert.Contains(result.Nodes, n => n.Path == "doc1.md");
        Assert.Contains(result.Nodes, n => n.Path == "doc3.md");
        Assert.DoesNotContain(result.Nodes, n => n.Path == "doc4.md");
    }

    [Fact]
    public async Task QueryAsync_NoStartNode_ShouldReturnFullGraph()
    {
        // Arrange
        await _graph.AddEdgeAsync("doc1.md", "doc2.md", EdgeType.Reference);
        await _graph.AddEdgeAsync("doc3.md", "doc4.md", EdgeType.Reference);

        // Act
        var result = await _graph.QueryAsync();

        // Assert
        Assert.Equal(4, result.Nodes.Count);
        Assert.Equal(2, result.Edges.Count);
    }

    public void Dispose()
    {
        _graph?.Dispose();
        _connection?.Dispose();
    }
}
