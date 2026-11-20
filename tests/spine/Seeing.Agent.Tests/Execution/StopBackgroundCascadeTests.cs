using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// <see cref="SessionRuntime.StopAsync"/> 的后台级联（spec §9.2 取消矩阵）：
/// 停止会话必须对其**自身 + 整棵子会话子树**逐个调 <c>CancelBySessionAsync(SessionStopped)</c>，
/// 否则「已停止」的会话仍会有后台活动继续产出并回写 digest。
/// </summary>
public class StopBackgroundCascadeTests
{
    /// <summary>即时完成的假执行体：本用例只关心停止时的级联取消，不需要在途窗口。</summary>
    private sealed class CompletingRunner : IExecutionRunner
    {
        public Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            _ = batch;
            _ = executionToken;
            record.Status = ExecutionStatus.Completed;
            return Task.FromResult(record);
        }
    }

    private sealed class NoopPublisher : IExecutionEventPublisher
    {
        public void Publish(string sessionId, IMessageEvent evt) { }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(
            string sessionId, CancellationToken cancellationToken) => Stream();

        private static async IAsyncEnumerable<IMessageEvent> Stream()
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => [];

        public void ClearBuffer(string sessionId) { }

        public void CompleteSession(string sessionId) { }

        public ILiveEventSubscription SubscribeLive(string sessionId) => LiveEventSubscription.Completed;
    }

    private static SessionRuntime CreateRuntime(
        SessionManager sessionManager,
        ISessionGroupManager groupManager,
        IBackgroundActivityControl control)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton(groupManager);
        services.AddSingleton(control);
        var provider = services.BuildServiceProvider();

        var options = new ExecutionOptions
        {
            MaxConcurrentExecutions = -1,
            SessionIdleTimeout = TimeSpan.FromMinutes(30),
            // 拉长清理间隔，避免定时器在测试期间自动触发
            CleanupInterval = TimeSpan.FromHours(1)
        };

        return new SessionRuntime(
            new CompletingRunner(), new NoopPublisher(), provider, options,
            NullLogger<SessionRuntime>.Instance);
    }

    private static SessionGroupManager CreateGroupManager(SessionManager sessionManager) =>
        new(
            sessionManager,
            new FileSessionGroupStore(
                Path.Combine(Path.GetTempPath(), "seeing-grp-stop-tests", Guid.NewGuid().ToString("N"))),
            new SessionForker(NullLogger<SessionForker>.Instance, sessionManager),
            NullLogger<SessionGroupManager>.Instance);

    [Fact]
    public async Task StopAsync_ShouldCancelActivities_ForSelfAndDescendants()
    {
        var control = new RecordingBackgroundActivityControl();
        var sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);
        var groupManager = CreateGroupManager(sessionManager);
        var runtime = CreateRuntime(sessionManager, groupManager, control);

        await sessionManager.EnsureSessionAsync("parent");
        var child = await groupManager.CreateChildAsync(
            "parent", "general", "child", Array.Empty<SessionPermissionRule>(), null);

        await runtime.StopAsync("parent");

        // 父与子都必须收到按会话取消，且原因是「会话被停止」。
        control.SessionCalls.Select(c => c.SessionId)
            .Should().BeEquivalentTo(new[] { "parent", child.Id });
        control.SessionCalls.Should().OnlyContain(
            c => c.Reason == BackgroundActivityCancelReason.SessionStopped);
    }
}
