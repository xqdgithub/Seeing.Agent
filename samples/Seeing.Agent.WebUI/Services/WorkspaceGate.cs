namespace Seeing.Agent.WebUI.Services;

/// <summary>未选定工作区时把任何业务路由压回工作区首页。</summary>
public static class WorkspaceGate
{
    /// <summary>工作区首页路由。</summary>
    public const string HomePath = "/workspaces";

    /// <summary>「返回控制台」回跳目标的 query 键。</summary>
    public const string ReturnQueryKey = "from";

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

    /// <summary>
    /// 从当前业务页打开工作区管理页时携带回跳路径。
    /// <paramref name="currentRelativePath"/> 为
    /// <see cref="Microsoft.AspNetCore.Components.NavigationManager.ToBaseRelativePath"/> 的结果。
    /// </summary>
    public static string BuildManagePath(string? currentRelativePath)
    {
        var relative = (currentRelativePath ?? string.Empty).Split('#')[0].Trim();
        if (string.IsNullOrEmpty(relative))
            return HomePath;

        var withSlash = relative.StartsWith('/') ? relative : "/" + relative;
        if (IsWorkspacesPath(withSlash.Split('?', '#')[0]) || withSlash == "/")
            return HomePath;

        return $"{HomePath}?{ReturnQueryKey}={Uri.EscapeDataString(withSlash)}";
    }

    /// <summary>
    /// 解析「返回控制台」目标：仅接受本应用相对路径；缺失或不安全时回落首页。
    /// </summary>
    public static string ResolveReturnPath(string? fromValue)
    {
        if (string.IsNullOrWhiteSpace(fromValue))
            return "/";

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(fromValue.Trim());
        }
        catch (UriFormatException)
        {
            return "/";
        }

        if (!decoded.StartsWith('/') || decoded.StartsWith("//", StringComparison.Ordinal))
            return "/";

        var pathOnly = decoded.Split('?', '#')[0];
        if (IsWorkspacesPath(pathOnly) || pathOnly.Contains('\\', StringComparison.Ordinal))
            return "/";

        return decoded.Split('#')[0];
    }

    private static bool IsWorkspacesPath(string path) =>
        string.Equals(path.Trim('/'), HomePath.Trim('/'), StringComparison.OrdinalIgnoreCase);
}
