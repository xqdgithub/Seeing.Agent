using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Core.Tools;

/// <summary>
/// 工作区切换重载处理器：按新工作区重新加载工具禁用状态。
/// <para>
/// <c>tool-state.json</c> 有用户级与项目级两份。项目级是工作区派生状态：
/// 切换后旧工作区被禁用的工具必须「解禁」，新工作区被禁用的工具必须生效。
/// </para>
/// <para>
/// <see cref="ToolManager.LoadToolStateAsync"/> 会用文件内容**整体替换**两个集合
/// （不是增量合并），因此旧的项目级禁用集合天然被清空，无需额外方法；
/// 只要求调用它，而不是在启动期之外做局部合并。
/// </para>
/// </summary>
public sealed class ToolStateReloadHandler : ReloadHandlerBase<WorkspaceChange>
{
    private readonly ToolManager _tools;
    private readonly ILogger<ToolStateReloadHandler>? _logger;

    /// <summary>创建处理器。</summary>
    /// <param name="tools">工具管理器（加载禁用状态并递增 schema 版本）。</param>
    /// <param name="logger">日志（可选）。</param>
    public ToolStateReloadHandler(ToolManager tools, ILogger<ToolStateReloadHandler>? logger = null)
    {
        _tools = tools;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override string ComponentId => "tool-state";

    /// <inheritdoc/>
    protected override async Task ReloadAsync(WorkspaceChange change, CancellationToken ct)
    {
        await _tools.LoadToolStateAsync(ct).ConfigureAwait(false);
        _logger?.LogInformation("工作区切换：工具禁用状态已按新工作区重载: {Root}", change.NewWorkspace);
    }
}
