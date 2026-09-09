namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 进程级结算宿主默认项（由 Host Shape 登记）。
/// Boot 优先级：<see cref="BootOverride"/> &gt; 文件 Boot &gt; <see cref="HostDefaultBoot"/> &gt; <c>*</c>。
/// </summary>
public sealed class ProcessSettlementOptions
{
    /// <summary>
    /// Host Shape 默认 scenario 名（进程默认工作模式；仅诊断/会话默认，不驱动 Activate）。
    /// </summary>
    public string? HostDefaultScenario { get; set; }

    /// <summary>
    /// Host Shape 默认 Boot（如 Embed→<c>minimal</c>）；文件未配置 <c>Boot</c> 时使用。
    /// </summary>
    public string? HostDefaultBoot { get; set; }

    /// <summary>
    /// 宿主/CLI/env 强制 Boot；优先于文件 Boot 与 <see cref="HostDefaultBoot"/>。
    /// 由 <see cref="BootOverrideSource"/> 从 <c>--boot</c> / <c>SEEING_BOOT</c> 解析后写入。
    /// </summary>
    public string? BootOverride { get; set; }

    /// <summary>
    /// Host Shape 默认 seam 绑定（至少应含 <c>executionWorld=io.local</c>，避免空 seam 拒启）。
    /// 用户 <c>SeeingAgent.Seams</c> 覆盖本表。
    /// </summary>
    public Dictionary<string, string>? HostDefaultSeams { get; set; }
}
