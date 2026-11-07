using Seeing.Agent.Configuration;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 单例切换器与 circuit 维度进度之间的桥。
/// <para>
/// <see cref="Seeing.Agent.Hosting.Execution.IWorkspaceSwitcher"/> 是单例（进程级互斥），
/// 而进度层是 circuit 维度（每个浏览器标签各一份）。单例不能注入 Scoped 服务，
/// 于是用 <see cref="AsyncLocal{T}"/> 表达「这次调用属于哪个 circuit」：
/// 调用方在 <see cref="Use"/> 作用域内发起切换，切换器的上报就落到那个 circuit 上。
/// </para>
/// <para>没有作用域（无界面宿主、后台触发）时静默丢弃——切换照常进行，只是不显示进度。</para>
/// </summary>
public sealed class WorkspaceSwitchProgressHub : IWorkspaceSwitchProgress
{
    private static readonly AsyncLocal<WorkspaceSwitchProgress?> s_current = new();

    /// <summary>把后续切换上报绑定到指定 circuit 的进度实例。</summary>
    public static IDisposable Use(WorkspaceSwitchProgress progress) => new Scope(progress);

    /// <inheritdoc/>
    public void Begin(string message) => s_current.Value?.Begin(message);

    /// <inheritdoc/>
    public void Report(string message) => s_current.Value?.Report(message);

    /// <inheritdoc/>
    public void End() => s_current.Value?.End();

    private sealed class Scope : IDisposable
    {
        private readonly WorkspaceSwitchProgress? _previous;
        private bool _disposed;

        public Scope(WorkspaceSwitchProgress progress)
        {
            _previous = s_current.Value;
            s_current.Value = progress;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            s_current.Value = _previous;
        }
    }
}
