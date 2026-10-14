namespace Seeing.Agent.Abstractions.Execution;

/// <summary>
/// 进程内按路径写序列化端口 — 供文件工具与文件工作区共用，消除同进程并发写竞态。
/// <para>best-effort：仅保证同一进程内按绝对路径串行；跨进程不保证。</para>
/// </summary>
public interface IPathWriteLock
{
    /// <summary>
    /// 获取指定路径的独占写锁；返回的句柄 Dispose 时释放。
    /// 同一规范化路径的并发调用将串行执行。
    /// </summary>
    Task<IDisposable> AcquireAsync(string path, CancellationToken cancellationToken = default);
}
