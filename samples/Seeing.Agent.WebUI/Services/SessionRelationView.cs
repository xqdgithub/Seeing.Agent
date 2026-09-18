using System;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>会话关系在 UI 中归属的分区。</summary>
public enum SessionSection
{
    /// <summary>主线历史（锚点、交接链）</summary>
    MainLineHistory,
    /// <summary>派生会话（分叉 / 备份）</summary>
    Derived,
    /// <summary>子代理会话</summary>
    Children
}

/// <summary>会话关系 / 状态的纯映射（无副作用，便于测试）。</summary>
public static class SessionRelationView
{
    /// <summary>trim 备份会话标签前缀。</summary>
    public const string TrimBackupPrefix = "trim-backup";

    /// <summary>映射会话徽标文本。</summary>
    /// <param name="relation">成员关系。</param>
    /// <param name="isAnchor">是否为当前锚点。</param>
    /// <param name="label">成员标签（如 trim 备份前缀）。</param>
    /// <param name="successorTitle">交接前任的后继标题（非空时显示"已交接 → X"）。</param>
    public static string BadgeText(
        SessionRelation relation, bool isAnchor, string? label, string? successorTitle = null)
    {
        if (isAnchor)
        {
            if (relation == SessionRelation.Child)
                return "子代理（只读）·主线缺失";
            if (relation == SessionRelation.HandoffSuccessor)
                return "主线 · 交接后继";
            return "主线";
        }

        return relation switch
        {
            SessionRelation.HandoffPredecessor => string.IsNullOrEmpty(successorTitle)
                ? "已交接"
                : $"已交接 → {successorTitle}",
            SessionRelation.HandoffSuccessor => "交接后继",
            SessionRelation.Fork => (label?.StartsWith(TrimBackupPrefix, StringComparison.Ordinal) ?? false) ? "备份" : "分叉",
            SessionRelation.Child => "子代理（只读）",
            _ => "会话"
        };
    }

    /// <summary>映射会话归属分区。</summary>
    public static SessionSection Section(SessionRelation relation, bool isAnchor) => relation switch
    {
        SessionRelation.Fork => SessionSection.Derived,
        SessionRelation.Child => SessionSection.Children,
        _ => SessionSection.MainLineHistory
    };
}
