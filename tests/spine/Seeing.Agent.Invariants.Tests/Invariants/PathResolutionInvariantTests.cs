using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Invariants;

/// <summary>
/// 路径/工作区事实的守卫（spec §5.12）。
/// <para>
/// 扫描事实而不是形状：进程 CWD 的**写入点**、构造期 cwd 快照、多根工厂的引用计数。
/// 单参 <c>GetFullPath</c> 已由编译期 <c>Obsolete(error: true)</c> 硬保证，不再重复扫描。
/// </para>
/// </summary>
public class PathResolutionInvariantTests
{
    [Fact]
    public void Src_ShouldOnlyMutateProcessCwdInWorkspaceProvider()
    {
        var hits = ScanSrc("SetCurrentDirectory")
            .Where(file => !file.EndsWith("WorkspaceProvider.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        hits.Should().BeEmpty(
            "进程 CWD 的写入点只允许 WorkspaceProvider（且受 MutatesProcessCwd 保护）；命中: {0}",
            string.Join("; ", hits));
    }

    [Fact]
    public void Src_ShouldNotContainConstructionTimeWorkingDirectoryField()
    {
        ScanSrc("_workingDirectory").Should().BeEmpty(
            "工具是 Singleton，禁止在构造期快照 cwd/工作区路径");
    }

    [Fact]
    public void FileWorkspaceFactory_ShouldNotUseReferenceCount()
    {
        var file = Path.Combine(
            FindRepoRoot(), "src", "capabilities", "Seeing.Agent.Files", "FileWorkspaceFactory.cs");

        File.Exists(file).Should().BeTrue();
        File.ReadAllText(file).Should().NotContain("RefCount",
            "多根工厂不做引用计数：读操作不应带副作用，Release 幂等全清");
    }

    [Fact]
    public void ExecutionChains_ShouldNotConsumeProcessCwd()
    {
        // 主链与调度旁路都不得读 IExecutionWorld.Cwd（那是进程 CWD）。
        // Git/ACP 的子进程兜底不在本断言范围：它们是"无执行 cwd 时"的最后防线，且已由工具层显式传值。
        var hits = new[]
        {
            ScanSrcIn("src/hosting", "executionWorld.Cwd"),
            ScanSrcIn("src/hosting", "_world.Cwd"),
            ScanSrcIn("src/capabilities/Seeing.Agent.Scheduler", "executionWorld.Cwd"),
            ScanSrcIn("src/capabilities/Seeing.Agent.Scheduler", "_world.Cwd"),
        }.SelectMany(h => h).Distinct().ToArray();

        hits.Should().BeEmpty(
            "执行链不得消费进程 CWD；命中: {0}", string.Join("; ", hits));
    }

    [Fact]
    public void FileWorkbench_ShouldNotDependOnCollaborationWritePort()
    {
        // 协同目录是会话功能：管理入口在会话工具栏，文件工作台只消费只读根列表。
        var dir = Path.Combine(FindRepoRoot(), "samples", "Seeing.Agent.WebUI.Files");
        Directory.Exists(dir).Should().BeTrue();

        var hits = Directory
            .EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains("ICollaborationDirectories", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(dir, file))
            .ToArray();

        hits.Should().BeEmpty(
            "文件工作台不得持有协同目录写端口；命中: {0}", string.Join("; ", hits));
    }

    private static string[] ScanSrcIn(string relativeDir, string token)
    {
        var dir = Path.Combine(FindRepoRoot(), relativeDir);
        if (!Directory.Exists(dir))
            return [];

        return Directory
            .EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(dir, file))
            .ToArray();
    }

    private static string[] ScanSrc(string token)
    {
        var root = Path.Combine(FindRepoRoot(), "src");
        if (!Directory.Exists(root))
            return [];

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => File.ReadAllText(file).Contains(token, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
