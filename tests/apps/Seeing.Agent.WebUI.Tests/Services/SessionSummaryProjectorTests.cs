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

    /// <summary>
    /// 回归：父标题尚未加载（内存未命中）时不得断言「来源已删除」——
    /// 曾因组关系丢失 → ParentSessionId 为 null → 误报「来源已删除」。
    /// </summary>
    [Fact]
    public void Build_SourceLinkKnownButTitleMissing_ShouldNotClaimDeleted()
    {
        var snapshot = SessionSummaryProjector.Build(
            Session(), isExecuting: false, status: ExecutionStatus.Completed,
            parentTitle: null, relation: SessionRelation.Child, isAnchor: false,
            successorTitle: null, sourceLinkKnown: true);

        snapshot.SourceLine.Should().NotContain("已删除");
        snapshot.SourceLine.Should().StartWith("来源");
    }

    /// <summary>回归：来源链接本身缺失（父 ID 为 null）才是「来源已删除」。</summary>
    [Fact]
    public void Build_SourceLinkMissing_ShouldReportDeleted()
    {
        var snapshot = SessionSummaryProjector.Build(
            Session(), isExecuting: false, status: ExecutionStatus.Completed,
            parentTitle: null, relation: SessionRelation.Child, isAnchor: false,
            successorTitle: null, sourceLinkKnown: false);

        snapshot.SourceLine.Should().Be("来源已删除");
    }

    /// <summary>非锚点但无来源关系（None）时不渲染来源行，也不应报已删除。</summary>
    [Fact]
    public void Build_NonAnchorWithoutRelation_ShouldHaveEmptySourceLine()
    {
        var snapshot = SessionSummaryProjector.Build(
            Session(), isExecuting: false, status: ExecutionStatus.Completed,
            parentTitle: null, relation: SessionRelation.None, isAnchor: false,
            successorTitle: null, sourceLinkKnown: false);

        snapshot.SourceLine.Should().BeEmpty();
    }
}
