using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Commands;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.MCP;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 扩展上下文 - 提供运行时信息和服务引用
    /// </summary>
    public class ExtensionContext
    {
        /// <summary>服务提供者</summary>
        public IServiceProvider Services { get; set; } = null!;

        /// <summary>配置</summary>
        public IConfiguration Configuration { get; set; } = null!;

        /// <summary>当前工作目录</summary>
        public string Directory { get; set; } = "";

        /// <summary>工作区根目录</summary>
        public string WorkspaceRoot { get; set; } = "";

        // 核心服务引用
        /// <summary>Hook 管理器</summary>
        public HookManager HookManager { get; set; } = null!;

        /// <summary>工具调用器</summary>
        public ToolManager ToolInvoker { get; set; } = null!;

        /// <summary>权限服务</summary>
        public IPermissionService PermissionService { get; set; } = null!;

        /// <summary>技能管理器</summary>
        public SkillManager SkillManager { get; set; } = null!;

        /// <summary>Agent 注册表</summary>
        public IAgentRegistry AgentRegistry { get; set; } = null!;

        /// <summary>MCP 客户端管理器</summary>
        public McpClientManager McpClientManager { get; set; } = null!;

        /// <summary>命令注册表</summary>
        public ICommandRegistry CommandRegistry { get; set; } = null!;
    }

    /// <summary>
    /// 扩展元数据
    /// </summary>
    public class ExtensionMeta
    {
        /// <summary>状态：first（首次加载）、updated（更新）、same（相同）</summary>
        public string State { get; set; } = "first";

        /// <summary>扩展唯一标识</summary>
        public string Id { get; set; } = "";

        /// <summary>来源：npm 或 file</summary>
        public string Source { get; set; } = "";

        /// <summary>原始 spec</summary>
        public string Spec { get; set; } = "";

        /// <summary>目标路径（程序集路径）</summary>
        public string Target { get; set; } = "";

        /// <summary>版本（NuGet 插件）</summary>
        public string? Version { get; set; }

        /// <summary>加载次数</summary>
        public int LoadCount { get; set; } = 1;

        /// <summary>首次加载时间（Unix 毫秒）</summary>
        public long FirstTime { get; set; }

        /// <summary>最后加载时间（Unix 毫秒）</summary>
        public long LastTime { get; set; }

        /// <summary>指纹（用于检测变更）</summary>
        public string Fingerprint { get; set; } = "";
    }

    /// <summary>
    /// 扩展接口 - 插件化扩展能力
    /// <para>
    /// 参考 opencode Plugin 设计，支持：
    /// - 服务注册（ConfigureServices）
    /// - 异步初始化（InitializeAsync）
    /// - 组件提供（Agent/Tool/Hook/MCP/Skill）
    /// </para>
    /// </summary>
    public interface IExtension
    {
        #region 元数据

        /// <summary>
        /// 扩展唯一标识
        /// <para>可选，NuGet 包默认使用包名，文件插件必须提供</para>
        /// </summary>
        string? Id => null;

        /// <summary>
        /// 版本号
        /// </summary>
        string Version => "1.0.0";

        /// <summary>
        /// 显示名称
        /// </summary>
        string Name => "";

        /// <summary>
        /// 描述
        /// </summary>
        string Description => "";

        /// <summary>
        /// 目标运行时：server 或 tui
        /// <para>当前仅支持 server</para>
        /// </summary>
        string Target => "server";

        #endregion

        #region 生命周期

        /// <summary>
        /// 注册服务（DI 容器构建前调用）
        /// <para>类似 ASP.NET Core IStartup.ConfigureServices</para>
        /// </summary>
        /// <param name="services">服务集合</param>
        void ConfigureServices(IServiceCollection services) { }

        /// <summary>
        /// 初始化扩展（服务容器构建后调用）
        /// <para>参考 opencode: async (api, options, meta) =&gt; Promise&lt;void&gt;</para>
        /// </summary>
        /// <param name="context">扩展上下文</param>
        /// <param name="meta">扩展元数据</param>
        Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
            => Task.CompletedTask;

        /// <summary>
        /// 清理资源（停用时调用）
        /// </summary>
        Task DisposeAsync() => Task.CompletedTask;

        #endregion

        #region 组件提供

        /// <summary>
        /// 提供的 Agent 实现
        /// </summary>
        IEnumerable<IAgent> GetAgents() => Enumerable.Empty<IAgent>();

        /// <summary>
        /// 提供的工具
        /// </summary>
        IEnumerable<ITool> GetTools() => Enumerable.Empty<ITool>();

        /// <summary>
        /// 提供的 Hook 处理器
        /// </summary>
        IEnumerable<IHookHandler> GetHookHandlers() => Enumerable.Empty<IHookHandler>();

        /// <summary>
        /// 提供的 MCP Server 配置
        /// </summary>
        IEnumerable<McpServerConfig> GetMcpServers() => Enumerable.Empty<McpServerConfig>();

        /// <summary>
        /// 提供的 Skill 搜索路径
        /// </summary>
        IEnumerable<string> GetSkillPaths() => Enumerable.Empty<string>();

        /// <summary>
        /// 提供的命令（新增）
        /// </summary>
        IEnumerable<ICommand> GetCommands() => Enumerable.Empty<ICommand>();

        #endregion
    }
}