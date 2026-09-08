using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Buffer;

public class MemoryFlushServiceTests
{
    [Fact]
    public void TryFlushAfterTurn_WhenRateLimited_ShouldRebufferAndRestoreTurns()
    {
        var options = MemoryTestOptions.Monitor(new MemoryOptions
        {
            Extraction = new MemoryExtractionOptions
            {
                ExtractEveryNTurns = 2,
                MaxCandidatesPerMinute = 1
            }
        });
        var buffer = new SessionMemoryBuffer(options);
        var queue = new Mock<IMemoryWorkQueue>();
        queue.Setup(q => q.TryEnqueue(It.IsAny<MemoryBatch>())).Returns(true);
        var pipeline = new Mock<IMemoryPipeline>(MockBehavior.Strict);

        var flush = new MemoryFlushService(
            buffer,
            queue.Object,
            pipeline.Object,
            options,
            NullLogger<MemoryFlushService>.Instance);

        buffer.Add(new MemoryCandidate("1", "s1", null, MemorySource.Chat, null,
            "用户偏好使用 PostgreSQL 并且要求分页。", DateTimeOffset.UtcNow));

        flush.TryFlushAfterTurn("s1").Should().BeFalse(); // turn 1
        flush.TryFlushAfterTurn("s1").Should().BeTrue();  // turn 2 -> enqueue

        // Exhaust rate: second batch should fail enqueue path via rate limit
        buffer.Add(new MemoryCandidate("2", "s1", null, MemorySource.Chat, null,
            "用户还要求所有 API 必须分页返回。", DateTimeOffset.UtcNow));
        flush.TryFlushAfterTurn("s1"); // turn 1
        flush.TryFlushAfterTurn("s1").Should().BeFalse(); // rate limited

        buffer.GetPendingCount("s1").Should().Be(1);
        buffer.GetTurnCount("s1").Should().Be(2);
    }
}
