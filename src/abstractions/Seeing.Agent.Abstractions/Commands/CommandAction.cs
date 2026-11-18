namespace Seeing.Agent.Abstractions.Commands;

/// <summary>
/// 命令向宿主提出的、与具体 UI 无关的操作请求。
/// <para>
/// 命令只声明“想让宿主做什么”，不直接依赖任何宿主类型：宿主认识 <see cref="Name"/> 就执行，
/// 不认识就回退展示命令的文本结果。
/// </para>
/// </summary>
public sealed record CommandAction
{
    /// <summary>动作名称（宿主无关 token，内置值见 <see cref="CommandActionNames"/>）。</summary>
    public required string Name { get; init; }

    /// <summary>动作载荷（弱类型字典，由动作处理器解释）。</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; init; } = Empty;

    /// <summary>空载荷（共享只读实例）。</summary>
    public static IReadOnlyDictionary<string, object?> Empty { get; } =
        new Dictionary<string, object?>();
}

/// <summary>
/// 内置命令动作名。
/// <para>扩展动作可以自造，但只有宿主注册了同名处理器才会执行；否则回退展示命令文本。</para>
/// </summary>
public static class CommandActionNames
{
    /// <summary>打开模型选择器。</summary>
    public const string ModelPicker = "model.picker";

    /// <summary>切换模型（载荷：<c>modelId</c>）。</summary>
    public const string ModelSet = "model.set";

    /// <summary>展开 Agent 选择器。</summary>
    public const string AgentPicker = "agent.picker";

    /// <summary>切换 Agent（载荷：<c>agentId</c>）。</summary>
    public const string AgentSet = "agent.set";

    /// <summary>导航到宿主页面（载荷：<c>path</c>）。</summary>
    public const string PageNavigate = "page.navigate";

    /// <summary>显示一条临时提示（载荷：<c>text</c>、可选 <c>level</c>）。</summary>
    public const string Notice = "notice";

    /// <summary>新建会话（载荷：可选 <c>title</c>）。</summary>
    public const string SessionNew = "session.new";

    /// <summary>分支当前会话。</summary>
    public const string SessionFork = "session.fork";

    /// <summary>清空当前会话。</summary>
    public const string SessionClear = "session.clear";
}
