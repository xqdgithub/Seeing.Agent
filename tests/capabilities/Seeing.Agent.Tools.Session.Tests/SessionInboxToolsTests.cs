using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.Session.Tests;

/// <summary>
/// 会话 Inbox 三工具（session_inbox_send / session_takeover / session_release）测试。
/// <para>本文件自带 <see cref="RecordingSessionRuntime"/> 桩；harness 的
/// <c>StubExecutionSubmitter</c> 迁移归 Task 3.6。</para>
/// </summary>
public class SessionInboxToolsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(
        string sessionId, IPermissionAuthorizerFactory? factory = null)
    {
        var ctx = new ToolContext { SessionId = sessionId };
        if (factory is not null)
            ctx.Services = new StubServiceProvider().Add(factory);
        return ctx;
    }

    // ================= session_inbox_send =================

    [Fact]
    public async Task InboxSend_Should_Deliver_WakeUser_ToSameGroup()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var child = await h.Groups.CreateChildAsync(
            root.Id, "build", "child", Array.Empty<SessionPermissionRule>(), null,
            TestContext.Current.CancellationToken);

        var runtime = new RecordingSessionRuntime();
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = child.Id, body = "hello child" }), Context(root.Id));

        result.Success.Should().BeTrue();
        runtime.DeliverCount.Should().Be(1);
        runtime.LastMessage!.TargetSessionId.Should().Be(child.Id);
        runtime.LastMessage.Body.Should().Be("hello child");
        runtime.LastMessage.Sender.Kind.Should().Be(InboxSenderKind.Human);
        runtime.LastMessage.Delivery.Should().Be(InboxDelivery.Wake);
        runtime.LastMessage.Presentation.Should().Be(InboxPresentation.User);
        result.Metadata["target_session_id"].Should().Be(child.Id);
    }

    [Fact]
    public async Task InboxSend_Should_ParseInjectDelivery()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var runtime = new RecordingSessionRuntime();
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root.Id, body = "note", delivery = "inject" }), Context(root.Id));

        result.Success.Should().BeTrue();
        runtime.LastMessage!.Delivery.Should().Be(InboxDelivery.Inject);
    }

    [Fact]
    public async Task InboxSend_Should_Fail_WhenBodyMissing()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var runtime = new RecordingSessionRuntime();
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root.Id }), Context(root.Id));

        result.Success.Should().BeFalse();
        runtime.DeliverCount.Should().Be(0);
    }

    [Fact]
    public async Task InboxSend_Should_Fail_WhenDeliveryUnknown()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var runtime = new RecordingSessionRuntime();
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root.Id, body = "x", delivery = "bogus" }), Context(root.Id));

        result.Success.Should().BeFalse();
        runtime.DeliverCount.Should().Be(0);
    }

    [Fact]
    public async Task InboxSend_CrossGroup_Should_Authorize_AndDeny()
    {
        using var h = new SessionToolTestHarness();
        var a = await h.CreateRootGroupedAsync("a");
        var b = await h.CreateRootGroupedAsync("b");
        var runtime = new RecordingSessionRuntime();
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = b.Id, body = "x" }), Context(a.Id, factory));

        result.Success.Should().BeFalse();
        runtime.DeliverCount.Should().Be(0);
        factory.LastRequest!.Resource.Should().Be("session_inbox_send");
    }

    [Fact]
    public async Task InboxSend_CrossGroup_Should_Allow_WhenAuthorized()
    {
        using var h = new SessionToolTestHarness();
        var a = await h.CreateRootGroupedAsync("a");
        var b = await h.CreateRootGroupedAsync("b");
        var runtime = new RecordingSessionRuntime();
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = b.Id, body = "x" }), Context(a.Id, factory));

        result.Success.Should().BeTrue();
        runtime.DeliverCount.Should().Be(1);
    }

    [Fact]
    public async Task InboxSend_Should_Fail_WhenDeliverFails()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var runtime = new RecordingSessionRuntime(InboxResult.Failed("boom"));
        var tool = new SessionInboxSendTool(
            NullLogger<SessionInboxSendTool>.Instance, h.Sessions, h.Groups, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root.Id, body = "x" }), Context(root.Id));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("boom");
    }

    // ================= session_takeover =================

    [Fact]
    public async Task Takeover_Should_SetOwnerHuman_AndPersist()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var child = await h.Groups.CreateChildAsync(
            root.Id, "build", "child", Array.Empty<SessionPermissionRule>(), null,
            TestContext.Current.CancellationToken);

        var tool = new SessionTakeoverTool(
            NullLogger<SessionTakeoverTool>.Instance, h.Sessions, h.Groups);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = child.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        child.GetControl().Owner.Should().Be(SessionOwner.Human);
        result.Metadata["owner"].Should().Be("human");
    }

    [Fact]
    public async Task Takeover_Should_Fail_WhenSessionMissing()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var tool = new SessionTakeoverTool(
            NullLogger<SessionTakeoverTool>.Instance, h.Sessions, h.Groups);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = "does-not-exist" }), Context(root.Id));

        result.Success.Should().BeFalse();
    }

    // ================= session_release =================

    [Fact]
    public async Task Release_Should_SetOwnerParent()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var child = await h.Groups.CreateChildAsync(
            root.Id, "build", "child", Array.Empty<SessionPermissionRule>(), null,
            TestContext.Current.CancellationToken);
        child.SetOwner(SessionOwner.Human, "agent");
        await h.Sessions.SaveAsync(child.Id);

        var tool = new SessionReleaseTool(
            NullLogger<SessionReleaseTool>.Instance, h.Sessions, h.Groups);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = child.Id }), Context(root.Id));

        result.Success.Should().BeTrue();
        child.GetControl().Owner.Should().Be(SessionOwner.Parent);
        result.Metadata["owner"].Should().Be("parent");
    }

    [Fact]
    public async Task Release_CrossGroup_Should_Deny()
    {
        using var h = new SessionToolTestHarness();
        var a = await h.CreateRootGroupedAsync("a");
        var b = await h.CreateRootGroupedAsync("b");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = new SessionReleaseTool(
            NullLogger<SessionReleaseTool>.Instance, h.Sessions, h.Groups);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = b.Id }), Context(a.Id, factory));

        result.Success.Should().BeFalse();
        factory.LastRequest!.Resource.Should().Be("session_release");
    }
}

/// <summary>记录投递参数并返回预设结果的会话运行时桩。</summary>
internal sealed class RecordingSessionRuntime : ISessionRuntime
{
    private readonly InboxResult _result;

    public RecordingSessionRuntime(InboxResult? result = null)
    {
        _result = result ?? InboxResult.Ok(messageId: "msg-1");
    }

    public InboxMessage? LastMessage { get; private set; }
    public int DeliverCount { get; private set; }
    public List<string> StoppedSessions { get; } = new();

    public Task<InboxResult> DeliverAsync(InboxMessage message, CancellationToken ct = default)
    {
        DeliverCount++;
        LastMessage = message;
        return Task.FromResult(_result);
    }

    public Task<bool> InterruptAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<int> StopAsync(string sessionId, CancellationToken ct = default)
    {
        StoppedSessions.Add(sessionId);
        return Task.FromResult(0);
    }

    public Task<int> CancelAllInFlightAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task WaitForIdleAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

    public SessionExecutionOverview GetOverview(string sessionId) => new();

    public bool HasAnyActiveExecution() => false;

    public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) =>
        Array.Empty<IMessageEvent>();

    public async IAsyncEnumerable<IMessageEvent> SubscribeEvents(
        string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<int> ReconcileIncompleteTasksAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(0);
}
