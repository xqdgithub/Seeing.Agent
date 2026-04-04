using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// 聊天角色常量
    /// </summary>
    public static class ChatRole
    {
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Tool = "tool";
    }

    /// <summary>
    /// 聊天消息
    /// </summary>
    public class ChatMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// 与 <see cref="Seeing.Agent.Llm.ChatMessage.Parts"/> 相同语义：多模态段；非空时 Agent/宿主应优先使用本集合构造 LLM 消息。
        /// </summary>
        public List<ChatContentPart>? Parts { get; set; }

        public string? ReasoningContent { get; set; }
        public List<ToolCall>? ToolCalls { get; set; }
        public List<ToolCallResult>? ToolCallResults { get; set; }
        
        /// <summary>
        /// 是否是思考消息
        /// </summary>
        public bool IsThought => !string.IsNullOrEmpty(ReasoningContent);

        /// <summary>与 <see cref="Seeing.Agent.Llm.ChatMessage.GetEffectiveParts"/> 行为一致。</summary>
        public IReadOnlyList<ChatContentPart> GetEffectiveParts()
        {
            if (Parts is { Count: > 0 })
                return Parts;
            if (!string.IsNullOrEmpty(Content))
                return new[] { ChatContentPart.CreateText(Content) };
            return Array.Empty<ChatContentPart>();
        }
        
        public override string ToString()
        {
            var result = $"{Role}:\n{Content}";
            if (ToolCalls?.Count > 0)
            {
                result += $"\nToolCalls: {System.Text.Json.JsonSerializer.Serialize(ToolCalls)}";
            }
            if (ToolCallResults?.Count > 0)
            {
                result += $"\nToolCallResults: {System.Text.Json.JsonSerializer.Serialize(ToolCallResults)}";
            }
            return result;
        }
    }

    /// <summary>
    /// 工具调用
    /// </summary>
    public class ToolCall
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public object? Arguments { get; set; }
    }

    /// <summary>
    /// 工具调用结果
    /// </summary>
    public class ToolCallResult
    {
        public bool Success { get; set; }
        public ToolCall? ToolCall { get; set; }
        public object? CallResult { get; set; }
        public object? Message { get; set; }
        
        /// <summary>
        /// 转换为简化结果
        /// </summary>
        public SimpleToolCallResult ToSimpleResult()
        {
            return new SimpleToolCallResult
            {
                Success = Success,
                ToolName = ToolCall?.Name ?? string.Empty,
                CallResult = CallResult,
                Message = Message
            };
        }
    }

    /// <summary>
    /// 简化的工具调用结果
    /// </summary>
    public class SimpleToolCallResult
    {
        public bool Success { get; set; }
        public string ToolName { get; set; } = string.Empty;
        public object? CallResult { get; set; }
        public object? Message { get; set; }
    }
}