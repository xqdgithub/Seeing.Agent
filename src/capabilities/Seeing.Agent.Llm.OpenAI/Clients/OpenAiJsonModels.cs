using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.OpenAI.Clients;

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

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatCompletionThinking? Thinking { get; set; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }

    /// <summary>流式选项（stream=true 时声明 include_usage 以获得 trailing usage chunk）</summary>
    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatCompletionStreamOptions? StreamOptions { get; set; }
}

/// <summary>Chat Completions 流式选项。</summary>
internal class ChatCompletionStreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; set; }
}

internal class ChatCompletionThinking
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "enabled";
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
    /// <summary>item 类型：null=普通消息；"function_call" / "function_call_output"=工具调用往返</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>工具调用关联 ID（function_call / function_call_output）</summary>
    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; set; }

    /// <summary>function_call 的函数名</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>function_call 的参数 JSON 字符串</summary>
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }

    /// <summary>
    /// function_call_output 的结果内容。
    /// <para>
    /// 可能是字符串，也可能是内容对象数组（如 <c>[{type:"input_image", image_url:"..."}]</c>）。
    /// Responses API 官方支持后者用于「函数返回图片/文件」。
    /// </para>
    /// </summary>
    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Output { get; set; }
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

#region Embeddings 请求/响应

/// <summary>Embeddings API 请求体（POST {baseUrl}/embeddings，spec §7.8）。</summary>
internal class EmbeddingApiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>输入文本数组；服务端以 index 回显与输入的对应关系。</summary>
    [JsonPropertyName("input")]
    public List<string> Input { get; set; } = new();

    /// <summary>期望维度（仅 text-embedding-3-* 等支持该参数的模型生效）。</summary>
    [JsonPropertyName("dimensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dimensions { get; set; }

    /// <summary>编码格式（float / base64）。</summary>
    [JsonPropertyName("encoding_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingFormat { get; set; }
}

/// <summary>Embeddings API 响应体。</summary>
internal class EmbeddingApiResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// 向量条目。<b>index 可能乱序</b>（服务端并行批处理），客户端须按 Index 升序回排。
    /// </summary>
    [JsonPropertyName("data")]
    public List<EmbeddingApiItem>? Data { get; set; }

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }
}

/// <summary>Embeddings 单条向量条目。</summary>
internal class EmbeddingApiItem
{
    /// <summary>对应输入序号；乱序响应以此为准排序。</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("embedding")]
    public List<float>? Embedding { get; set; }
}

#endregion

#region Audio Speech 请求/响应

/// <summary>TTS 请求体（POST {baseUrl}/audio/speech，spec §7.10）。响应体是原始音频字节，无 JSON 模型。</summary>
internal class SpeechSynthesisApiRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>待合成文本（OpenAI 协议字段名为 input）。</summary>
    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("voice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("speed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Speed { get; set; }
}

#endregion

#region Audio Transcriptions 请求/响应

/// <summary>ASR 响应体（verbose_json；POST {baseUrl}/audio/transcriptions，spec §7.10）。</summary>
internal class TranscriptionApiResponse
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>细粒度段落；仅 verbose_json 提供，缺省为 null。</summary>
    [JsonPropertyName("segments")]
    public List<TranscriptionApiSegment>? Segments { get; set; }

    /// <summary>token 用量（gpt-4o-transcribe 系返回；whisper 系通常为空）。</summary>
    [JsonPropertyName("usage")]
    public OpenAiTranscriptionUsage? Usage { get; set; }
}

/// <summary>ASR verbose_json 段落条目。</summary>
internal class TranscriptionApiSegment
{
    /// <summary>
    /// 段落序号。
    /// </summary>
    /// <remarks>
    /// <para><c>verbose_json</c> 里这是<b>整数序号</b>（whisper-1 返回 <c>0</c>）。</para>
    /// <para>部分 OpenAI 兼容服务（如 SiliconFlow / XingChenASR）把它序列化成字符串
    /// <c>"0"</c>，属实现不规范；<see cref="JsonNumberHandling.AllowReadingFromString"/>
    /// 让两种形状都能读进来，无需为此把类型放宽成 <c>object</c> 而丢掉契约表达力。</para>
    /// <para>注：OpenAI 的 <c>diarized_json</c> 用 <c>"seg_001"</c> 这类非数字字符串。
    /// 本客户端只请求 <c>verbose_json</c>，因此读不进来即说明误用了别的响应格式，
    /// 报错是正确的。</para>
    /// </remarks>
    [JsonPropertyName("id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Id { get; set; }

    /// <summary>起始秒。</summary>
    [JsonPropertyName("start")]
    public double Start { get; set; }

    /// <summary>结束秒。</summary>
    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>ASR token 用量。</summary>
internal class OpenAiTranscriptionUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>ASR 流式 SSE 增量条目（stream=true：delta 文本，结束行带 finish_reason）。</summary>
internal class TranscriptionStreamDelta
{
    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

#endregion
