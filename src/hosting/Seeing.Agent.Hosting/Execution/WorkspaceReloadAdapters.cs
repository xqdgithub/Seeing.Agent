using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>把 <see cref="UnifiedConfigManager.LoadAsync"/> 收窄成切换流程需要的一步。</summary>
internal sealed class WorkspaceConfigReloader : IWorkspaceConfigReloader
{
    private readonly UnifiedConfigManager _config;

    public WorkspaceConfigReloader(UnifiedConfigManager config) => _config = config;

    public Task ReloadAsync(CancellationToken ct = default) => _config.LoadAsync(ct);}

/// <summary>
/// 一轮显式全量重载：抑制编排器的自动触发，自己跑 WorkspaceChange + ConfigChange，
/// 并把每个处理器的成败返回给调用方（编排器日志会吞掉处理器异常，只留在结果里）。
/// </summary>
internal sealed class WorkspaceReloadInvoker : IWorkspaceReloadInvoker
{
    private readonly ReloadOrchestrator _orchestrator;
    private readonly IServiceProvider _services;
    private readonly ILogger<WorkspaceReloadInvoker> _logger;

    public WorkspaceReloadInvoker(
        ReloadOrchestrator orchestrator,
        IServiceProvider services,
        ILogger<WorkspaceReloadInvoker> logger)
    {
        _orchestrator = orchestrator;
        _services = services;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ReloadResult>> ReloadAsync(
        WorkspaceApplyContext ctx, CancellationToken ct)
    {
        using var _ = _orchestrator.SuppressAutoReload();

        // 首次选定：宿主启动时未决议，Handler 还没挂载。这里补挂（幂等）。
        _services.AttachReloadHandlers();

        var workspaceResults = await _orchestrator.ReloadAsync(
            new WorkspaceChange
            {
                OldWorkspace = ctx.OldRoot ?? string.Empty,
                NewWorkspace = ctx.NewRoot,
            }, ct).ConfigureAwait(false);

        var configResults = await _orchestrator.ReloadAsync(
            new ConfigChange { ChangedSections = Array.Empty<string>() }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "工作区重载完成: 工作区处理器 {WorkspaceCount} 个，配置处理器 {ConfigCount} 个",
            workspaceResults.Count, configResults.Count);

        return workspaceResults.Concat(configResults).ToArray();
    }
}
