namespace Seeing.Agent.Abstractions.Files;

/// <summary>文件工作区工厂 — 按会话有效根解析隔离的 facade。</summary>
public interface IFileWorkspaceFactory
{
    /// <summary>
    /// 解析会话的**主根**（项目根；本期会话 cwd 恒等于它）。
    /// 根集合来自 <c>ISessionRootSet.Roots</c>，本接口不自行推导。
    /// </summary>
    Task<IFileWorkspace> ResolveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>列出会话全部根（项目根 + 协同目录），保持根集合顺序。</summary>
    Task<IReadOnlyList<IFileWorkspace>> ListAsync(string sessionId, CancellationToken ct = default);

    /// <summary>按 <c>RootId</c> 精确取 facade；不存在返回 null。</summary>
    Task<IFileWorkspace?> GetAsync(string sessionId, string rootId, CancellationToken ct = default);

    /// <summary>按绝对路径路由到所属根（最长前缀 + PathBoundary）；不属于任何根返回 null。</summary>
    Task<IFileWorkspace?> ResolveAsync(
        string sessionId, string absolutePath, CancellationToken ct = default);

    /// <summary>幂等全清该会话的 facade；不做引用计数。</summary>
    void Release(string sessionId);
}
