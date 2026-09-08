using System.ComponentModel.DataAnnotations;

namespace Seeing.Gateway.QQ;

/// <summary>
/// QQ 官方机器人配置
/// </summary>
public sealed class QQOptions
{
    public const string SectionName = "QQ";

    public bool Enabled { get; set; }

    [Required]
    [Display(Name = "App ID", Description = "QQ 机器人 AppID")]
    public string AppId { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Client Secret", Description = "QQ 机器人 ClientSecret")]
    public string ClientSecret { get; set; } = string.Empty;

    [Display(Name = "API 地址", Description = "QQ OpenAPI Base URL")]
    public string ApiBase { get; set; } = "https://api.sgroup.qq.com";

    [Display(Name = "Bot 前缀", GroupName = "消息")]
    public string? BotPrefix { get; set; }

    [Display(Name = "启用 Markdown", GroupName = "消息")]
    public bool MarkdownEnabled { get; set; } = true;

    [Display(Name = "群聊共享会话", GroupName = "会话")]
    public bool ShareSessionInGroup { get; set; } = true;

    [Display(Name = "会话空闲超时（分钟）", GroupName = "会话", Description = "0 表示禁用")]
    public int SessionIdleTimeoutMinutes { get; set; } = 30;

    [Display(Name = "最大重连次数", GroupName = "连接", Description = "-1 表示无限重连")]
    public int MaxReconnectAttempts { get; set; } = -1;

    [Display(Name = "收到消息提示", GroupName = "消息", Description = "非命令消息先回此提示，让用户知道已收到；置空则关闭")]
    public string? AckMessage { get; set; } = "收到，正在处理…";

    /// <summary>有效 Ack 文案；空白表示关闭。</summary>
    public string? EffectiveAckMessage =>
        string.IsNullOrWhiteSpace(AckMessage) ? null : AckMessage.Trim();

    [Display(Name = "媒体缓存目录", GroupName = "媒体")]
    public string? MediaCacheDirectory { get; set; }

    [Display(Name = "最大媒体大小（字节）", GroupName = "媒体")]
    public int MaxMediaBytes { get; set; } = 10 * 1024 * 1024;

    [Display(Name = "自动批准低风险", GroupName = "权限")]
    public bool AutoApproveLowRisk { get; set; } = true;

    [Display(Name = "需确认的风险等级", GroupName = "权限")]
    public List<string> PromptRiskLevels { get; set; } = ["high", "critical"];

    [Display(Name = "需确认的权限类型", GroupName = "权限")]
    public List<string> PromptPermissionKinds { get; set; } = ["shell", "file", "mcp_tool"];

    [Display(Name = "处理超时（秒）", GroupName = "消息")]
    public int ProcessingMaxDurationSeconds { get; set; } = 180;

    public int EffectiveProcessingMaxDurationSeconds =>
        ProcessingMaxDurationSeconds > 0 ? ProcessingMaxDurationSeconds : 180;
}
