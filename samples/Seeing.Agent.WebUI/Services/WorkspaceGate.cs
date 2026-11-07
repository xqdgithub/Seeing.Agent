namespace Seeing.Agent.WebUI.Services;

/// <summary>未选定工作区时把任何业务路由压回工作区首页。</summary>
public static class WorkspaceGate
{
    /// <summary>工作区首页路由。</summary>
    public const string HomePath = "/workspaces";

    /// <summary>
    /// 是否需要强制跳到工作区首页。
    /// </summary>
    /// <param name="isResolved">工作区是否已选定。</param>
    /// <param name="relativePath"><see cref="Microsoft.AspNetCore.Components.NavigationManager.ToBaseRelativePath"/> 的结果（无前导斜杠，可含 query/hash）。</param>
    public static bool ShouldForceWorkspaceHome(bool isResolved, string relativePath)
    {
        if (isResolved)
            return false;

        var path = relativePath.Split('?', '#')[0].Trim('/');
        return !string.Equals(path, HomePath.TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }
}
