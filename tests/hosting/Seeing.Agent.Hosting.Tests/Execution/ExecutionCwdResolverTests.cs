using FluentAssertions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

public class ExecutionCwdResolverTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "seeing_cwd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Resolve_NoInputs_ShouldUseProjectRootAndRequestWriteBack()
    {
        var root = NewRoot();
        var result = ExecutionCwdResolver.Resolve(null, null, new WorkspaceProvider(root));

        result.Path.Should().Be(Path.GetFullPath(root));
        result.NeedsSessionWriteBack.Should().BeTrue();
    }

    [Fact]
    public void Resolve_SessionCwdOutsideRoot_ShouldConverge()
    {
        var root = NewRoot();
        var result = ExecutionCwdResolver.Resolve(null, "D:/somewhere-else", new WorkspaceProvider(root));

        result.Path.Should().Be(Path.GetFullPath(root));
        result.NeedsSessionWriteBack.Should().BeTrue();
        result.Warning.Should().NotBeNull();
    }

    [Fact]
    public void Resolve_RequestCwdOutsideRoot_ShouldConverge()
    {
        var root = NewRoot();
        var result = ExecutionCwdResolver.Resolve("D:/else", null, new WorkspaceProvider(root));

        result.Path.Should().Be(Path.GetFullPath(root));
    }

    [Fact]
    public void Resolve_SessionCwdMatchingRoot_ShouldNotWriteBack()
    {
        var root = NewRoot();
        var result = ExecutionCwdResolver.Resolve(null, root, new WorkspaceProvider(root));

        result.Path.Should().Be(Path.GetFullPath(root));
        result.NeedsSessionWriteBack.Should().BeFalse();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public void Resolve_UnresolvedWorkspace_ShouldThrow()
    {
        var act = () => ExecutionCwdResolver.Resolve(null, null, WorkspaceProvider.CreateUnresolved());

        act.Should().Throw<InvalidOperationException>();
    }
}
