using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.Background;

/// <summary>
/// 后台活动输出工具 — 按 offset / max_bytes 分页读取活动输出。
/// <para>
/// 单页上限 32KB（与输出存储一致），工具自身声明 <c>output.skip</c>，
/// 由本工具负责分页与截断提示，避免被通用输出限制再次包装。
/// </para>
/// </summary>
[ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
public sealed class ActivityOutputTool : ToolBase
{
    private const int MaxBytesLimit = 32768;
    private const int DefaultMaxBytes = 32768;

    private readonly IBackgroundActivityManager _activities;

    /// <summary>创建 ActivityOutputTool 实例。</summary>
    public ActivityOutputTool(
        ILogger<ActivityOutputTool> logger,
        IBackgroundActivityManager activities) : base(logger)
    {
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
    }

    /// <inheritdoc />
    public override string Id => "activity_output";

    /// <inheritdoc />
    public override string Description =>
        "分页读取后台活动的输出。\n\n" +
        "- offset 为起始字节偏移（默认 0），max_bytes 默认且最大 32768（32KB）。\n" +
        "- 返回内容未结束时按提示用新的 offset 续读；活动仍在运行也可读取当前已写入部分。\n" +
        "- 只返回本会话归属的活动；他人活动返回空页。";

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
            offset = new
            {
                type = "integer",
                minimum = 0,
                description = "起始字节偏移（默认 0）"
            },
            max_bytes = new
            {
                type = "integer",
                minimum = 1,
                maximum = MaxBytesLimit,
                description = $"单次读取字节数（默认 {DefaultMaxBytes}，上限 {MaxBytesLimit}=32KB）"
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

        var offset = Math.Max(GetIntArgument(arguments, "offset") ?? 0, 0);
        var maxBytes = Math.Clamp(GetIntArgument(arguments, "max_bytes") ?? DefaultMaxBytes, 1, MaxBytesLimit);

        var page = await _activities.ReadOutputAsync(
            activityId,
            offset,
            maxBytes,
            context.SessionId,
            context.CancellationToken).ConfigureAwait(false);

        var metadata = new Dictionary<string, object>
        {
            ["activity_id"] = activityId,
            ["offset"] = offset,
            ["next_offset"] = page.NextOffset,
            ["end_of_stream"] = page.EndOfStream,
            ["truncated"] = page.Truncated,
        };

        if (string.IsNullOrEmpty(page.Content) && page.EndOfStream)
        {
            return Success(
                offset == 0
                    ? "活动暂无输出（或不存在 / 不属于本会话）。"
                    : $"已到输出末尾（offset={offset}）。",
                metadata);
        }

        var builder = new StringBuilder(page.Content);
        if (!page.EndOfStream)
            builder.Append("\n\n[输出未结束：用 offset=").Append(page.NextOffset).Append(" 继续读取]");
        if (page.Truncated)
            builder.Append("\n[输出已达活动限额，仅保留前段]");

        return Success(builder.ToString(), metadata);
    }
}
