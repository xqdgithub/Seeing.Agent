namespace Seeing.Gateway.WeCom;

/// <summary>
/// 企业微信 AI Bot 配置
/// </summary>
public sealed class WeComOptions
{
    public const string SectionName = "WeCom";

    public bool Enabled { get; set; }

    public string BotId { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public string WsUrl { get; set; } = "wss://openws.work.weixin.qq.com";

    public bool ShareSessionInGroup { get; set; } = true;

    public int MaxReconnectAttempts { get; set; } = -1;

    public bool StreamingEnabled { get; set; } = true;

    public string? BotPrefix { get; set; }

    public string? WelcomeText { get; set; } = "你好！我是 Seeing Agent，有什么可以帮你的？";

    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public int DeltaThrottleMilliseconds { get; set; } = 150;

    public int ProcessingRefreshSeconds { get; set; } = 20;

    public int ProcessingMaxDurationSeconds { get; set; } = 180;

    /// <summary>低风险权限是否自动批准</summary>
    public bool AutoApproveLowRisk { get; set; } = true;

    /// <summary>需要用户确认的风险等级</summary>
    public List<string> PromptRiskLevels { get; set; } = ["high", "critical"];

    /// <summary>需要用户确认的权限类型</summary>
    public List<string> PromptPermissionKinds { get; set; } = ["shell", "file", "mcp_tool"];

    /// <summary>模板卡片批准按钮 event_key</summary>
    public List<string> AllowEventKeys { get; set; } = ["allow", "approve", "confirm"];

    /// <summary>模板卡片拒绝按钮 event_key</summary>
    public List<string> DenyEventKeys { get; set; } = ["deny", "reject", "false_alarm"];

    /// <summary>媒体缓存目录</summary>
    public string? MediaCacheDirectory { get; set; }

    /// <summary>单文件最大字节数</summary>
    public int MaxMediaBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>权限卡片映射 TTL（秒）</summary>
    public int PermissionCardTtlSeconds { get; set; } = 600;
}
