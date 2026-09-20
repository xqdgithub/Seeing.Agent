using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Questions;

/// <summary>
/// 问题请求
/// </summary>
public class QuestionRequest
{
    /// <summary>请求 ID</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>会话 ID</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    /// <summary>问题列表</summary>
    [JsonPropertyName("questions")]
    public List<Question> Questions { get; set; } = new();

    /// <summary>关联的工具调用信息（可选）</summary>
    [JsonPropertyName("tool")]
    public ToolReference? Tool { get; set; }
}
