using System.ComponentModel.DataAnnotations;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企业微信 AI Bot 配置
/// </summary>
public sealed class WeComOptions
{
    public const string SectionName = "WeCom";

    public bool Enabled { get; set; }

    [Required]
    [Display(Name = "Bot ID", Description = "企业微信 AI Bot 的 BotId")]
    public string BotId { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Secret", Description = "企业微信 AI Bot 的 Secret")]
    public string Secret { get; set; } = string.Empty;

    [Display(Name = "WebSocket 地址", Description = "企业微信 WebSocket 接入地址")]
    public string WsUrl { get; set; } = "wss://openws.work.weixin.qq.com";

    [Display(Name = "群聊共享会话", GroupName = "会话")]
    public bool ShareSessionInGroup { get; set; } = true;

    [Display(Name = "会话空闲超时（分钟）", GroupName = "会话", Description = "超过该时间无消息则自动开启新 sessionId；0 表示禁用")]
    public int SessionIdleTimeoutMinutes { get; set; } = 30;

    [Display(Name = "enter_chat 超时重置", GroupName = "会话", Description = "用户重新打开聊天且已超时时自动开启新 session")]
    public bool ResetOnEnterChatWhenIdle { get; set; } = true;

    [Display(Name = "会话状态文件", GroupName = "会话", Description = "conversationKey 映射持久化路径，默认 .seeing/gateway-clients/wecom.sessions.json")]
    public string? SessionStateFile { get; set; }

    [Display(Name = "欢迎语附加命令提示", GroupName = "会话")]
    public bool AppendCommandHintsToWelcome { get; set; } = true;

    public const string DefaultCommandHints = "发送 /new 开启新对话，/clear 清空当前上下文。";

    public const string DefaultWelcomeText = "你好！我是 Seeing Agent，有什么可以帮你的？";

    /// <summary>获取带命令提示的欢迎语</summary>
    public string GetEffectiveWelcomeText()
    {
        var baseText = string.IsNullOrWhiteSpace(WelcomeText) ? DefaultWelcomeText : WelcomeText!;
        if (!AppendCommandHintsToWelcome || baseText.Contains("/new", StringComparison.Ordinal))
            return baseText;

        return $"{baseText}\n\n{DefaultCommandHints}";
    }

    [Display(Name = "最大重连次数", GroupName = "连接")]
    public int MaxReconnectAttempts { get; set; } = -1;

    [Display(Name = "流式回复", GroupName = "消息")]
    public bool StreamingEnabled { get; set; } = true;

    [Display(Name = "Bot 前缀", GroupName = "消息")]
    public string? BotPrefix { get; set; }

    [Display(Name = "欢迎语", GroupName = "消息")]
    public string? WelcomeText { get; set; } = DefaultWelcomeText;

    [Display(Name = "心跳间隔（秒）", GroupName = "连接")]
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    [Display(Name = "增量节流（毫秒）", GroupName = "消息")]
    public int DeltaThrottleMilliseconds { get; set; } = 150;

    [Display(Name = "处理中刷新（秒）", GroupName = "消息")]
    public int ProcessingRefreshSeconds { get; set; } = 20;

    [Display(Name = "处理超时（秒）", GroupName = "消息")]
    public int ProcessingMaxDurationSeconds { get; set; } = 180;

    /// <summary>低风险权限是否自动批准</summary>
    [Display(Name = "自动批准低风险", GroupName = "权限")]
    public bool AutoApproveLowRisk { get; set; } = true;

    /// <summary>需要用户确认的风险等级</summary>
    [Display(Name = "需确认的风险等级", GroupName = "权限")]
    public List<string> PromptRiskLevels { get; set; } = ["high", "critical"];

    /// <summary>需要用户确认的权限类型</summary>
    [Display(Name = "需确认的权限类型", GroupName = "权限")]
    public List<string> PromptPermissionKinds { get; set; } = ["shell", "file", "mcp_tool"];

    /// <summary>模板卡片批准按钮 event_key</summary>
    [Display(Name = "批准按钮 Key", GroupName = "权限")]
    public List<string> AllowEventKeys { get; set; } = ["allow", "approve", "confirm"];

    /// <summary>模板卡片拒绝按钮 event_key</summary>
    [Display(Name = "拒绝按钮 Key", GroupName = "权限")]
    public List<string> DenyEventKeys { get; set; } = ["deny", "reject", "false_alarm"];

    /// <summary>媒体缓存目录</summary>
    [Display(Name = "媒体缓存目录", GroupName = "媒体")]
    public string? MediaCacheDirectory { get; set; }

    /// <summary>单文件最大字节数</summary>
    [Display(Name = "最大媒体大小（字节）", GroupName = "媒体")]
    public int MaxMediaBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>权限卡片映射 TTL（秒）</summary>
    [Display(Name = "权限卡片 TTL（秒）", GroupName = "权限")]
    public int PermissionCardTtlSeconds { get; set; } = 600;
}
