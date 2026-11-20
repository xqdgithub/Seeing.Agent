using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.Background;

/// <summary>
/// 后台活动状态工具 — 给 id 查单个详情；不给 id 列出本会话的全部活动。
/// <para>
/// 只读本会话归属的活动；跨会话读取由 manager 的归属/可见性规则决定。
/// 读到终态时标记结果已观察，避免完成后重复唤醒发起会话。
/// </para>
/// </summary>
[ToolCapability(ToolCapabilityKeys.LoopSkip, "true")]
public sealed class ActivityStatusTool : ToolBase
{
    private readonly IBackgroundActivityManager _activities;

    /// <summary>创建 ActivityStatusTool 实例。</summary>
    public ActivityStatusTool(
        ILogger<ActivityStatusTool> logger,
        IBackgroundActivityManager activities) : base(logger)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
    }

    /// <inheritdoc />
    public override string Id => "activity_status";

    /// <inheritdoc />
    public override string Description =>
        "查看后台活动状态：给 activity_id 查单个，不给则列出本会话全部活动。\n\n" +
        "- 只返回本会话归属的活动；他人活动视为不存在。\n" +
        "- 活动终态为 Succeeded / Failed / Cancelled / Interrupted，之后不再变化。\n" +
        "- 读输出用 `activity_output`，等待结束用 `activity_wait`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            activity_id = new
            {
                type = "string",
                description = "后台活动 id（如 act_xxx）；省略则列出本会话全部活动"
            }
        }
    });

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var activityId = GetStringArgument(arguments, "activity_id");

        if (!string.IsNullOrWhiteSpace(activityId))
        {
            var activity = _activities.Get(activityId, context.SessionId);
            if (activity is null)
            {
                return Task.FromResult(Success(
                    $"未找到活动 {activityId}：它不存在，或不属于本会话。",
                    new Dictionary<string, object> { ["activity_id"] = activityId }));
            }

            // 终态一旦被读到即视为已观察：dispatcher 不再投递完成唤醒。
            if (BackgroundActivityText.IsTerminal(activity.Status))
                _activities.MarkResultObserved(activityId, context.SessionId);

            return Task.FromResult(Success(
                BackgroundActivityText.FormatDetail(activity),
                new Dictionary<string, object>
                {
                    ["activity_id"] = activity.Id,
                    ["status"] = activity.Status.ToString(),
                    ["kind"] = activity.Kind,
                }));
        }

        var own = _activities.List(new BackgroundActivityQuery(), context.SessionId);
        return Task.FromResult(Success(
            BackgroundActivityText.FormatList(own, "当前会话没有后台活动。"),
            new Dictionary<string, object>
            {
                ["total"] = own.Count,
                ["returned"] = own.Count,
            }));
    }
}
