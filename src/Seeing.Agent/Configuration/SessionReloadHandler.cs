using Seeing.Agent.Abstractions.Configuration;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区切换重载处理器：将会话存储切换到新工作区目录并清空内存缓存
/// </summary>
public sealed class SessionReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly ISessionStore _store;
    private readonly SessionManager _sessionManager;

    /// <summary>
    /// 创建处理器
    /// </summary>
    /// <param name="store">会话存储（DI 解析，通常为 IRelocatableSessionStore 实现）</param>
    /// <param name="sessionManager">会话管理器（用于清空内存缓存）</param>
    public SessionReloadHandler(ISessionStore store, SessionManager sessionManager)
    {
        _store = store;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc/>
    public override string ComponentId => "session";

    /// <inheritdoc/>
    protected override Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
    {
        // 支持重定位的存储：切换到新工作区的会话目录（{新工作区}/.seeing/sessions）
        if (_store is IRelocatableSessionStore relocatable && !string.IsNullOrEmpty(change.NewWorkspace))
        {
            relocatable.SetBaseDirectory(Path.Combine(change.NewWorkspace, ".seeing", "sessions"));
        }

        // 无论存储是否支持重定位，都清空内存缓存，避免旧工作区会话残留
        _sessionManager.ClearCache();
        return Task.CompletedTask;
    }
}
