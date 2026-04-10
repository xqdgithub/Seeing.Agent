namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 扩展加载结果
    /// </summary>
    public record ExtensionLoadResult
    {
        /// <summary>是否成功</summary>
        public bool Ok { get; init; }

        /// <summary>错误信息</summary>
        public string? Error { get; init; }

        /// <summary>
        /// 失败阶段
        /// <para>install - 安装/下载失败</para>
        /// <para>entry - 无有效入口点</para>
        /// <para>load - 加载程序集失败</para>
        /// <para>init - 初始化失败</para>
        /// </summary>
        public string? Stage { get; init; }

        /// <summary>已加载的扩展（成功时）</summary>
        public LoadedExtension? Loaded { get; init; }
    }

    /// <summary>
    /// 已加载的扩展
    /// </summary>
    public record LoadedExtension
    {
        /// <summary>扩展 ID</summary>
        public string Id { get; init; } = "";

        /// <summary>原始 spec</summary>
        public string Spec { get; init; } = "";

        /// <summary>来源：npm / file</summary>
        public string Source { get; init; } = "";

        /// <summary>目标路径（程序集路径）</summary>
        public string Target { get; init; } = "";

        /// <summary>扩展实例</summary>
        public Core.Interfaces.IExtension Instance { get; init; } = null!;

        /// <summary>插件选项</summary>
        public Dictionary<string, object>? Options { get; init; }

        /// <summary>扩展元数据</summary>
        public Core.Interfaces.ExtensionMeta Meta { get; init; } = null!;

        /// <summary>是否启用</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否激活（已初始化并注册组件）</summary>
        public bool Active { get; set; }
    }

    /// <summary>
    /// 扩展状态
    /// </summary>
    public record ExtensionStatus
    {
        /// <summary>扩展 ID</summary>
        public string Id { get; init; } = "";

        /// <summary>来源</summary>
        public string Source { get; init; } = "";

        /// <summary>原始 spec</summary>
        public string Spec { get; init; } = "";

        /// <summary>是否启用</summary>
        public bool Enabled { get; init; }

        /// <summary>是否激活</summary>
        public bool Active { get; init; }
    }
}