using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>文件树刷新通知（Scoped，circuit 维度）：磁盘变更事件按路径广播给树节点。</summary>
public sealed class FileTreeState
{
    /// <summary>磁盘变更事件（树节点据此判断是否重新列目录）。</summary>
    public event Action<FileChangeEvent>? Changed;

    /// <summary>广播一个磁盘变更事件。</summary>
    public void Notify(FileChangeEvent evt) => Changed?.Invoke(evt);

    /// <summary>变更项是否位于 <paramref name="dirRelative"/>（正斜杠相对路径，根为 ""）的直接子级。</summary>
    public static bool IsDirectChildOf(FileResourceId changed, string dirRelative)
    {
        var rel = changed.RelativePath.Replace('\\', '/');
        var idx = rel.LastIndexOf('/');
        var parent = idx < 0 ? "" : rel[..idx];
        return string.Equals(parent, dirRelative, StringComparison.Ordinal);
    }
}
