using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using System.Collections.Immutable;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>会话摘要投影：预览、动作行、状态条的纯函数判定。</summary>
public class SessionSummaryProjectorTests
{
    private static SessionData Session(params SessionMessage[] messages)
    {
        var session = SessionData.Create();
        foreach (var m in messages) session.AddMessage(m);
        return session;
    }

    private static SessionSummarySnapshot Build(
        SessionData? session, bool isExecuting = false,
        ExecutionStatus? status = ExecutionStatus.Completed,
        string? parentTitle = null,
        SessionRelation relation = SessionRelation.None,
        bool isAnchor = true)
        => SessionSummaryProjector.Build(session, isExecuting, status, parentTitle, relation, isAnchor, successorTitle: null);

    [Fact]
    public void Build_NullSession_ShouldReturnPlaceholder()
        => Build(null).Preview.Should().Be("暂无消息");

    [Fact]
    public void Build_EmptySession_ShouldReturnPlaceholder()
        => Build(Session()).Preview.Should().Be("暂无消息");

    [Fact]
    public void Build_WithAssistantMessage_ShouldPreviewLatestAssistant()
    {
        var s = Session(
            new SessionMessage { Id = "u1", Role = MessageRole.User, Content = "提问" },
            new SessionMessage { Id = "a1", Role = MessageRole.Assistant, Content = "这是回答内容" });

        Build(s).Preview.Should().Contain("这是回答内容");
    }

    [Fact]
    public void Build_WithRunningTasks_ShouldReportSubAgentCount()
    {
        var s = Session(new SessionMessage
        {
            Id = "a1",
            Role = MessageRole.Assistant,
            Content = "",
            ToolCalls = ImmutableList.Create(
                new SessionToolCall { Id = "t1", Name = "task", Status = "running", TaskId = "c1" },
                new SessionToolCall { Id = "t2", Name = "task", Status = "running", TaskId = "c2" })
        });

        var snapshot = Build(s, isExecuting: true, status: ExecutionStatus.Running);

        snapshot.ActionLine.Should().Contain("2 个子智能体");
        snapshot.StateKind.Should().Be(SummaryState.Running);
    }

    [Fact]
    public void Build_WithRunningTool_ShouldReportToolName()
    {
        var s = Session(new SessionMessage
        {
            Id = "a1",
            Role = MessageRole.Assistant,
            Content = "",
            ToolCalls = ImmutableList.Create(new SessionToolCall { Id = "t1", Name = "bash", Status = "running" })
        });

        Build(s, isExecuting: true, status: ExecutionStatus.Running).ActionLine.Should().Contain("bash");
    }

    [Theory]
    [InlineData(ExecutionStatus.Failed, "conference-tile--failed")]
    [InlineData(ExecutionStatus.Cancelled, "conference-tile--cancelled")]
    [InlineData(ExecutionStatus.Completed, "conference-tile--done")]
    public void Build_Status_ShouldMapStatusBarClass(ExecutionStatus status, string expected)
        => Build(Session(), status: status).StatusBarClass.Should().Be(expected);

    [Fact]
    public void Build_WithParent_ShouldProduceSourceLine()
        => Build(Session(), parentTitle: "父会话", relation: SessionRelation.Child, isAnchor: false)
            .SourceLine.Should().Contain("父会话");
}
