using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>文件树状态（Scoped，circuit 维度）：磁盘变更事件按路径广播给树节点；承载当前激活文件（选中联动）。</summary>
public sealed class FileTreeState
{
    /// <summary>磁盘变更事件（树节点据此判断是否重新列目录）。</summary>
    public event Action<FileChangeEvent>? Changed;

    /// <summary>选中文件变化事件（树节点据此高亮文件/展开祖先目录）。</summary>
    public event Action? SelectionChanged;

    /// <summary>当前激活文件（正斜杠相对路径；null = 无选中）。</summary>
    public string? SelectedFile { get; private set; }

    /// <summary>选中版本号（每次选中变化自增，供节点判断是否需要重新滚动定位）。</summary>
    public int SelectionVersion { get; private set; }

    /// <summary>广播一个磁盘变更事件。</summary>
    public void Notify(FileChangeEvent evt) => Changed?.Invoke(evt);

    /// <summary>设置选中文件（null 表示取消选中）。值未变化时不触发事件。</summary>
    public void SetSelected(string? relativePath)
    {
        var normalized = relativePath?.Replace('\\', '/');
        if (string.Equals(normalized, SelectedFile, StringComparison.OrdinalIgnoreCase)) return;
        SelectedFile = normalized;
        SelectionVersion++;
        SelectionChanged?.Invoke();
    }

    /// <summary>变更项是否位于 <paramref name="dirRelative"/>（正斜杠相对路径，根为 ""）的直接子级。</summary>
    public static bool IsDirectChildOf(FileResourceId changed, string dirRelative)
    {
        var rel = changed.RelativePath.Replace('\\', '/');
        var idx = rel.LastIndexOf('/');
        var parent = idx < 0 ? "" : rel[..idx];
        return string.Equals(parent, dirRelative, StringComparison.Ordinal);
    }
}
