namespace Seeing.Agent.Abstractions.Tools
{
    /// <summary>
    /// 工具能力声明（默认接口方法）。
    /// <para>
    /// 工具通过 <see cref="Capabilities"/> 返回预定义能力键值对，向框架声明静态能力元数据
    /// （超时豁免、缓存策略等）。未实现/返回 null 表示「无特殊声明，使用框架默认行为」。
    /// </para>
    /// <para>
    /// 此成员通过 DIM（默认接口方法）提供 null 默认实现，直接实现 <see cref="ITool"/> 的
    /// 类（McpTool、ReflectedTool、第三方工具）无需改动即可保持兼容。
    /// </para>
    /// </summary>
    public interface IToolCapabilities
    {
        /// <summary>
        /// 工具能力元数据字典（键为 <see cref="ToolCapabilityKeys"/> 中的 kebab-case 键）。
        /// <para>默认从实现类上的类级 [ToolCapability] 属性读取（惰性缓存）；子类可覆盖。</para>
        /// </summary>
        IReadOnlyDictionary<string, string>? Capabilities => ToolCapabilityAttribute.ReadFromType(GetType());
    }
}
