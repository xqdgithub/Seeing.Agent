namespace Seeing.Agent.Abstractions.Files;

/// <summary>文件工作区工厂 — 按会话有效根解析隔离的 facade。</summary>
public interface IFileWorkspaceFactory
{
    /// <summary>
    /// 解析会话对应的文件工作区（异步，可回落 LoadAsync）。
    /// 有效根 = ISessionWorkspaceResolver.ResolveWorkingDirectory(sessionId) ?? IWorkspaceProvider.GetProjectRoot()。
    /// 同一 (sessionId, 有效根) 复用；有效根变化重建。
    /// </summary>
    Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>引用计数减一，归零回收并释放 watcher 引用；幂等。</summary>
    void Release(string sessionId);
}
