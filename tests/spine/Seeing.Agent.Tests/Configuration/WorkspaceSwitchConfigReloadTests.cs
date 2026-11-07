using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// 验证 <see cref="SeeingAgentInitializationExtensions.InitializeSeeingAsync"/> 按启动决议
/// 设定工作区，并在设根后重新加载项目级配置（如 Permission.AutoApproveAll）。
/// </summary>
public class WorkspaceSwitchConfigReloadTests
{
    [Fact]
    public async Task InitializeSeeing_ExplicitWorkspace_ShouldReloadProjectConfig()
    {
        // Arrange：启动目录无 Permission；决议指向含 Permission.AutoApproveAll=true 的目录
        using var startupWorkspace = new TempWorkspace();
        using var targetWorkspace = new TempWorkspace();

        var targetSeeing = Path.Combine(targetWorkspace.Root, ".seeing");
        Directory.CreateDirectory(targetSeeing);
        await File.WriteAllTextAsync(
            Path.Combine(targetSeeing, "seeing.json"),
            """{ "SeeingAgent": { "Permission": { "AutoApproveAll": true } } }""",
            TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<WorkspaceProvider>();
        services.AddSingleton<IWorkspaceProvider>(sp => sp.GetRequiredService<WorkspaceProvider>());
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
        // 必须替换真实历史存储：否则初始化会把测试临时目录写进 ~/.seeing/workspace_history.json
        services.AddSingleton<IWorkspaceHistoryStore>(new InMemoryHistory());
        services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
            WorkspaceLaunchMode.Interactive,
            startupWorkspace.Root,
            ExecutableDirectory: null,
            ExplicitPath: targetWorkspace.Root,
            InheritedEnvironment: null,
            History: WorkspaceHistorySnapshot.Empty)));
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();
        var configManager = sp.GetRequiredService<UnifiedConfigManager>();

        // Act
        await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

        // Assert：决议生效后，项目级 Permission 应被读取
        sp.GetRequiredService<IWorkspaceProvider>().GetProjectRoot().Should().Be(targetWorkspace.Root);
        configManager.SeeingAgent.Permission?.AutoApproveAll.Should().BeTrue(
            "工作区设定后必须重新加载项目级配置");
    }

    [Fact]
    public async Task InitializeSeeing_LastInteractive_ShouldLoadThatWorkspaceConfig()
    {
        // Arrange：启动目录与决议目录都不是「当前目录」，只有历史里记着目标目录
        using var startupWorkspace = new TempWorkspace();
        using var targetWorkspace = new TempWorkspace();

        var targetSeeing = Path.Combine(targetWorkspace.Root, ".seeing");
        Directory.CreateDirectory(targetSeeing);
        await File.WriteAllTextAsync(
            Path.Combine(targetSeeing, "seeing.json"),
            """{ "SeeingAgent": { "Permission": { "AutoApproveAll": true } } }""",
            TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        var registry = ConfigSectionRegistry.CreateWithSpine();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<WorkspaceProvider>();
        services.AddSingleton<IWorkspaceProvider>(sp => sp.GetRequiredService<WorkspaceProvider>());
        services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
        services.AddSingleton<IWorkspaceHistoryStore>(
            new FixedHistory(new WorkspaceHistorySnapshot(new[] { targetWorkspace.Root }, targetWorkspace.Root)));
        services.AddSingleton(new WorkspaceLaunchRequest(new WorkspaceLaunchInput(
            WorkspaceLaunchMode.Interactive,
            startupWorkspace.Root,
            ExecutableDirectory: null,
            ExplicitPath: null,
            InheritedEnvironment: null,
            History: WorkspaceHistorySnapshot.Empty)));
        services.AddSeeingCore(registry);

        await using var sp = services.BuildServiceProvider();
        var configManager = sp.GetRequiredService<UnifiedConfigManager>();

        // Act
        await sp.InitializeSeeingAsync(TestContext.Current.CancellationToken);

        // Assert
        sp.GetRequiredService<IWorkspaceProvider>().GetProjectRoot().Should().Be(targetWorkspace.Root);
        configManager.SeeingAgent.Permission?.AutoApproveAll.Should().BeTrue();
    }

    private sealed class FixedHistory : IWorkspaceHistoryStore
    {
        private readonly WorkspaceHistorySnapshot _snapshot;
        public FixedHistory(WorkspaceHistorySnapshot snapshot) => _snapshot = snapshot;

        public WorkspaceHistorySnapshot Read() => _snapshot;

        public void RecordOpened(string path, bool asDefault)
        {
        }

        public void Remove(string path)
        {
        }
    }

    /// <summary>内存历史：避免测试把临时目录写进用户真实的 ~/.seeing/workspace_history.json。</summary>
    private sealed class InMemoryHistory : IWorkspaceHistoryStore
    {
        public WorkspaceHistorySnapshot Read() => WorkspaceHistorySnapshot.Empty;

        public void RecordOpened(string path, bool asDefault)
        {
        }

        public void Remove(string path)
        {
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-agent-ws-" + Guid.NewGuid().ToString("N"));
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
                // 忽略清理时的文件锁竞争
            }
        }
    }
}
