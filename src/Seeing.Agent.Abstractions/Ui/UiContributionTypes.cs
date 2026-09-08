namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// 侧栏导航贡献。
/// </summary>
/// <param name="Route">路由路径。</param>
/// <param name="Title">显示标题。</param>
/// <param name="Icon">图标标识。</param>
/// <param name="Requires">依赖的模块 id 列表。</param>
/// <param name="Scenarios">可选场景白名单；省略表示模块启用即显示。</param>
public record NavContribution(
    string Route,
    string Title,
    string Icon,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string>? Scenarios = null);

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
