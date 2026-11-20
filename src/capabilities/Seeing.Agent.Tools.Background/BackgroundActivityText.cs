using Seeing.Agent.Abstractions.Background;
using System.Text;

namespace Seeing.Agent.Tools.Background;

/// <summary>后台活动的文本渲染（状态行与详情），供 activity_* 工具共用。</summary>
internal static class BackgroundActivityText
{
    /// <summary>是否终态（终态只进一次，不可逆）。</summary>
    public static bool IsTerminal(BackgroundActivityStatus status) =>
        status is BackgroundActivityStatus.Succeeded
            or BackgroundActivityStatus.Failed
            or BackgroundActivityStatus.Cancelled
            or BackgroundActivityStatus.Interrupted;

    /// <summary>单行摘要：id | 状态 | kind | 标题 | 输出 | 结果。</summary>
    public static string FormatLine(BackgroundActivitySnapshot activity)
    {
        var builder = new StringBuilder()
            .Append(activity.Id)
            .Append(" | ")
            .Append(activity.Status)
            .Append(" | ")
            .Append(activity.Kind)
            .Append(" | ")
            .Append(activity.Title);

        if (activity.OutputBytes is { } bytes)
            builder.Append(" | 输出 ").Append(FormatBytes(bytes));

        if (activity.OutputTruncated)
            builder.Append("(已截断)");

        if (activity.Result?.ExitCode is { } exitCode)
            builder.Append(" | exit ").Append(exitCode);

        if (!string.IsNullOrEmpty(activity.Result?.ErrorMessage))
            builder.Append(" | ").Append(Truncate(activity.Result.ErrorMessage, 200));

        if (activity.CancelReason is { } reason)
            builder.Append(" | 取消原因 ").Append(reason);

        return builder.ToString();
    }

    /// <summary>详情：摘要 + 时间 + 输出 + 结果引用。</summary>
    public static string FormatDetail(BackgroundActivitySnapshot activity)
    {
        var lines = new List<string> { FormatLine(activity) };

        lines.Add(
            $"创建 {FormatTime(activity.CreatedAt)}"
            + $" | 开始 {FormatTime(activity.StartedAt)}"
            + $" | 完成 {FormatTime(activity.CompletedAt)}");

        if (activity.OutputBytes is { } bytes)
        {
            lines.Add(
                $"输出 {FormatBytes(bytes)} / 上限 {FormatBytes(activity.OutputLimitBytes)}"
                + (activity.OutputTruncated ? "（已达上限，仅保留前段）" : string.Empty));
        }

        if (activity.Progress is { } progress)
        {
            var text = new List<string>();
            if (!string.IsNullOrEmpty(progress.Message))
                text.Add(progress.Message);
            if (progress.UnitsDone is { } done && progress.UnitsTotal is { } total)
                text.Add($"{done}/{total}{progress.UnitsName}");
            else if (progress.Fraction is { } fraction)
                text.Add($"{fraction:P0}");
            if (text.Count > 0)
                lines.Add($"进度 {string.Join(" ", text)}");
        }

        if (activity.Result is { } result)
        {
            var text = new List<string> { $"kind={result.Kind}" };
            if (result.ExitCode is { } exitCode)
                text.Add($"exit={exitCode}");
            if (!string.IsNullOrEmpty(result.ArtifactPath))
                text.Add($"artifact={result.ArtifactPath}");
            if (!string.IsNullOrEmpty(result.ResultSessionId))
                text.Add($"session={result.ResultSessionId}");
            if (!string.IsNullOrEmpty(result.ErrorCode))
                text.Add($"error={result.ErrorCode}");
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                text.Add(Truncate(result.ErrorMessage, 400));
            lines.Add($"结果 {string.Join(" | ", text)}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>列表渲染；空列表返回 <paramref name="emptyMessage"/>。</summary>
    public static string FormatList(
        IReadOnlyList<BackgroundActivitySnapshot> activities,
        string emptyMessage)
    {
        if (activities.Count == 0)
            return emptyMessage;

        return string.Join("\n", activities.Select(FormatLine));
    }

    private static string FormatTime(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    private static string FormatBytes(long bytes) =>
        bytes < 1024
            ? $"{bytes}B"
            : bytes < 1024 * 1024
                ? $"{bytes / 1024.0:0.#}KB"
                : $"{bytes / (1024.0 * 1024.0):0.#}MB";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
