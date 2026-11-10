using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.WebUI.Components.Scheduler;
using Seeing.Agent.WebUI.Components.Settings;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// <b>过渡期</b>壳层设置卡贡献（旧形状）。
/// <para>
/// 页面路由与侧栏已由 <see cref="Seeing.Agent.WebUI.Shell.WebUiHostContributor"/> 以信封形状声明；
/// 设置卡片在批次 D 之前仍走旧通道（<see cref="Settings.razor"/> 读
/// <see cref="IUiContributionRegistry.SettingsCards"/>），因此这里暂时保留 4 条卡片。
/// </para>
/// <para>
/// <b>批次 D 会删除本类</b>（届时 <c>Settings.razor</c> 改读
/// <c>SettingsCardContribution</c> + 视图表，与 <c>WebUiHostContributor</c> 的声明对齐）。
/// 在那之前删除它会让设置页只剩两个静态页签。
/// </para>
/// </summary>
public sealed class CoreShellUiContribution : IUiContribution
{
    /// <summary>旧通道的壳层模块 id。</summary>
    public const string ModuleIdValue = "__webui.shell__";

    /// <inheritdoc />
    public string ModuleId => ModuleIdValue;

    /// <inheritdoc />
    public IReadOnlyList<object> Contribute() =>
    [
        new LegacySettingsCardContribution(
            "/settings/boot",
            "启动能力",
            typeof(BootCapabilitySettingsCard),
            []),
        new LegacySettingsCardContribution(
            "/settings/scenarios",
            "工作模式",
            typeof(ScenariosSettingsCard),
            []),
        new LegacySettingsCardContribution(
            "/settings/scheduler",
            "调度器",
            typeof(SchedulerSettingsCard),
            ["scheduler"]),
        // 记忆设置不能作为 Nav 挂在 /memory 下：更短前缀规则会隐藏带更短前缀的子路由。
        // 设置类内容统一走设置卡片。
        new LegacySettingsCardContribution(
            "/settings/memory",
            "记忆",
            typeof(MemorySettingsCard),
            ["memory"]),
    ];
}
