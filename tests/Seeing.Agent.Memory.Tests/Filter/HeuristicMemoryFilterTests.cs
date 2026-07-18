using FluentAssertions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Filter;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Filter;

public class HeuristicMemoryFilterTests
{
    [Fact]
    public void Evaluate_TooShort_ShouldReject()
    {
        var filter = new HeuristicMemoryFilter(Options.Create(new MemoryOptions
        {
            Filter = new MemoryFilterOptions { MinChars = 20 }
        }));
        var d = filter.Evaluate(new MemoryCandidate("1", "s", null, MemorySource.Chat, null, "hi", DateTimeOffset.UtcNow));
        d.Accepted.Should().BeFalse();
        d.Reason.Should().Be("too_short");
    }

    [Fact]
    public void Evaluate_AckOnly_ShouldReject()
    {
        var filter = new HeuristicMemoryFilter(Options.Create(new MemoryOptions
        {
            Filter = new MemoryFilterOptions { MinChars = 1 }
        }));
        var d = filter.Evaluate(new MemoryCandidate("1", "s", null, MemorySource.Chat, null, "好的", DateTimeOffset.UtcNow));
        d.Accepted.Should().BeFalse();
        d.Reason.Should().Be("ack");
    }

    [Fact]
    public void Evaluate_SubstantialContent_ShouldAccept()
    {
        var filter = new HeuristicMemoryFilter(Options.Create(new MemoryOptions()));
        var text = "用户偏好使用 PostgreSQL，并且要求所有 API 必须分页。";
        filter.Evaluate(new MemoryCandidate("1", "s", null, MemorySource.Chat, null, text, DateTimeOffset.UtcNow))
            .Accepted.Should().BeTrue();
    }
}
