using Seeing.Agent.Configuration;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 工作区切换进度的 circuit 维度实现。
/// <para>
/// 切换本身跑在 circuit 的同步上下文里，长耗时的取消 / 重载会让整个页面没有任何反馈；
/// 这里把「正在做什么」推给 <see cref="WorkspaceSwitchOverlay"/>，
/// 由它渲染一个覆盖全页的进度层（带步骤文字与不确定进度条）。
/// </para>
/// </summary>
public sealed class WorkspaceSwitchProgress : IWorkspaceSwitchProgress
{
    private int _running;

    /// <summary>当前步骤文字；null 表示未在切换。</summary>
    public string? Message { get; private set; }

    /// <summary>是否正在切换。</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>状态变化（覆盖层据此重绘）。</summary>
    public event Action? Changed;

    /// <inheritdoc/>
    public void Begin(string message)
    {
        Volatile.Write(ref _running, 1);
        Message = message;
        Notify();
    }

    /// <inheritdoc/>
    public void Report(string message)
    {
        if (!IsRunning)
            return;

        Message = message;
        Notify();
    }

    /// <inheritdoc/>
    public void End()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        Message = null;
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}
