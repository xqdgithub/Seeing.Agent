namespace Seeing.Agent.Workspace;

/// <summary>
/// <see cref="WorkspaceGitExcluder"/> 单次执行的结果（诊断 / 日志用）。
/// </summary>
public enum GitExcludeOutcome
{
    /// <summary>已向 <c>.git/info/exclude</c> 追加托管标记块。</summary>
    Applied,

    /// <summary>目标路径已被任意来源忽略（含 .gitignore / 全局 excludes），无需写入。</summary>
    AlreadyIgnored,

    /// <summary>托管标记块已存在，幂等跳过。</summary>
    AlreadyManaged,

    /// <summary>工作区不是 git 仓库。</summary>
    NotARepository,

    /// <summary>工作区目录位于仓库根之外（无锚定模式可表达）。</summary>
    OutsideRepository,

    /// <summary>git 不可执行（未安装 / 无法启动）。</summary>
    Unavailable,

    /// <summary>读写排除文件失败（权限等）或超时。</summary>
    Failed,
}
