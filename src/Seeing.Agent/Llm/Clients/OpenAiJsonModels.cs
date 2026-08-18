using Seeing.Agent.Abstractions.Llm;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.Clients;

#region Chat Completions 请求

internal class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<ChatCompletionMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatCompletionTool>? Tools { get; set; }
}

internal class ChatCompletionMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Content { get; set; } // string | null | List<ChatCompletionContentPart>

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }
}

internal class ChatCompletionContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatCompletionImageUrl? ImageUrl { get; set; }
}

internal class ChatCompletionImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

internal class ChatCompletionTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public ChatCompletionFunction? Function { get; set; }
}

internal class ChatCompletionFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

internal class ChatCompletionToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public ChatCompletionToolCallFunction? Function { get; set; }
}

internal class ChatCompletionToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

#endregion

#region Chat Completions 响应

internal class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatCompletionChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }
}

internal class ChatCompletionChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public ChatCompletionDelta? Delta { get; set; }

    [JsonPropertyName("message")]
    public ChatCompletionResponseMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

internal class ChatCompletionDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionChunkToolCall>? ToolCalls { get; set; }
}

internal class ChatCompletionChunkToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public ChatCompletionToolCallFunction? Function { get; set; }
}

internal class ChatCompletionResponseMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }
}

internal class ChatCompletionChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<ChatCompletionChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }
}

#endregion

#region Responses API 请求

internal class ResponsesRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public List<ResponsesInputItem> Input { get; set; } = new();

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesTool>? Tools { get; set; }
}

internal class ResponsesInputItem
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal class ResponsesTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

#endregion

#region Responses API 响应（SSE 事件）

internal class ResponsesStreamEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    // response.created / response.completed
    [JsonPropertyName("response")]
    public ResponsesResponseObj? Response { get; set; }

    // response.output_item.added / response.output_item.done
    [JsonPropertyName("output_index")]
    public int? OutputIndex { get; set; }

    [JsonPropertyName("item")]
    public ResponsesOutputItem? Item { get; set; }

    // response.content_part.added / response.content_part.done
    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    [JsonPropertyName("content_index")]
    public int? ContentIndex { get; set; }

    [JsonPropertyName("part")]
    public ResponsesContentPart? Part { get; set; }

    // response.output_text.delta / response.reasoning_summary_text.delta
    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    // response.output_text.done / response.reasoning_summary_text.done
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    // response.function_call_arguments.delta / .done
    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

internal class ResponsesResponseObj
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiResponsesUsage? Usage { get; set; }

    [JsonPropertyName("output")]
    public List<ResponsesOutputBlock>? Output { get; set; }
}

internal class OpenAiResponsesUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("output_tokens_details")]
    public OpenAiOutputTokensDetails? OutputTokensDetails { get; set; }
}

internal class ResponsesOutputBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("content")]
    public List<ResponsesContentPart>? Content { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("call_id")]
    public string? CallId { get; set; }
}

internal class ResponsesOutputItem
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal class ResponsesContentPart
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

#endregion

#region 共用 Usage 模型

internal class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("completion_tokens_details")]
    public OpenAiCompletionTokensDetails? CompletionTokensDetails { get; set; }
}

internal class OpenAiCompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }
}

internal class OpenAiOutputTokensDetails
{
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }
}

#endregion

#region List Models

internal class ListModelsResponse
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("data")]
    public List<ModelInfo>? Data { get; set; }
}

internal class ModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }
}

#endregion
