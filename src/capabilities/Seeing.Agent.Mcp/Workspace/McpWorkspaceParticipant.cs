using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Configuration;
using Seeing.Agent.Mcp.Configuration;

namespace Seeing.Agent.Mcp.Workspace;

/// <summary>
/// 工作区切换参与者：Applying 断开项目级会话且禁止 Save；
/// Applied 后按新根加载项目级配置并连接（对齐 Initialize 对未禁用 server 的策略）。
/// </summary>
public sealed class McpWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly McpClientManager _manager;
    private readonly IMcpConfigPersistence _persistence;
    private readonly ILogger<McpWorkspaceParticipant> _logger;

    public McpWorkspaceParticipant(
        McpClientManager manager,
        IMcpConfigPersistence persistence,
        ILogger<McpWorkspaceParticipant> logger)
    {
        _manager = manager;
        _persistence = persistence;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ComponentId => "mcp";

    /// <inheritdoc />
    public async Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        await _manager.PrepareWorkspaceSwitchAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "工作区切换：已断开项目级 MCP 会话并抑制项目保存（{Old} → {New}）",
            context.OldRoot, context.NewRoot);
    }

    /// <inheritdoc />
    public async Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        try
        {
            var projectConfigs = await _persistence
                .LoadAsync(ConfigLevel.Project, ct)
                .ConfigureAwait(false);

            await _manager.CompleteWorkspaceSwitchAsync(projectConfigs, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "工作区切换：已按新根加载 {Count} 个项目级 MCP server: {Root}",
                projectConfigs.Count, context.NewRoot);
        }
        catch (Exception ex)
        {
            // Applied 失败不推翻切换；记日志即可。
            _logger.LogError(ex, "工作区切换：MCP 项目级重载失败: {Root}", context.NewRoot);
            _manager.SetProjectSaveSuppressed(false);
        }
    }
}
