using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI.Clients;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// Chat Completions 的工具图片出站形状。
/// <para>
/// <b>协议约束（必须守住）</b>：<c>role:"tool"</c> 只允许 text content；
/// 且 <c>assistant(tool_calls)</c> 之后所有 tool 消息必须**连续**——
/// <c>tool, tool, user</c> ✅ / <c>tool, user, tool</c> ❌ 400（实测确认）。
/// </para>
/// </summary>
public class OpenAiChatClientToolImageShapeTests
{
    private static OpenAiChatClient Create()
    {
        var config = new ProviderConfig
        {
            Id = "test",
            Type = ProviderTypes.OpenAi,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "k"
        };

        return new OpenAiChatClient(
            config,
            new HttpClient { BaseAddress = new Uri("https://api.openai.com/v1/") },
            NullLogger<OpenAiChatClient>.Instance);
    }

    private static ChatMessage ToolMessage(string callId, string text, params string[] base64Images)
        => new()
        {
            Role = ChatRole.Tool,
            ToolCallId = callId,
            Content = text,
            Parts = base64Images.Length == 0
                ? null
                : base64Images.Select(b => ChatContentPart.CreateImageFromBase64(b, "image/png")).ToList()
        };

    [Fact]
    public void NoImages_ShouldProduceOnlyToolMessages_Unchanged()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages =
            [
                ToolMessage("c1", "结果一"),
                ToolMessage("c2", "结果二")
            ]
        };

        var result = Create().BuildMessages(request);

        result.Select(m => m.Role).Should().Equal("tool", "tool");
        result.Should().OnlyContain(m => m.Content is string);
    }

    [Fact]
    public void TwoToolsWithImages_ShouldEmitBothToolsThenOneAggregatedUser()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages =
            [
                ToolMessage("c1", "截图一", "AAA"),
                ToolMessage("c2", "截图二", "BBB")
            ]
        };

        var result = Create().BuildMessages(request);

        // 关键：tool 必须连续，图片聚合成**一条** user（不能 tool,user,tool）
        result.Select(m => m.Role).Should().Equal("tool", "tool", "user");

        var images = result[2].Content.Should().BeAssignableTo<IEnumerable<ChatCompletionContentPart>>().Subject.ToList();
        images.Should().HaveCount(2);
        images.Should().OnlyContain(p => p.Type == "image_url");
    }

    [Fact]
    public void ToolImagesFollowedByUserMessage_ShouldFlushBeforeThatUser()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages =
            [
                ToolMessage("c1", "截图", "AAA"),
                new ChatMessage { Role = ChatRole.User, Content = "继续" }
            ]
        };

        var result = Create().BuildMessages(request);

        result.Select(m => m.Role).Should().Equal("tool", "user", "user");
        // 图片消息必须在后续用户消息之前（顺序即语义）
        result[1].Content.Should().BeAssignableTo<IEnumerable<ChatCompletionContentPart>>();
        result[2].Content.Should().Be("继续");
    }

    [Fact]
    public void SingleToolWithImage_ShouldStillAggregateIntoUser()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages = [ToolMessage("c1", "截图", "AAA")]
        };

        var result = Create().BuildMessages(request);

        result.Select(m => m.Role).Should().Equal("tool", "user");
        result[0].Content.Should().Be("截图");
        result[0].ToolCallId.Should().Be("c1");
    }

    [Fact]
    public void ToolText_ShouldNotBeDuplicatedIntoUserMessage()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages = [ToolMessage("c1", "很长的工具输出", "AAA")]
        };

        var result = Create().BuildMessages(request);

        var images = result[1].Content.Should().BeAssignableTo<IEnumerable<ChatCompletionContentPart>>().Subject.ToList();
        // user 消息只放图片，不重复工具文本（重复会浪费 token）
        images.Should().OnlyContain(p => p.Type == "image_url");
    }

    [Fact]
    public void ImageUrl_ShouldBeDataUrl()
    {
        var request = new ChatRequest
        {
            Model = "m",
            Messages = [ToolMessage("c1", "截图", "QUJD")]
        };

        var result = Create().BuildMessages(request);

        var images = result[1].Content.Should().BeAssignableTo<IEnumerable<ChatCompletionContentPart>>().Subject.ToList();
        images[0].ImageUrl!.Url.Should().Be("data:image/png;base64,QUJD");
    }
}
