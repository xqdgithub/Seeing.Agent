using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Session.Core;
using Seeing.Session.Persistence;
using Seeing.Session.Storage;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 工作区切换重载处理器：将会话组存储切换到新工作区目录（{新工作区}/.seeing/session-groups），
/// 并清空组管理器内存缓存，避免旧工作区的组继续命中。
/// </summary>
public sealed class SessionGroupReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly ISessionGroupStore _store;
    private readonly ISessionGroupManager _groups;
    private readonly ILogger<SessionGroupReloadHandler>? _logger;

    /// <summary>
    /// 创建处理器
    /// </summary>
    /// <param name="store">会话组存储（DI 解析，通常为 IRelocatableSessionGroupStore 实现）</param>
    /// <param name="groups">会话组管理器（重定位后清空其内存缓存）</param>
    /// <param name="logger">日志（可选）</param>
    public SessionGroupReloadHandler(
        ISessionGroupStore store,
        ISessionGroupManager groups,
        ILogger<SessionGroupReloadHandler>? logger = null)
    {
        _store = store;
        _groups = groups;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override string ComponentId => "session.groups";

    /// <inheritdoc/>
    protected override async Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
    {
        // 切换目录前先落盘写回缓冲：确保待写会话组仍写入旧工作区目录
        // 落盘失败不得阻止重定位与缓存清理（仅记 Warning 后继续）
        if (_store is IPersistenceFlusher flusher)
        {
            try
            {
                await flusher.FlushAllAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "工作区切换前会话组落盘失败，继续重定位");
            }
        }

        // 支持重定位的存储：切换到新工作区的会话组目录
        if (_store is IRelocatableSessionGroupStore relocatable && !string.IsNullOrEmpty(change.NewWorkspace))
        {
            relocatable.SetBaseDirectory(Path.Combine(change.NewWorkspace, ".seeing", "session-groups"));
        }

        // 无论是否重定位，均清空缓存：避免旧工作区遗留的组缓存继续被命中
        _groups.ClearCache();
    }
}
