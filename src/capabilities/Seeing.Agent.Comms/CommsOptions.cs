namespace Seeing.Agent.Comms;

/// <summary>
/// 通联配置节（<c>seeing.json</c> 的 <c>Comms</c>）。
/// 目录与审计文件是运行时数据，不进配置节注册表。
/// </summary>
public sealed class CommsOptions
{
    public const string SectionName = "Comms";

    /// <summary>每用户每小时主动投递上限。0 关闭频控。</summary>
    public int RateLimitPerHour { get; set; } = 20;

    /// <summary>目录文件。空则 <c>~/.seeing/comms/directory.json</c>。</summary>
    public string? DirectoryFilePath { get; set; }

    /// <summary>审计日志。空则不写。</summary>
    public string? AuditLogPath { get; set; }
}
