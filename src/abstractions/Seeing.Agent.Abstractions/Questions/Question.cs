using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Questions;

/// <summary>
/// 问题类型：单选 / 多选 / 文本
/// </summary>
public enum QuestionKind
{
    Single,
    Multiple,
    Text
}

/// <summary>
/// 问题选项
/// </summary>
public class QuestionOption
{
    /// <summary>选项显示文本（简洁，1-5 个字）</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

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
    public string Id { get; set; } = "";

    /// <summary>简短标题（最多 30 字符）</summary>
    [JsonPropertyName("header")]
    public string Header { get; set; } = "";

    /// <summary>完整问题文本</summary>
    [JsonPropertyName("question")]
    public string QuestionText { get; set; } = "";

    /// <summary>可选答案列表</summary>
    [JsonPropertyName("options")]
    public List<QuestionOption> Options { get; set; } = new();

    /// <summary>问题类型：单选 / 多选 / 文本</summary>
    [JsonPropertyName("kind")]
    public QuestionKind Kind { get; set; } = QuestionKind.Single;

    /// <summary>是否允许用户输入自定义答案（“其他”）</summary>
    [JsonPropertyName("custom")]
    public bool AllowCustom { get; set; } = true;

    /// <summary>是否必填</summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    /// <summary>默认选中的选项标签列表</summary>
    [JsonPropertyName("defaultSelectedLabels")]
    public List<string> DefaultSelectedLabels { get; set; } = new();

    /// <summary>默认自定义答案</summary>
    [JsonPropertyName("defaultCustomAnswer")]
    public string? DefaultCustomAnswer { get; set; }
}

/// <summary>
/// 工具引用
/// </summary>
public class ToolReference
{
    /// <summary>消息 ID</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = "";

    /// <summary>工具调用 ID</summary>
    [JsonPropertyName("callId")]
    public string CallId { get; set; } = "";
}
