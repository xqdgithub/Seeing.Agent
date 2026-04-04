namespace Seeing.Agent.Tools.Attributes
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
}