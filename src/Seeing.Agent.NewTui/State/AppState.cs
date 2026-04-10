using System.Text;
using Seeing.Agent.Sessions;
using Seeing.Agent.Core.Events;

namespace Seeing.Agent.NewTui.State;

public class AppState
{
    private readonly SynchronizationContext? _syncContext;
    private CancellationTokenSource? _currentCts;

    /// <summary>工作区根目录</summary>
    public string WorkspaceRoot { get; set; } = Environment.CurrentDirectory;

    /// <summary>当前会话</summary>
    public SessionData? CurrentSession { get; set; }

    /// <summary>当前 Agent 名称</summary>
    public string CurrentAgent { get; set; } = "primary";

    /// <summary>是否正在处理</summary>
    public bool IsProcessing { get; private set; }

    /// <summary>流式输出内容</summary>
    public StringBuilder StreamingContent { get; } = new();

    /// <summary>流式推理内容</summary>
    public StringBuilder StreamingReasoning { get; } = new();

    /// <summary>活跃的工具调用</summary>
    public List<ToolCallDisplay> ActiveToolCalls { get; } = new();

    /// <summary>最后一次错误</summary>
    public string? LastError { get; set; }

    /// <summary>状态变更事件</summary>
    public event Action? StateChanged;

    public AppState() => _syncContext = SynchronizationContext.Current;

    public CancellationToken StartProcessing()
    {
        _currentCts = new CancellationTokenSource();
        IsProcessing = true;
        StreamingContent.Clear();
        StreamingReasoning.Clear();
        ActiveToolCalls.Clear();
        LastError = null;
        NotifyChanged();
        return _currentCts.Token;
    }

    public void EndProcessing()
    {
        IsProcessing = false;
        _currentCts?.Dispose();
        _currentCts = null;
        NotifyChanged();
    }

    public void CancelProcessing()
    {
        _currentCts?.Cancel();
    }

    public void NotifyChanged()
    {
        if (_syncContext != null)
            _syncContext.Post(_ => StateChanged?.Invoke(), null);
        else
            StateChanged?.Invoke();
    }
}

public record ToolCallDisplay
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ToolCallStatus Status { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public TimeSpan? Duration { get; set; }
}
