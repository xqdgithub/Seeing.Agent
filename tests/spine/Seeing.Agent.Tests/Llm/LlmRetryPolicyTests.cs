using System.Net;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class LlmRetryPolicyTests
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    [Fact]
    public void IsRetryable_UserCancel_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = new OperationCanceledException(cts.Token);

        LlmRetryPolicy.IsRetryable(ex, cts.Token).Should().BeFalse();
    }

    [Fact]
    public void IsRetryable_TimeoutStyleOce_ReturnsTrue()
    {
        // 超时 CTS：异常 token 未请求，调用方 token 也未取消
        var ex = new OperationCanceledException(CancellationToken.None);

        LlmRetryPolicy.IsRetryable(ex, CancellationToken.None).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_Http4xx_ReturnsFalse()
    {
        var ex = new HttpRequestException("bad", null, HttpStatusCode.BadRequest);
        LlmRetryPolicy.IsRetryable(ex, CancellationToken.None).Should().BeFalse();
    }

    [Fact]
    public void IsRetryable_Http5xx_ReturnsTrue()
    {
        var ex = new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
        LlmRetryPolicy.IsRetryable(ex, CancellationToken.None).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_TimeoutException_ReturnsTrue()
    {
        LlmRetryPolicy.IsRetryable(new TimeoutException(), CancellationToken.None).Should().BeTrue();
    }

    [Fact]
    public void ComputeDelay_ShouldFollowExponentialSequence_AndRespectCap()
    {
        var expected = new[]
        {
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromMilliseconds(2000),
            TimeSpan.FromMilliseconds(4000),
            TimeSpan.FromMilliseconds(8000),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10)
        };

        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            LlmRetryPolicy.ComputeDelay(attempt, BaseDelay, MaxDelay)
                .Should().Be(expected[attempt - 1], "attempt={0}", attempt);
        }
    }

    [Fact]
    public void ComputeDelay_ShouldClampNonPositiveAttempt()
    {
        LlmRetryPolicy.ComputeDelay(0, BaseDelay, MaxDelay)
            .Should().Be(BaseDelay);
    }

    [Fact]
    public void ShouldRetry_UnlimitedRetries_UsesBudgetOnly()
    {
        var elapsed = TimeSpan.FromSeconds(100);
        var next = TimeSpan.FromSeconds(10);

        LlmRetryPolicy.ShouldRetry(attempt: 100, elapsed, next, maxRetries: 0, budget: TimeSpan.FromSeconds(120))
            .Should().BeTrue();

        LlmRetryPolicy.ShouldRetry(attempt: 100, elapsed, next, maxRetries: 0, budget: TimeSpan.FromSeconds(109))
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRetry_BudgetBoundary_IsInclusive()
    {
        var budget = TimeSpan.FromSeconds(10);

        LlmRetryPolicy.ShouldRetry(
                attempt: 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), maxRetries: 0, budget)
            .Should().BeTrue();

        LlmRetryPolicy.ShouldRetry(
                attempt: 1, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5001), maxRetries: 0, budget)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRetry_MaxRetriesCapsAttempts()
    {
        var elapsed = TimeSpan.Zero;
        var next = TimeSpan.FromMilliseconds(1);

        LlmRetryPolicy.ShouldRetry(attempt: 1, elapsed, next, maxRetries: 2, budget: TimeSpan.FromMinutes(1))
            .Should().BeTrue();
        LlmRetryPolicy.ShouldRetry(attempt: 2, elapsed, next, maxRetries: 2, budget: TimeSpan.FromMinutes(1))
            .Should().BeFalse();
    }
}
