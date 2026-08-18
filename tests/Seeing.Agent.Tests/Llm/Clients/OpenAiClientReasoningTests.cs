using FluentAssertions;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

/// <summary>
/// 验证 OpenAiClient 的 thinking/reasoning 相关数据模型逻辑。
/// 这些测试不发起真实网络请求，仅验证映射和字段行为。
/// </summary>
public class OpenAiClientReasoningTests
{
    [Fact]
    public void TokenUsage_ReasoningTokens_默认值为0()
    {
        var usage = new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 500
        };

        usage.ReasoningTokens.Should().Be(0);
    }

    [Fact]
    public void TokenUsage_ReasoningTokens_不计入TotalTokens()
    {
        var usage = new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 500,
            ReasoningTokens = 2000
        };

        usage.TotalTokens.Should().Be(600);  // 100 + 500，reasoning 不计入
        usage.ReasoningTokens.Should().Be(2000);
    }

    [Fact]
    public void ChatMessage_ReasoningContent为null_IsThought为false()
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant, Content = "hello" };

        msg.IsThought.Should().BeFalse();
    }

    [Fact]
    public void ChatMessage_ReasoningContent非空_IsThought为true()
    {
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "最终回答",
            ReasoningContent = "让我思考一下..."
        };

        msg.IsThought.Should().BeTrue();
        msg.ReasoningContent.Should().Be("让我思考一下...");
    }

    [Fact]
    public void StreamUpdate_默认_ReasoningDelta为null()
    {
        var update = new StreamUpdate { ContentDelta = "text" };

        update.ReasoningDelta.Should().BeNull();
        update.ContentDelta.Should().Be("text");
    }

    [Fact]
    public void StreamUpdate_设置ReasoningDelta_应正确保留()
    {
        var update = new StreamUpdate
        {
            Id = "resp-1",
            ReasoningDelta = "正在分析...",
            IsComplete = false
        };

        update.ReasoningDelta.Should().Be("正在分析...");
        update.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ChatMessage_ToolCalls存在_Reasoning为非空时_IsThought仍为true()
    {
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "",
            ReasoningContent = "需要调工具...",
            ToolCalls = new()
            {
                new ToolCall
                {
                    Id = "call_1",
                    Type = "function",
                    Function = new FunctionCall { Name = "search", Arguments = "{}" }
                }
            }
        };

        msg.IsThought.Should().BeTrue();
        msg.ToolCalls.Should().HaveCount(1);
    }
}
