namespace Seeing.Agent.Abstractions.Background;

/// <summary>
/// 按 <see cref="Kind"/> 路由执行的启动器。
/// <para>
/// launcher 只负责执行面：从 <see cref="BackgroundActivityLaunchContext.Request"/> 取入参（<c>Payload</c> 由它与发起方约定），
/// 原样使用 <c>ctx.Handle</c> 的 reporter 与 token；<b>不得</b>引用 <c>ToolContext.CancellationToken</c> /
/// <c>ToolContext.EventSink</c>，也不得自建 CTS 或活动句柄。
/// </para>
/// </summary>
public interface IBackgroundActivityLauncher
{
    /// <summary>本 launcher 处理的 kind。</summary>
    string Kind { get; }

    /// <summary>本 kind 的能力声明。</summary>
    BackgroundActivityCapabilities Capabilities { get; }

    /// <summary>启动执行；返回的 <see cref="BackgroundActivityLaunchResult.Execution"/> 由 manager 观察。</summary>
    Task<BackgroundActivityLaunchResult> LaunchAsync(
        BackgroundActivityLaunchContext ctx,
        CancellationToken ct);

    /// <summary>取消执行（manager 已先写入 <c>Cancelled</c> 再通知 launcher）。</summary>
    Task CancelAsync(BackgroundActivityHandle handle, BackgroundActivityCancelReason reason);
}
