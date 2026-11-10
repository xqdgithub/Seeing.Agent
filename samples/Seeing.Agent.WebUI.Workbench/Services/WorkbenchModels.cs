using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>dock 内的一个工作台实例（不可变投影）。</summary>
/// <param name="Key">实例键。</param>
/// <param name="Title">标题（来自贡献声明）。</param>
/// <param name="ViewKey">视图键。</param>
/// <param name="OpenPayload">打开载荷（仅内存，一次性指令）。</param>
/// <param name="ViewState">工作台上报的可恢复小状态。</param>
public sealed record WorkbenchInstance(
    WorkbenchInstanceKey Key,
    string Title,
    string ViewKey,
    System.Text.Json.JsonElement? OpenPayload,
    IReadOnlyDictionary<string, string>? ViewState)
{
    /// <summary>激活态由布局的 <c>Active*</c> 决定，实例本身不携带（避免两处真相）。</summary>
    public bool IsActive { get; init; }
}

/// <summary>某会话的工作台布局（不可变投影）。</summary>
/// <param name="Left">左 dock 实例（按打开顺序）。</param>
/// <param name="Right">右 dock 实例（按打开顺序）。</param>
/// <param name="ActiveLeft">左 dock 激活实例键文本；null = 无激活。</param>
/// <param name="ActiveRight">右 dock 激活实例键文本；null = 无激活。</param>
/// <param name="LeftWidthPercent">左 dock 宽度百分比。</param>
/// <param name="RightWidthPercent">右 dock 宽度百分比。</param>
public sealed record WorkbenchSessionLayout(
    IReadOnlyList<WorkbenchInstance> Left,
    IReadOnlyList<WorkbenchInstance> Right,
    string? ActiveLeft,
    string? ActiveRight,
    double LeftWidthPercent,
    double RightWidthPercent)
{
    /// <summary>
    /// 聊天主列是否被用户隐藏（工作区独占全宽）。
    /// <para>
    /// 作为 <c>init</c> 属性而不是位置参数：布局的构造点很多（状态、恢复、测试），
    /// 加位置参数会让每个构造点都要多填一个几乎总是默认值的实参。
    /// </para>
    /// </summary>
    public bool ChatHidden { get; init; }

    /// <summary>空布局（默认宽度）。</summary>
    public static WorkbenchSessionLayout Empty { get; } =
        new([], [], null, null, WorkbenchSizing.Default.DefaultPercent, WorkbenchSizing.Default.DefaultPercent);

    /// <summary>取指定 dock 的实例列表。</summary>
    public IReadOnlyList<WorkbenchInstance> For(WorkbenchDock dock)
        => dock == WorkbenchDock.Left ? Left : Right;

    /// <summary>取指定 dock 的激活实例键文本。</summary>
    public string? ActiveKeyFor(WorkbenchDock dock)
        => dock == WorkbenchDock.Left ? ActiveLeft : ActiveRight;

    /// <summary>取指定 dock 的宽度百分比。</summary>
    public double WidthFor(WorkbenchDock dock)
        => dock == WorkbenchDock.Left ? LeftWidthPercent : RightWidthPercent;

    /// <summary>该 dock 是否为空（无实例 → 不渲染 DOM）。</summary>
    public bool IsEmpty(WorkbenchDock dock) => For(dock).Count == 0;
}
