namespace Seeing.Agent.Abstractions.Directories;

/// <summary>可浏览根的类别。</summary>
public enum SessionRootKind
{
    /// <summary>工作区根（项目根）。</summary>
    Project,

    /// <summary>会话协同目录。</summary>
    Collaboration
}

/// <summary>一个可浏览根的投影（不落存储）。</summary>
/// <param name="Path">规范化绝对路径。</param>
/// <param name="Kind">根类别。</param>
/// <param name="Label">可选显示名/说明。</param>
/// <param name="Exists">目录当前是否存在（失效不删除，仅标记）。</param>
public sealed record SessionRoot(string Path, SessionRootKind Kind, string? Label, bool Exists);

/// <summary>
/// 会话目录判定的唯一权威（只读）。
/// <para>
/// 两个判定必须保持语义分离：<see cref="IsAuthorized"/> 含临时白名单（工具读写），
/// <see cref="IsTrusted"/> 只含根（附件准入、工作台展示）。合并两者会让附件获得白名单豁免。
/// </para>
/// </summary>
public interface ISessionRootSet
{
    /// <summary>可浏览根：项目根 + 协同目录（不含临时白名单）。</summary>
    IReadOnlyList<SessionRoot> Roots(string sessionId);

    /// <summary>工具/工作台读写：根 ∪ 临时白名单。</summary>
    bool IsAuthorized(string sessionId, string absolutePath);

    /// <summary>仅根（附件/工作台展示）：不含临时白名单。</summary>
    bool IsTrusted(string sessionId, string absolutePath);
}
