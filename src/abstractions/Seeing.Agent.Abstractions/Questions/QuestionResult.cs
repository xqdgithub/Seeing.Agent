using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.Questions;

/// <summary>
/// 问题结果状态
/// </summary>
public enum QuestionResultStatus
{
    Completed,
    Cancelled,
    Timeout,
    Unavailable
}

/// <summary>
/// 问题结果（包含所有问题的回答）
/// </summary>
public class QuestionResult
{
    /// <summary>请求 ID</summary>
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    /// <summary>结果状态</summary>
    [JsonPropertyName("status")]
    public QuestionResultStatus Status { get; set; }

    /// <summary>回答列表</summary>
    [JsonPropertyName("answers")]
    public List<QuestionAnswer> Answers { get; set; } = new();
}
