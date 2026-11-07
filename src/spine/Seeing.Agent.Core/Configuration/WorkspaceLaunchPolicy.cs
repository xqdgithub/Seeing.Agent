using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 启动决议矩阵（纯函数）。宿主把输入填好，结果决定工作区与历史写法。
/// <para>顺序与语义见 docs/superpowers/specs/2026-11-07-workspace-home-design.md §3。</para>
/// </summary>
public static class WorkspaceLaunchPolicy
{
    /// <summary>按启动模式决议工作区。</summary>
    /// <param name="input">宿主在 Build 之前填好的输入。</param>
    /// <param name="directoryExists">目录存在性判定（注入以便测试）。</param>
    public static WorkspaceLaunchDecision Resolve(
        WorkspaceLaunchInput input,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(directoryExists);

        return input.Mode switch
        {
            WorkspaceLaunchMode.Invocation => ResolveInvocation(input, directoryExists),
            WorkspaceLaunchMode.Service => ResolveService(input, directoryExists),
            _ => ResolveInteractive(input, directoryExists),
        };
    }

    private static WorkspaceLaunchDecision ResolveInvocation(
        WorkspaceLaunchInput input,
        Func<string, bool> exists)
    {
        // TUI 的位置参数是用户本次显式指定；没有它才用启动目录。
        // 残留的 SEEING_WORKSPACE_ROOT 不参与：CLI 每次都可能在不同目录。
        var target = FirstNonEmpty(input.ExplicitPath, input.StartupDirectory);
        if (target is null || !exists(target))
            return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {target ?? "(空)"}");

        return new WorkspaceLaunchDecision(
            WorkspaceLaunchOutcome.Resolved, Normalize(target),
            WorkspaceResolutionSource.StartupDirectory, null,
            UpdateRecent: true, UpdateLastInteractive: false);
    }

    private static WorkspaceLaunchDecision ResolveService(
        WorkspaceLaunchInput input,
        Func<string, bool> exists)
    {
        var explicitPath = FirstNonEmpty(input.ExplicitPath);
        var fromEnvironment = FirstNonEmpty(input.InheritedEnvironment);
        var target = FirstNonEmpty(explicitPath, fromEnvironment, input.StartupDirectory);

        if (target is null || !exists(target))
            return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {target ?? "(空)"}");

        var source = explicitPath is not null
            ? WorkspaceResolutionSource.ManualSwitch
            : fromEnvironment is not null
                ? WorkspaceResolutionSource.EnvironmentVariable
                : WorkspaceResolutionSource.StartupDirectory;

        return new WorkspaceLaunchDecision(
            WorkspaceLaunchOutcome.Resolved, Normalize(target), source, null,
            UpdateRecent: true, UpdateLastInteractive: false);
    }

    private static WorkspaceLaunchDecision ResolveInteractive(
        WorkspaceLaunchInput input,
        Func<string, bool> exists)
    {
        // 1. --workspace：目录无效直接失败，不回退。
        var explicitPath = FirstNonEmpty(input.ExplicitPath);
        if (explicitPath is not null)
        {
            if (!exists(explicitPath))
                return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {explicitPath}");

            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(explicitPath),
                WorkspaceResolutionSource.ManualSwitch, null,
                UpdateRecent: true, UpdateLastInteractive: false);
        }

        // 2. 父进程交给这一个子进程的环境变量：同样不回退。
        var fromEnvironment = FirstNonEmpty(input.InheritedEnvironment);
        if (fromEnvironment is not null)
        {
            if (!exists(fromEnvironment))
                return WorkspaceLaunchDecision.Failed($"工作区目录不存在: {fromEnvironment}");

            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(fromEnvironment),
                WorkspaceResolutionSource.EnvironmentVariable, null,
                UpdateRecent: true, UpdateLastInteractive: false);
        }

        // 3. 从某个打开过的目录启动：直接进入它。
        //    exe 目录一律不算（双击时 cwd 就是 exe 目录）。
        var startup = FirstNonEmpty(input.StartupDirectory);
        if (startup is not null
            && exists(startup)
            && !IsSameDirectory(startup, input.ExecutableDirectory)
            && input.History.Recent.Any(p => IsSameDirectory(p, startup)))
        {
            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(startup),
                WorkspaceResolutionSource.StartupDirectory, null,
                UpdateRecent: false, UpdateLastInteractive: false);
        }

        // 4. 上次在界面里选定的目录。
        var last = FirstNonEmpty(input.History.LastInteractive);
        if (last is not null && exists(last))
        {
            return new WorkspaceLaunchDecision(
                WorkspaceLaunchOutcome.Resolved, Normalize(last),
                WorkspaceResolutionSource.GlobalDefault, null,
                UpdateRecent: false, UpdateLastInteractive: false);
        }

        // 5. 都没有：首页。
        return WorkspaceLaunchDecision.Unresolved();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);
    }
}
