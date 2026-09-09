using System.Net;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class LlmRetryPolicyTests
{
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
}
