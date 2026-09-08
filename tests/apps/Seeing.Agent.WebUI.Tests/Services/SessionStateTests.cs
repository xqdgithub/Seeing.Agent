using FluentAssertions;
using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Tests.Services;

public class SessionStateTests
{
    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var state = new SessionState();
        state.Dispose();
        state.Dispose(); // 防重入：不抛异常
    }

    [Fact]
    public void Dispose_AfterStartExecution_ShouldReleaseResourcesWithoutThrow()
    {
        var state = new SessionState();
        state.StartExecution(); // 占用执行锁并创建取消令牌
        state.Dispose();        // 释放锁 + cts，不抛
    }
}
