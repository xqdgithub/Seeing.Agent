namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>文件面板开合状态（Scoped，circuit 维度）。</summary>
public sealed class FilePanelState
{
    /// <summary>变更事件。</summary>
    public event Action? Changed;

    /// <summary>是否打开。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>当前会话 id。</summary>
    public string? SessionId { get; private set; }

    /// <summary>打开。</summary>
    public void Open(string sessionId) { IsOpen = true; SessionId = sessionId; Changed?.Invoke(); }

    /// <summary>关闭。</summary>
    public void Close() { IsOpen = false; Changed?.Invoke(); }

    /// <summary>切换。</summary>
    public void Toggle(string sessionId) { if (IsOpen) Close(); else Open(sessionId); }
}
