using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>文件树状态（Scoped，circuit 维度）：磁盘变更事件按资源标识广播给树节点；承载当前激活文件（选中联动）。</summary>
public sealed class FileTreeState
{
    /// <summary>磁盘变更事件（树节点据此判断是否重新列目录）。</summary>
    public event Action<FileChangeEvent>? Changed;

    /// <summary>选中文件变化事件（树节点据此高亮文件/展开祖先目录）。</summary>
    public event Action? SelectionChanged;

    /// <summary>节点右键请求（条目 + 视口坐标）；由面板统一渲染上下文菜单。</summary>
    public event Action<FileEntry, double, double>? ContextRequested;

    /// <summary>请求面板打开某节点的上下文菜单。</summary>
    public void RequestContext(FileEntry entry, double x, double y)
        => ContextRequested?.Invoke(entry, x, y);

    /// <summary>当前激活文件（含根标识；null = 无选中）。</summary>
    public FileResourceId? SelectedId { get; private set; }

    /// <summary>当前选中的目录（新建文件的落点；null = 未选，回退项目根）。</summary>
    public FileResourceId? SelectedDirectoryId { get; private set; }

    /// <summary>设置选中的目录（新建操作的默认落点）。</summary>
    public void SetSelectedDirectory(FileResourceId id)
    {
        if (Same(SelectedDirectoryId, id)) return;
        SelectedDirectoryId = id;
        SelectionChanged?.Invoke();
    }

    /// <summary>选中版本号（每次选中变化自增，供节点判断是否需要重新滚动定位）。</summary>
    public int SelectionVersion { get; private set; }

    /// <summary>广播一个磁盘变更事件。</summary>
    public void Notify(FileChangeEvent evt) => Changed?.Invoke(evt);

    /// <summary>设置选中文件（null 表示取消选中）。值未变化时不触发事件。</summary>
    public void SetSelected(FileResourceId? id)
    {
        var normalized = id is { } value
            ? value with { RelativePath = value.RelativePath.Replace('\\', '/') }
            : (FileResourceId?)null;

        if (Same(normalized, SelectedId)) return;
        SelectedId = normalized;
        SelectionVersion++;
        SelectionChanged?.Invoke();
    }

    private static bool Same(FileResourceId? left, FileResourceId? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return string.Equals(left.Value.RootId, right.Value.RootId, StringComparison.Ordinal)
            && string.Equals(left.Value.RelativePath, right.Value.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>变更项是否位于 <paramref name="dir"/> 的直接子级（同一根内比较）。</summary>
    public static bool IsDirectChildOf(FileResourceId changed, FileResourceId dir)
    {
        if (!string.Equals(changed.RootId, dir.RootId, StringComparison.Ordinal))
            return false;

        var rel = changed.RelativePath.Replace('\\', '/');
        var idx = rel.LastIndexOf('/');
        var parent = idx < 0 ? "" : rel[..idx];
        return string.Equals(parent, dir.RelativePath.Trim('/'), StringComparison.Ordinal);
    }
}
