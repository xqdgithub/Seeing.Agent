using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class InitializeSeeingUnresolvedTests
{
    [Fact]
    public async Task InitializeSeeing_WithoutLaunchRequest_ShouldStayUnresolved()
    {
        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
            services.AddSingleton<IConfigSectionRegistry>(registry);
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
            services.AddSeeingCore(registry);

            await using var sp = services.BuildServiceProvider();

            await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

            sp.GetRequiredService<WorkspaceProvider>().IsResolved.Should().BeTrue(
                "没有启动请求的宿主保持兼容语义：以启动目录为根");
    }

    [Fact]
    public async Task InitializeSeeing_InteractiveWithoutHistory_ShouldStayUnresolved()
    {
        var startup = Path.Combine(Path.GetTempPath(), "seeing-unresolved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(startup);

        try
        {
            var services = new ServiceCollection();
            var registry = ConfigSectionRegistry.CreateWithSpine();
            services.AddSingleton<IConfigSectionRegistry>(registry);
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
            services.AddSingleton<IWorkspaceHistoryStore>(new EmptyHistory());
            services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
                WorkspaceLaunchMode.Interactive, startup, null, null, null, WorkspaceHistorySnapshot.Empty)));
            services.AddSeeingCore(registry);

            await using var sp = services.BuildServiceProvider();

            await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

            sp.GetRequiredService<WorkspaceProvider>().IsResolved.Should().BeFalse(
                "没有历史也没有 --workspace 时应停在首页");
            sp.GetRequiredService<IWorkspaceLaunchState>().IsReady.Should().BeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(startup, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task InitializeSeeing_ExplicitPathMissing_ShouldThrow()
    {
        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
        services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
            WorkspaceLaunchMode.Interactive,
            Path.GetTempPath(),
            null,
            ExplicitPath: Path.Combine(Path.GetTempPath(), "seeing-missing-" + Guid.NewGuid().ToString("N")),
            InheritedEnvironment: null,
            History: WorkspaceHistorySnapshot.Empty)));
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();

        await sp.Invoking(s => s.InitializeSeeingAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>历史固定为空：避免测试机真实历史（~/.seeing/workspace_history.json）影响断言。</summary>
    private sealed class EmptyHistory : IWorkspaceHistoryStore
    {
        public WorkspaceHistorySnapshot Read() => WorkspaceHistorySnapshot.Empty;

        public void RecordOpened(string path, bool asDefault)
        {
        }

        public void Remove(string path)
        {
        }
    }
}
