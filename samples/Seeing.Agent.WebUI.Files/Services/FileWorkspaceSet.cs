using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Paths;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>
/// 一组已加载的文件根，负责把资源标识/绝对路径路由到归属根。
/// <para>
/// 文件工作台只读消费根集合（来源 <see cref="IFileWorkspaceFactory.ListAsync"/>）；
/// 管理协同目录是会话层的职责，这里不持有任何写端口。
/// </para>
/// </summary>
public sealed class FileWorkspaceSet
{
    private readonly Dictionary<string, IFileWorkspace> _byRootId;

    /// <summary>构造一个根集合（顺序即展示顺序）。</summary>
    public FileWorkspaceSet(IReadOnlyList<IFileWorkspace> roots)
    {
        Roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _byRootId = new Dictionary<string, IFileWorkspace>(StringComparer.Ordinal);
        foreach (var root in roots)
            _byRootId[root.RootId] = root;
    }

    /// <summary>全部根（项目根在前，协同目录随后）。</summary>
    public IReadOnlyList<IFileWorkspace> Roots { get; }

    /// <summary>按资源标识的根哈希找到归属根；不属于任何根时返回 null。</summary>
    public IFileWorkspace? Find(FileResourceId id)
        => _byRootId.TryGetValue(id.RootId, out var workspace) ? workspace : null;

    /// <summary>按绝对路径找归属根（最长前缀优先，避免 D:\a 命中 D:\ab）。</summary>
    public IFileWorkspace? FindByPath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return null;

        // 只接受绝对路径：相对路径按进程 CWD 解析会违背「cwd 只来自工作区」的约束。
        if (!Path.IsPathFullyQualified(absolutePath))
            return null;

        IFileWorkspace? best = null;
        foreach (var workspace in Roots)
        {
            if (!PathBoundary.IsWithin(absolutePath, workspace.RootPath))
                continue;

            if (best is null || workspace.RootPath.Length > best.RootPath.Length)
                best = workspace;
        }

        return best;
    }
}
