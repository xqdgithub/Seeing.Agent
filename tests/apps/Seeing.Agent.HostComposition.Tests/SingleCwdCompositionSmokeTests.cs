using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Hosting;
using Xunit;

namespace Seeing.Agent.HostComposition.Tests;

/// <summary>
/// 真实组合根 smoke：新端口在 Full 能力集下可解析，且目录判定/多根工厂按预期接线。
/// <para>不发 LLM、不建会话，只验证"实现是否真的接上了"。</para>
/// </summary>
public class SingleCwdCompositionSmokeTests
{
    private static (ServiceProvider Provider, string Root) Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "seeing_smoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<IConfiguration>(configuration);
        // 覆盖 Core 的 TryAddSingleton：让工作区决议为真实临时根（而不是进程 CWD）
        services.AddSingleton<IWorkspaceProvider>(new WorkspaceProvider(root));

        services.AddSeeingFullCapabilities(registry, configuration, includeGatewayServer: false);
        services.AddSessionHosting();
        services.AddExecutionEngine();
        services.AddSeeingCore(registry);

        // Core 用自定义 SeeingAgentOptionsMonitor（直接读 UnifiedConfigManager），
        // 不消费 IConfigureOptions；这里替换 IOptionsMonitor 以模拟「硬边界开启」。
        var options = new Moq.Mock<Microsoft.Extensions.Options.IOptionsMonitor<SeeingAgentOptions>>();
        options.SetupGet(o => o.CurrentValue).Returns(new SeeingAgentOptions
        {
            Workspace = new WorkspaceOptions { RestrictToWorkspace = true }
        });
        services.AddSingleton(options.Object);

        return (services.BuildServiceProvider(), root);
    }

    [Fact]
    public void FullComposition_ShouldResolveDirectoryPorts()
    {
        var (provider, root) = Build();
        using var _ = provider;

        provider.GetRequiredService<ISessionRootSet>().Should().NotBeNull();
        provider.GetRequiredService<ICollaborationDirectories>().Should().NotBeNull();
        provider.GetRequiredService<IFileWorkspaceFactory>().Should().NotBeNull();
        provider.GetRequiredService<IWorkspacePathGate>().Should().NotBeNull();

        // 门闸委托的是 ISessionRootSet：项目根内放行、根外拒绝
        var gate = provider.GetRequiredService<IWorkspacePathGate>();
        gate.EnsureAllowed("smoke", Path.Combine(root, "a.txt")).Should().BeNull();
        gate.EnsureAllowed("smoke", Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"), "a.txt"))
            .Should().NotBeNull();

        provider.GetRequiredService<IWorkspaceProvider>().GetProjectRoot()
            .Should().Be(Path.GetFullPath(root));
    }

    [Fact]
    public async Task FileWorkspaceFactory_WithoutSession_ShouldExposeProjectRoot()
    {
        var (provider, root) = Build();
        using var _ = provider;

        var factory = provider.GetRequiredService<IFileWorkspaceFactory>();
        var roots = await factory.ListAsync("no-such-session", TestContext.Current.CancellationToken);

        roots.Should().ContainSingle();
        roots[0].Root.Kind.Should().Be(FileWorkspaceRootKind.Project);
        roots[0].RootPath.Should().Be(Path.GetFullPath(root));

        // 释放幂等（无引用计数）
        factory.Release("no-such-session");
        factory.Release("no-such-session");
    }

    [Fact]
    public void CollaborationContextSource_ShouldBeScoped()
    {
        var (provider, _) = Build();
        using var __ = provider;

        using var scope = provider.CreateScope();
        var sources = scope.ServiceProvider.GetServices<Seeing.Agent.Abstractions.Context.IContextSource>();

        sources.Should().Contain(s => s.GetType().Name == "CollaborationDirectoriesContextSource");
    }
}
