using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Core.Instructions;
using Xunit;

namespace Seeing.Agent.Tests.Instructions;

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
    public async Task DiscoverAsync_WhenCwdIsNested_ReturnsFilesInSpecificationOrder()
    {
        var cwd = Path.Combine(_workspace, "src", "feature");
        Directory.CreateDirectory(cwd);

        var expectedPaths = new[]
        {
            await WriteAgentsFileAsync(Path.Combine(_userHome, ".agents"), "user agents"),
            await WriteAgentsFileAsync(Path.Combine(_userHome, ".seeing"), "user seeing"),
            await WriteAgentsFileAsync(Path.Combine(_workspace, ".agents"), "workspace agents"),
            await WriteAgentsFileAsync(Path.Combine(_workspace, ".seeing"), "workspace seeing"),
            await WriteAgentsFileAsync(_workspace, "workspace root"),
            await WriteAgentsFileAsync(Path.Combine(_workspace, "src"), "src"),
            await WriteAgentsFileAsync(cwd, "feature")
        };
        var discovery = CreateDiscovery();

        var result = await discovery.DiscoverAsync(cwd, _workspace, TestContext.Current.CancellationToken);

        PathsShouldEqual(result.Select(file => file.Path), expectedPaths);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCwdEqualsWorkspace_ReturnsUserAndWorkspaceFilesWithoutAncestorEntries()
    {
        var expectedPaths = new[]
        {
            await WriteAgentsFileAsync(Path.Combine(_userHome, ".agents"), "user agents"),
            await WriteAgentsFileAsync(Path.Combine(_userHome, ".seeing"), "user seeing"),
            await WriteAgentsFileAsync(Path.Combine(_workspace, ".agents"), "workspace agents"),
            await WriteAgentsFileAsync(Path.Combine(_workspace, ".seeing"), "workspace seeing"),
            await WriteAgentsFileAsync(_workspace, "workspace root")
        };
        var discovery = CreateDiscovery();

        var result = await discovery.DiscoverAsync(
            _workspace,
            _workspace,
            TestContext.Current.CancellationToken);

        PathsShouldEqual(result.Select(file => file.Path), expectedPaths);
    }

    [Fact]
    public async Task DiscoverAsync_WhenCwdIsOutsideWorkspace_ReturnsOnlyUserFiles()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var expectedPaths = new[]
        {
            await WriteAgentsFileAsync(Path.Combine(_userHome, ".agents"), "user agents"),
            await WriteAgentsFileAsync(Path.Combine(_userHome, ".seeing"), "user seeing")
        };
        await WriteAgentsFileAsync(Path.Combine(_workspace, ".agents"), "workspace agents");
        await WriteAgentsFileAsync(Path.Combine(_workspace, ".seeing"), "workspace seeing");
        await WriteAgentsFileAsync(_workspace, "workspace root");
        await WriteAgentsFileAsync(outside, "outside");
        var discovery = CreateDiscovery();

        var result = await discovery.DiscoverAsync(
            outside,
            _workspace,
            TestContext.Current.CancellationToken);

        PathsShouldEqual(result.Select(file => file.Path), expectedPaths);
    }

    [Fact]
    public async Task DiscoverAsync_WhenLocationsResolveToSamePath_KeepsFirstOccurrence()
    {
        var agentsPath = await WriteAgentsFileAsync(Path.Combine(_workspace, ".agents"), "agents");
        var seeingPath = await WriteAgentsFileAsync(Path.Combine(_workspace, ".seeing"), "seeing");
        var rootPath = await WriteAgentsFileAsync(_workspace, "root");
        var discovery = new InstructionDiscovery(
            NullLogger<InstructionDiscovery>.Instance,
            () => _workspace);

        var result = await discovery.DiscoverAsync(
            _workspace,
            _workspace,
            TestContext.Current.CancellationToken);

        PathsShouldEqual(result.Select(file => file.Path), [agentsPath, seeingPath, rootPath]);
    }

    [Fact]
    public async Task DiscoverAsync_WhenFileExists_PopulatesContentTimestampAndSha256Hash()
    {
        const string content = "你好, progressive instructions";
        var path = await WriteAgentsFileAsync(Path.Combine(_userHome, ".agents"), content);
        var discovery = CreateDiscovery();
        var expectedHash = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()}";

        var result = await discovery.DiscoverAsync(
            _workspace,
            _workspace,
            TestContext.Current.CancellationToken);

        var file = result.Should().ContainSingle().Subject;
        StringComparer.OrdinalIgnoreCase.Equals(
            Path.GetFullPath(file.Path),
            Path.GetFullPath(path)).Should().BeTrue();
        file.Content.Should().Be(content);
        file.LastModified.Should().NotBe(default);
        file.Hash.Should().Be(expectedHash);
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

    private static async Task<string> WriteAgentsFileAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "AGENTS.md");
        await File.WriteAllTextAsync(path, content);
        return Path.GetFullPath(path);
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
