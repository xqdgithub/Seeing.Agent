using FluentAssertions;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using System.Collections.Immutable;
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

        var history = ExecutionRunner.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
        history[0].Content.Should().Be("hi");
    }

    [Fact]
    public void BuildHistory_SkipsTransientFlaggedSystemMessage()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var transient = SessionMessage.SystemMessage("anything");
        transient.Metadata = new Dictionary<string, object> { ["transient"] = true }.ToImmutableDictionary();
        session.AddMessage(transient);

        var history = ExecutionRunner.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
    }

    [Fact]
    public void BuildHistory_KeepsNormalSystemReminder()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        session.AddMessage(SessionMessage.SystemMessage("<system-reminder>todo</system-reminder>"));

        var history = ExecutionRunner.BuildHistoryFromSession(session);

        history.Should().HaveCount(2);
    }

    [Fact]
    public void BuildHistory_SkipsNonePresentationInboxMessage()
    {
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var banner = SessionMessage.SystemMessage("已取消");
        banner.Metadata = new Dictionary<string, object> { ["inbox.presentation"] = "none" }
            .ToImmutableDictionary();
        session.AddMessage(banner);

        var history = ExecutionRunner.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
        history[0].Content.Should().Be("hi");
    }

    [Fact]
    public void BuildHistory_SkipsNonePresentationInboxMessage_WhenMetadataIsJsonElement()
    {
        // 落盘再加载后 Metadata 值可能为 JsonElement
        var session = SessionData.Create();
        session.AddMessage(SessionMessage.UserMessage("hi"));
        var banner = SessionMessage.SystemMessage("已取消");
        banner.Metadata = new Dictionary<string, object>
        {
            ["inbox.presentation"] = System.Text.Json.JsonSerializer.SerializeToElement("none")
        }.ToImmutableDictionary();
        session.AddMessage(banner);

        var history = ExecutionRunner.BuildHistoryFromSession(session);

        history.Should().HaveCount(1);
        history[0].Content.Should().Be("hi");
    }
}
