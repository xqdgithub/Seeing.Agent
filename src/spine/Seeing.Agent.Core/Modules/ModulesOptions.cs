namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级模块装配覆盖（seeing.json <c>Modules</c> 节）。
/// <see cref="Enabled"/> 已废除主路径（结算警告并忽略）；请改用 CapabilitySet + Boot。
/// <see cref="Disabled"/> 仍为 boot 全局模块层黑名单。
/// </summary>
public sealed class ModulesOptions
{
    /// <summary>
    /// 显式启用列表。<b>已忽略</b>：若配置则警告并忽略，请改用 <c>CapabilitySets</c> + <c>Boot</c>。
    /// </summary>
    public List<string>? Enabled { get; set; }

    /// <summary>从 bootEnabled 中剔除的模块 id（全局，所有 Boot 都扣）。</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>工具级裁剪（会话/进程工具可见集；进程级 Activate 不读此项）。</summary>
    public ModuleToolsOptions Tools { get; set; } = new();
}

/// <summary>modules.tools 子节。</summary>
public sealed class ModuleToolsOptions
{
    /// <summary>禁用的工具 id。</summary>
    public List<string> Disabled { get; set; } = [];
}
