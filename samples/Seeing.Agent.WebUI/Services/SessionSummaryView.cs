using System;
using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.WebUI.Services;

/// <summary>侧栏摘要卡片的状态分层。</summary>
public enum SummaryState
{
    /// <summary>执行中</summary>
    Running,
    /// <summary>排队中</summary>
    Queued,
    /// <summary>执行失败</summary>
    Failed,
    /// <summary>已取消</summary>
    Cancelled,
    /// <summary>已完成（含无执行）</summary>
    Done
}

/// <summary>来源行的来源方向：无来源 / 来源于其他会话 / 已交接给其他会话。</summary>
public enum RelationshipSource
{
    /// <summary>无来源（锚点或未派生）</summary>
    None,
    /// <summary>来源为其他会话（分叉 / 子代理 / 交接后继）</summary>
    Source,
    /// <summary>已交接 → 后继（交接前任）</summary>
    HandoffTo
}

/// <summary>会话摘要卡片的纯映射逻辑（无副作用，便于测试）。</summary>
public static class SessionSummaryView
{
    /// <summary>预览完整文本的最大长度。</summary>
    public const int PreviewMaxLength = 120;

    /// <summary>紧凑卡片摘要的最大长度。</summary>
    public const int CompactMaxLength = 20;

    /// <summary>预览文本：空的返回"暂无消息"，超长截断并追加省略号。</summary>
    public static string PreviewSnippet(string? full, int max = PreviewMaxLength)
    {
        if (string.IsNullOrWhiteSpace(full))
            return "暂无消息";

        return full.Length <= max ? full : full[..max] + "…";
    }

    /// <summary>紧凑摘要：空的返回"暂无消息"，超长截断并追加省略号。</summary>
    public static string CompactSnippet(string? preview, int max = CompactMaxLength)
    {
        if (string.IsNullOrWhiteSpace(preview))
            return "暂无消息";

        return preview.Length <= max ? preview : preview[..max] + "…";
    }

    /// <summary>执行态 → 摘要状态分层。</summary>
    public static SummaryState Classify(bool isExecuting, ExecutionStatus? status)
    {
        if (isExecuting || status is ExecutionStatus.Running or ExecutionStatus.Pending)
            return SummaryState.Running;
        if (status == ExecutionStatus.Queued)
            return SummaryState.Queued;
        if (status == ExecutionStatus.Failed)
            return SummaryState.Failed;
        if (status == ExecutionStatus.Cancelled)
            return SummaryState.Cancelled;
        return SummaryState.Done;
    }

    /// <summary>已运行时长：null → 空；不足 1 小时返回 mm:ss，否则 hh:mm:ss。</summary>
    public static string Elapsed(DateTime? startedAt, DateTime now)
    {
        if (startedAt is null)
            return string.Empty;

        var delta = now - startedAt.Value;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        return delta.TotalHours >= 1
            ? $"{(int)delta.TotalHours:00}:{delta.Minutes:00}:{delta.Seconds:00}"
            : $"{delta.Minutes:00}:{delta.Seconds:00}";
    }

    /// <summary>来源行：无来源 → 空；来源缺失 → "来源已删除"；否则按方向渲染。</summary>
    public static string SourceLine(RelationshipSource src, string? otherTitle)
    {
        if (src == RelationshipSource.None)
            return string.Empty;

        if (string.IsNullOrWhiteSpace(otherTitle))
            return "来源已删除";

        return src == RelationshipSource.HandoffTo
            ? $"已交接 → {otherTitle}"
            : $"来源：{otherTitle}";
    }
}
