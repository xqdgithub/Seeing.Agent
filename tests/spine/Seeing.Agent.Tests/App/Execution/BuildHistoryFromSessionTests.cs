using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

public class BuildHistoryFromSessionTests
{
    [Theory]
    [InlineData("错误: 网络连接错误")]
    [InlineData("对话已取消: user")]
    [InlineData("压缩失败: 第一次错误")]
    [InlineData("⚠️ 执行已取消")]
    [InlineData("❌ 执行出错: boom")]
    public void BuildHistory_SkipsLegacyRuntimeSystemMessages(string content)
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.SystemMessage(content));

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
        history[0].Content.Should().Be("hi");
    }

    [Fact]
    public void BuildHistory_SkipsTransientFlaggedSystemMessage()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var transient = SessionMessage.SystemMessage("anything");
        transient.Metadata = new Dictionary<string, object> { ["transient"] = true };
        session.AddMessage(transient);

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
    }

    [Fact]
    public void BuildHistory_KeepsNormalSystemReminder()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.SystemMessage("<system-reminder>todo</system-reminder>"));

        var history = ExecutionJobService.BuildHistoryFromSession(session);

        history.Should().HaveCount(2);
    }
}
