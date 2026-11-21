using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Tools.Background;

/// <summary>
/// 后台活动列表工具 — 按状态 / 种类 / 是否含终态筛选本会话的活动。
/// <para>只返回归属本会话的活动；跨会话查询一律不可用。</para>
/// </summary>
public sealed class ActivityListTool : ToolBase
{
    private readonly IBackgroundActivityManager _activities;

    /// <summary>创建 ActivityListTool 实例。</summary>
    public ActivityListTool(
        ILogger<ActivityListTool> logger,
        IBackgroundActivityManager activities) : base(logger)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
    }

    /// <inheritdoc />
    public override string Id => "activity_list";

    /// <inheritdoc />
    public override string Description =>
        "列出本会话的后台活动，可按状态、种类筛选。\n\n" +
        "- status：Pending / Running / Succeeded / Failed / Cancelled / Interrupted。\n" +
        "- kind：活动种类（如 process），开放字符串，精确匹配。\n" +
        "- include_completed 默认 true；置 false 只看未结束的活动。\n" +
        "- 返回 id / 状态 / 种类 / 标题 / 输出大小 / 结果摘要；查看详情用 `activity_status`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            status = new
            {
                type = "string",
                @enum = new[]
                {
                    "Pending",
                    "Running",
                    "Succeeded",
                    "Failed",
                    "Cancelled",
                    "Interrupted"
                },
                description = "只看指定状态的活动（Pending / Running / Succeeded / Failed / Cancelled / Interrupted）；省略为全部状态"
            },
            kind = new
            {
                type = "string",
                description = "只看指定种类的活动（如 process）；省略为全部种类"
            },
            include_completed = new
            {
                type = "boolean",
                description = "是否包含已结束的活动（默认 true）"
            }
        }
    });

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var statusText = GetStringArgument(arguments, "status");
        BackgroundActivityStatus? status = null;
        if (!string.IsNullOrWhiteSpace(statusText))
        {
            if (!Enum.TryParse<BackgroundActivityStatus>(statusText, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                return Task.FromResult(Failure(
                    $"未知的 status: {statusText}。可选值：Pending、Running、Succeeded、Failed、Cancelled、Interrupted"));
            }

            status = parsed;
        }

        var kind = GetStringArgument(arguments, "kind");
        var includeCompleted = GetBoolArgument(arguments, "include_completed") ?? true;

        var query = new BackgroundActivityQuery(
            status,
            string.IsNullOrWhiteSpace(kind) ? null : kind,
            includeCompleted);

        var activities = _activities.List(query, context.SessionId);

        return Task.FromResult(Success(
            BackgroundActivityText.FormatList(activities, "未找到匹配的后台活动。"),
            new Dictionary<string, object>
            {
                ["total"] = activities.Count,
                ["returned"] = activities.Count,
            }));
    }
}
