namespace Seeing.Agent.App;

/// <summary>
/// 运行跟踪器 - 管理执行状态和取消
/// </summary>
public class ChatRunTracker
{
    private readonly Dictionary<string, CancellationTokenSource> _runs = new();
    private readonly object _lock = new();

    /// <summary>
    /// 注册运行
    /// </summary>
    public CancellationToken Register(string sessionId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(sessionId, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }

            var cts = new CancellationTokenSource();
            _runs[sessionId] = cts;
            return cts.Token;
        }
    }

    /// <summary>
    /// 尝试取消
    /// </summary>
    public bool TryCancel(string sessionId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(sessionId, out var cts))
            {
                cts.Cancel();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 完成运行
    /// </summary>
    public void Complete(string sessionId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(sessionId, out var cts))
            {
                cts.Dispose();
                _runs.Remove(sessionId);
            }
        }
    }

    /// <summary>
    /// 检查是否正在运行
    /// </summary>
    public bool IsRunning(string sessionId)
    {
        lock (_lock)
        {
            return _runs.ContainsKey(sessionId);
        }
    }
}
