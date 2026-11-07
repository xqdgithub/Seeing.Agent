using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>局部更新心跳配置：未提供的字段保持不变。</summary>
public sealed class HeartbeatUpdateTool : ToolBase
{
    private readonly IHeartbeatService _service;

    public HeartbeatUpdateTool(ILogger<HeartbeatUpdateTool> logger, IHeartbeatService service) : base(logger)
    {
        _service = service;
    }

    public override string Id => "heartbeat_update";

    public override string Description =>
        "局部更新心跳配置；只改传入的字段，未传的字段保持原值。首次调用会创建心跳配置。\n\n" +
        "- enabled：开关心跳周期触发。\n" +
        "- prompt：每次心跳交给 Agent 的指令（启用时必填）。\n" +
        "- every：间隔，如 6h / 30m。心跳只支持间隔语义，不接受 cron 表达式或一次性时间。\n" +
        "- windows：生效时段（可选），空数组表示全天；允许跨午夜、禁止交叠。\n" +
        "- session_id / conversation：目标会话或出站对话，两者至少一项。\n" +
        "- agent / model / timeout_seconds：执行身份与超时。\n" +
        "- 返回更新后的完整状态；校验失败会说明原因且配置不变。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            enabled = new { type = "boolean", description = "是否启用周期触发" },
            prompt = new { type = "string", description = "心跳指令（交给 Agent 的任务文本）" },
            every = new { type = "string", description = "间隔，如 6h、30m" },
            timezone = new { type = "string", description = "时区 ID，默认本地" },
            windows = new
            {
                type = "array",
                description = "可选生效时段；空数组表示恢复全天。start 缺省 00:00，end 缺省 23:59:59。",
                items = new
                {
                    type = "object",
                    description = "单个生效时段：start 与 end 均为 timezone 墙钟时间",
                    properties = new
                    {
                        start = new { type = "string", description = "HH:mm 或 HH:mm:ss" },
                        end = new { type = "string", description = "HH:mm 或 HH:mm:ss" }
                    }
                }
            },
            session_id = new { type = "string", description = "目标会话 ID" },
            conversation = new
            {
                type = "object",
                description = "可选出站目标；channelId/kind/externalId 需齐备",
                properties = new
                {
                    channelId = new { type = "string", description = "通道 ID（如 qq / wecom）" },
                    kind = new { type = "string", description = "会话种类：direct（私聊）| group（群聊）| guild（频道）" },
                    externalId = new { type = "string", description = "通道侧会话 ID（群号 / 用户 ID / 频道 ID）" },
                    title = new { type = "string", description = "可选显示名" }
                },
                required = new[] { "channelId", "kind", "externalId" }
            },
            agent = new { type = "string", description = "Agent ID，留空沿用默认 Agent" },
            model = new { type = "string", description = "模型 ID" },
            timeout_seconds = new { type = "integer", description = "单次执行超时秒数" }
        }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var update = new HeartbeatUpdate
        {
            Enabled = GetBoolArgument(arguments, "enabled"),
            Prompt = GetStringArgument(arguments, "prompt"),
            Every = GetStringArgument(arguments, "every"),
            Timezone = GetStringArgument(arguments, "timezone"),
            SessionId = GetStringArgument(arguments, "session_id"),
            Agent = GetStringArgument(arguments, "agent"),
            Model = GetStringArgument(arguments, "model"),
            TimeoutSeconds = GetIntArgument(arguments, "timeout_seconds"),
            Windows = ReadWindows(arguments),
            Conversation = ReadConversation(arguments)
        };

        try
        {
            var snapshot = await _service.UpdateAsync(update, context.CancellationToken).ConfigureAwait(false);
            var spec = snapshot.Spec!;

            var sb = new StringBuilder();
            sb.AppendLine(ScheduleTextFormatter.DescribeJob(
                spec, snapshot.Status, schedulerStarted: snapshot.SchedulerStarted));
            sb.AppendLine($"  上次执行: {ScheduleTextFormatter.DescribeHeartbeatLastRun(snapshot.LastRun)}");

            return Success("心跳配置已更新", sb.ToString().TrimEnd());
        }
        catch (ArgumentException ex)
        {
            return Failure($"心跳配置无效: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    private static IReadOnlyList<ScheduleWindow>? ReadWindows(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("windows", out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<ScheduleWindow>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(new ScheduleWindow
            {
                Start = item.TryGetProperty("start", out var s) ? s.GetString() : null,
                End = item.TryGetProperty("end", out var e) ? e.GetString() : null
            });
        }

        return list;
    }

    private static ConversationSpec? ReadConversation(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("conversation", out var element)
            || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var spec = new ConversationSpec
        {
            ChannelId = element.TryGetProperty("channelId", out var c) ? c.GetString() : null,
            Kind = element.TryGetProperty("kind", out var k) ? k.GetString() : null,
            ExternalId = element.TryGetProperty("externalId", out var x) ? x.GetString() : null,
            Title = element.TryGetProperty("title", out var t) ? t.GetString() : null
        };

        return spec.IsComplete ? spec : null;
    }
}
