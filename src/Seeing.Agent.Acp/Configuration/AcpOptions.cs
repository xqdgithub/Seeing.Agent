namespace Seeing.Agent.Acp.Configuration;

/// <summary>
/// ACP 集成配置（JSON: SeeingAgent:Acp）
/// </summary>
public class AcpOptions
{
    public const string SectionName = "Acp";

    /// <summary>是否启用 ACP 集成</summary>
    public bool Enabled { get; set; }

    /// <summary>默认 ACP 后端标识</summary>
    public string? DefaultBackend { get; set; }

    /// <summary>请求超时</summary>
    public TimeSpan RequestTimeout { get; set; } = AcpOptionsDefaults.DefaultRequestTimeout;

    /// <summary>空闲超时（子进程无使用后多久终止）</summary>
    public TimeSpan IdleTimeout { get; set; } = AcpOptionsDefaults.DefaultIdleTimeout;

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
