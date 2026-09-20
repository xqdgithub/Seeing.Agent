using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 全局问答收件箱（Singleton，只读投影，非权威）。
/// <para>
/// 输入：<see cref="IQuestionRequestManager.GetAllPending"/> + <see cref="IQuestionRequestManager.PendingChanged"/>；
/// 输出：<see cref="QuestionCardModel"/> 全量投影。每次通知全量重建，天然对账。
/// </para>
/// <para>并发契约：内部 <c>_gate</c> 串行写；<see cref="Changed"/> 锁外触发；不持有任何 Scoped 引用。</para>
/// </summary>
public sealed class QuestionInbox : IDisposable
{
    private readonly IQuestionRequestManager _manager;
    private readonly ILogger<QuestionInbox>? _logger;
    private readonly object _gate = new();

    private IReadOnlyList<QuestionCardModel> _cards = Array.Empty<QuestionCardModel>();
    private bool _disposed;

    public QuestionInbox(IQuestionRequestManager manager, ILogger<QuestionInbox>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
        _manager.PendingChanged += OnPendingChanged;
        Rebuild();
    }

    /// <summary>投影变更通知（锁外触发）。</summary>
    public event Action? Changed;

    /// <summary>全部在途卡片快照。</summary>
    public IReadOnlyList<QuestionCardModel> GetAll()
    {
        lock (_gate)
            return _cards;
    }

    /// <summary>指定会话的在途卡片快照。</summary>
    public IReadOnlyList<QuestionCardModel> GetBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<QuestionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>指定工具调用关联的在途卡片快照。</summary>
    public IReadOnlyList<QuestionCardModel> GetByCallId(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return Array.Empty<QuestionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>全部在途卡片数。</summary>
    public int TotalCount
    {
        get { lock (_gate) return _cards.Count; }
    }

    private void OnPendingChanged() => Rebuild();

    private void Rebuild()
    {
        if (_disposed)
            return;

        IReadOnlyList<QuestionRequest> pending;
        try
        {
            pending = _manager.GetAllPending();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取全局在途问答请求失败");
            return;
        }

        var cards = pending
            .Where(p => !string.IsNullOrEmpty(p.Id))
            .Select(ToCard)
            .ToList();

        var changed = false;
        lock (_gate)
        {
            if (!SameIds(_cards, cards))
            {
                _cards = cards;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    private static bool SameIds(
        IReadOnlyList<QuestionCardModel> current, IReadOnlyList<QuestionCardModel> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].RequestId, next[i].RequestId, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static QuestionCardModel ToCard(QuestionRequest request) => new()
    {
        RequestId = request.Id,
        SessionId = request.SessionId,
        CallId = request.Tool?.CallId,
        MessageId = request.Tool?.MessageId,
        Questions = request.Questions.Select(ToItem).ToList(),
        IsPending = true
    };

    private static QuestionCardItem ToItem(Question question) => new()
    {
        Id = question.Id,
        Header = question.Header,
        QuestionText = question.QuestionText,
        Kind = question.Kind,
        Options = question.Options
            .Select(o => new QuestionCardOption { Label = o.Label, Description = o.Description })
            .ToList(),
        AllowCustom = question.AllowCustom,
        Required = question.Required,
        DefaultSelectedLabels = question.DefaultSelectedLabels.ToList(),
        DefaultCustomAnswer = question.DefaultCustomAnswer
    };

    private void NotifyChanged()
    {
        if (_disposed)
            return;

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "问答收件箱变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.PendingChanged -= OnPendingChanged;
        Changed = null;
        lock (_gate)
            _cards = Array.Empty<QuestionCardModel>();
    }
}
