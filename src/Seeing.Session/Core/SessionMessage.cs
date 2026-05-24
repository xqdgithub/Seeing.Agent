using System.Text.Json.Serialization;

namespace Seeing.Session.Core
{
    /// <summary>
    /// 消息角色常量
    /// </summary>
    public static class MessageRole
    {
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Tool = "tool";
    }

    /// <summary>
    /// 内容段类型常量
    /// </summary>
    public static class ContentPartType
    {
        public const string Text = "text";
        public const string Image = "image";
        public const string File = "file";
        public const string Audio = "audio";
    }

    /// <summary>
    /// Session 消息内容段 - 支持多模态（文本、图片、文件、音频等）
    /// </summary>
    public class SessionContentPart
    {
        /// <summary>段类型：text、image、file、audio</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = ContentPartType.Text;

        /// <summary>文本内容（text 类型使用）</summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>图片/文件 URL（可公开访问的 https URL 或 data: URL）</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>Base64 编码数据（不含 data: 前缀）</summary>
        [JsonPropertyName("data_base64")]
        public string? DataBase64 { get; set; }

        /// <summary>MIME 类型，如 image/png、application/pdf、audio/wav</summary>
        [JsonPropertyName("mime_type")]
        public string? MimeType { get; set; }

        /// <summary>原始文件名（文件段建议填写）</summary>
        [JsonPropertyName("file_name")]
        public string? FileName { get; set; }

        /// <summary>已上传文件的 Provider ID（如 OpenAI file-id）</summary>
        [JsonPropertyName("file_id")]
        public string? FileId { get; set; }

        /// <summary>图片细节级别：auto、low、high（OpenAI 参数）</summary>
        [JsonPropertyName("image_detail")]
        public string? ImageDetail { get; set; }

        /// <summary>创建文本段</summary>
        public static SessionContentPart CreateText(string text) =>
            new() { Type = ContentPartType.Text, Text = text };

        /// <summary>创建图片段（URL）</summary>
        public static SessionContentPart CreateImageFromUrl(string url, string? imageDetail = null) =>
            new() { Type = ContentPartType.Image, Url = url, ImageDetail = imageDetail };

        /// <summary>创建图片段（Base64）</summary>
        public static SessionContentPart CreateImageFromBase64(string base64, string mimeType, string? imageDetail = null) =>
            new() { Type = ContentPartType.Image, DataBase64 = base64, MimeType = mimeType, ImageDetail = imageDetail };

        /// <summary>创建文件段（Base64）</summary>
        public static SessionContentPart CreateFileFromBase64(string base64, string mimeType, string? fileName = null) =>
            new() { Type = ContentPartType.File, DataBase64 = base64, MimeType = mimeType, FileName = fileName };

        /// <summary>创建文件段（Provider ID）</summary>
        public static SessionContentPart CreateFileFromProviderId(string fileId, string? fileName = null) =>
            new() { Type = ContentPartType.File, FileId = fileId, FileName = fileName };

        /// <summary>创建音频段（Base64）</summary>
        public static SessionContentPart CreateAudioFromBase64(string base64, string mimeType) =>
            new() { Type = ContentPartType.Audio, DataBase64 = base64, MimeType = mimeType };
    }

    /// <summary>
    /// Session 工具调用 - Assistant 发起的工具调用请求
    /// </summary>
    public class SessionToolCall
    {
        /// <summary>工具调用 ID（由 LLM 生成，用于关联工具响应）</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>工具类型（固定为 "function"）</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        /// <summary>函数名称</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>函数参数（JSON 字符串）</summary>
        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = "{}";

        /// <summary>工具调用结果（可选，执行后填充）</summary>
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        /// <summary>工具调用状态：pending、running、completed、error</summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "pending";

        /// <summary>错误信息（如果调用失败）</summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Session Token 使用统计
    /// </summary>
    public class SessionTokenUsage
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
    /// Session 消息 - 完整的消息模型，支持多模态、工具调用、推理内容等场景
    /// </summary>
    public class SessionMessage
    {
        /// <summary>消息 ID</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>消息角色：system、user、assistant、tool</summary>
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        /// <summary>消息文本内容（简化的文本消息使用）</summary>
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        /// <summary>多模态内容段列表（图片、文件、音频等）</summary>
        [JsonPropertyName("parts")]
        public List<SessionContentPart>? Parts { get; set; }

        /// <summary>推理/思考内容（用于 DeepSeek-R1 等思考模型）</summary>
        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }

        /// <summary>工具调用列表（Assistant 消息发起的工具调用请求）</summary>
        [JsonPropertyName("tool_calls")]
        public List<SessionToolCall>? ToolCalls { get; set; }

        /// <summary>工具调用 ID（Tool 消息中标识对应的工具调用）</summary>
        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }

        /// <summary>工具名称（Tool 消息中的工具名）</summary>
        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; }

        /// <summary>创建时间</summary>
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>Token 使用统计（可选）</summary>
        [JsonPropertyName("token_usage")]
        public SessionTokenUsage? TokenUsage { get; set; }

        /// <summary>引用的消息 ID（回复特定消息时使用）</summary>
        [JsonPropertyName("reply_to")]
        public string? ReplyTo { get; set; }

        /// <summary>额外元数据</summary>
        [JsonPropertyName("metadata")]
        public Dictionary<string, object>? Metadata { get; set; }

        /// <summary>是否是思考消息（包含推理内容）</summary>
        [JsonIgnore]
        public bool IsThought => !string.IsNullOrEmpty(ReasoningContent);

        /// <summary>是否包含工具调用</summary>
        [JsonIgnore]
        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;

        /// <summary>是否是多模态消息</summary>
        [JsonIgnore]
        public bool IsMultimodal => Parts != null && Parts.Count > 0;

        /// <summary>获取有效内容段</summary>
        public IReadOnlyList<SessionContentPart> GetEffectiveParts()
        {
            if (Parts is { Count: > 0 })
                return Parts;
            if (!string.IsNullOrEmpty(Content))
                return new[] { SessionContentPart.CreateText(Content) };
            return Array.Empty<SessionContentPart>();
        }

        /// <summary>创建系统消息</summary>
        public static SessionMessage SystemMessage(string content)
        {
            return new SessionMessage
            {
                Role = MessageRole.System,
                Content = content
            };
        }

        /// <summary>创建用户消息（文本）</summary>
        public static SessionMessage UserMessage(string content)
        {
            return new SessionMessage
            {
                Role = MessageRole.User,
                Content = content
            };
        }

        /// <summary>创建用户消息（多模态）</summary>
        public static SessionMessage UserMessageWithParts(List<SessionContentPart> parts)
        {
            return new SessionMessage
            {
                Role = MessageRole.User,
                Parts = parts
            };
        }

        /// <summary>创建用户消息（带图片）</summary>
        public static SessionMessage UserMessageWithImage(string text, string imageUrl)
        {
            return new SessionMessage
            {
                Role = MessageRole.User,
                Parts = new List<SessionContentPart>
                {
                    SessionContentPart.CreateText(text),
                    SessionContentPart.CreateImageFromUrl(imageUrl)
                }
            };
        }

        /// <summary>创建助手消息（文本）</summary>
        public static SessionMessage AssistantMessage(string content)
        {
            return new SessionMessage
            {
                Role = MessageRole.Assistant,
                Content = content
            };
        }

        /// <summary>创建助手消息（带推理内容）</summary>
        public static SessionMessage AssistantMessageWithReasoning(string content, string reasoning)
        {
            return new SessionMessage
            {
                Role = MessageRole.Assistant,
                Content = content,
                ReasoningContent = reasoning
            };
        }

        /// <summary>创建助手消息（带工具调用）</summary>
        public static SessionMessage AssistantMessageWithToolCalls(List<SessionToolCall> toolCalls, string? content = null)
        {
            return new SessionMessage
            {
                Role = MessageRole.Assistant,
                Content = content ?? string.Empty,
                ToolCalls = toolCalls
            };
        }

        /// <summary>创建工具响应消息</summary>
        public static SessionMessage ToolMessage(string content, string toolCallId, string? toolName = null)
        {
            return new SessionMessage
            {
                Role = MessageRole.Tool,
                Content = content,
                ToolCallId = toolCallId,
                ToolName = toolName
            };
        }

        /// <summary>创建带 Token 统计的消息</summary>
        public SessionMessage WithTokenUsage(int inputTokens, int outputTokens)
        {
            TokenUsage = new SessionTokenUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens
            };
            return this;
        }

        /// <summary>创建带元数据的消息</summary>
        public SessionMessage WithMetadata(string key, object value)
        {
            Metadata ??= new Dictionary<string, object>();
            Metadata[key] = value;
            return this;
        }

        public override string ToString()
        {
            var result = $"{Role}:\n{Content}";
            if (!string.IsNullOrEmpty(ReasoningContent))
            {
                result += $"\n[Reasoning: {ReasoningContent.Length} chars]";
            }
            if (ToolCalls?.Count > 0)
            {
                result += $"\n[ToolCalls: {ToolCalls.Count}]";
            }
            if (Parts?.Count > 0)
            {
                result += $"\n[Parts: {Parts.Count}]";
            }
            if (!string.IsNullOrEmpty(ToolCallId))
            {
                result += $"\n[ToolCallId: {ToolCallId}]";
            }
            return result;
        }
    }
}