namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台活动治理参数。
/// <para>
/// 只承载「上限 / 默认值」这类进程级治理事实；kind 专属策略不放这里（那属于 launcher）。
/// </para>
/// </summary>
public sealed class BackgroundActivityOptions
{
    /// <summary><c>seeing.json</c> 中的节名（写在 <c>SeeingAgent</c> 下）。</summary>
    public const string SectionName = "BackgroundActivities";

    /// <summary>单会话同时运行的活动上限（<c>Pending</c> 也计入）。默认 8。</summary>
    public int MaxConcurrentPerSession { get; set; } = 8;

    /// <summary>请求未指定超时时的默认值。默认 30 分钟。</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>请求指定的超时上限（超出按此截断）。默认 24 小时。</summary>
    public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>请求未指定输出上限时的默认值（字节）。默认 5 MiB。</summary>
    public long DefaultOutputLimitBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// 终态活动的保留期：<see cref="BackgroundActivitySnapshot.CompletedAt"/> 早于
    /// <c>now - Retention</c> 的记录会被回收。默认 7 天。
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>保留期回收的扫描间隔。默认 1 小时。</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 会话面 <c>List</c> 单次返回的最大条数（按创建时间倒序截断）。
    /// <para>非正值表示不截断；默认 200。</para>
    /// </summary>
    public int ListLimit { get; set; } = 200;
}
