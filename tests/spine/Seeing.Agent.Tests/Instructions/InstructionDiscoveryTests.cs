using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Instructions;
using Xunit;

namespace Seeing.Agent.Tests.Instructions;

/// <summary>
/// 搜索根规则锁定：候选路径顺序、去重与「工作区外只给用户级」。
/// <para>生产路径只使用 <see cref="InstructionDiscovery.EnumerateCandidatePaths"/>；
/// 存在性判断与读取分类由 <c>InstructionFileReader</c> 独占。</para>
/// </summary>
public sealed class InstructionDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _userHome;
    private readonly string _workspace;

    public InstructionDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"instruction_discovery_{Guid.NewGuid():N}");
        _userHome = Path.Combine(_root, "user");
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_userHome);
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public void EnumerateCandidatePaths_WhenCwdIsNested_ReturnsPathsInSpecificationOrder()
    {
        var cwd = Path.Combine(_workspace, "src", "feature");
        Directory.CreateDirectory(cwd);

        var expectedPaths = new[]
        {
            Path.Combine(_userHome, ".agents", "AGENTS.md"),
            Path.Combine(_userHome, ".seeing", "AGENTS.md"),
            Path.Combine(_workspace, ".agents", "AGENTS.md"),
            Path.Combine(_workspace, ".seeing", "AGENTS.md"),
            Path.Combine(_workspace, "AGENTS.md"),
            Path.Combine(_workspace, "src", "AGENTS.md"),
            Path.Combine(cwd, "AGENTS.md")
        };
        var discovery = CreateDiscovery();

        var result = discovery.EnumerateCandidatePaths(cwd, _workspace);

        PathsShouldEqual(result, expectedPaths);
    }

    [Fact]
    public void EnumerateCandidatePaths_WhenCwdEqualsWorkspace_ReturnsUserAndWorkspacePathsWithoutAncestorEntries()
    {
        var expectedPaths = new[]
        {
            Path.Combine(_userHome, ".agents", "AGENTS.md"),
            Path.Combine(_userHome, ".seeing", "AGENTS.md"),
            Path.Combine(_workspace, ".agents", "AGENTS.md"),
            Path.Combine(_workspace, ".seeing", "AGENTS.md"),
            Path.Combine(_workspace, "AGENTS.md")
        };
        var discovery = CreateDiscovery();

        var result = discovery.EnumerateCandidatePaths(_workspace, _workspace);

        PathsShouldEqual(result, expectedPaths);
    }

    [Fact]
    public void EnumerateCandidatePaths_WhenCwdIsOutsideWorkspace_ReturnsOnlyUserPaths()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var expectedPaths = new[]
        {
            Path.Combine(_userHome, ".agents", "AGENTS.md"),
            Path.Combine(_userHome, ".seeing", "AGENTS.md")
        };
        var discovery = CreateDiscovery();

        var result = discovery.EnumerateCandidatePaths(outside, _workspace);

        PathsShouldEqual(result, expectedPaths);
    }

    [Fact]
    public void EnumerateCandidatePaths_WhenLocationsResolveToSamePath_KeepsFirstOccurrence()
    {
        // 用户目录 == 工作区：两组候选重复，去重后保留首次出现的顺序
        var discovery = new InstructionDiscovery(
            NullLogger<InstructionDiscovery>.Instance,
            () => _workspace);

        var result = discovery.EnumerateCandidatePaths(_workspace, _workspace);

        PathsShouldEqual(result,
        [
            Path.Combine(_workspace, ".agents", "AGENTS.md"),
            Path.Combine(_workspace, ".seeing", "AGENTS.md"),
            Path.Combine(_workspace, "AGENTS.md")
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private InstructionDiscovery CreateDiscovery()
    {
        return new InstructionDiscovery(
            NullLogger<InstructionDiscovery>.Instance,
            () => _userHome);
    }

    private static void PathsShouldEqual(
        IEnumerable<string> actualPaths,
        IEnumerable<string> expectedPaths)
    {
        var actual = actualPaths.Select(Path.GetFullPath).ToList();
        var expected = expectedPaths.Select(Path.GetFullPath).ToList();

        actual.Should().HaveCount(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            StringComparer.OrdinalIgnoreCase.Equals(actual[i], expected[i])
                .Should().BeTrue($"path at index {i} should be {expected[i]}, but found {actual[i]}");
        }
    }
}
