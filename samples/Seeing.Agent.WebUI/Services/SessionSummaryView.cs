using Seeing.Agent.Abstractions.Execution;

namespace Seeing.Agent.WebUI.Services;

/// <summary>侧栏摘要卡片的状态分层。</summary>
public enum SummaryState
{
    /// <summary>执行中</summary>
    Running,
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

    /// <summary>预览文本：空的返回"暂无消息"，超长截断并追加省略号。</summary>
    public static string PreviewSnippet(string? full, int max = PreviewMaxLength)
    {
        if (string.IsNullOrWhiteSpace(full))
            return "暂无消息";

        return full.Length <= max ? full : full[..max] + "…";
    }

    /// <summary>执行态 → 摘要状态分层。</summary>
    public static SummaryState Classify(bool isExecuting, ExecutionStatus? status)
    {
        if (isExecuting || status is ExecutionStatus.Running or ExecutionStatus.Pending)
            return SummaryState.Running;
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

    /// <summary>
    /// 来源行：无来源 → 空；来源链接缺失 → "来源已删除"；
    /// 有链接但标题尚未解析 → "来源：…"（不得误判为已删除）；否则按方向渲染。
    /// </summary>
    /// <param name="src">来源方向。</param>
    /// <param name="otherTitle">来源/后继会话标题（可为空）。</param>
    /// <param name="linkKnown">
    /// 是否确实存在来源链接（父/后继会话 ID 非空）。
    /// <para>
    /// 仅当这一位为 <c>false</c> 才是「来源已删除」。组关系缺失时父 ID 会解析为 null，
    /// 此时确实是关系已丢失；而标题只是内存缓存未命中，不应据此断言会话被删。
    /// </para>
    /// </param>
    public static string SourceLine(RelationshipSource src, string? otherTitle, bool linkKnown = true)
    {
        if (src == RelationshipSource.None)
            return string.Empty;

        if (!linkKnown)
            return "来源已删除";

        if (string.IsNullOrWhiteSpace(otherTitle))
            // 链接存在但标题未解析（会话尚未加载进内存）：保持中性文案，避免误报「已删除」
            return "来源：…";

        return src == RelationshipSource.HandoffTo
            ? $"已交接 → {otherTitle}"
            : $"来源：{otherTitle}";
    }
}
