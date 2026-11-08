using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI 界面文案的唯一出处：术语、状态标签、通用提示。
/// <para>
/// 规则（见 <c>docs/architecture/04-development-standards.md</c> §界面文案）：
/// 面向用户一律易懂中文；仅保留产品与协议专名（MCP、Gateway、ACP、WebSocket、JSON、
/// SQLite、Quartz）以及通用技术词（Token、Embedding、Prompt、Provider、API Key、Base URL）。
/// 页面与组件不得再各写一套译名——同一种状态在不同页面出现两种说法属于缺陷。
/// </para>
/// </summary>
public static class UiText
{
    // ===== 术语 =====

    /// <summary>Agent 的界面称呼；标题等场景可单独使用「智能体」。</summary>
    public const string Agent = "智能体";

    /// <summary>Session 的界面称呼。</summary>
    public const string Session = "会话";

    /// <summary>Scenario 的界面称呼（配置侧与运行侧统一，不再混用「场景」）。</summary>
    public const string Scenario = "工作模式";

    /// <summary>出站 conversation 的界面称呼。</summary>
    public const string Conversation = "对话";

    /// <summary>Tool 的界面称呼。</summary>
    public const string Tool = "工具";

    /// <summary>Skill 的界面称呼。</summary>
    public const string Skill = "技能";

    /// <summary>「启用」态标签。</summary>
    public const string Enabled = "已启用";

    /// <summary>「禁用」态标签。</summary>
    public const string Disabled = "已禁用";

    /// <summary>未选择任何值时统一占位符。</summary>
    public const string None = "—";

    /// <summary>省略号（中文全角），禁止用 <c>...</c> 或 <c>…</c> 混排。</summary>
    public const string Ellipsis = "……";

    // ===== 智能体运行时 =====

    /// <summary>
    /// 智能体运行时标签（Native / ACP）。
    /// <para>这两个值是运行时分类名，属于 §10 U1 允许保留的技术专名，不译成中文。</para>
    /// </summary>
    public static string AgentRuntimeLabel(AgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.Runtime == Seeing.Agent.Abstractions.Agents.AgentRuntime.AcpPassthrough ? "ACP" : "Native";
    }

    /// <summary>智能体模式的界面称呼。</summary>
    public static string AgentModeLabel(AgentMode mode) => mode switch
    {
        Seeing.Agent.Abstractions.Agents.AgentMode.Primary => "主智能体",
        Seeing.Agent.Abstractions.Agents.AgentMode.SubAgent => "子智能体",
        _ => "全部"
    };

    // ===== MCP 连接状态 =====

    /// <summary>MCP 连接状态标签（首页、MCP 页、状态面板共用）。</summary>
    public static string McpState(McpServerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (status.IsDisabled)
            return Disabled;

        return status.State switch
        {
            McpConnectionState.Connected => "已连接",
            McpConnectionState.Connecting => "连接中",
            McpConnectionState.Reconnecting => "重连中",
            McpConnectionState.Paused => "已暂停",
            McpConnectionState.Error => "异常",
            McpConnectionState.Pending => "等待中",
            McpConnectionState.Removed => "已移除",
            _ => Disabled
        };
    }

    /// <summary>MCP 传输方式的界面称呼（不直出枚举名）。</summary>
    public static string McpTransport(McpTransportType type) => type switch
    {
        McpTransportType.Stdio => "标准输入输出",
        McpTransportType.StreamableHttp => "HTTP",
        McpTransportType.Sse => "SSE",
        _ => None
    };

    // ===== 定时任务 =====

    /// <summary>任务状态标签。</summary>
    public static string JobStateText(JobState state, bool expired = false)
    {
        if (expired && state == JobState.Scheduled)
            return "已过期";

        return state switch
        {
            JobState.Scheduled => "已调度",
            JobState.Running => "运行中",
            JobState.Paused => "已暂停",
            JobState.Disabled => "已禁用",
            _ => "未知"
        };
    }

    /// <summary>任务触发来源（不直出 <c>cron</c> / <c>heartbeat</c> 等内部值）。</summary>
    public static string ScheduleSource(string? source) => source?.ToLowerInvariant() switch
    {
        "cron" => "定时任务",
        "heartbeat" => "心跳",
        "manual" => "手动触发",
        _ => string.IsNullOrWhiteSpace(source) ? None : source
    };

    /// <summary>任务类型（不直出 <c>agent</c> / <c>text</c>）。</summary>
    public static string ScheduleTaskType(string? taskType) => taskType?.ToLowerInvariant() switch
    {
        "agent" => "智能体任务",
        "text" => "文本消息",
        _ => string.IsNullOrWhiteSpace(taskType) ? None : taskType
    };

    /// <summary>调度方式（不直出 <c>cron</c> / <c>interval</c> / <c>once</c>）。</summary>
    public static string ScheduleKind(string? type) => type?.ToLowerInvariant() switch
    {
        "cron" => "Cron 表达式",
        "interval" => "固定间隔",
        "once" => "一次性",
        _ => string.IsNullOrWhiteSpace(type) ? None : type
    };

    /// <summary>执行记录状态（不直出 <c>success</c> / <c>failed</c>）。</summary>
    public static string ExecutionStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "success" => "成功",
        "failed" => "失败",
        "running" => "运行中",
        "cancelled" => "已取消",
        _ => string.IsNullOrWhiteSpace(status) ? None : status
    };

    /// <summary>执行记录状态对应的 Tag 颜色。</summary>
    public static string ExecutionStatusColor(string? status) => status?.ToLowerInvariant() switch
    {
        "success" => "green",
        "failed" => "red",
        _ => "blue"
    };

    // ===== 工具 =====

    /// <summary>工具分类的界面称呼（不直出枚举名）。</summary>
    public static string ToolCategoryText(ToolCategory category) => category switch
    {
        Seeing.Agent.Abstractions.Tools.ToolCategory.General => "通用",
        Seeing.Agent.Abstractions.Tools.ToolCategory.FileSystem => "文件系统",
        Seeing.Agent.Abstractions.Tools.ToolCategory.Network => "网络",
        Seeing.Agent.Abstractions.Tools.ToolCategory.Database => "数据库",
        Seeing.Agent.Abstractions.Tools.ToolCategory.Computation => "计算",
        Seeing.Agent.Abstractions.Tools.ToolCategory.LlmInteraction => "模型交互",
        Seeing.Agent.Abstractions.Tools.ToolCategory.ExternalService => "外部服务",
        _ => "其他"
    };

    /// <summary>工具分类的界面称呼（按枚举名，供分组键已是字符串的场景）。</summary>
    public static string ToolCategoryText(string categoryName) =>
        Enum.TryParse<ToolCategory>(categoryName, ignoreCase: true, out var category)
            ? ToolCategoryText(category)
            : categoryName;

    /// <summary>工具分类对应的 Tag 颜色。</summary>
    public static string ToolCategoryColor(ToolCategory category) => category switch
    {
        Seeing.Agent.Abstractions.Tools.ToolCategory.FileSystem => "blue",
        Seeing.Agent.Abstractions.Tools.ToolCategory.Network => "cyan",
        Seeing.Agent.Abstractions.Tools.ToolCategory.LlmInteraction => "purple",
        _ => "default"
    };

    // ===== 模型 =====

    /// <summary>模型类型（用途）的界面称呼。</summary>
    public static string ModelTypeText(ModelType type) => type switch
    {
        Seeing.Agent.Abstractions.Llm.ModelType.Text => "文本",
        Seeing.Agent.Abstractions.Llm.ModelType.Embedding => "向量",
        Seeing.Agent.Abstractions.Llm.ModelType.Rerank => "重排",
        Seeing.Agent.Abstractions.Llm.ModelType.Decision => "决策",
        Seeing.Agent.Abstractions.Llm.ModelType.ImageGeneration => "图像生成",
        Seeing.Agent.Abstractions.Llm.ModelType.Video => "视频生成",
        Seeing.Agent.Abstractions.Llm.ModelType.Speech => "语音",
        _ => "其他"
    };

    // ===== 记忆 =====

    /// <summary>记忆类型标签。</summary>
    public static string MemoryTypeText(MemoryType type) => type switch
    {
        MemoryType.Session => "会话记忆",
        MemoryType.Daily => "每日整理",
        MemoryType.Digest => "长期摘要",
        _ => "其他"
    };

    /// <summary>记忆检索模式的界面称呼。</summary>
    public static string MemoryRetrievalModeText(string? mode) => mode switch
    {
        "Both" => "混合（自动注入 + 工具）",
        "AutoInject" => "自动注入",
        "ToolsOnly" => "仅工具调用",
        _ => mode ?? None
    };

    // ===== 权限 =====

    /// <summary>权限规则的资源类型。</summary>
    public static string PermissionKindText(PermissionKind kind) => kind switch
    {
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Tool => "工具",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Agent => "智能体",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.File => "文件",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Network => "网络",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.McpTool => "MCP 工具",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Skill => "技能",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Shell => "命令",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Environment => "环境变量",
        Seeing.Agent.Abstractions.Permissions.PermissionKind.Editor => "编辑器",
        _ => "其他"
    };

    /// <summary>权限规则的效果。</summary>
    public static string PermissionEffectText(PermissionEffect effect) => effect switch
    {
        Seeing.Agent.Abstractions.Permissions.PermissionEffect.Allow => "允许",
        Seeing.Agent.Abstractions.Permissions.PermissionEffect.Deny => "拒绝",
        Seeing.Agent.Abstractions.Permissions.PermissionEffect.Ask => "询问",
        _ => "未知"
    };

    /// <summary>
    /// 权限种类文案（载荷形如 <c>filesystem.read</c> / <c>tool.execute</c>）。
    /// </summary>
    public static string PermissionKindCodeText(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return None;

        var value = kind.Trim();
        var separator = value.IndexOf('.');
        var domain = (separator >= 0 ? value[..separator] : value).ToLowerInvariant();

        return domain switch
        {
            "filesystem" => "文件",
            "tool" => "工具",
            "shell" or "command" => "命令",
            "network" or "http" => "网络",
            "mcp" => "MCP 工具",
            "skill" => "技能",
            "agent" => "智能体",
            "environment" or "env" => "环境变量",
            "editor" => "编辑器",
            _ => value
        };
    }

    /// <summary>风险等级文案。</summary>
    public static string RiskLevelText(string? level) => level?.ToLowerInvariant() switch
    {
        "low" => "低风险",
        "medium" => "中风险",
        "high" => "高风险",
        "critical" => "极高风险",
        _ => string.IsNullOrWhiteSpace(level) ? None : level
    };

    // ===== 复盘 =====

    /// <summary>复盘触发方式。</summary>
    public static string ReflectionTriggerText(ReflectionTriggerKind kind) => kind switch
    {
        ReflectionTriggerKind.TurnNudge => "轮次",
        ReflectionTriggerKind.Idle => "空闲",
        ReflectionTriggerKind.Manual => "手动",
        _ => kind.ToString()
    };

    /// <summary>
    /// 复盘结果（<c>failed:timeout</c> 一类内部形态转成可读短语）。
    /// </summary>
    public static string ReflectionOutcomeText(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return None;

        var value = outcome.Trim();
        var separator = value.IndexOf(':');
        var head = separator >= 0 ? value[..separator] : value;
        var tail = separator >= 0 ? value[(separator + 1)..] : null;

        return head.ToLowerInvariant() switch
        {
            "suggested" => "已产出建议",
            "created" => "已创建技能",
            "skipped" => tail is null ? "已跳过" : $"已跳过（{SkipReasonText(tail)}）",
            "failed" => tail is null ? "失败" : $"失败（{SkipReasonText(tail)}）",
            _ => value
        };
    }

    /// <summary>复盘跳过/失败原因。</summary>
    public static string SkipReasonText(string? reason) => reason?.ToLowerInvariant() switch
    {
        "disabled" => "功能已关闭",
        "quota" => "超出当日额度",
        "rate-limit" => "触发过于频繁",
        "no-source" => "源会话不存在",
        "timeout" => "执行超时",
        "failed" => "执行失败",
        "parse-failed" => "结果无法解析",
        _ => reason ?? "未知原因"
    };

    // ===== 通用 =====

    /// <summary>耗时（统一 <c>12 分 3 秒</c> 形态，不再输出 <c>12m 3s</c> 或 <c>12s</c>）。</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";

        if (duration.TotalSeconds >= 1)
            return $"{duration.TotalSeconds:0.#} 秒";

        return $"{duration.TotalMilliseconds:0} 毫秒";
    }

    /// <summary>文件大小（1024 进制，保留一位小数）。</summary>
    public static string FileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} 字节";

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;

        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return $"{value:0.#} {units[unit]}";
    }
}
