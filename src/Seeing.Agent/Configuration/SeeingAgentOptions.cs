using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Seeing.Agent 配置选项
    /// </summary>
    public class SeeingAgentOptions
    {
        /// <summary>默认模型</summary>
        public string? DefaultModel { get; set; }

        /// <summary>默认 Provider</summary>
        public string? DefaultProvider { get; set; }

        /// <summary>默认 Agent</summary>
        public string? DefaultAgent { get; set; }

        /// <summary>全局模型目录（与 ModelScope 单模型条目结构一致：modalities、limit、options）</summary>
        public Dictionary<string, ModelConfig>? Models { get; set; }

        /// <summary>ModelScope 风格块（与 <see cref="Models"/> 合并，通常包含 models 字典）</summary>
        public ModelScopeSection? ModelScope { get; set; }

        /// <summary>Provider 配置列表</summary>
        public Dictionary<string, ProviderConfig> Providers { get; set; } = new();

        /// <summary>技能配置</summary>
        public SkillsConfig Skills { get; set; } = new();

        /// <summary>权限配置</summary>
        public PermissionOptions Permission { get; set; } = new();

        /// <summary>Gateway 配置</summary>
        public GatewayOptions Gateway { get; set; } = new();

        /// <summary>Gateway Client（Channel Bridge）管理配置</summary>
        public GatewayClientsOptions GatewayClients { get; set; } = new();

        /// <summary>ACP 集成配置</summary>
        public AcpOptions Acp { get; set; } = new();

        /// <summary>
        /// 插件列表
        /// <para>
        /// 支持格式：
        /// - NuGet 包名：@seeing/analytics@1.0.0
        /// - 文件路径：./plugins/MyExtension.dll
        /// - file:// URL：file://./plugins/MyExtension.dll
        /// </para>
        /// </summary>
        public List<PluginSpec> Plugins { get; set; } = new();

        /// <summary>
        /// 插件启用状态覆盖
        /// <para>
        /// key: 插件 ID（NuGet 包名或文件名）
        /// value: true 启用，false 禁用
        /// </para>
        /// </summary>
        public Dictionary<string, bool> PluginEnabled { get; set; } = new();

        /// <summary>工作区配置</summary>
        public WorkspaceOptions Workspace { get; set; } = new();

        /// <summary>Token 预算全局配置</summary>
        public TokenBudgetOptions TokenBudget { get; set; } = new();
    }

    /// <summary>
    /// 权限配置选项
    /// </summary>
    public class PermissionOptions
    {
        /// <summary>
        /// 自动批准所有权限请求（危险！）
        /// <para>
        /// 安全警告：启用此选项后，Agent 将在无需用户确认的情况下执行所有操作，
        /// 包括文件写入、命令执行、工具调用等。仅建议在受控环境中使用。
        /// </para>
        /// <para>
        /// 默认值：false（需要用户确认）
        /// </para>
        /// </summary>
        public bool AutoApproveAll { get; set; } = false;
    }

    /// <summary>
    /// 技能配置
    /// </summary>
    public class SkillsConfig
    {
        /// <summary>本地技能路径列表</summary>
        public List<string> Paths { get; set; } = new();

        /// <summary>远程技能 URL 列表（index.json 格式）</summary>
        public List<string> Urls { get; set; } = new();
    }

    /// <summary>
    /// ModelScope 兼容配置节（JSON: SeeingAgent:ModelScope）
    /// </summary>
    public class ModelScopeSection
    {
        /// <summary>模型 ID → 模型定义（与 Provider.models 条目相同）</summary>
        public Dictionary<string, ModelConfig>? Models { get; set; }
    }

    /// <summary>
    /// ACP 集成配置
    /// </summary>
    public class AcpOptions
    {
        /// <summary>是否启用 ACP 集成</summary>
        public bool Enabled { get; set; }

        /// <summary>默认 ACP 后端标识</summary>
        public string? DefaultBackend { get; set; }

        /// <summary>请求超时</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>空闲超时（子进程无使用后多久终止）</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Session 销毁后的宽限期。
        /// <para>在此期间，同一 session 再次请求可复用 ACP 客户端进程和 ACP Session，提高响应速度。</para>
        /// <para>设置为 TimeSpan.Zero 可禁用宽限期（立即终止进程）。</para>
        /// <para>默认值：5 分钟</para>
        /// </summary>
        public TimeSpan SessionGracePeriod { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>ACP 后端配置（key 为后端标识）</summary>
        public Dictionary<string, AcpBackendConfig> Backends { get; set; } = new();
    }

    /// <summary>
    /// 单个 ACP 后端配置
    /// </summary>
    public class AcpBackendConfig
    {
        /// <summary>启动命令</summary>
        public string? Command { get; set; }

        /// <summary>命令参数</summary>
        public List<string>? Args { get; set; }

        /// <summary>环境变量</summary>
        public Dictionary<string, string>? Environment { get; set; }
    }

    /// <summary>
    /// 工作区配置选项（项目级）
    /// </summary>
    public class WorkspaceOptions
    {
        /// <summary>
        /// 是否使用全局默认工作区
        /// <para>true: 使用用户级 GlobalWorkspaceRoot</para>
        /// <para>false 或未设置: 使用启动目录（默认）</para>
        /// </summary>
        public bool UseGlobal { get; set; } = false;

        /// <summary>
        /// 项目特定的自定义工作区路径
        /// <para>设置后忽略 UseGlobal，直接使用此路径</para>
        /// </summary>
        public string? CustomPath { get; set; }
    }

    /// <summary>
    /// Token 预算配置选项
    /// </summary>
    public class TokenBudgetOptions
    {
        /// <summary>
        /// 用户配置的会话上下文最大大小（可选）
        /// 设置后会与模型 context limit 取较小值
        /// </summary>
        public int? MaxContextTokens { get; set; }

        /// <summary>
        /// 无模型时的默认上下文大小
        /// 默认 200000 (200K)
        /// </summary>
        public int DefaultMaxContextTokens { get; set; } = 200000;

        /// <summary>警告阈值</summary>
        public ThresholdOptions WarningThreshold { get; set; } = new() { Percentage = 80 };

        /// <summary>压缩阈值</summary>
        public ThresholdOptions CompactionThreshold { get; set; } = new() { Percentage = 90 };

        /// <summary>压缩策略类型</summary>
        public CompactionStrategyType CompactionStrategy { get; set; } = CompactionStrategyType.SlidingWindow;

        /// <summary>滑动窗口保留 Token 数</summary>
        public int SlidingWindowKeepTokens { get; set; } = 20000;

        /// <summary>摘要目标 Token 数</summary>
        public int SummaryTargetTokens { get; set; } = 4000;

        /// <summary>是否启用自动压缩</summary>
        public bool AutoCompactionEnabled { get; set; } = true;
    }

    /// <summary>
    /// 阈值配置选项
    /// </summary>
    public class ThresholdOptions
    {
        /// <summary>
        /// Threshold as percentage of max tokens (0-100).
        /// Takes precedence over AbsoluteTokens if both are set.
        /// </summary>
        public int? Percentage { get; set; }

        /// <summary>
        /// Threshold as absolute token count.
        /// Ignored if Percentage is also set.
        /// </summary>
        public int? AbsoluteTokens { get; set; }
    }
}
