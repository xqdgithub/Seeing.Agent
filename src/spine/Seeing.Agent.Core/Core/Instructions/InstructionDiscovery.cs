using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// AGENTS.md 候选路径发现器：搜索根规则的唯一实现。
/// <para>只负责枚举候选路径（不读取、不判断存在性）：读取与三态分类由
/// <c>InstructionFileReader</c> 完成，避免「扫描规则」与「读取分类」两套实现漂移。</para>
/// </summary>
internal sealed class InstructionDiscovery
{
    private const string InstructionFileName = "AGENTS.md";
    private readonly ILogger<InstructionDiscovery> _logger;
    private readonly Func<string> _userProfileProvider;
    private readonly StringComparer _pathComparer;

    /// <summary>
    /// 创建候选路径发现器。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="userProfileProvider">用户目录提供器（测试可注入；默认为当前用户目录）。</param>
    public InstructionDiscovery(
        ILogger<InstructionDiscovery> logger,
        Func<string>? userProfileProvider = null)
    {
        _logger = logger;
        _userProfileProvider = userProfileProvider
            ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    /// <summary>
    /// 枚举全部候选指令文件路径（去重、绝对化，但不做存在性判断）。
    /// <para>搜索根规则：用户级候选 + 工作区候选 + cwd 各级祖先候选。</para>
    /// </summary>
    public IReadOnlyList<string> EnumerateCandidatePaths(string cwd, string workspaceRoot)
    {
        var normalizedCwd = Path.GetFullPath(cwd);
        var normalizedWorkspace = Path.GetFullPath(workspaceRoot);
        var candidatePaths = new List<string>();
        AddUserCandidates(candidatePaths);

        if (IsWithinWorkspace(normalizedCwd, normalizedWorkspace))
        {
            candidatePaths.Add(Path.Combine(normalizedWorkspace, ".agents", InstructionFileName));
            candidatePaths.Add(Path.Combine(normalizedWorkspace, ".seeing", InstructionFileName));
            candidatePaths.Add(Path.Combine(normalizedWorkspace, InstructionFileName));
            AddAncestorCandidates(candidatePaths, normalizedCwd, normalizedWorkspace);
        }
        else
        {
            _logger.LogDebug(
                "当前目录 {Cwd} 位于工作区 {WorkspaceRoot} 之外，仅发现用户级指令",
                normalizedCwd,
                normalizedWorkspace);
        }

        var normalized = new List<string>(candidatePaths.Count);
        var seen = new HashSet<string>(_pathComparer);
        foreach (var candidatePath in candidatePaths)
        {
            var fullPath = Path.GetFullPath(candidatePath);
            if (seen.Add(fullPath))
            {
                normalized.Add(fullPath);
            }
        }

        return normalized;
    }

    private void AddUserCandidates(List<string> candidatePaths)
    {
        var userProfile = _userProfileProvider();
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        var normalizedUserProfile = Path.GetFullPath(userProfile);
        candidatePaths.Add(Path.Combine(normalizedUserProfile, ".agents", InstructionFileName));
        candidatePaths.Add(Path.Combine(normalizedUserProfile, ".seeing", InstructionFileName));
    }

    private static void AddAncestorCandidates(
        List<string> candidatePaths,
        string cwd,
        string workspaceRoot)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, cwd);
        if (relativePath == ".")
        {
            return;
        }

        var currentPath = workspaceRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            candidatePaths.Add(Path.Combine(currentPath, InstructionFileName));
        }
    }

    private static bool IsWithinWorkspace(string cwd, string workspaceRoot)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, cwd);
        return relativePath == "."
            || (!Path.IsPathRooted(relativePath)
                && relativePath != ".."
                && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
