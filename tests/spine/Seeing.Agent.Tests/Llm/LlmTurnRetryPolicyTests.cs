using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using System.Net;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class LlmTurnRetryPolicyTests
{
    private static DefaultLlmTurnRetryPolicy Create(
        int maxAttempts = 0, int totalBudgetMs = 0, int baseDelayMs = 500, int maxDelayMs = 10_000, bool enabled = true)
        => new(new LlmTurnRetryOptions
        {
            Enabled = enabled,
            MaxAttempts = maxAttempts,
            TotalBudgetMs = totalBudgetMs,
            BaseDelayMs = baseDelayMs,
            MaxDelayMs = maxDelayMs
        });

    [Fact]
    public void CanRetry_Http4xx_ReturnsFalse()
        => Create().CanRetry(new HttpRequestException("400", null, HttpStatusCode.BadRequest), default)
            .Should().BeFalse();

    [Fact]
    public void CanRetry_IOException_ReturnsTrue()
        => Create().CanRetry(new IOException("broken"), default).Should().BeTrue();

    [Fact]
    public void CanRetry_LlmStreamingExceptionWithInnerIOException_ReturnsTrue()
        => Create().CanRetry(
                new LlmStreamingException("流式响应读取失败", new IOException("remote closed")), default)
            .Should().BeTrue();

    [Fact]
    public void CanRetry_LlmRetryExhausted_ReturnsFalse()
        => Create().CanRetry(
                new LlmRetryExhaustedException(3, new IOException("x")), default)
            .Should().BeFalse();

    [Fact]
    public void NextDelay_GrowsExponentiallyAndCaps()
    {
        var p = Create(baseDelayMs: 500, maxDelayMs: 2000);
        p.NextDelay(1).TotalMilliseconds.Should().Be(500);
        p.NextDelay(2).TotalMilliseconds.Should().Be(1000);
        p.NextDelay(3).TotalMilliseconds.Should().Be(2000);
        p.NextDelay(4).TotalMilliseconds.Should().Be(2000);
    }

    [Fact]
    public void ShouldRetry_NoLimits_AlwaysTrue()
        => Create().ShouldRetry(attempt: 100, TimeSpan.Zero, TimeSpan.FromSeconds(10)).Should().BeTrue();

    [Fact]
    public void ShouldRetry_MaxAttemptsCaps()
        => Create(maxAttempts: 3).ShouldRetry(attempt: 3, TimeSpan.Zero, TimeSpan.FromSeconds(1)).Should().BeFalse();

    [Fact]
    public void ShouldRetry_BudgetCaps()
        => Create(totalBudgetMs: 5_000)
            .ShouldRetry(attempt: 1, TimeSpan.FromMilliseconds(4_500), TimeSpan.FromSeconds(1)).Should().BeFalse();

    [Fact]
    public void ShouldRetry_Disabled_ReturnsFalse()
        => Create(enabled: false).ShouldRetry(attempt: 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)).Should().BeFalse();
}
