using Seeing.Gateway.Models;

namespace Seeing.Gateway.Channels;

/// <summary>
/// 按 Gateway 完成信号契约，从 <see cref="GatewayEvent"/> 流累积 assistant 可见文本。
/// <para>
/// 契约：<see cref="GatewayEventObject.Content"/>+Delta 为增量；
/// <see cref="GatewayEventObject.Message"/>+Completed 为单轮 assistant 快照（仅填补空累积）；
/// <see cref="GatewayRunTerminal.Completed"/> 表示整轮结束。
/// </para>
/// </summary>
public sealed class GatewayAssistantReplyCollector
{
    public const string LoopCompleteSourceType = "LoopComplete";

    public string Text { get; private set; } = string.Empty;

    public GatewayRunTerminal Terminal { get; private set; } = GatewayRunTerminal.None;

    public string? TerminalMessage { get; private set; }

    /// <summary>应用事件并返回建议的 Bridge 动作。</summary>
    public GatewayReplyDisposition Apply(GatewayEvent gatewayEvent)
    {
        switch (gatewayEvent.Object)
        {
            case GatewayEventObject.Content when gatewayEvent.Data?.Delta == true:
                if (!string.IsNullOrEmpty(gatewayEvent.Data.Text))
                    Text += gatewayEvent.Data.Text;
                return GatewayReplyDisposition.StreamUpdated;

            case GatewayEventObject.Message when gatewayEvent.Status == GatewayEventStatus.Completed:
                ApplyCompletedMessage(gatewayEvent.Data?.Text);
                return GatewayReplyDisposition.MessageSnapshot;

            case GatewayEventObject.Response when gatewayEvent.Status == GatewayEventStatus.Completed
                && IsLoopComplete(gatewayEvent):
                Terminal = GatewayRunTerminal.Completed;
                return GatewayReplyDisposition.RunCompleted;

            case GatewayEventObject.Response when gatewayEvent.Status == GatewayEventStatus.Cancelled:
                Terminal = GatewayRunTerminal.Cancelled;
                TerminalMessage = gatewayEvent.Data?.CancelReason ?? "已取消";
                return GatewayReplyDisposition.RunCancelled;

            case GatewayEventObject.Error:
                Terminal = GatewayRunTerminal.Failed;
                TerminalMessage = gatewayEvent.Data?.Error ?? "Agent 执行失败";
                return GatewayReplyDisposition.RunFailed;

            case GatewayEventObject.Permission when gatewayEvent.Status == GatewayEventStatus.InProgress:
                return GatewayReplyDisposition.Permission;

            default:
                return GatewayReplyDisposition.Ignored;
        }
    }

    public bool IsTerminal => Terminal != GatewayRunTerminal.None;

    private void ApplyCompletedMessage(string? completedText)
    {
        if (string.IsNullOrWhiteSpace(completedText))
            return;

        if (string.IsNullOrWhiteSpace(Text))
            Text = completedText;
    }

    private static bool IsLoopComplete(GatewayEvent gatewayEvent) =>
        string.Equals(gatewayEvent.SourceType, LoopCompleteSourceType, StringComparison.Ordinal);
}

public enum GatewayRunTerminal
{
    None,
    Completed,
    Cancelled,
    Failed
}

public enum GatewayReplyDisposition
{
    Ignored,
    StreamUpdated,
    MessageSnapshot,
    Permission,
    RunCompleted,
    RunCancelled,
    RunFailed
}
