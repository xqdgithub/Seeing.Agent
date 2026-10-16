using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Configuration;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Workspace.Tests;

/// <summary>
/// <see cref="WorkspaceGitExcludeModule.ActivateAsync"/> 端到端：在临时仓库中激活后，
/// <c>.seeing</c> 应被加入 <c>info/exclude</c>，且 ReloadHandler 被登记。
/// </summary>
public class WorkspaceGitExcludeModuleActivationTests : IDisposable
{
    private readonly string _root;

    public WorkspaceGitExcludeModuleActivationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seeing-act-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Activate_ShouldExcludeSeeing_AndRegisterReloadHandler()
    {
        RunGit("init", "-q");

        var registry = new FakeReloadHandlerRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<IExecutionWorld>(new LocalExecutionWorld());
        services.AddSingleton<IFileSystem>(sp => sp.GetRequiredService<IExecutionWorld>().FileSystem);
        services.AddSingleton<ISubprocessFactory>(sp => sp.GetRequiredService<IExecutionWorld>().Subprocess);
        services.AddSingleton<IWorkspaceProvider>(new FakeWorkspaceProvider(_root));
        services.AddSingleton<IReloadHandlerRegistry>(registry);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        var module = new WorkspaceGitExcludeModule();
        module.ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        var exclude = File.ReadAllText(Path.Combine(_root, ".git", "info", "exclude"));
        exclude.Should().Contain(WorkspaceGitExcluder.MarkerBegin);
        exclude.Should().Contain("/.seeing/");
        registry.Registered.Should().ContainSingle()
            .Which.ComponentId.Should().Be("workspace.git-exclude");

        // Deactivate 对称撤销
        await module.DeactivateAsync(provider, TestContext.Current.CancellationToken);
        registry.Unregistered.Should().ContainSingle();
    }

    [Fact]
    public async Task Activate_OnNonGitDirectory_ShouldNotThrow()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExecutionWorld>(new LocalExecutionWorld());
        services.AddSingleton<IFileSystem>(sp => sp.GetRequiredService<IExecutionWorld>().FileSystem);
        services.AddSingleton<ISubprocessFactory>(sp => sp.GetRequiredService<IExecutionWorld>().Subprocess);
        services.AddSingleton<IWorkspaceProvider>(new FakeWorkspaceProvider(_root));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        var module = new WorkspaceGitExcludeModule();
        module.ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var act = async () => await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        File.Exists(Path.Combine(_root, ".git", "info", "exclude")).Should().BeFalse();
    }

    private void RunGit(params string[] args)
    {
        var world = new LocalExecutionWorld();
        var spec = new Seeing.Agent.Abstractions.Execution.SubprocessSpec
        {
            FileName = "git",
            Arguments = string.Join(' ', args),
            WorkingDirectory = _root,
            CancellationToken = TestContext.Current.CancellationToken,
        };
        using var process = world.Subprocess.Start(spec);
        process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
        process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
        process.WaitForExitAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private sealed class FakeReloadHandlerRegistry : IReloadHandlerRegistry
    {
        public List<IReloadHandler> Registered { get; } = [];
        public List<IReloadHandler> Unregistered { get; } = [];

        public void RegisterHandler(IReloadHandler handler) => Registered.Add(handler);
        public void UnregisterHandler(IReloadHandler handler) => Unregistered.Add(handler);
    }

    private sealed class FakeWorkspaceProvider(string root) : IWorkspaceProvider
    {
        public string UserSeeingDirectory => Path.Combine(root, ".user-seeing");
        public string ProjectSeeingDirectory => Path.Combine(root, ".seeing");
        public string StartupDirectory => root;
        public WorkspaceResolutionSource ResolutionSource => WorkspaceResolutionSource.StartupDirectory;
        public string? GlobalWorkspaceRoot => null;

#pragma warning disable CS0067 // 测试替身无需触发工作区变更事件
        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;
#pragma warning restore CS0067

        public void SetWorkspaceRoot(string workspaceRoot) { }
        public string GetSeeingDirectory(ConfigLevel level) => ProjectSeeingDirectory;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
