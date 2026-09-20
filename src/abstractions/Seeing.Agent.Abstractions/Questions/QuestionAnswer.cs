using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Questions;

/// <summary>
/// 问题回答
/// </summary>
public class QuestionAnswer
{
    /// <summary>问题 ID</summary>
    [JsonPropertyName("questionId")]
    public string QuestionId { get; set; } = "";

    /// <summary>选中的选项标签列表</summary>
    [JsonPropertyName("selectedLabels")]
    public List<string> SelectedLabels { get; set; } = new();

    /// <summary>自定义回答文本</summary>
    [JsonPropertyName("customAnswer")]
    public string? CustomAnswer { get; set; }
}
