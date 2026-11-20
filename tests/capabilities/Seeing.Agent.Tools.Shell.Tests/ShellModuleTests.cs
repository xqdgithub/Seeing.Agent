using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tests.Tools;
using Seeing.Agent.Tools.Shell;
using Xunit;

namespace Seeing.Agent.Tools.Shell.Tests;

public class ShellModuleTests
{
    [Fact]
    public void Module_Id_IsShell()
    {
        var module = new ShellModule();
        module.Id.Should().Be("shell");
    }

    [Fact]
    public void ProvidedTools_ContainsBash()
    {
        var module = new ShellModule();
        module.ProvidedTools.Should().Contain("bash");
    }

    [Fact]
    public void DependsOn_IncludesIoLocal()
    {
        var module = new ShellModule();
        module.DependsOn.Should().Contain("io.local");
    }

    [Fact]
    public async Task Activate_ShouldRegisterProcessLauncher()
    {
        var control = new Mock<IBackgroundActivityControl>();
        var tools = new Mock<IToolManager>();
        var launcher = CreateLauncher();

        var services = new FakeServiceProvider()
            .Add<IToolManager>(tools.Object)
            .Add<IBackgroundActivityControl>(control.Object)
            .Add(launcher)
            .Add(CreateBashTool());

        await new ShellModule().ActivateAsync(services, TestContext.Current.CancellationToken);

        control.Verify(
            c => c.RegisterLauncher("shell", It.Is<IBackgroundActivityLauncher>(l => ReferenceEquals(l, launcher))),
            Times.Once);
    }

    [Fact]
    public async Task Activate_WithoutBackgroundControl_ShouldNotThrow()
    {
        var tools = new Mock<IToolManager>();
        var services = new FakeServiceProvider()
            .Add<IToolManager>(tools.Object)
            .Add(CreateLauncher())
            .Add(CreateBashTool());

        var act = async () => await new ShellModule().ActivateAsync(services);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Deactivate_ShouldUnregisterProcessLauncher()
    {
        var control = new Mock<IBackgroundActivityControl>();
        control.Setup(c => c.UnregisterLauncherAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var tools = new Mock<IToolManager>();
        var services = new FakeServiceProvider()
            .Add<IToolManager>(tools.Object)
            .Add<IBackgroundActivityControl>(control.Object)
            .Add(CreateLauncher())
            .Add(CreateBashTool());

        await new ShellModule().DeactivateAsync(services, TestContext.Current.CancellationToken);

        control.Verify(
            c => c.UnregisterLauncherAsync(ProcessBackgroundActivityLauncher.ProcessKind, It.IsAny<CancellationToken>()),
            Times.Once);
        tools.Verify(
            t => t.UnregisterToolAsync("bash", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static ProcessBackgroundActivityLauncher CreateLauncher()
    {
        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(Path.GetTempPath());
        world.Setup(w => w.Subprocess).Returns(new Mock<ISubprocessFactory>().Object);

        var shell = new Mock<IShellService>();
        var env = new Mock<IShellEnvironmentService>();

        return new ProcessBackgroundActivityLauncher(
            world.Object, shell.Object, env.Object, NullLogger<ProcessBackgroundActivityLauncher>.Instance);
    }

    private static BashTool CreateBashTool()
    {
        var options = new Mock<IOptionsMonitor<ShellOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new ShellOptions());

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(Path.GetTempPath());

        return new BashTool(
            NullLogger<BashTool>.Instance,
            world.Object,
            new Mock<IShellService>().Object,
            new Mock<IShellEnvironmentService>().Object,
            options.Object,
            AllowAllWorkspacePathGate.Instance);
    }

    /// <summary>只按类型供服务的极简容器（避免为两个类型拖进完整 DI 栈）。</summary>
    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public FakeServiceProvider Add<T>(T service) where T : class
        {
            _services[typeof(T)] = service;
            return this;
        }

        public object? GetService(Type serviceType)
            => _services.TryGetValue(serviceType, out var service) ? service : null;
    }
}
