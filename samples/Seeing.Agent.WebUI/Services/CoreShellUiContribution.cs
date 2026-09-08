using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components.Scheduler;
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
        new NavContribution("/sessions", "会话", "team", [], ComponentType: typeof(SessionsPage)),
        new NavContribution("/agents", "智能体管理", "usergroup-add", [], ComponentType: typeof(AgentsPage)),
        new NavContribution("/agent-config", "智能体配置", "edit", [], ComponentType: typeof(AgentConfigPage)),
        new NavContribution("/models", "模型", "cloud-server", [], ComponentType: typeof(ModelsPage)),
        new NavContribution("/security", "安全", "lock", [], ComponentType: typeof(SecurityPage)),
        new NavContribution("/settings", "系统设置", "tool", [], ComponentType: typeof(Settings)),
        // 设置页模块卡片：Requires 由 Settings 按 IModuleCatalog 过滤；ComponentType 仅 WebUI 可知
        new SettingsCardContribution(
            "/settings/scheduler",
            "调度器",
            typeof(SchedulerSettingsCard),
            ["scheduler"]),
    ];
}
