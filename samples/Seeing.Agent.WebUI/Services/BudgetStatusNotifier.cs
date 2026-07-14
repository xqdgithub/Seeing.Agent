using Seeing.Agent.TokenBudget;
using Seeing.Agent.TokenBudget.Api.Responses;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Token Budget 状态通知器实现
/// </summary>
public class BudgetStatusNotifier : IBudgetStatusNotifier
{
    private readonly Dictionary<string, BudgetStatusResponse> _currentStatuses = new();
    private readonly Dictionary<string, List<Action<BudgetStatusResponse>>> _subscribers = new();
    private readonly object _lock = new();

    public IDisposable Subscribe(string sessionId, Action<BudgetStatusResponse> onUpdate)
    {
        lock (_lock)
        {
            if (!_subscribers.ContainsKey(sessionId))
            {
                _subscribers[sessionId] = new List<Action<BudgetStatusResponse>>();
            }
            _subscribers[sessionId].Add(onUpdate);

            // 如果已有状态，立即通知
            if (_currentStatuses.TryGetValue(sessionId, out var status))
            {
                onUpdate(status);
            }
        }

        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                if (_subscribers.TryGetValue(sessionId, out var list))
                {
                    list.Remove(onUpdate);
                    if (list.Count == 0)
                    {
                        _subscribers.Remove(sessionId);
                    }
                }
            }
        });
    }

    public void Publish(string sessionId, BudgetStatusResponse status)
    {
        List<Action<BudgetStatusResponse>>? subscribersCopy;
        
        lock (_lock)
        {
            _currentStatuses[sessionId] = status;
            subscribersCopy = _subscribers.TryGetValue(sessionId, out var list) 
                ? list.ToList() 
                : null;
        }

        // 在锁外执行回调，避免死锁
        if (subscribersCopy != null)
        {
            foreach (var subscriber in subscribersCopy)
            {
                try
                {
                    subscriber(status);
                }
                catch
                {
                    // Ignore subscriber errors
                }
            }
        }
    }

    public BudgetStatusResponse? GetCurrentStatus(string sessionId)
    {
        lock (_lock)
        {
            return _currentStatuses.TryGetValue(sessionId, out var status) ? status : null;
        }
    }

    private class Unsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public Unsubscriber(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _unsubscribe();
            }
        }
    }
}
