using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Evolution;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Storage;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Evolution;

public class LlmMemoryEvolutionGraphTests : IDisposable
{
    private readonly string _dir;

    public LlmMemoryEvolutionGraphTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "seeing-memory-evolution-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task EvolveSessionAsync_WhenEvolves_ShouldUpdateGraphWithNodesAndEdges()
    {
        // Arrange
        var store = new LocalFileStore(_dir, NullLogger<LocalFileStore>.Instance);

        // Pre-create a daily file that references a specific session
        var dailyPath = $"daily/2026-07-30/test123.md";
        var dailyContent = """
            ---
            id: test123
            type: daily
            title: "Test observation"
            tags: [important]
            importance: 0.9
            source_session: sess1
            created_at: 2026-07-30T00:00:00.0000000+00:00
            ---

            User prefers PostgreSQL
            """;
        await store.WriteAsync(dailyPath, dailyContent);

        var index = new Mock<IMemoryIndex>();
        var graph = new Mock<IMemoryGraph>();
        var completion = new Mock<ITextCompletion>();

        // Mock LLM response: evolve the daily into a new entry
        var evolutionJson = """
            {
              "items": [
                {
                  "title": "User DB preference",
                  "content": "User prefers PostgreSQL [[daily/2026-07-30/test123.md]] for all new projects.",
                  "importance": 0.9,
                  "tags": ["db", "preference"]
                }
              ]
            }
            """;
        completion.Setup(c => c.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(evolutionJson);

        var options = Options.Create(new MemoryOptions());

        var evolution = new LlmMemoryEvolution(
            store,
            index.Object,
            graph.Object,
            completion.Object,
            options,
            NullLogger<LlmMemoryEvolution>.Instance);

        // Act
        await evolution.EvolveSessionAsync("sess1", CancellationToken.None);

        // Assert
        index.Verify(i => i.IndexAsync(It.IsAny<FileNode>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // 验证图谱节点被添加
        graph.Verify(g => g.AddNodeAsync(
            It.Is<string>(p => p.StartsWith("digest/") || p.StartsWith("daily/")),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
