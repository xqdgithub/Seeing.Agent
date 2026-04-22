namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// 输入状态 - 管理用户输入和处理状态
/// </summary>
public class InputState
{
    private readonly object _lock = new();
    private bool _isProcessing;
    private bool _isMultilineMode;

    /// <summary>是否正在处理</summary>
    public bool IsProcessing
    {
        get { lock (_lock) return _isProcessing; }
        set
        {
            lock (_lock)
            {
                if (_isProcessing != value)
                {
                    _isProcessing = value;
                    OnStateChanged?.Invoke(RenderRegion.Input);
                }
            }
        }
    }

    /// <summary>是否多行模式</summary>
    public bool IsMultilineMode
    {
        get { lock (_lock) return _isMultilineMode; }
        set { lock (_lock) _isMultilineMode = value; }
    }

    /// <summary>当前任务的取消令牌源</summary>
    public CancellationTokenSource? CurrentTaskCts { get; set; }

    /// <summary>状态变更回调</summary>
    public Action<RenderRegion>? OnStateChanged { get; set; }

    /// <summary>取消当前任务</summary>
    public void CancelCurrentTask()
    {
        CurrentTaskCts?.Cancel();
        CurrentTaskCts = null;
        IsProcessing = false;
    }

    /// <summary>创建新的取消令牌</summary>
    public CancellationTokenSource CreateCancellationTokenSource()
    {
        CurrentTaskCts = new CancellationTokenSource();
        return CurrentTaskCts;
    }
}