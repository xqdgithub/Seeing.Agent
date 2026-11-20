using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Runtime;

public interface ISessionDirectory
{
    Task<SessionData?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<SessionData> EnsureAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionData>> ListAsync(CancellationToken ct = default);
    /// <summary>创建会话。工作目录不可指定：创建即钉到工作区根，执行期再校验。</summary>
    Task<SessionData> CreateAsync(string? title = null, string? agentId = null, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task RenameAsync(string sessionId, string newTitle, CancellationToken ct = default);
    Task<SessionData> BranchAsync(string sessionId, string? title = null, CancellationToken ct = default);

    /// <summary>列出会话摘要（轻量列表，不含消息体；用于列表页）。</summary>
    Task<IReadOnlyList<SessionSummary>> ListSummariesAsync(CancellationToken ct = default);

    /// <summary>列出已归档会话摘要。</summary>
    Task<IReadOnlyList<SessionSummary>> ListArchivedAsync(CancellationToken ct = default);

    /// <summary>归档会话（按组整体归档）。</summary>
    Task ArchiveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>恢复归档会话（按组整体恢复）。</summary>
    Task RestoreAsync(string sessionId, CancellationToken ct = default);

    /// <summary>从内存释放会话（不删除磁盘数据）。</summary>
    Task EvictAsync(string sessionId, CancellationToken ct = default);
}
