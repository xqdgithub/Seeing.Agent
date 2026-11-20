namespace Seeing.Agent.Abstractions.Files;

/// <summary>文件工作台根的类别。</summary>
public enum FileWorkspaceRootKind
{
    /// <summary>项目根（工作区根）。</summary>
    Project,

    /// <summary>会话工作目录（本期恒等于项目根，结构保留供未来基准切换）。</summary>
    Session,

    /// <summary>协同目录。</summary>
    Collaboration
}

/// <summary>
/// 文件工作台根描述。由 <see cref="IFileWorkspace.Root"/> 拥有，不再另设 Scope 包装类型。
/// </summary>
/// <param name="RootId">根路径规范化哈希（与 <see cref="FileResourceId.RootId"/> 同源）。</param>
/// <param name="RootPath">根绝对路径。</param>
/// <param name="Label">显示名（协同目录取备注或目录名）。</param>
/// <param name="Kind">根类别。</param>
/// <param name="Exists">目录当前是否存在；失效根仍返回，不回落主根。</param>
public sealed record FileWorkspaceRoot(
    string RootId,
    string RootPath,
    string Label,
    FileWorkspaceRootKind Kind,
    bool Exists);
