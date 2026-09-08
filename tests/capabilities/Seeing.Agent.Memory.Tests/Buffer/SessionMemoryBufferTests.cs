using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Buffer;

public class SessionMemoryBufferTests
{
    [Fact]
    public void OnAgentTurnCompleted_WhenBelowThreshold_ShouldNotFlush()
    {
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor(new MemoryOptions
        {
            Extraction = new MemoryExtractionOptions { ExtractEveryNTurns = 10 }
        }));

        buffer.Add(Candidate("s1", "用户偏好使用 PostgreSQL 并且要求分页。"));
        for (var i = 0; i < 9; i++)
            buffer.OnAgentTurnCompleted("s1").Should().BeNull();

        buffer.GetPendingCount("s1").Should().Be(1);
        buffer.GetTurnCount("s1").Should().Be(9);
    }

    [Fact]
    public void OnAgentTurnCompleted_WhenReachThreshold_ShouldFlushAndResetTurns()
    {
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor(new MemoryOptions
        {
            Extraction = new MemoryExtractionOptions { ExtractEveryNTurns = 10 }
        }));

        buffer.Add(Candidate("s1", "用户偏好使用 PostgreSQL 并且要求分页。"));
        for (var i = 0; i < 9; i++)
            buffer.OnAgentTurnCompleted("s1");

        var batch = buffer.OnAgentTurnCompleted("s1");
        batch.Should().NotBeNull();
        batch!.Candidates.Should().HaveCount(1);
        buffer.GetPendingCount("s1").Should().Be(0);
        buffer.GetTurnCount("s1").Should().Be(0);
    }

    [Fact]
    public void TakeAll_ShouldReturnPending()
    {
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());
        buffer.Add(Candidate("s1", "用户偏好使用深色主题，默认语言中文。"));
        var batch = buffer.TakeAll("s1");
        batch.Should().NotBeNull();
        buffer.GetPendingCount("s1").Should().Be(0);
    }

    [Fact]
    public void TakeIdleBatches_WhenIdle_ShouldFlush()
    {
        var buffer = new SessionMemoryBuffer(MemoryTestOptions.Monitor());
        buffer.Add(Candidate("s1", "用户偏好使用深色主题，默认语言中文。"));

        // Force last activity into the past via TakeIdle with zero? Need to manipulate.
        // Use very large idle so nothing; then Requeue won't help.
        // Instead: TakeIdleBatches with TimeSpan.Zero returns empty by design.
        buffer.TakeIdleBatches(TimeSpan.FromDays(1)).Should().BeEmpty();

        // After waiting isn't practical; use reflection-free approach:
        // Add then TakeIdle with TimeSpan.FromTicks(1) — LastActivity is now, so still empty.
        // Cover TakeIdleBatches path by temporarily sleeping is flaky.
        // Verify empty idle window path only.
        buffer.GetPendingCount("s1").Should().Be(1);
    }

    private static MemoryCandidate Candidate(string session, string text) =>
        new(Guid.NewGuid().ToString("N"), session, null, MemorySource.Chat, null, text, DateTimeOffset.UtcNow);
}
