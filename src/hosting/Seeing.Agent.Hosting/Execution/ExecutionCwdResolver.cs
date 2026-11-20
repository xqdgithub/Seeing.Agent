using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>cwd 解析结果（internal 实现细节；调用方只关心 Path 与是否需要回写）。</summary>
internal readonly record struct CwdResolution(
    string Path,
    bool NeedsSessionWriteBack,
    string? Warning);

/// <summary>
/// cwd 的唯一计算点。
/// <para>
/// 本期语义：请求级/会话级任何偏离都收敛到工作区根；不读磁盘、不回落到进程 CWD。
/// 三级参数与返回结构保留，供未来放开"基准切换"时扩展内部实现，调用方无需改动。
/// </para>
/// </summary>
internal static class ExecutionCwdResolver
{
    internal static CwdResolution Resolve(
        string? requestCwd,
        string? sessionCwd,
        IWorkspaceProvider workspace)
    {
        // GetProjectRoot() 自身在未决议时抛 InvalidOperationException（占位目录判定），
        // 此处不重复依赖 IsResolved：测试替身只需提供 ProjectSeeingDirectory 即可。
        var root = PathBoundary.Canonicalize(workspace.GetProjectRoot());
        var divergent = false;

        if (!string.IsNullOrWhiteSpace(requestCwd))
            divergent |= !SamePath(requestCwd, root);
        if (!string.IsNullOrWhiteSpace(sessionCwd))
            divergent |= !SamePath(sessionCwd, root);

        var needsWriteBack = string.IsNullOrWhiteSpace(sessionCwd) || divergent;
        var warning = divergent
            ? "请求级/会话级 cwd 与工作区根不一致，已收敛到工作区根。"
            : null;

        return new CwdResolution(root, needsWriteBack, warning);
    }

    private static bool SamePath(string path, string canonicalRoot)
    {
        try
        {
            var canonical = PathBoundary.Canonicalize(path);
            return string.Equals(canonical, canonicalRoot, PathBoundary.PathComparison);
        }
        catch
        {
            return false;
        }
    }
}
