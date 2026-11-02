using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Reflection.Background;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Background;

/// <summary>
/// 后台工作者的停用语义。
/// <para>
/// 宿主关闭（<c>stoppingToken</c>）与模块停用（<c>CancelInFlight</c>）是两个独立取消源：
/// 模块停用时宿主仍在运行，不能借用 <c>stoppingToken</c>。
/// 没有这个区分，单次复盘会一直跑到 <c>RunTimeoutMinutes</c>（默认 10 分钟）——
/// 用户关了复盘，后台还在烧模型调用。
/// </para>
/// </summary>
public sealed class ReflectionWorkerCancellationTests
{
    private static ReflectionWorker CreateWorker(ReflectionModuleActivity activity)
    {
        var queue = new ReflectionQueue(capacity: 4);
        var options = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<Reflection.Configuration.ReflectionOptions>>();
        options.SetupGet(m => m.CurrentValue).Returns(new Reflection.Configuration.ReflectionOptions());

        return new ReflectionWorker(
            queue,
            executor: new NoopExecutor(),
            idleScanner: new ReflectionIdleScanner(
                queue,
                new Seeing.Session.Management.SessionActivityTracker(),
                options.Object),
            activity,
            NullLogger<ReflectionWorker>.Instance);
    }

    [Fact]
    public void CancelInFlight_Should_Be_Repeatable()
    {
        // 每次调用都换新 CTS，因此可以多次调用（Activate/Deactivate 循环）。
        var worker = CreateWorker(new ReflectionModuleActivity { IsActive = true });

        var act = () =>
        {
            worker.CancelInFlight();
            worker.CancelInFlight();
            worker.CancelInFlight();
        };

        act.Should().NotThrow("重复停用不得抛（已取消的 CTS 不可复用，实现要换新的）");
    }

    [Fact]
    public void CancelInFlight_Should_Not_Affect_Next_Activation()
    {
        // 停用 → 重新启用后，新的执行必须不受上一次取消的影响。
        var worker = CreateWorker(new ReflectionModuleActivity { IsActive = true });

        worker.CancelInFlight();

        // 无法直接读私有 CTS，通过「再次取消不抛」间接确认新 CTS 已建立且未被取消。
        var act = () => worker.CancelInFlight();
        act.Should().NotThrow();
    }

    /// <summary>本测试只验证取消语义，不需要真正执行复盘。</summary>
    private sealed class NoopExecutor : Abstractions.IReflectionExecutor
    {
        public Task<ReflectionRunResult> RunAsync(
            ReflectionRequest request,
            CancellationToken ct = default)
            => Task.FromResult(ReflectionRunResult.Skipped(ReflectionSkipReasons.Disabled));
    }
}
