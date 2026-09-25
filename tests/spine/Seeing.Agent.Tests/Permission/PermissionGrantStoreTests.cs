using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 授权存储（spec §5.4）：决策记忆（精确 / 目录前缀 / 作用域）+ 白名单目录面。
/// </summary>
public class PermissionGrantStoreTests
{
    private static readonly string TempRoot = Path.GetTempPath();
    private static readonly string WorkspaceRoot = Path.Combine(TempRoot, "seeing-grant-store-ws");
    private static readonly string Base = Path.Combine(TempRoot, "seeing-grant-store-base");
    private static readonly string SubDir = Path.Combine(Base, "sub");
    private static readonly string Sibling = Path.Combine(Base, "sub-sibling");
    private static readonly string OutsidePath = Path.Combine(TempRoot, "seeing-grant-store-outside", "a.txt");

    [Fact]
    public void Add_Lookup_ExactResource_ShouldReturnGrant()
    {
        var store = new PermissionGrantStore();
        var grant = new PermissionGrant("filesystem.read", "secret.pem", PermissionGrantScope.Session, PermissionEffect.Allow);

        store.Add("s1", grant);

        var found = store.Lookup("s1", "filesystem.read", "secret.pem");
        found.Should().ContainSingle().Which.Should().Be(grant);
    }

    [Fact]
    public void Lookup_DifferentResource_ShouldReturnEmpty()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", "a.pem", PermissionGrantScope.Session, PermissionEffect.Allow));

        store.Lookup("s1", "filesystem.read", "b.pem").Should().BeEmpty();
    }

    [Fact]
    public void Lookup_DifferentKind_ShouldReturnEmpty()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", "a.pem", PermissionGrantScope.Session, PermissionEffect.Allow));

        store.Lookup("s1", "filesystem.write", "a.pem").Should().BeEmpty();
    }

    [Fact]
    public void Lookup_SessionDirectory_SubPath_ShouldMatch()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", SubDir, PermissionGrantScope.SessionDirectory, PermissionEffect.Allow));

        var resource = Path.Combine(SubDir, "nested", "a.txt");
        store.Lookup("s1", "filesystem.read", resource).Should().ContainSingle();
    }

    [Fact]
    public void Lookup_SessionScope_SubPath_ShouldNotMatch()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", SubDir, PermissionGrantScope.Session, PermissionEffect.Allow));

        var resource = Path.Combine(SubDir, "nested", "a.txt");
        store.Lookup("s1", "filesystem.read", resource).Should().BeEmpty();
    }

    [Fact]
    public void Lookup_SessionDirectory_SiblingPrefix_ShouldNotMatch()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", SubDir, PermissionGrantScope.SessionDirectory, PermissionEffect.Allow));

        store.Lookup("s1", "filesystem.read", Path.Combine(Sibling, "a.txt")).Should().BeEmpty();
    }

    [Fact]
    public void AddSessionDirectory_ShouldBeQueryableAfterTrimEnd()
    {
        var store = new PermissionGrantStore();

        store.AddSessionDirectory("s1", SubDir + Path.DirectorySeparatorChar);

        store.ContainsSessionPath("s1", Path.Combine(SubDir, "a.txt")).Should().BeTrue();
        store.ContainsSessionPath("s1", SubDir).Should().BeTrue();
        store.ContainsSessionPath("s1", Sibling).Should().BeFalse();
    }

    [Fact]
    public void ContainsSessionPath_UnknownSession_ShouldBeFalse()
    {
        var store = new PermissionGrantStore();

        store.ContainsSessionPath("unknown", SubDir).Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldDeleteGrant()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", "a.pem", PermissionGrantScope.Session, PermissionEffect.Allow));

        store.Remove("s1", "filesystem.read", "a.pem");

        store.Lookup("s1", "filesystem.read", "a.pem").Should().BeEmpty();
    }

    [Fact]
    public void ClearSession_ShouldRemoveGrantsAndDirectories()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", "a.pem", PermissionGrantScope.Session, PermissionEffect.Allow));
        store.AddSessionDirectory("s1", SubDir);

        store.ClearSession("s1");

        store.Lookup("s1", "filesystem.read", "a.pem").Should().BeEmpty();
        store.ContainsSessionPath("s1", Path.Combine(SubDir, "a.txt")).Should().BeFalse();
    }

    [Fact]
    public void ClearAll_ShouldRemoveEverything()
    {
        var store = new PermissionGrantStore();
        store.Add("s1", new PermissionGrant("filesystem.read", "a.pem", PermissionGrantScope.Session, PermissionEffect.Allow));
        store.AddSessionDirectory("s2", SubDir);

        store.ClearAll();

        store.Lookup("s1", "filesystem.read", "a.pem").Should().BeEmpty();
        store.ContainsSessionPath("s2", Path.Combine(SubDir, "a.txt")).Should().BeFalse();
    }

    [Fact]
    public void WorkspacePathGate_ShouldAllowWhitelistedDirectory_EndToEnd()
    {
        var store = new PermissionGrantStore();
        store.AddSessionDirectory("s1", SubDir);
        var gate = CreateGate(store, restrict: true);

        gate.EnsureAllowed("s1", Path.Combine(SubDir, "a.txt")).Should().BeNull();
        gate.EnsureAllowed("s1", OutsidePath).Should().NotBeNull();
        gate.EnsureAllowed("unknown-session", Path.Combine(SubDir, "a.txt")).Should().NotBeNull();
    }

    [Fact]
    public void WorkspacePathGate_RestrictFalse_ShouldAllowEverything()
    {
        var store = new PermissionGrantStore();
        var gate = CreateGate(store, restrict: false);

        gate.EnsureAllowed("s1", OutsidePath).Should().BeNull();
        gate.EnsureAllowed(string.Empty, OutsidePath).Should().BeNull();
    }

    [Theory]
    [InlineData("tool.execute", PermissionKind.Tool)]
    [InlineData("filesystem.read", PermissionKind.File)]
    [InlineData("filesystem.external", PermissionKind.File)]
    [InlineData("shell.run", PermissionKind.Shell)]
    [InlineData("network.http", PermissionKind.Network)]
    [InlineData("mcp.execute", PermissionKind.McpTool)]
    [InlineData("mcp.tool", PermissionKind.McpTool)]
    [InlineData("unknown.kind", PermissionKind.Tool)]
    [InlineData(null, PermissionKind.Tool)]
    public void PermissionKindMapper_ShouldMapKindStrings(string? kind, PermissionKind expected)
    {
        PermissionKindMapper.Map(kind).Should().Be(expected);
    }

    private static WorkspacePathGate CreateGate(IPermissionGrantStore store, bool restrict)
    {
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.SetupGet(w => w.ProjectSeeingDirectory).Returns(Path.Combine(WorkspaceRoot, ".seeing"));

        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Workspace = new WorkspaceOptions { RestrictToWorkspace = restrict }
        });

        return new WorkspacePathGate(workspace.Object, store, options.Object);
    }
}
