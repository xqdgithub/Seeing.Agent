namespace Seeing.Agent.Hosting;

/// <summary>
/// Host Shape 自描述载体：声明本形态的 id、可承载的模块类型与默认场景。
/// <para>
/// <b>本类型只在此处定义一份。</b>此前 Web / Tui / Headless / Embed / Gateway 五个 Shape 包各定义了
/// 一个同名的 <c>HostShapeDescriptor</c>，于是出现五个跨程序集同名类型，且新增字段要改五处。
/// 各 Shape 包现在只保留自己的 id 常量与静态 <c>Descriptor</c> 实例。
/// </para>
/// <para>
/// <b>关于 <see cref="CarriableModuleTypes"/>：</b>它是形态自描述（「本形态能承载什么」），
/// 不是运行时开关。当前全仓无运行时读取方——实际启用集由宿主 ProjectReference + Boot/能力集结算决定。
/// 保留字段是为了让形态声明可被守门测试对照，不要用它当启用真相源。
/// </para>
/// </summary>
public sealed class HostShapeDescriptor
{
    /// <summary>形态 id（如 web / tui / headless / embed / gateway）。</summary>
    public required string Id { get; init; }

    /// <summary>可承载的模块 id 列表（自描述；非运行时启用真相源）。</summary>
    public required IReadOnlyList<string> CarriableModuleTypes { get; init; }

    /// <summary>未配置 <c>seeing.json</c> 工作模式时的进程级默认场景。</summary>
    public string? DefaultScenario { get; init; }
}
