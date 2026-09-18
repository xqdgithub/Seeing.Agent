using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Core.Tools.Session.Tests;

public class SessionHandoffToolTests
{
    private static SessionHandoffTool CreateTool(SessionToolTestHarness h, IExecutionSubmitter submitter) =>
        new(NullLogger<SessionHandoffTool>.Instance, h.Sessions, h.Groups, submitter);

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
            new StubExecutionSubmitter(ExecutionSubmitResult.Failed("x")));

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
        var submitter = new StubExecutionSubmitter(ExecutionSubmitResult.Succeeded("exec-1"));
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h, submitter);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work", title = "handoff" }), Context(root.Id, factory));

        result.Success.Should().BeTrue();
        result.TurnDirective.Should().Be(ToolTurnDirective.EndTurn);
        result.TurnDirectiveReason.Should().Be("handoff");

        var targetId = (string)result.Metadata["target_session_id"];
        result.Output.Should().Contain(targetId);

        var group = await h.Groups.GetGroupForSessionAsync(root.Id);
        group!.ResolveActiveId().Should().Be(targetId);

        var members = await h.Groups.ListMembersAsync(group.Id);
        members.Should().Contain(m =>
            m.SessionId == targetId && m.Relation == SessionRelation.HandoffSuccessor);

        var target = await h.Sessions.GetOrLoadAsync(targetId);
        target.Messages.Should().HaveCount(1);
        target.Messages[0].Role.Should().Be("user");
        target.Messages[0].Content.Should().Be("continue work");

        submitter.LastSessionId.Should().Be(targetId);
        submitter.LastInput!.Text.Should().Be("continue work");
        submitter.LastOptions!.SkipUserMessagePersist.Should().BeTrue();
        submitter.LastOptions.AgentId.Should().Be(target.SelectedAgent);
        submitter.LastOptions.ModelId.Should().Be(target.SelectedModel);

        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.Resource.Should().Be("session_handoff");
    }

    [Fact]
    public async Task Handoff_SameCallId_ShouldCreateSingleSuccessorAndSubmitOnce()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");
        var submitter = new StubExecutionSubmitter(ExecutionSubmitResult.Succeeded("exec-1"));
        var tool = CreateTool(h, submitter);
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

        var group = await h.Groups.GetGroupForSessionAsync(root.Id);
        var members = await h.Groups.ListMembersAsync(group!.Id);
        members.Count(m => m.Relation == SessionRelation.HandoffSuccessor).Should().Be(1);
        submitter.SubmitCount.Should().Be(1);
    }

    [Fact]
    public async Task Handoff_Should_Rollback_WhenSubmitFails()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");
        var submitter = new StubExecutionSubmitter(ExecutionSubmitResult.Failed("submit boom"));
        var tool = CreateTool(h, submitter);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work" }), Context(root.Id));

        result.Success.Should().BeFalse();
        result.TurnDirective.Should().Be(ToolTurnDirective.Continue);

        var targetId = submitter.LastSessionId!;
        targetId.Should().NotBeNullOrEmpty();
        h.Sessions.Get(targetId).Should().BeNull();

        var group = await h.Groups.GetGroupForSessionAsync(root.Id);
        group!.ResolveActiveId().Should().Be(root.Id);
        var members = await h.Groups.ListMembersAsync(group.Id);
        members.Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);
    }

    [Fact]
    public async Task Handoff_Should_Fail_WhenAuthorizationDenied()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var submitter = new StubExecutionSubmitter(ExecutionSubmitResult.Succeeded("exec-1"));
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Deny);
        var tool = CreateTool(h, submitter);

        var groupBefore = await h.Groups.GetGroupForSessionAsync(root.Id);
        var membersBefore = await h.Groups.ListMembersAsync(groupBefore!.Id);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work" }), Context(root.Id, factory));

        result.Success.Should().BeFalse();
        submitter.LastSessionId.Should().BeNull();

        var membersAfter = await h.Groups.ListMembersAsync(groupBefore.Id);
        membersAfter.Should().HaveCount(membersBefore.Count);
    }

    [Fact]
    public async Task Handoff_Should_Fail_WhenSourceIsNotAnchor()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        await h.AddMessageAsync(root.Id, "user", "hello");

        // 源已是前任（已有后继）：将其标记为非锚点 HandoffPredecessor
        var group = await h.Groups.GetGroupForSessionAsync(root.Id);
        await h.Groups.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = root.Id,
            Relation = SessionRelation.HandoffPredecessor,
            IsAnchor = false,
        });

        var submitter = new StubExecutionSubmitter(ExecutionSubmitResult.Succeeded("exec-1"));
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h, submitter);

        var result = await tool.ExecuteAsync(
            Args(new { prompt = "continue work" }), Context(root.Id, factory));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("锚点");
        submitter.SubmitCount.Should().Be(0);

        var members = await h.Groups.ListMembersAsync(group.Id);
        members.Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);
    }

    [Fact]
    public async Task Handoff_Should_Fail_WhenPromptMissing()
    {
        using var h = new SessionToolTestHarness();
        var root = await h.CreateRootGroupedAsync("root");
        var submitter = new StubExecutionSubmitter(ExecutionSubmitResult.Succeeded("exec-1"));
        var tool = CreateTool(h, submitter);

        var result = await tool.ExecuteAsync(Args(new { }), Context(root.Id));

        result.Success.Should().BeFalse();
    }
}
