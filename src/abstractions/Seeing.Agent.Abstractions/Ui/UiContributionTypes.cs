namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// 侧栏导航贡献。
/// </summary>
/// <param name="Route">路由路径。</param>
/// <param name="Title">显示标题。</param>
/// <param name="Icon">图标标识。</param>
/// <param name="Requires">依赖的模块 id 列表。</param>
/// <param name="Scenarios">可选场景白名单；省略表示模块启用即显示。</param>
/// <param name="ComponentType">可选 Blazor 页面/组件类型（WebUI 侧渲染；能力包可不填）。</param>
/// <param name="Group">侧栏分组标题（如「控制中心」）；null/空则扁平展示在顶层。</param>
/// <param name="GroupIcon">分组图标（AntDesign Outline 名，如 control / folder / setting）。</param>
/// <param name="Order">同组内排序（升序）。</param>
public record NavContribution(
    string Route,
    string Title,
    string Icon,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string>? Scenarios = null,
    Type? ComponentType = null,
    string? Group = null,
    string? GroupIcon = null,
    int Order = 0);

/// <summary>WebUI 侧栏内置分组名（能力包贡献时应对齐，便于合并 SubMenu）。</summary>
public static class NavGroups
{
    public const string Control = "控制中心";
    public const string Workspace = "工作区";
    public const string Settings = "设置";

    public const string ControlIcon = "control";
    public const string WorkspaceIcon = "folder";
    public const string SettingsIcon = "setting";
}

/// <summary>
/// 设置页卡片贡献。
/// </summary>
/// <param name="Route">卡片路由或锚点。</param>
/// <param name="Title">卡片标题。</param>
/// <param name="ComponentType">Blazor 组件类型（WebUI 侧再转为 RenderFragment）。</param>
/// <param name="Requires">依赖的模块 id 列表。</param>
public record SettingsCardContribution(
    string Route,
    string Title,
    Type ComponentType,
    IReadOnlyList<string> Requires);

/// <summary>
/// 命名插槽贡献。
/// </summary>
/// <param name="Name">插槽名称（如 session_prompt、message_toolbar）。</param>
/// <param name="ComponentType">Blazor 组件类型（WebUI 侧再转为 RenderFragment）。</param>
/// <param name="Requires">依赖的模块 id 列表。</param>
/// <param name="Scenarios">可选场景白名单；省略表示模块启用即显示。</param>
public record SlotContribution(
    string Name,
    Type ComponentType,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string>? Scenarios = null);

/// <summary>
/// 消息类型渲染器贡献。
/// </summary>
/// <param name="MessageType">消息类型标识。</param>
/// <param name="ComponentType">Blazor 组件类型（WebUI 侧再转为渲染器）。</param>
public record MessageRendererContribution(
    string MessageType,
    Type ComponentType);

/// <summary>
/// 纯路由贡献（不进入侧栏 Nav；供 ModuleRouter 渲染，如壳页 /session）。
/// </summary>
/// <param name="Route">路由模板（可含 {Param} / {*CatchAll}）。</param>
/// <param name="Title">页面标题（未启用提示等）。</param>
/// <param name="ComponentType">Blazor 页面/组件类型。</param>
/// <param name="Requires">依赖的模块 id 列表；空表示壳层始终可用。</param>
public record RouteContribution(
    string Route,
    string Title,
    Type ComponentType,
    IReadOnlyList<string>? Requires = null);
