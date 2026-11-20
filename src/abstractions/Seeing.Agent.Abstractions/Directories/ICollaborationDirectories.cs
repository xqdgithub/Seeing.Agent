using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Directories;

/// <summary>
/// 会话协同目录的持久化配置（唯一写入口）。
/// <para>
/// 只负责协同目录本身：项目根是环境事实（<c>IWorkspaceProvider</c>），临时授权是审批结果
/// （<c>IPermissionGrantStore</c>），工作台根是投影（<c>ISessionRootSet.Roots</c>）。
/// 审批与落盘在实现内部完成；调用方不得直接改 <c>SessionData</c>。
/// </para>
/// </summary>
public interface ICollaborationDirectories
{
    /// <summary>读取会话协同目录（绝对路径、已规范化、去重）。</summary>
    Task<IReadOnlyList<CollaborationDirectory>> GetAsync(string sessionId, CancellationToken ct = default);

    /// <summary>添加协同目录。实现负责路径规范化与去重。</summary>
    Task AddAsync(string sessionId, string absolutePath, string? note = null, CancellationToken ct = default);

    /// <summary>移除协同目录。</summary>
    Task RemoveAsync(string sessionId, string absolutePath, CancellationToken ct = default);
}
