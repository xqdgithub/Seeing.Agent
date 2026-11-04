namespace Seeing.Agent.WebUI.Services;

public static class MessageListWindow
{
    /// <summary>首屏进入会话时挂载的条目数（与 <see cref="MaxMounted"/> 保持一致）。</summary>
    public const int InitialWindowSize = 20;

    /// <summary>向上滚动时每次补充的条目数。</summary>
    public const int LoadMoreBatch = 20;

    /// <summary>
    /// 贴底时最多保持挂载的条目数。
    /// <para>
    /// 从 80 降到 40 再降到 20：浏览器「上色 / 渲染」耗时与挂载数近似线性
    /// （实测 80 条时 Paint 1816ms，40 条时 1396ms）。切换会话时全部条目都是新建组件，
    /// <c>content-visibility</c> 对首次渲染不生效，因此只能靠压低挂载数来控制成本。
    /// </para>
    /// <para>
    /// 一屏通常只显示 3~10 条消息，20 条可覆盖 2~6 屏；更早内容经顶部懒加载
    /// （<see cref="LoadMoreBatch"/>）补齐。
    /// </para>
    /// </summary>
    public const int MaxMounted = 20;

    public static int ComputeInitialStart(int total, int initialWindow = InitialWindowSize)
        => total <= initialWindow ? 0 : total - initialWindow;

    public static int SlideWhilePinned(int currentStart, int total, int maxMounted = MaxMounted)
    {
        var mounted = total - currentStart;
        if (mounted <= maxMounted)
            return currentStart;
        return Math.Max(0, total - maxMounted);
    }

    public static int LoadMore(int currentStart, int batch = LoadMoreBatch)
        => Math.Max(0, currentStart - batch);
}
