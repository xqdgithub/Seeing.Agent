using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 按层级发现并读取 AGENTS.md 指令文件。
/// </summary>
internal sealed class InstructionDiscovery
{
    private const string InstructionFileName = "AGENTS.md";
    private readonly ILogger<InstructionDiscovery> _logger;
    private readonly Func<string> _userProfileProvider;
    private readonly StringComparer _pathComparer;

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

    public async Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default)
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

        var discovered = new List<InstructionFile>();
        var seen = new HashSet<string>(_pathComparer);

        foreach (var candidatePath in candidatePaths)
        {
            ct.ThrowIfCancellationRequested();
            var normalizedPath = Path.GetFullPath(candidatePath);
            if (!seen.Add(normalizedPath) || !File.Exists(normalizedPath))
            {
                continue;
            }

            var instructionFile = await TryReadAsync(normalizedPath, ct);
            if (instructionFile is not null)
            {
                discovered.Add(instructionFile);
            }
        }

        return discovered;
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

    private async Task<InstructionFile?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, ct);
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));

            return new InstructionFile
            {
                Path = path,
                Content = content,
                LastModified = File.GetLastWriteTimeUtc(path),
                Hash = $"sha256:{Convert.ToHexString(hashBytes).ToLowerInvariant()}"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取指令文件失败，已跳过: {Path}", path);
            return null;
        }
    }
}
