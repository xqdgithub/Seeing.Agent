using System.Text.Json.Serialization;

namespace Seeing.Agent.Core.Questions;

/// <summary>
/// 问题选项
/// </summary>
public class QuestionOption
{
    /// <summary>选项显示文本（简洁，1-5 个字）</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>选项说明</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>
/// 问题定义
/// </summary>
public class Question
{
    /// <summary>问题唯一标识</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>简短标题（最多 30 字符）</summary>
    [JsonPropertyName("header")]
    public string Header { get; set; } = string.Empty;

    /// <summary>完整问题文本</summary>
    [JsonPropertyName("question")]
    public string QuestionText { get; set; } = string.Empty;

    /// <summary>可选答案列表</summary>
    [JsonPropertyName("options")]
    public List<QuestionOption> Options { get; set; } = new();

    /// <summary>是否允许选择多个选项</summary>
    [JsonPropertyName("multiple")]
    public bool AllowMultiple { get; set; } = false;

    /// <summary>是否允许用户输入自定义答案</summary>
    [JsonPropertyName("custom")]
    public bool AllowCustom { get; set; } = true;
}

/// <summary>
/// 问题请求
/// </summary>
public class QuestionRequest
{
    /// <summary>请求 ID</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>会话 ID</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>问题列表</summary>
    [JsonPropertyName("questions")]
    public List<Question> Questions { get; set; } = new();

    /// <summary>关联的工具调用信息（可选）</summary>
    [JsonPropertyName("tool")]
    public ToolReference? Tool { get; set; }
}

/// <summary>
/// 工具引用
/// </summary>
public class ToolReference
{
    /// <summary>消息 ID</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>工具调用 ID</summary>
    [JsonPropertyName("callId")]
    public string CallId { get; set; } = string.Empty;
}

/// <summary>
/// 问题回答
/// </summary>
public class QuestionAnswer
{
    /// <summary>问题 ID</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = string.Empty;

    /// <summary>选中的选项标签列表</summary>
    [JsonPropertyName("selectedLabels")]
    public List<string> SelectedLabels { get; set; } = new();

    /// <summary>自定义回答文本</summary>
    [JsonPropertyName("customAnswer")]
    public string? CustomAnswer { get; set; }
}

/// <summary>
/// 问题结果（包含所有问题的回答）
/// </summary>
public class QuestionResult
{
    /// <summary>请求 ID</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>回答列表</summary>
    [JsonPropertyName("answers")]
    public List<QuestionAnswer> Answers { get; set; } = new();
}