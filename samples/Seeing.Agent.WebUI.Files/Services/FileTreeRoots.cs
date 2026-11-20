using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>
/// 文件树顶层根节点的投影：把工作区根转成可渲染的目录项与视觉标识。
/// 只做展示，不参与权限判定。
/// </summary>
public static class FileTreeRoots
{
    /// <summary>根节点的目录项（相对路径恒为空）。</summary>
    public static FileEntry Entry(IFileWorkspace workspace)
        => new(new FileResourceId(workspace.RootId, ""), DisplayName(workspace.Root), true, null, null);

    /// <summary>根类别徽标文案。</summary>
    public static string Badge(FileWorkspaceRootKind kind) => kind switch
    {
        FileWorkspaceRootKind.Project => "项目",
        FileWorkspaceRootKind.Collaboration => "协同",
        _ => ""
    };

    /// <summary>根类别徽标的样式类。</summary>
    public static string BadgeClass(FileWorkspaceRootKind kind) => kind switch
    {
        FileWorkspaceRootKind.Project => "file-tree-badge--project",
        FileWorkspaceRootKind.Collaboration => "file-tree-badge--collab",
        _ => ""
    };

    /// <summary>显示名：优先备注，其次目录名；失效根追加「（不可用）」。</summary>
    public static string DisplayName(FileWorkspaceRoot root)
    {
        var name = string.IsNullOrWhiteSpace(root.Label)
            ? SafeDirectoryName(root.RootPath)
            : root.Label;

        return root.Exists ? name : name + "（不可用）";
    }

    private static string SafeDirectoryName(string path)
    {
        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
