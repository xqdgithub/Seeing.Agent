using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Pages;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI 壳层页面贡献（非能力模块；始终登记，供 ModuleRouter 渲染已去掉 @page 的 *Page）。
/// </summary>
public sealed class CoreShellUiContribution : IUiContribution
{
    public const string ModuleIdValue = "__webui.shell__";

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new NavContribution("/sessions", "会话列表", "unordered-list", [], ComponentType: typeof(SessionsPage)),
        new NavContribution("/agents", "Agents", "team", [], ComponentType: typeof(AgentsPage)),
        new NavContribution("/agent-config", "Agent 配置", "setting", [], ComponentType: typeof(AgentConfigPage)),
        new NavContribution("/models", "模型", "cloud", [], ComponentType: typeof(ModelsPage)),
        new NavContribution("/security", "安全", "safety", [], ComponentType: typeof(SecurityPage)),
    ];
}
