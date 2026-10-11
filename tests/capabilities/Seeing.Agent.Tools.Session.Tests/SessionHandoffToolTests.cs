using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tools.Session.Tests;

public class SessionHandoffToolTests
{
    private static SessionHandoffTool CreateTool(SessionToolTestHarness h, ISessionRuntime runtime) =>
        new(NullLogger<SessionHandoffTool>.Instance, h.Sessions, h.Groups, runtime);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(string sessionId, IPermissionAuthorizerFactory? factory = null)
    {
        var ctx = new ToolContext { SessionId = sessionId };
        if (factory is not null)
            ctx.Services = new StubServiceProvider().Add(factory);
        return ctx;
    }

    [Fact]
    public void Capabilities_Should_SkipTimeout_And_DisableCache()
    {
        var tool = new SessionHandoffTool(
            NullLogger<SessionHandoffTool>.Instance,
            new Mock<ISessionManager>().Object,
            new Mock<ISessionGroupManager>().Object,
            new StubSessionRuntime(InboxResult.Failed("x")));

        tool.Capabilities.Should().NotBeNull();
        tool.Capabilities!["timeout.skip"].Should().Be("true");
        tool.Capabilities!["cache.enabled"].Should().Be("false");
    }

    [Fact]
    public async Task Handoff_Should_CreateSuccessor_Submit_And_EndTurn()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");
        var runtime = new StubSessionRuntime(InboxResult.Ok(messageId: "exec-1"), sessions: h.Sessions);
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work", title = "handoff" }), Context(root.Id, factory));

        result.Success.Should().BeTrue();
        result.TurnDirective.Should().Be(ToolTurnDirective.EndTurn);
        result.TurnDirectiveReason.Should().Be("handoff");

        var targetId = (string)result.Metadata["target_session_id"];
        result.Output.Should().Contain(targetId);

        var group = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        group!.ResolveActiveId().Should().Be(targetId);

        var members = await h.Groups.ListMembersAsync(group.Id, TestContext.Current.CancellationToken);
        members.Should().Contain(m =>
            m.SessionId == targetId && m.Relation == SessionRelation.HandoffSuccessor);

        var target = await h.Sessions.GetOrLoadAsync(targetId, TestContext.Current.CancellationToken);
        target.Messages.Should().HaveCount(1);
        target.Messages[0].Role.Should().Be("user");
        target.Messages[0].Content.Should().Be("continue work");

        runtime.LastSessionId.Should().Be(targetId);
        runtime.LastBody.Should().Be("continue work");
        runtime.LastAgentId.Should().Be(target.SelectedAgent);
        runtime.LastModelId.Should().Be(target.SelectedModel);

        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.Resource.Should().Be("session_handoff");
    }

    [Fact]
    public async Task Handoff_SameCallId_ShouldCreateSingleSuccessorAndSubmitOnce()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");
        var runtime = new StubSessionRuntime(InboxResult.Ok(messageId: "exec-1"), sessions: h.Sessions);
        var tool = CreateTool(h, runtime);
        var ctx = Context(root.Id);
        ctx.CallId = "call-42";

        var first = await tool.ExecuteAsync(Args(new { prompt = "go" }), ctx);
        var second = await tool.ExecuteAsync(Args(new { prompt = "go" }), ctx);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.TurnDirective.Should().Be(ToolTurnDirective.EndTurn);
        second.TurnDirectiveReason.Should().Be("handoff");

        var firstTarget = (string)first.Metadata["target_session_id"];
        var secondTarget = (string)second.Metadata["target_session_id"];
        secondTarget.Should().Be(firstTarget);

        var group = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var members = await h.Groups.ListMembersAsync(group!.Id, TestContext.Current.CancellationToken);
        members.Count(m => m.Relation == SessionRelation.HandoffSuccessor).Should().Be(1);
        runtime.DeliverCount.Should().Be(1);
    }

    [Fact]
    public async Task Handoff_Should_Rollback_WhenSubmitFails()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");
        var runtime = new StubSessionRuntime(InboxResult.Failed("submit boom"), sessions: h.Sessions);
        var tool = CreateTool(h, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work" }), Context(root.Id));

        result.Success.Should().BeFalse();
        result.TurnDirective.Should().Be(ToolTurnDirective.Continue);

        var targetId = runtime.LastSessionId!;
        targetId.Should().NotBeNullOrEmpty();
        h.Sessions.Get(targetId).Should().BeNull();

        var group = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        group!.ResolveActiveId().Should().Be(root.Id);
        var members = await h.Groups.ListMembersAsync(group.Id, TestContext.Current.CancellationToken);
        members.Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);
    }

    [Fact]
    public async Task Handoff_WhenSubmitFailsAfterCancellation_ShouldStillRollback()
    {
        // P1-15：提交失败常因取消令牌已触发；回滚须内部改用 CancellationToken.None，
        // 否则组锁 WaitAsync(ct) 立即抛 OCE，后继成为孤儿并劫持锚点。
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runtime = new StubSessionRuntime(
            InboxResult.Failed("submit boom"), onDelivering: () => cts.Cancel(), sessions: h.Sessions);
        var tool = CreateTool(h, runtime);

        var ctx = Context(root.Id);
        ctx.CancellationToken = cts.Token;

        var result = await tool.ExecuteAsync(Args(new { prompt = "continue work" }), ctx);

        result.Success.Should().BeFalse();

        var targetId = runtime.LastSessionId!;
        targetId.Should().NotBeNullOrEmpty();
        h.Sessions.Get(targetId).Should().BeNull("取消令牌下回滚仍须删除孤儿后继");

        var group = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        group!.ResolveActiveId().Should().Be(root.Id, "活跃会话须还原为源锚点");
        (await h.Groups.ListMembersAsync(group.Id, TestContext.Current.CancellationToken))
            .Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);
    }

    [Fact]
    public async Task Handoff_Should_Fail_WhenAuthorizationDenied()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var runtime = new StubSessionRuntime(InboxResult.Ok(messageId: "exec-1"), sessions: h.Sessions);
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = CreateTool(h, runtime);

        var groupBefore = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        var membersBefore = await h.Groups.ListMembersAsync(groupBefore!.Id, TestContext.Current.CancellationToken);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work" }), Context(root.Id, factory));

        result.Success.Should().BeFalse();
        runtime.LastSessionId.Should().BeNull();

        var membersAfter = await h.Groups.ListMembersAsync(groupBefore.Id, TestContext.Current.CancellationToken);
        membersAfter.Should().HaveCount(membersBefore.Count);
    }

    [Fact]
    public async Task Handoff_Should_Fail_WhenSourceIsNotAnchor()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");

        // 源已是前任（已有后继）：将其标记为非锚点 HandoffPredecessor
        var group = await h.Groups.GetGroupForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        await h.Groups.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = root.Id,
            Relation = SessionRelation.HandoffPredecessor,
            IsAnchor = false,
        }, TestContext.Current.CancellationToken);

        var runtime = new StubSessionRuntime(InboxResult.Ok(messageId: "exec-1"), sessions: h.Sessions);
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h, runtime);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work" }), Context(root.Id, factory));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("锚点");
        runtime.DeliverCount.Should().Be(0);

        var members = await h.Groups.ListMembersAsync(group.Id, TestContext.Current.CancellationToken);
        members.Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);
    }

    [Fact]
    public async Task Handoff_Should_Fail_WhenPromptMissing()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var runtime = new StubSessionRuntime(InboxResult.Ok(messageId: "exec-1"), sessions: h.Sessions);
        var tool = CreateTool(h, runtime);

        var result = await tool.ExecuteAsync(Args(new { }), Context(root.Id));

        result.Success.Should().BeFalse();
    }
}
