using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.Background;

/// <summary>
/// 后台活动取消工具 — 取消活动及其子树，取消原因记为 <see cref="BackgroundActivityCancelReason.User"/>。
/// <para>只能取消本会话归属的活动；越权或活动已结束返回失败。</para>
/// </summary>
public sealed class ActivityCancelTool : ToolBase
{
    private readonly IBackgroundActivityManager _activities;

    /// <summary>创建 ActivityCancelTool 实例。</summary>
    public ActivityCancelTool(
        ILogger<ActivityCancelTool> logger,
        IBackgroundActivityManager activities) : base(logger)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
    }

    /// <inheritdoc />
    public override string Id => "activity_cancel";

    /// <inheritdoc />
    public override string Description =>
        "取消一个后台活动及其全部子活动（原因记为 User）。\n\n" +
        "- 只能取消本会话归属且尚未结束的活动；活动已结束、越权或不存在时返回失败。\n" +
        "- 取消后仍可用 `activity_status` 查看最终状态与输出。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            activity_id = new
            {
                type = "string",
                description = "要取消的后台活动 id（如 act_xxx）"
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

        var cancelled = await _activities.CancelAsync(
            activityId,
            BackgroundActivityCancelReason.User,
            context.SessionId,
            context.CancellationToken).ConfigureAwait(false);

        if (!cancelled)
            return Failure($"无法取消活动 {activityId}：它不存在、已结束，或不属于本会话。");

        return Success(
            $"已请求取消活动 {activityId}（含子活动）。",
            new Dictionary<string, object>
            {
                ["activity_id"] = activityId,
                ["cancel_reason"] = BackgroundActivityCancelReason.User.ToString(),
            });
    }
}
