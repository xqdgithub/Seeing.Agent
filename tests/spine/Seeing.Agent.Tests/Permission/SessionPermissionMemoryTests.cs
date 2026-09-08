using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

public class SessionPermissionMemoryTests
{
    [Fact]
    public void Match_ExactResource_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("tool.execute", "bash", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_DifferentKind_ShouldNotMatch()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("filesystem.read", "bash", "s1");

        result.Should().BeNull();
    }

    [Fact]
    public void Match_DirectoryPrefix_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.external",
            Resource = "/home/user/projects/",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("filesystem.external", "/home/user/projects/foo/bar.txt", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_DirectoryPrefix_WithBackslashes_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.external",
            Resource = "C:\\Users\\test\\",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("filesystem.external", "C:\\Users\\test\\subdir\\file.txt", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_DirectoryPrefix_MixedSeparators_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.external",
            Resource = "/home/user/projects/",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("filesystem.external", "\\home\\user\\projects\\foo\\bar.txt", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_RecursiveWildcard_ShouldReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.external",
            Resource = "/home/user/**",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("filesystem.external", "/home/user/projects/deep/nested/file.txt", "s1");

        result.Should().NotBeNull();
        result!.Action.Should().Be(PermissionMemoryAction.Allow);
    }

    [Fact]
    public void Match_DifferentSession_ShouldNotReturnEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        var result = memory.Match("tool.execute", "bash", "s2");

        result.Should().BeNull();
    }

    [Fact]
    public void ClearSession_ShouldRemoveAllEntries()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });

        memory.ClearSession("s1");

        memory.Match("tool.execute", "bash", "s1").Should().BeNull();
    }

    [Fact]
    public void Forget_SpecificResource_ShouldRemoveOnlyThatEntry()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "read",
            Action = PermissionMemoryAction.Deny
        });

        memory.Forget("s1", "bash");

        memory.Match("tool.execute", "bash", "s1").Should().BeNull();
        memory.Match("tool.execute", "read", "s1").Should().NotBeNull();
    }

    [Fact]
    public void Forget_NullResource_ShouldRemoveAllEntries()
    {
        var memory = new SessionPermissionMemory();
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "tool.execute",
            Resource = "bash",
            Action = PermissionMemoryAction.Allow
        });
        memory.Remember("s1", new PermissionMemoryEntry
        {
            PermissionKind = "filesystem.read",
            Resource = "/tmp/",
            Action = PermissionMemoryAction.Allow
        });

        memory.Forget("s1", null);

        memory.Match("tool.execute", "bash", "s1").Should().BeNull();
        memory.Match("filesystem.read", "/tmp/test.txt", "s1").Should().BeNull();
    }
}
