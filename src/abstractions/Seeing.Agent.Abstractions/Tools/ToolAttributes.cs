namespace Seeing.Agent.Abstractions.Tools
{
    /// <summary>
    /// 标记方法为可调用的工具
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class ToolAttribute : Attribute
    {
        /// <summary>工具名称（可选，默认使用方法名）</summary>
        public string? Name { get; set; }

        /// <summary>工具描述</summary>
        public string Description { get; }

        public ToolAttribute(string description)
        {
            Description = description;
        }
    }

    /// <summary>
    /// 参数描述注解
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class ToolParamAttribute : Attribute
    {
        /// <summary>参数描述</summary>
        public string Description { get; set; } = "";

        public ToolParamAttribute(string description)
        {
            Description = description;
        }
    }

    /// <summary>
    /// 标记参数为必需
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class RequiredAttribute : Attribute
    {
    }

    /// <summary>
    /// 标记类型或属性为工具参数类型
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false)]
    public class ToolParamTypeAttribute : Attribute
    {
        /// <summary>类型描述</summary>
        public string Description { get; set; } = "";

        public ToolParamTypeAttribute(string description)
        {
            Description = description;
        }
    }

    /// <summary>
    /// 工具能力声明（Attribute 语法）。
    /// <para>
    /// 可挂在工具类（类级，ToolBase 子类经默认 Capabilities 实现读取）或工具方法
    /// （方法级，ToolDiscovery 读取后填入 ReflectedTool.Capabilities）。
    /// 键见 <see cref="ToolCapabilityKeys"/>。
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class ToolCapabilityAttribute : Attribute
    {
        /// <summary>能力键（kebab-case）</summary>
        public string Key { get; }

        /// <summary>能力值</summary>
        public string Value { get; }

        public ToolCapabilityAttribute(string key, string value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>
        /// 从类型读取类级 [ToolCapability] 属性，合并为字典。无则返回 null。
        /// 静态缓存避免重复反射。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyDictionary<string, string>?>
            Cache = new();

        public static IReadOnlyDictionary<string, string>? ReadFromType(Type type)
        {
            return Cache.GetOrAdd(type, static t =>
            {
                var attrs = t.GetCustomAttributes(typeof(ToolCapabilityAttribute), inherit: true);
                if (attrs.Length == 0)
                    return null;

                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (ToolCapabilityAttribute attr in attrs)
                    dict[attr.Key] = attr.Value;
                return dict;
            });
        }
    }
}