using System.Security;
using System.Text;

namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 把工具结果格式化为「回传给模型的内容」。
/// <para>
/// 成功：原样返回 <see cref="ToolResult.Output"/>，不改动。
/// 失败/拒绝/取消：包装为 <c>tool_result</c> XML 信封（含防误判 <c>notice</c> 与转义的 <c>error</c>），
/// 与 <c>system-reminder</c> / <c>project-instructions</c> 等既有信封约定一致，
/// 让模型明确区分「错误信息」与「工具返回的数据」，避免误判与错误消费。
/// </para>
/// <para>
/// 这是「工具结果 → 模型内容」的唯一格式化入口：本轮 <c>AgentExecutor</c>、跨轮历史重建
/// <c>ExecutionJobService</c>、UI 预览 <c>TaskCardAggregator</c> 均应收敛到此。
/// </para>
/// </summary>
public static class ToolResultFormatting
{
    /// <summary>
    /// 生成回传给模型的内容。
    /// </summary>
    /// <param name="succeeded">工具是否成功。</param>
    /// <param name="output">成功输出或失败时的兜底原因。</param>
    /// <param name="error">失败原因（优先于 <paramref name="output"/>）。</param>
    /// <param name="toolName">工具 Id（写入信封 <c>name</c> 属性）。</param>
    /// <param name="status">非正常终态：<c>failed</c>/<c>rejected</c>/<c>cancelled</c>（其余归一为 failed）。</param>
    /// <param name="title">可读标题（如「执行超时」「重试耗尽」）。</param>
    public static string ToModelContent(
        bool succeeded,
        string? output,
        string? error,
        string? toolName = null,
        string? status = null,
        string? title = null)
    {
        if (succeeded)
            return output ?? string.Empty;

        var normalizedStatus = NormalizeStatus(status);
        var reason = !string.IsNullOrEmpty(error) ? error : output;
        if (string.IsNullOrEmpty(reason))
            reason = "(未提供错误详情)";

        var sb = new StringBuilder();
        sb.Append("<tool_result");
        if (!string.IsNullOrEmpty(toolName))
            sb.Append(" name=\"").Append(EscapeAttribute(toolName)).Append('"');
        sb.Append(" status=\"").Append(EscapeAttribute(normalizedStatus)).Append('"');
        if (!string.IsNullOrEmpty(title))
            sb.Append(" title=\"").Append(EscapeAttribute(title)).Append('"');
        sb.AppendLine(">");
        sb.AppendLine("<notice>");
        sb.AppendLine(ResolveNotice(normalizedStatus));
        sb.AppendLine("</notice>");
        sb.Append("<error>").Append(EscapeBody(reason)).AppendLine("</error>");
        sb.Append("</tool_result>");
        return sb.ToString();
    }

    private static string NormalizeStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "rejected" => "rejected",
        "cancelled" => "cancelled",
        _ => "failed"
    };

    private static string ResolveNotice(string status) => status switch
    {
        "rejected" => "该工具调用被拒绝；以下为原因，不是工具返回的数据。",
        "cancelled" => "该工具调用已取消；以下为原因，不是工具返回的数据。",
        _ => "该工具调用未成功；以下为错误信息，不是工具返回的数据。请据此修正后重试。"
    };

    private static string EscapeAttribute(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string EscapeBody(string body) => body.Replace("</", "<\\/", StringComparison.Ordinal);
}
