using Seeing.Agent.Abstractions.Questions;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// 问答在途请求投影：订阅 <see cref="IQuestionRequestManager.PendingChanged"/> 维护快照，并回传作答。
/// </summary>
public sealed class TuiQuestionQueue : IDisposable
{
    private readonly IQuestionRequestManager _manager;
    private IReadOnlyList<QuestionRequest> _pending = [];

    public TuiQuestionQueue(IQuestionRequestManager manager)
    {
        _manager = manager;
        _manager.PendingChanged += OnPendingChanged;
        Refresh();
    }

    public IReadOnlyList<QuestionRequest> Pending => _pending;

    public bool TryResolve(QuestionRequest request, QuestionResult result)
        => _manager.TryResolve(request.Id, result, expectedSessionId: request.SessionId);

    public void Dispose() => _manager.PendingChanged -= OnPendingChanged;

    private void OnPendingChanged() => Refresh();

    private void Refresh() => _pending = _manager.GetAllPending();
}
