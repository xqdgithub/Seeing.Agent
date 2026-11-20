namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 后台活动句柄——由 manager 唯一权威创建，经 <see cref="BackgroundActivityLaunchContext"/> 下发给 launcher。
/// <para>
/// launcher 必须原样使用 <see cref="Reporter"/> 与 <see cref="CancellationToken"/>，
/// 不得自建 CTS 或 reporter，也不得自行构造本记录。
/// </para>
/// </summary>
public sealed record BackgroundActivityHandle(
    string ActivityId,
    IBackgroundActivityReporter Reporter,
    CancellationToken CancellationToken);

/// <summary>launcher 启动上下文。</summary>
public sealed record BackgroundActivityLaunchContext(
    BackgroundActivityHandle Handle,
    BackgroundActivityStartRequest Request,
    string? WorkingDirectory);

/// <summary>
/// launcher 启动结果：<see cref="Execution"/> 是活动实际执行的任务。
/// <para>
/// manager 观察该任务：正常结束但未上报终态记 <c>Failed("no_terminal_state")</c>；抛异常记 <c>Failed</c>。
/// </para>
/// </summary>
public sealed record BackgroundActivityLaunchResult(Task Execution);
