using FluentAssertions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class WorkspaceProviderUnresolvedTests
{
    [Fact]
    public void NewProvider_ShouldResolveToStartupDirectory_ForCompatibility()
    {
        var provider = new WorkspaceProvider();

        // 兼容语义：无启动请求的宿主（测试 / 极简宿主）仍以启动目录为根
        provider.IsResolved.Should().BeTrue();
        provider.ResolvedRoot.Should().Be(provider.StartupDirectory);
    }

    [Fact]
    public void CreateUnresolved_ShouldNotExposeStartupDirectoryAsWorkspace()
    {
        var provider = WorkspaceProvider.CreateUnresolved();

        provider.IsResolved.Should().BeFalse();
        provider.ResolvedRoot.Should().BeNull();

        // 未选定时项目路径是占位目录（Generic Host 启动期会构造依赖它的单例），
        // 但「未选定」不能被当成真实工作区
        provider.ProjectSeeingDirectory.Should().NotBe(
            Path.Combine(provider.StartupDirectory, ".seeing"));
        provider.Invoking(p => p.GetProjectRoot())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task InitializeAsync_WithoutInitialWorkspace_ShouldNotResolve()
    {
        var provider = WorkspaceProvider.CreateUnresolved();

        await provider.InitializeAsync(TestContext.Current.CancellationToken);

        provider.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void SetInitialWorkspace_ShouldResolveAndExposeProjectRoot()
    {
        using var dir = new TempDir();
        var provider = new WorkspaceProvider();

        provider.SetInitialWorkspace(dir.Root, WorkspaceResolutionSource.StartupDirectory);

        provider.IsResolved.Should().BeTrue();
        provider.ResolvedRoot.Should().Be(dir.Root);
        provider.ProjectSeeingDirectory.Should().Be(Path.Combine(dir.Root, ".seeing"));
        provider.GetProjectRoot().Should().Be(dir.Root);
    }

    [Fact]
    public void ExplicitConstructor_ShouldStillResolve()
    {
        using var dir = new TempDir();
        var provider = new WorkspaceProvider(dir.Root);

        provider.IsResolved.Should().BeTrue();
        provider.GetProjectRoot().Should().Be(dir.Root);
    }

    [Fact]
    public void SetInitialWorkspace_ShouldRaiseChangeEvent_FromEmptyRoot()
    {
        using var dir = new TempDir();
        var provider = WorkspaceProvider.CreateUnresolved();
        WorkspaceChangedEventArgs? captured = null;
        provider.WorkspaceRootChanged += (_, e) => captured = e;

        provider.SetInitialWorkspace(dir.Root, WorkspaceResolutionSource.ManualSwitch);

        captured.Should().NotBeNull();
        captured!.OldWorkspace.Should().BeEmpty();
        captured.NewWorkspace.Should().Be(dir.Root);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
