using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 问答卡片交互服务（Scoped）：卡片动作 → <see cref="IQuestionRequestManager.TryResolve"/>。
/// <para>
/// 调用前校验：RequestId 非空、卡片仍为 pending；归属会话经 expectedSessionId 回传 Manager 校验。
/// 校验不通过则 no-op（返回 false，不触碰 Manager）。
/// </para>
/// </summary>
public sealed class QuestionInteractionService
{
    private readonly IQuestionRequestManager _manager;
    private readonly ILogger<QuestionInteractionService>? _logger;

    public QuestionInteractionService(
        IQuestionRequestManager manager,
        ILogger<QuestionInteractionService>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
    }

    /// <summary>提交作答（Completed）。</summary>
    public bool Submit(QuestionCardModel card, IReadOnlyList<QuestionAnswer> answers)
        => Resolve(card, QuestionResultStatus.Completed, answers);

    /// <summary>取消作答（Cancelled）。</summary>
    public bool Cancel(QuestionCardModel card)
        => Resolve(card, QuestionResultStatus.Cancelled, Array.Empty<QuestionAnswer>());

    private bool Resolve(
        QuestionCardModel card, QuestionResultStatus status, IReadOnlyList<QuestionAnswer>? answers)
    {
        if (card is null || string.IsNullOrEmpty(card.RequestId))
            return false;
        if (!card.IsPending)
            return false;

        var result = new QuestionResult
        {
            RequestId = card.RequestId,
            Status = status,
            Answers = answers is null ? new List<QuestionAnswer>() : answers.ToList()
        };

        return _manager.TryResolve(
            card.RequestId,
            result,
            expectedSessionId: string.IsNullOrEmpty(card.SessionId) ? null : card.SessionId);
    }
}
