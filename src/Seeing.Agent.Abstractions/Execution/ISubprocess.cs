namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 已启动的子进程句柄。
/// </summary>
public interface ISubprocess : IDisposable
{
    /// <summary>操作系统进程 ID。</summary>
    int Id { get; }

    /// <summary>标准输出流。</summary>
    TextReader StandardOutput { get; }

    /// <summary>标准错误流。</summary>
    TextReader StandardError { get; }

    /// <summary>进程是否已退出。</summary>
    bool HasExited { get; }

    /// <summary>等待进程退出。</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>终止进程；默认终止整棵进程树。</summary>
    void Kill(bool entireTree = true);
}

/// <summary>
/// 子进程工厂 — 唯一合法的 spawn 入口。
/// </summary>
public interface ISubprocessFactory
{
    /// <summary>按规格启动子进程。</summary>
    ISubprocess Start(SubprocessSpec spec);
}
