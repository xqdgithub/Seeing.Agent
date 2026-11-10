namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// UI 贡献信封。
/// <para>
/// 归属由 <see cref="Requires"/> 表达（实测 10 处模块贡献的 Requires 全部等于自身模块 id；
/// 只有壳层自己的页面/设置卡是空数组），因此不另设 ModuleId 字段。
/// </para>
/// </summary>
public abstract record UiContribution
{
    public required string Point { get; init; }
    public required string Id { get; init; }

    /// <summary>归属模块 id 列表；空 = 进程级（不受模块启停影响）。</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    public int Order { get; init; }
}
