using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>打开文件请求（可携带跳转位置，line/column 均 1-based，可空）。</summary>
/// <param name="Id">目标资源标识。</param>
/// <param name="Line">目标行（1-based）。</param>
/// <param name="Column">目标列（1-based）。</param>
public sealed record FileOpenRequest(FileResourceId Id, int? Line = null, int? Column = null);

/// <summary>文件面板相对聊天区的位置。</summary>
public enum FilePanelSide
{
    /// <summary>左侧（默认）。</summary>
    Left,

    /// <summary>右侧。</summary>
    Right,
}

/// <summary>文件面板开合状态（Scoped，circuit 维度）。</summary>
public sealed class FilePanelState
{
    /// <summary>变更事件。</summary>
    public event Action? Changed;

    /// <summary>是否打开。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>当前会话 id。</summary>
    public string? SessionId { get; private set; }

    /// <summary>面板相对聊天区的位置（左/右）。</summary>
    public FilePanelSide Side { get; private set; } = FilePanelSide.Left;

    /// <summary>目录树是否可见（用户偏好；agent 新开面板时默认隐藏）。</summary>
    public bool TreeVisible { get; private set; } = true;

    /// <summary>待处理的打开请求（面板/宿主尚未就绪时暂存，由宿主消费）。</summary>
    public FileOpenRequest? PendingOpen { get; private set; }

    private readonly Dictionary<string, FileTabsState> _tabsBySession = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取指定会话的标签状态（按会话缓存）。
    /// 状态挂在 Scoped 的面板状态上，页面导航往返不丢失。
    /// </summary>
    public FileTabsState GetTabs(string sessionId)
    {
        if (!_tabsBySession.TryGetValue(sessionId, out var tabs))
        {
            tabs = new FileTabsState();
            _tabsBySession[sessionId] = tabs;
        }
        return tabs;
    }

    /// <summary>打开（用户入口；保持树可见性偏好）。</summary>
    public void Open(string sessionId)
    {
        IsOpen = true;
        SessionId = sessionId;
        Changed?.Invoke();
    }

    /// <summary>
    /// 由 agent 请求打开：面板未开则打开并默认隐藏目录树；已开则保持现状。记录待处理打开请求。
    /// </summary>
    public void RequestOpen(string sessionId, FileOpenRequest request)
    {
        var sameSession = IsOpen && string.Equals(SessionId, sessionId, StringComparison.Ordinal);
        if (!sameSession)
            TreeVisible = false;

        IsOpen = true;
        SessionId = sessionId;
        PendingOpen = request;
        Changed?.Invoke();
    }

    /// <summary>取出并清空待处理打开请求。</summary>
    public FileOpenRequest? ConsumePendingOpen()
    {
        var req = PendingOpen;
        PendingOpen = null;
        return req;
    }

    /// <summary>关闭。</summary>
    public void Close()
    {
        IsOpen = false;
        PendingOpen = null;
        Changed?.Invoke();
    }

    /// <summary>切换。</summary>
    public void Toggle(string sessionId)
    {
        if (IsOpen && string.Equals(SessionId, sessionId, StringComparison.Ordinal))
            Close();
        else
            Open(sessionId);
    }

    /// <summary>设置目录树可见性。</summary>
    public void SetTreeVisible(bool visible)
    {
        if (TreeVisible == visible)
            return;
        TreeVisible = visible;
        Changed?.Invoke();
    }

    /// <summary>切换面板左/右位置。</summary>
    public void ToggleSide() => SetSide(Side == FilePanelSide.Right ? FilePanelSide.Left : FilePanelSide.Right);

    /// <summary>设置面板左/右位置（相同值不触发事件）。</summary>
    public void SetSide(FilePanelSide side)
    {
        if (Side == side)
            return;
        Side = side;
        Changed?.Invoke();
    }
}
