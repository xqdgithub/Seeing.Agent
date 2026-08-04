namespace Seeing.Agent.WebUI.Services;

public static class MessageListWindow
{
    public const int InitialWindowSize = 40;
    public const int LoadMoreBatch = 20;
    public const int MaxMounted = 80;

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
