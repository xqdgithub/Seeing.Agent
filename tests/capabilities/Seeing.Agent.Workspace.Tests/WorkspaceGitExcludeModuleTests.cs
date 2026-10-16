using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Seeing.Agent.Workspace.Tests;

/// <summary>
/// <see cref="WorkspaceGitExcludeModule"/> 契约：模块 id / 依赖 / DI 登记。
/// </summary>
public class WorkspaceGitExcludeModuleTests
{
    [Fact]
    public void ModuleMetadata_ShouldBeStable()
    {
        var module = new WorkspaceGitExcludeModule();

        module.Id.Should().Be("workspace.git-exclude");
        module.DependsOn.Should().Equal("io.local");
        module.ProvidedTools.Should().BeEmpty();
        module.ProvidedSeams.Should().BeEmpty();
    }

    [Fact]
    public void ConfigureServices_ShouldRegisterExcluderAndHandler()
    {
        var services = new ServiceCollection();

        new WorkspaceGitExcludeModule().ConfigureServices(services);

        services.Should().Contain(d => d.ServiceType == typeof(WorkspaceGitExcluder));
        services.Should().Contain(d => d.ServiceType == typeof(WorkspaceGitExcludeReloadHandler));
    }

    [Fact]
    public void ReloadHandler_ShouldSubscribeToWorkspaceChange()
    {
        var services = new ServiceCollection();
        services.AddSingleton<WorkspaceGitExcluder>(sp =>
            new WorkspaceGitExcluder(
                new Seeing.IO.Local.LocalExecutionWorld().Subprocess,
                new Seeing.IO.Local.LocalExecutionWorld().FileSystem,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceGitExcluder>.Instance));
        services.AddSingleton<WorkspaceGitExcludeReloadHandler>(sp =>
            new WorkspaceGitExcludeReloadHandler(
                sp.GetRequiredService<WorkspaceGitExcluder>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceGitExcludeReloadHandler>.Instance));

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<WorkspaceGitExcludeReloadHandler>();

        handler.ComponentId.Should().Be("workspace.git-exclude");
        handler.ChangeTypes.Should().Equal(typeof(Seeing.Agent.Abstractions.Configuration.WorkspaceChange));
    }
}
