using Seeing.Agent.Abstractions.Questions;

namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 问答内联卡片投影模型（UI 只读视图，非权威）。
/// <para>
/// 权威在途状态在 <see cref="IQuestionRequestManager"/>；本模型由
/// <c>QuestionInbox</c> 按 <c>RequestId</c> 聚合、按 <c>CallId</c> 关联工具卡片。
/// </para>
/// </summary>
public sealed class QuestionCardModel
{
    /// <summary>在途请求唯一 ID（@key 关联键）。</summary>
    public required string RequestId { get; init; }

    /// <summary>所属会话 ID（决议回传时用作 expectedSessionId 校验）。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>工具调用 ID：内联卡片关联键。</summary>
    public string? CallId { get; init; }

    /// <summary>所属消息 ID。</summary>
    public string? MessageId { get; init; }

    /// <summary>问题列表。</summary>
    public IReadOnlyList<QuestionCardItem> Questions { get; init; } = Array.Empty<QuestionCardItem>();

    /// <summary>是否仍在途（决议后为 false）。</summary>
    public bool IsPending { get; internal set; } = true;
}

/// <summary>卡片中的单个问题投影。</summary>
public sealed class QuestionCardItem
{
    public required string Id { get; init; }

    public string Header { get; init; } = string.Empty;

    public string QuestionText { get; init; } = string.Empty;

    public QuestionKind Kind { get; init; } = QuestionKind.Single;

    public IReadOnlyList<QuestionCardOption> Options { get; init; } = Array.Empty<QuestionCardOption>();

    public bool AllowCustom { get; init; } = true;

    public bool Required { get; init; } = true;

    public IReadOnlyList<string> DefaultSelectedLabels { get; init; } = Array.Empty<string>();

    public string? DefaultCustomAnswer { get; init; }
}

/// <summary>问题选项投影。</summary>
public sealed class QuestionCardOption
{
    public required string Label { get; init; }

    public string? Description { get; init; }
}

/// <summary>一次卡片提交（投影卡片 + 用户答案）。</summary>
public sealed class QuestionSubmission
{
    public required QuestionCardModel Card { get; init; }

    public IReadOnlyList<QuestionAnswer> Answers { get; init; } = Array.Empty<QuestionAnswer>();
}
