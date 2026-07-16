using System.Text.Json;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Commands
{
    /// <summary>
    /// 命令类型 - 决定命令执行后的行为
    /// </summary>
    public enum CommandType
    {
        /// <summary>默认：透传给 Agent 处理</summary>
        Default = 0,
        
        /// <summary>系统命令：立即执行，返回结果，不进入 Agent</summary>
        System = 1,
        
        /// <summary>Skill 命令：替换 History 后继续执行 Agent</summary>
        Skill = 2
    }

    /// <summary>
    /// 命令分类
    /// </summary>
    public enum CommandCategory
    {
        /// <summary>基础命令 - help, exit, clear 等</summary>
        Basic,
        /// <summary>消息导航 - search, fold 等</summary>
        Navigation,
        /// <summary>Agent 管理 - agent, model 等</summary>
        Agent,
        /// <summary>工具与技能 - tools, skills, mcp 等</summary>
        Tools,
        /// <summary>系统管理 - config, rules 等</summary>
        System,
        /// <summary>扩展命令 - 插件提供的命令</summary>
        Extension,
        /// <summary>其他</summary>
        Other
    }

    /// <summary>
    /// 命令元数据 - 描述命令的完整信息
    /// </summary>
    public class CommandMetadata
    {
        /// <summary>命令名称（不含斜杠前缀）</summary>
        public required string Name { get; init; }

        /// <summary>命令别名</summary>
        public string[] Aliases { get; init; } = Array.Empty<string>();

        /// <summary>命令描述</summary>
        public required string Description { get; init; }

        /// <summary>用法说明</summary>
        public string Usage { get; init; } = "";

        /// <summary>命令分类</summary>
        public CommandCategory Category { get; init; } = CommandCategory.Other;

        /// <summary>参数 Schema（JSON Schema 格式）</summary>
        public JsonElement? ParametersSchema { get; init; }

        /// <summary>使用示例</summary>
        public string[] Examples { get; init; } = Array.Empty<string>();

        /// <summary>是否需要用户确认</summary>
        public bool RequiresConfirmation { get; init; } = false;

        /// <summary>是否隐藏（不在帮助中显示）</summary>
        public bool IsHidden { get; init; } = false;

        /// <summary>来源（用于标识插件提供的命令）</summary>
        public string? Source { get; init; }

        /// <summary>排序权重（用于帮助显示顺序）</summary>
        public int SortOrder { get; init; } = 100;

        /// <summary>命令模板（用于 Markdown 命令）</summary>
        public string? Template { get; init; }

        /// <summary>执行时切换的目标 Agent</summary>
        public string? Agent { get; init; }

        /// <summary>命令执行类型（决定执行后行为）</summary>
        public CommandType Type { get; init; } = CommandType.Default;

        /// <summary>
        /// 命令支持的 Runtime（空数组表示支持所有 Runtime）
        /// </summary>
        public AgentRuntime[] SupportedRuntimes { get; init; } = Array.Empty<AgentRuntime>();

        /// <summary>
        /// 是否支持指定 Runtime
        /// </summary>
        public bool SupportsRuntime(AgentRuntime runtime) =>
            SupportedRuntimes.Length == 0 || SupportedRuntimes.Contains(runtime);

        /// <summary>创建简单元数据</summary>
        public static CommandMetadata Simple(string name, string description, string usage = "", CommandCategory category = CommandCategory.Other)
            => new() { Name = name, Description = description, Usage = usage, Category = category };
    }

    /// <summary>
    /// 命令上下文 - 提供命令执行所需的依赖和信息
    /// </summary>
    public class CommandContext
    {
        /// <summary>命令名称</summary>
        public string CommandName { get; init; } = "";

        /// <summary>原始输入（包含命令名，只读）</summary>
        public string RawInput { get; init; } = "";

        /// <summary>当前输入（可修改，默认等于 RawInput）</summary>
        public string Input { get; set; } = "";

        /// <summary>命令参数（不含命令名）</summary>
        public string Arguments { get; init; } = "";

        /// <summary>会话 ID</summary>
        public string SessionId { get; init; } = "";

        /// <summary>消息 ID（可选）</summary>
        public string? MessageId { get; init; }

        /// <summary>服务提供者</summary>
        public IServiceProvider? Services { get; init; }

        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>工作区根目录</summary>
        public string? WorkspaceRoot { get; init; }

        /// <summary>目标 Agent（从命令元数据传递）</summary>
        public string? TargetAgent { get; init; }

        /// <summary>消息历史（只读，用于参考）</summary>
        public IReadOnlyList<ChatMessage>? History { get; init; }

        /// <summary>附加数据（用于扩展）</summary>
        public Dictionary<string, object> Data { get; } = new();
    }

    /// <summary>
    /// 命令执行结果
    /// </summary>
    public class CommandResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; init; } = true;

        /// <summary>结果消息（用于显示）</summary>
        public string? Message { get; init; }

        /// <summary>错误信息</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>是否应退出应用</summary>
        public bool ShouldExit { get; init; } = false;

        /// <summary>是否需要刷新界面</summary>
        public bool NeedsRefresh { get; init; } = false;

        /// <summary>
        /// 是否继续执行 Agent（默认 true）
        /// <para>false = 命令已完全处理，不再执行 Agent</para>
        /// <para>true = 继续执行 Agent（透传或 Skill 展开）</para>
        /// </summary>
        public bool ShouldContinue { get; init; } = true;

        /// <summary>附加数据</summary>
        public Dictionary<string, object>? Metadata { get; init; }

        /// <summary>创建成功结果</summary>
        public static CommandResult Ok(string? message = null, bool needsRefresh = false, bool shouldContinue = true)
            => new() { Success = true, Message = message, NeedsRefresh = needsRefresh, ShouldContinue = shouldContinue };

        /// <summary>创建退出结果</summary>
        public static CommandResult Exit(string? message = null)
            => new() { Success = true, ShouldExit = true, Message = message };

        /// <summary>创建错误结果</summary>
        public static CommandResult Fail(string errorMessage, string? message = null)
            => new() { Success = false, ErrorMessage = errorMessage, Message = message };

        /// <summary>创建带数据的结果</summary>
        public static CommandResult WithData(string? message, Dictionary<string, object> data)
            => new() { Success = true, Message = message, Metadata = data };
    }

    /// <summary>
    /// 命令接口 - 所有斜杠命令的实现基础
    /// <para>
    /// 支持两种实现方式：
    /// 1. 实现 ExecuteAsync 方法（代码模式）
    /// 2. 返回 Metadata 属性（可被自动发现）
    /// </para>
    /// </summary>
    public interface ICommand
    {
        /// <summary>命令元数据</summary>
        CommandMetadata Metadata { get; }

        /// <summary>执行命令</summary>
        /// <param name="context">命令上下文</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>命令执行结果</returns>
        Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default);
    }
}