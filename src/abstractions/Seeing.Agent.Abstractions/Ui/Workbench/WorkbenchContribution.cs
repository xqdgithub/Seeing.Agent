using System.Text.Json;

namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>
/// 工作台贡献：挂在宿主声明的 <c>workbench.dock</c> 扩展点上。
/// <para>
/// <b>它就是全部</b>：不需要额外的插件接口、注册表或描述符类型。信封已经提供了所有维度 ——
/// <see cref="UiContribution.Requires"/> 是归属与模块门控，<see cref="UiContribution.Id"/> 是工作台 id，
/// <see cref="ViewKey"/> 是宿主侧组件解析键，<see cref="UiContribution.Order"/> 是目录排序。
/// 迁移前的 <c>IWorkbenchPlugin</c> / <c>IWorkbenchRegistry</c> / <c>WorkbenchDescriptor</c> 三件套
/// 与信封完全重叠，已废弃。
/// </para>
/// </summary>
public sealed record WorkbenchContribution : UiContribution
{
    /// <summary>显示名（目录、标签页、上下文叙述共用）。</summary>
    public required string Title { get; init; }

    /// <summary>视图键（宿主 <c>ViewKey → Type</c> 表解析；契约层不认识 Blazor）。</summary>
    public required string ViewKey { get; init; }

    /// <summary>中性图标 token（宿主翻译成实际图标名；未知 token 原样渲染）。</summary>
    public string? Icon { get; init; }

    /// <summary>默认停靠位。</summary>
    public WorkbenchDock DefaultDock { get; init; } = WorkbenchDock.Right;

    /// <summary>尺寸偏好；null 表示 <see cref="WorkbenchSizing.Default"/>。</summary>
    public WorkbenchSizing? Sizing { get; init; }

    /// <summary>
    /// 打开载荷的 JSON Schema（可选）。仅用于给 agent 一个「该传什么」的提示，
    /// 以及打开时的<b>浅校验</b>（required 键存在 + 基本类型）；深度校验归工作台自己。
    /// </summary>
    public JsonElement? OpenPayloadSchema { get; init; }

    /// <summary>该工作台自己提供的 agent 工具 id（仅供目录展示与提示，不做登记）。</summary>
    public IReadOnlyList<string> ProvidedTools { get; init; } = [];

    /// <summary>
    /// 是否把「当前查看什么」作为上下文源参与提示词。
    /// <para>默认 false：摘要可能包含用户文件/网页内容，必须由工作台作者显式开启。</para>
    /// </summary>
    public bool ContributesContext { get; init; }
}
