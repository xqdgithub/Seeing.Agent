namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级模块装配覆盖（seeing.json <c>modules</c> 节）。
/// <c>Enabled == null</c> 表示沿用 scenario 的 modules 基线。
/// </summary>
public sealed class ModulesOptions
{
    /// <summary>显式启用列表；null = 使用 scenario base。</summary>
    public List<string>? Enabled { get; set; }

    /// <summary>从启用集中剔除的模块 id。</summary>
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
