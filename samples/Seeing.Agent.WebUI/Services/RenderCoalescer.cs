namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 渲染请求合并器：窗口期内的多次 <see cref="Request"/> 合并为一次回调，并在窗口结束时尾随执行。
/// <para>
/// 用途：事件流每个 delta 都会请求 UI 刷新。经本类合并后，渲染次数从「与 delta 次数线性相关」
/// 降为「与时间窗口数线性相关」，从而在高频流式输出时封顶渲染速率，并压平多窗口/回放同时触发的渲染爆发。
/// </para>
/// <para>
/// <b>线程契约</b>：<see cref="Request"/> 可在任意线程调用；回调由定时器线程触发，
/// 调用方必须自行切回 UI 线程。本类会兜住回调异常 —— 定时器线程上的未捕获异常会终止进程。
/// </para>
/// </summary>
public sealed class RenderCoalescer : IDisposable
{
    private readonly Action _render;
    private readonly TimeSpan _window;
    private readonly bool _flushOnDispose;
    private readonly object _gate = new();

    private Timer? _timer;
    private bool _pending;
    private bool _disposed;
    private int _requestCount;
    private int _renderCount;

    /// <param name="render">渲染回调（须自行切回 UI 线程）。</param>
    /// <param name="window">合并窗口。窗口内的多次请求合并为一次渲染。</param>
    /// <param name="flushOnDispose">
    /// 释放时是否尾随执行一次待处理渲染。默认 <c>false</c>：组件销毁后这次渲染没有观察者，
    /// 且在 circuit 已关闭时调用 <c>InvokeAsync(StateHasChanged)</c> 会抛异常。
    /// </param>
    public RenderCoalescer(Action render, TimeSpan window, bool flushOnDispose = false)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _window = window;
        _flushOnDispose = flushOnDispose;
    }

    /// <summary>累计请求次数（诊断用）。</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>累计实际渲染次数（诊断用）。</summary>
    public int RenderCount => Volatile.Read(ref _renderCount);

    /// <summary>请求一次渲染；窗口期内重复调用只会在窗口结束时渲染一次。</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            Interlocked.Increment(ref _requestCount);
            _pending = true;
            _timer ??= new Timer(OnTimerElapsed, null, _window, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>立即执行待处理请求（无待处理时直接返回）。</summary>
    public Task FlushAsync()
    {
        RenderIfPending();
        return Task.CompletedTask;
    }

    private void OnTimerElapsed(object? state)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        RenderIfPending();
    }

    private void RenderIfPending()
    {
        lock (_gate)
        {
            if (_disposed || !_pending)
                return;

            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }

        Interlocked.Increment(ref _renderCount);
        try
        {
            _render();
        }
        catch (Exception ex)
        {
            // 定时器线程上的未捕获异常会终止进程；渲染失败不应影响宿主存活
            System.Diagnostics.Debug.WriteLine($"[RenderCoalescer] 渲染回调异常: {ex}");
        }
    }

    /// <summary>
    /// 释放。默认不尾随渲染（见构造参数 <c>flushOnDispose</c>）。
    /// </summary>
    public void Dispose()
    {
        bool trailing;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            trailing = _flushOnDispose && _pending;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }

        if (!trailing)
            return;

        Interlocked.Increment(ref _renderCount);
        try
        {
            _render();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RenderCoalescer] 尾随渲染异常: {ex}");
        }
    }
}
