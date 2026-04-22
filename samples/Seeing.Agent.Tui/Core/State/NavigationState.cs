using Seeing.Agent.Tui.Core.Models;

namespace Seeing.Agent.Tui.Core.State;

/// <summary>
/// 消息导航状态 - 管理搜索、折叠、高亮等导航功能
/// </summary>
public class NavigationState
{
    /// <summary>搜索关键词</summary>
    public string? SearchKeyword { get; set; }
    /// <summary>搜索匹配的消息索引列表</summary>
    public List<int> SearchMatchIndices { get; } = new();
    /// <summary>当前搜索匹配索引</summary>
    public int CurrentSearchMatchIndex { get; set; }
    /// <summary>是否搜索模式</summary>
    public bool IsSearchMode { get; set; }
    /// <summary>折叠的消息ID集合</summary>
    public HashSet<string> FoldedMessageIds { get; } = new();
    /// <summary>高亮的消息索引</summary>
    public int HighlightedMessageIndex { get; set; } = -1;
    /// <summary>状态变更回调</summary>
    public Action<RenderRegion>? OnStateChanged { get; set; }
    /// <summary>消息存储引用</summary>
    public MessageStore? Messages { get; set; }
    /// <summary>获取终端高度</summary>
    public Func<int> GetTerminalHeight { get; set; } = () => 24;
    /// <summary>设置滚动偏移</summary>
    public Action<int> SetScrollOffset { get; set; } = _ => { };

    /// <summary>清空搜索状态</summary>
    public void ClearSearch()
    {
        SearchKeyword = null;
        SearchMatchIndices.Clear();
        IsSearchMode = false;
        HighlightedMessageIndex = -1;
        OnStateChanged?.Invoke(RenderRegion.Messages);
    }

    /// <summary>设置搜索关键词并执行搜索</summary>
    public void SetSearchKeyword(string? keyword)
    {
        SearchKeyword = keyword;
        SearchMatchIndices.Clear();
        IsSearchMode = false;
        HighlightedMessageIndex = -1;

        if (!string.IsNullOrEmpty(keyword) && Messages != null)
        {
            for (var i = 0; i < Messages.Count; i++)
            {
                var msg = Messages[i];
                if (msg.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    (msg.Reasoning?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                    SearchMatchIndices.Add(i);
            }
            if (SearchMatchIndices.Count > 0) { IsSearchMode = true; ScrollToMatch(0); }
        }
        OnStateChanged?.Invoke(RenderRegion.Messages);
    }

    /// <summary>导航到下一个/上一个匹配项</summary>
    public void NavigateMatch(int delta)
    {
        if (SearchMatchIndices.Count == 0) return;
        var count = SearchMatchIndices.Count;
        CurrentSearchMatchIndex = (CurrentSearchMatchIndex + delta + count) % count;
        ScrollToMatch(CurrentSearchMatchIndex);
    }

    /// <summary>滚动到指定匹配项</summary>
    private void ScrollToMatch(int idx)
    {
        if (idx < 0 || idx >= SearchMatchIndices.Count || Messages == null) return;
        HighlightedMessageIndex = SearchMatchIndices[idx];
        var height = Math.Max(10, GetTerminalHeight());
        var visible = Math.Max(1, (height - 10) / 4);
        var desired = Messages.Count - HighlightedMessageIndex - visible / 2;
        SetScrollOffset(Math.Max(0, Math.Min(desired, Messages.Count - visible)));
        OnStateChanged?.Invoke(RenderRegion.Messages);
    }

    /// <summary>切换消息折叠状态</summary>
    public void ToggleFold(string messageId)
    {
        if (!FoldedMessageIds.Remove(messageId)) FoldedMessageIds.Add(messageId);
        OnStateChanged?.Invoke(RenderRegion.Messages);
    }

    /// <summary>获取消息ID</summary>
    public static string GetMessageId(MessageDisplay msg) => msg.Timestamp.ToString("yyyyMMddHHmmssfff");
}