namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 会话级工作区白名单 - 允许 Agent 扩展可访问路径。
/// </summary>
public interface IWorkspaceWhitelist
{
    void Add(string sessionId, string directoryPath);

    /// <summary>判断 path 是否等于某白名单目录或位于其子目录内（子目录前缀匹配）</summary>
    bool Contains(string sessionId, string path);

    void ClearSession(string sessionId);

    /// <summary>清除全部会话的白名单（工作区根切换时调用）。</summary>
    void ClearAll();
}
