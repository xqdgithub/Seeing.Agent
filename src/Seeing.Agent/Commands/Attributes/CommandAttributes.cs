namespace Seeing.Agent.Commands.Attributes
{
    /// <summary>
    /// 命令方法注解 - 标记方法为可发现的命令
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CommandAttribute : Attribute
    {
        /// <summary>命令描述</summary>
        public string Description { get; }

        /// <summary>命令名称（可选，默认使用方法名）</summary>
        public string? Name { get; set; }

        /// <summary>命令别名</summary>
        public string[] Aliases { get; set; } = Array.Empty<string>();

        /// <summary>用法说明</summary>
        public string Usage { get; set; } = "";

        /// <summary>命令分类</summary>
        public CommandCategory Category { get; set; } = CommandCategory.Other;

        /// <summary>使用示例</summary>
        public string[] Examples { get; set; } = Array.Empty<string>();

        /// <summary>是否需要确认</summary>
        public bool RequiresConfirmation { get; set; } = false;

        /// <summary>是否隐藏</summary>
        public bool IsHidden { get; set; } = false;

        /// <summary>排序权重</summary>
        public int SortOrder { get; set; } = 100;

        /// <summary>创建命令注解</summary>
        public CommandAttribute(string description)
        {
            Description = description;
        }
    }

    /// <summary>
    /// 命令参数注解 - 描述命令参数
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class CommandParamAttribute : Attribute
    {
        /// <summary>参数描述</summary>
        public string Description { get; }

        /// <summary>参数名称（可选，默认使用参数名）</summary>
        public string? Name { get; set; }

        /// <summary>是否必需</summary>
        public bool Required { get; set; } = false;

        /// <summary>默认值</summary>
        public object? DefaultValue { get; set; }

        /// <summary>创建参数注解</summary>
        public CommandParamAttribute(string description)
        {
            Description = description;
        }
    }

    /// <summary>
    /// 命令类注解 - 标记类包含命令方法
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class CommandProviderAttribute : Attribute
    {
        /// <summary>提供者名称（用于命令来源标识）</summary>
        public string? Name { get; set; }

        /// <summary>是否自动注册所有命令方法</summary>
        public bool AutoRegister { get; set; } = true;
    }
}