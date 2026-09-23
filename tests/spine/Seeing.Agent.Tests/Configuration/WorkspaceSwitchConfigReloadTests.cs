using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// 验证 <see cref="SeeingAgentInitializationExtensions.InitializeSeeingAsync"/> 在工作区
/// 解析到全局/自定义路径后，会重新加载项目级配置（如 Permission.AutoApproveAll）。
/// </summary>
public class WorkspaceSwitchConfigReloadTests
{
    [Fact]
    public async Task InitializeSeeing_WorkspaceSwitchToGlobal_ShouldReloadProjectConfig()
    {
        // Arrange：启动目录无 Permission；全局工作区含 Permission.AutoApproveAll=true
        using var startupWorkspace = new TempWorkspace();
        using var globalWorkspace = new TempWorkspace();

        var startupSeeing = Path.Combine(startupWorkspace.Root, ".seeing");
        Directory.CreateDirectory(startupSeeing);
        await File.WriteAllTextAsync(
            Path.Combine(startupSeeing, "seeing.json"),
            """{ "SeeingAgent": { "Workspace": { "UseGlobal": true } } }""", TestContext.Current.CancellationToken);

        var globalSeeing = Path.Combine(globalWorkspace.Root, ".seeing");
        Directory.CreateDirectory(globalSeeing);
        await File.WriteAllTextAsync(
            Path.Combine(globalSeeing, "seeing.json"),
            """{ "SeeingAgent": { "Permission": { "AutoApproveAll": true } } }""", TestContext.Current.CancellationToken);

        var previousEnv = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", globalWorkspace.Root);

        try
        {
            var services = new ServiceCollection();
            var registry = ConfigSectionRegistry.CreateWithSpine();
            services.AddSingleton<IConfigSectionRegistry>(registry);
            services.AddSingleton<IWorkspaceProvider, WorkspaceProvider>();
            services.AddSingleton<WorkspaceProvider>();
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
            services.AddSeeingCore(registry);

            await using var sp = services.BuildServiceProvider();
            var configManager = sp.GetRequiredService<UnifiedConfigManager>();

            // Act
            await sp.InitializeSeeingAsync(CancellationToken.None);

            // Assert：工作区切换到全局后，项目级 Permission 应被读取
            sp.GetRequiredService<IWorkspaceProvider>().GetProjectRoot().Should().Be(globalWorkspace.Root);
            configManager.SeeingAgent.Permission?.AutoApproveAll.Should().BeTrue(
                "全局工作区的 Permission.AutoApproveAll=true 应在工作区解析后重新加载");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", previousEnv);
        }
    }

    [Fact]
    public async Task InitializeSeeing_StartupDirHasPermission_ShouldLoadIt()
    {
        // Arrange：启动目录即工作区（无 UseGlobal），项目级 Permission=true
        using var workspace = new TempWorkspace();
        var seeingDir = Path.Combine(workspace.Root, ".seeing");
        Directory.CreateDirectory(seeingDir);
        await File.WriteAllTextAsync(
            Path.Combine(seeingDir, "seeing.json"),
            """{ "SeeingAgent": { "Permission": { "AutoApproveAll": true } } }""", TestContext.Current.CancellationToken);

        var previousEnv = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", workspace.Root);

        try
        {
            var services = new ServiceCollection();
            var registry = ConfigSectionRegistry.CreateWithSpine();
            services.AddSingleton<IConfigSectionRegistry>(registry);
            services.AddSingleton<IWorkspaceProvider, WorkspaceProvider>();
            services.AddSingleton<WorkspaceProvider>();
            services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)));
            services.AddSeeingCore(registry);

            await using var sp = services.BuildServiceProvider();
            var configManager = sp.GetRequiredService<UnifiedConfigManager>();

            // Act
            await sp.InitializeSeeingAsync(CancellationToken.None);

            // Assert
            configManager.SeeingAgent.Permission?.AutoApproveAll.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", previousEnv);
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
