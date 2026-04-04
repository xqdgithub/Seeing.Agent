using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm;

/// <summary>
/// 聊天角色
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
    /// <summary>消息角色</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>消息内容</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 多模态内容段（文本、图片、文件、音频等）。非空时由 LLM 客户端优先映射到 Provider；
    /// 纯文本场景可只使用 <see cref="Content"/>。
    /// 若同时设置 <see cref="Content"/> 与本集合，以 <see cref="Parts"/> 为准（<see cref="Content"/> 不会自动合并）。
    /// </summary>
    [JsonPropertyName("parts")]
    public List<ChatContentPart>? Parts { get; set; }

    /// <summary>推理内容（用于思考模型）</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    /// <summary>工具调用列表</summary>
    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; set; }

    /// <summary>工具调用结果</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    /// <summary>
    /// 供下游使用的有效内容段：<see cref="Parts"/> 非空时返回之，否则将 <see cref="Content"/> 视为单段文本。
    /// </summary>
    public IReadOnlyList<ChatContentPart> GetEffectiveParts()
    {
        if (Parts is { Count: > 0 })
            return Parts;
        if (!string.IsNullOrEmpty(Content))
            return new[] { ChatContentPart.CreateText(Content) };
        return Array.Empty<ChatContentPart>();
    }
}

/// <summary>
/// 工具调用
/// </summary>
public class ToolCall
{
    /// <summary>工具调用 ID</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>工具类型</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>函数调用</summary>
    [JsonPropertyName("function")]
    public FunctionCall? Function { get; set; }
}

/// <summary>
/// 函数调用
/// </summary>
public class FunctionCall
{
    /// <summary>函数名称</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>函数参数（JSON 字符串）</summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

/// <summary>
/// 工具定义
/// </summary>
public class ToolDefinition
{
    /// <summary>工具类型</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    /// <summary>函数定义</summary>
    [JsonPropertyName("function")]
    public FunctionDefinition? Function { get; set; }
}

/// <summary>
/// 函数定义
/// </summary>
public class FunctionDefinition
{
    /// <summary>函数名称</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>函数描述</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>参数 Schema</summary>
    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

/// <summary>
/// Token 使用统计
/// </summary>
public class TokenUsage
{
    /// <summary>输入 Token 数</summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    /// <summary>输出 Token 数</summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    /// <summary>总 Token 数</summary>
    [JsonIgnore]
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// 聊天补全请求
/// </summary>
public class ChatRequest
{
    /// <summary>模型 ID</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>消息列表</summary>
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>系统提示词</summary>
    [JsonIgnore]
    public string? SystemPrompt { get; set; }

    /// <summary>工具定义列表</summary>
    [JsonPropertyName("tools")]
    public List<ToolDefinition>? Tools { get; set; }

    /// <summary>温度参数</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>Top-P 参数</summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    /// <summary>最大输出 Token 数</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>是否流式输出</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

/// <summary>
/// 聊天补全响应
/// </summary>
public class ChatResponse
{
    /// <summary>响应 ID</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>模型 ID</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>生成的消息</summary>
    [JsonIgnore]
    public ChatMessage Message { get; set; } = new();

    /// <summary>完成原因</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    /// <summary>Token 使用统计</summary>
    [JsonPropertyName("usage")]
    public TokenUsage? Usage { get; set; }
}

/// <summary>
/// 流式更新
/// </summary>
public class StreamUpdate
{
    /// <summary>响应 ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>内容增量</summary>
    public string? ContentDelta { get; set; }

    /// <summary>推理内容增量</summary>
    public string? ReasoningDelta { get; set; }

    /// <summary>工具调用增量</summary>
    public List<ToolCall>? ToolCallDeltas { get; set; }

    /// <summary>是否完成</summary>
    public bool IsComplete { get; set; }

    /// <summary>Token 使用统计</summary>
    public TokenUsage? Usage { get; set; }
}