using FluentAssertions;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Recall;
using Microsoft.Extensions.Options;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Recall;

public class MemoryRecallServiceTests
{
    [Fact]
    public async Task RecallAsync_ShouldExcludeSessionPaths()
    {
        var index = new Mock<IMemoryIndex>();
        index.Setup(i => i.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SearchHit>
            {
                Hit("session/s1/x.md", "session"),
                Hit("daily/2026-07-18/a.md", "daily"),
                Hit("digest/b.md", "digest"),
            });

        var svc = new MemoryRecallService(index.Object, Options.Create(new MemoryOptions()));
        var hits = await svc.RecallAsync("q");

        hits.Should().HaveCount(2);
        hits.Should().OnlyContain(h => !h.Node.Path.StartsWith("session/"));
    }

    [Fact]
    public async Task RecallAsync_WhenTimeout_ShouldReturnEmpty()
    {
        var index = new Mock<IMemoryIndex>();
        index.Setup(i => i.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (SearchQuery _, CancellationToken ct) =>
            {
                await Task.Delay(500, ct);
                return (IReadOnlyList<SearchHit>)Array.Empty<SearchHit>();
            });

        var svc = new MemoryRecallService(index.Object, Options.Create(new MemoryOptions
        {
            Retrieval = new MemoryRetrievalOptions { InjectTimeoutMs = 20 }
        }));

        var hits = await svc.RecallAsync("q");
        hits.Should().BeEmpty();
    }

    private static SearchHit Hit(string path, string type) =>
        new(
            FileNode.Create(path, "body", FileMetadata.Create("id", Enum.Parse<MemoryType>(type, true), path)),
            1.0, 1.0, 0);
}
