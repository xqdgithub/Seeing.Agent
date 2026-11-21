using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.Background;

/// <summary>
/// 后台活动等待工具 — 阻塞到活动进入终态或超时。
/// <para>
/// 只观察调用方的取消令牌：被取消时返回 <see cref="BackgroundActivityWaitOutcome.CallerCancelled"/>，
/// 绝不取消活动本身；活动随后完成仍会照常唤醒。
/// </para>
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
[ToolCapability(ToolCapabilityKeys.LoopSkip, "true")]
public sealed class ActivityWaitTool : ToolBase
{
    private const int DefaultTimeoutMs = 600_000;
    private const int MaxTimeoutMs = 86_400_000;

    private readonly IBackgroundActivityManager _activities;

    /// <summary>创建 ActivityWaitTool 实例。</summary>
    public ActivityWaitTool(
        ILogger<ActivityWaitTool> logger,
        IBackgroundActivityManager activities) : base(logger)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
    }

    /// <inheritdoc />
    public override string Id => "activity_wait";

    /// <inheritdoc />
    public override string Description =>
        "等待后台活动结束（或超时）后返回最终状态。\n\n" +
        "- timeout_ms 默认 600000（10 分钟），上限 86400000（24 小时）；0 表示只查当前状态。\n" +
        "- 超时不会取消活动，活动继续运行；可再次等待或用 `activity_status` 查看。\n" +
        "- 调用方中断只停止等待，不影响活动本身。\n" +
        "- 只对本会话归属的活动有效；他人活动按超时处理。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            activity_id = new
            {
                type = "string",
                description = "后台活动 id（如 act_xxx）"
            },
            timeout_ms = new
            {
                type = "integer",
                minimum = 0,
                maximum = MaxTimeoutMs,
                description = $"等待超时（毫秒），默认 {DefaultTimeoutMs}，上限 {MaxTimeoutMs}；0 表示立即返回当前状态"
            }
        },
        required = new[] { "activity_id" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var activityId = GetStringArgument(arguments, "activity_id");
        if (string.IsNullOrWhiteSpace(activityId))
            return Failure("缺少必填参数 activity_id。");

        var timeoutMs = Math.Clamp(GetIntArgument(arguments, "timeout_ms") ?? DefaultTimeoutMs, 0, MaxTimeoutMs);

        var wait = await _activities.WaitAsync(
            activityId,
            TimeSpan.FromMilliseconds(timeoutMs),
            context.SessionId,
            context.CancellationToken).ConfigureAwait(false);

        var metadata = new Dictionary<string, object>
        {
            ["activity_id"] = activityId,
            ["outcome"] = wait.Outcome.ToString(),
            ["timeout_ms"] = timeoutMs,
        };
        if (wait.Snapshot is not null)
            metadata["status"] = wait.Snapshot.Status.ToString();

        switch (wait.Outcome)
        {
            case BackgroundActivityWaitOutcome.Completed when wait.Snapshot is not null:
                _activities.MarkResultObserved(activityId, context.SessionId);

                // 终态时直接带上输出，避免调用方再补一次 activity_output。
                var (output, truncated) = await ReadOutputAsync(
                    activityId, context.SessionId, context.CancellationToken).ConfigureAwait(false);

                var text = $"活动已结束。\n{BackgroundActivityText.FormatDetail(wait.Snapshot)}";
                if (!string.IsNullOrEmpty(output))
                {
                    text += "\n\n输出:\n" + output;
                    if (truncated)
                        text += "\n\n...(输出已截断，完整内容用 `activity_output` 分页读取)";
                }

                return Success(text, metadata);

            case BackgroundActivityWaitOutcome.Completed:
                return Success("活动已结束（无可用快照）。", metadata);

            case BackgroundActivityWaitOutcome.CallerCancelled:
                return Success(
                    "等待已被调用方中断；活动本身继续运行，可用 `activity_status` 查看状态。",
                    metadata);

            case BackgroundActivityWaitOutcome.Detached:
                return Success(
                    "等待已转为后台；活动仍在运行，可用 `activity_status` 查看状态。",
                    metadata);

            default:
                var current = wait.Snapshot is null
                    ? string.Empty
                    : $"\n{BackgroundActivityText.FormatLine(wait.Snapshot)}";
                return Success(
                    $"等待超时（{timeoutMs}ms），活动仍在运行。{current}",
                    metadata);
        }
    }

    /// <summary>
    /// 分页读回活动输出（单页 32KB，上限 64KB）。
    /// 返回 <c>Truncated</c> 表示还有更多内容，调用方应使用 <c>activity_output</c> 继续读。
    /// </summary>
    private async Task<(string Text, bool Truncated)> ReadOutputAsync(
        string activityId,
        string sessionId,
        CancellationToken ct)
    {
        const int pageBytes = 32 * 1024;
        const int maxBytes = 64 * 1024;

        var builder = new StringBuilder();
        long offset = 0;
        var readBytes = 0;
        var truncated = false;

        while (readBytes < maxBytes)
        {
            var page = await _activities.ReadOutputAsync(
                    activityId, offset, Math.Min(pageBytes, maxBytes - readBytes), sessionId, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(page.Content))
            {
                builder.Append(page.Content);
                readBytes += Encoding.UTF8.GetByteCount(page.Content);
            }

            if (page.Truncated)
                truncated = true;

            if (page.EndOfStream)
                break;

            if (page.NextOffset <= offset)
            {
                truncated = true;
                break;
            }

            offset = page.NextOffset;
        }

        if (readBytes >= maxBytes)
            truncated = true;

        return (builder.ToString(), truncated);
    }
}
