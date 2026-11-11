using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>打开文件请求（可携带跳转位置，line/column 均 1-based，可空）。</summary>
/// <param name="Id">目标资源标识。</param>
/// <param name="Line">目标行（1-based）。</param>
/// <param name="Column">目标列（1-based）。</param>
/// <param name="Preview">是否预览（null = 用标签默认值；仅 Markdown 有意义）。</param>
public sealed record FileOpenRequest(FileResourceId Id, int? Line = null, int? Column = null, bool? Preview = null);

/// <summary>
/// 文件工作台的会话级 UI 状态（Scoped，circuit 维度）。
/// <para>
/// <b>只管「工作台内部」状态</b>：目录树可见性、待处理的打开请求、按会话缓存的标签。
/// 开合 / 停靠侧 / 宽度已移交 <c>WorkbenchDockState</c>——那块职责属于工作台平台，
/// 不属于文件工作台（否则每加一个工作台就要复制一份开合逻辑）。
/// </para>
/// </summary>
public sealed class FilePanelState
{
    /// <summary>变更事件。</summary>
    public event Action? Changed;

    /// <summary>目录树是否可见（用户偏好；agent 新开面板时默认隐藏）。</summary>
    public bool TreeVisible { get; private set; } = true;

    /// <summary>待处理的打开请求（工作台尚未就绪时暂存，由工作台消费）。</summary>
    public FileOpenRequest? PendingOpen { get; private set; }

    /// <summary>当前激活标签（由 FilePanelHost 在标签变化时同步；null = 无打开文件）。</summary>
    public OpenFile? ActiveFile { get; private set; }

    /// <summary>由宿主同步当前激活标签；变化时触发 <see cref="Changed"/>。</summary>
    /// <param name="file">当前激活标签。</param>
    public void SetActiveFile(OpenFile? file)
    {
        if (ReferenceEquals(ActiveFile, file)) return;
        ActiveFile = file;
        Changed?.Invoke();
    }

    private readonly Dictionary<string, FileTabsState> _tabsBySession = new(StringComparer.Ordinal);

    /// <summary>
    /// 获取指定会话的标签状态（按会话缓存）。
    /// 状态挂在 Scoped 的状态上，页面导航往返不丢失。
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

    /// <summary>请求打开文件（由 agent 或宿主调用）；记录待处理请求并触发变更。</summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="request">打开请求。</param>
    /// <param name="hideTree">是否同时把目录树置为隐藏（agent 驱动时默认隐藏，留出内容宽度）。</param>
    public void RequestOpen(string sessionId, FileOpenRequest request, bool hideTree = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (hideTree)
            TreeVisible = false;

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

    /// <summary>设置目录树可见性（相同值不触发事件）。</summary>
    public void SetTreeVisible(bool visible)
    {
        if (TreeVisible == visible)
            return;

        TreeVisible = visible;
        Changed?.Invoke();
    }
}
