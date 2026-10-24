using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Skills;
namespace Seeing.Agent.Abstractions.Tools
{
    /// <summary>
    /// 工具上下文
    /// </summary>
    public class ToolContext
    {
        public string SessionId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string? CallId { get; set; }
        public AgentDefinition? Agent { get; set; }
        public CancellationToken CancellationToken { get; set; }

        /// <summary>元数据出口（由 AgentExecutor 接线，可空）</summary>
        public IToolMetadataSink? MetadataSink { get; set; }

        /// <summary>
        /// 本轮执行钉住的技能快照（由执行器透传；工具须优先使用，缺失时才回退目录当前态）。
        /// <para>与 <see cref="ISkillCatalogPin"/> 指向同一对象，
        /// 保证「装配看到的清单」与「工具能加载的技能」一致。</para>
        /// </summary>
        public SkillCatalogSnapshot? SkillSnapshot { get; set; }

        /// <summary>
        /// 事件出口（子任务投影等）。由 AgentExecutor 在执行工具时接线。
        /// </summary>
        public IToolEventSink? EventSink { get; set; }

        /// <summary>可选服务定位（用于 Task 等需要额外依赖的工具）</summary>
        public IServiceProvider? Services { get; set; }

        /// <summary>
        /// 执行级授权器（由 ToolManager 从执行链注入；工具须优先使用，缺失时回退工厂）。
        /// </summary>
        public IPermissionAuthorizer? PermissionAuthorizer { get; set; }

        /// <summary>
        /// 执行级工具可见性**只读视图**（由执行器按执行创建并透传；可空）。
        /// 检索类工具据此读取语料与判定能力；写入（加载）只能由执行器应用 ToolResult 声明完成。
        /// </summary>
        public IExecutionToolScopeView? ToolScope { get; set; }

        /// <summary>
        /// 克隆本上下文并替换取消令牌（供装饰器透视执行使用）。
        /// 通过 <see cref="object.MemberwiseClone"/> 复制，保证所有字段（含未来新增字段）完整透传，
        /// 避免逐字段重建导致的遗漏（如授权器丢失引发安全绕过）。
        /// </summary>
        public ToolContext WithCancellationToken(CancellationToken cancellationToken)
        {
            var clone = (ToolContext)MemberwiseClone();
            clone.CancellationToken = cancellationToken;
            return clone;
        }
    }

    /// <summary>
    /// 工具接口 - LLM 可调用的工具
    /// </summary>
    public interface ITool : IToolCapabilities
    {
        /// <summary>工具 ID</summary>
        string Id { get; }

        /// <summary>工具描述</summary>
        string Description { get; }

        /// <summary>工具标签（用于分类和过滤）</summary>
        IReadOnlyList<string> Tags { get; }

        /// <summary>工具分类</summary>
        ToolCategory Category { get; }

        /// <summary>参数 Schema (JSON Schema)</summary>
        JsonElement ParametersSchema { get; }

        /// <summary>执行工具</summary>
        Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context);
    }

    /// <summary>
    /// 工具分类
    /// </summary>
    public enum ToolCategory
    {
        /// <summary>通用工具</summary>
        General,

        /// <summary>文件系统</summary>
        FileSystem,

        /// <summary>网络请求</summary>
        Network,

        /// <summary>数据库操作</summary>
        Database,

        /// <summary>计算处理</summary>
        Computation,

        /// <summary>LLM 交互</summary>
        LlmInteraction,

        /// <summary>外部服务</summary>
        ExternalService
    }
}