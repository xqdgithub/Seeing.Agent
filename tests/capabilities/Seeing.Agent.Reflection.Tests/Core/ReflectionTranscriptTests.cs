using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>
/// 复盘 transcript 的组装规则：压平、两级截断、多模态摘要。
/// <para>
/// 这些性质直接决定「模型看到的复盘输入」：漏掉工具调用会让复盘只看结论不看过程；
/// 漏掉多模态段会让「模型看过截图」这个事实在复盘里完全消失；
/// 把 base64 写进来则会瞬间吃满字数额度、把需要分析的对话挤掉。
/// </para>
/// </summary>
public sealed class ReflectionTranscriptTests
{
    [Fact]
    public void Transcript_Should_Flatten_Tool_Calls_With_Arguments_And_Result()
    {
        var message = SessionMessage.AssistantMessageWithToolCalls(
            [new SessionToolCall { Name = "bash", Arguments = """{"command":"dotnet build"}""", Result = "构建成功" }],
            content: "我先编译。");

        var text = ReflectionExecutor.BuildTranscript([message], maxToolResultChars: 100, maxTranscriptChars: 10_000);

        text.Should().Contain("[assistant]");
        text.Should().Contain("先编译");
        text.Should().Contain("调用 bash");
        text.Should().Contain("dotnet build");
        text.Should().Contain("构建成功", "工具结果就是「这一步实际发生了什么」的事实");
    }

    [Fact]
    public void Transcript_Should_Label_Tool_Role_With_Tool_Name()
    {
        var message = SessionMessage.ToolMessage("输出内容", "call-1", "grep");

        var text = ReflectionExecutor.BuildTranscript([message], maxToolResultChars: 100, maxTranscriptChars: 10_000);

        text.Should().Contain("[tool:grep]");
    }

    [Fact]
    public void Transcript_Should_Describe_Multimodal_Parts_Without_Embedding_Payload()
    {
        // 用户贴图 + 一段文本：复盘必须知道「有一张图」，但不能把 base64 灌进提示词。
        var message = SessionMessage.UserMessageWithParts(
        [
            SessionContentPart.CreateText("看这张截图"),
            new SessionContentPart
            {
                Type = ContentPartType.Image,
                DataBase64 = new string('A', 5000),
                MimeType = "image/png",
                FileName = "screenshot.png"
            }
        ]);

        var text = ReflectionExecutor.BuildTranscript([message], maxToolResultChars: 100, maxTranscriptChars: 10_000);

        text.Should().Contain("看这张截图");
        text.Should().Contain("图片:screenshot.png", "复核需要知道模型看过图片");
        text.Should().NotContain("AAAA", "base64 载荷绝不能进 transcript");
    }

    [Fact]
    public void Transcript_Should_Describe_File_Part_As_File()
    {
        var message = SessionMessage.UserMessageWithParts(
        [
            new SessionContentPart
            {
                Type = ContentPartType.File,
                MimeType = "application/pdf",
                FileName = "spec.pdf"
            }
        ]);

        var text = ReflectionExecutor.BuildTranscript([message], maxToolResultChars: 100, maxTranscriptChars: 10_000);

        text.Should().Contain("文件:spec.pdf");
    }

    [Fact]
    public void Transcript_Should_Not_Emit_Attachment_Line_When_Only_Text_Parts()
    {
        var message = SessionMessage.UserMessageWithParts([SessionContentPart.CreateText("纯文本")]);

        var text = ReflectionExecutor.BuildTranscript([message], maxToolResultChars: 100, maxTranscriptChars: 10_000);

        text.Should().NotContain("[附件]", "纯文本消息不该凭空多出一行空附件说明");
    }

    [Fact]
    public void Transcript_Should_Truncate_Each_Tool_Result()
    {
        var message = SessionMessage.ToolMessage(new string('x', 500), "call-1", "read");

        var text = ReflectionExecutor.BuildTranscript([message], maxToolResultChars: 20, maxTranscriptChars: 10_000);

        text.Should().Contain("已截断");
        text.Length.Should().BeLessThan(200, "单条工具结果按上限截断，避免噪声淹没骨架");
    }

    [Fact]
    public void Transcript_Should_Keep_Tail_When_Over_Total_Limit()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("这是开头，应该被截掉"),
            SessionMessage.UserMessage("这是结尾，必须保留")
        };

        var text = ReflectionExecutor.BuildTranscript(messages, maxToolResultChars: 100, maxTranscriptChars: 30);

        text.Should().Contain("前文已截断");
        text.Should().Contain("结尾", "复盘更关心任务如何收尾");
    }

    [Fact]
    public void Transcript_Should_Be_Empty_When_Total_Limit_Is_Zero()
    {
        var messages = new List<SessionMessage> { SessionMessage.UserMessage("内容") };

        ReflectionExecutor.BuildTranscript(messages, maxToolResultChars: 100, maxTranscriptChars: 0)
            .Should().BeEmpty();
    }
}
