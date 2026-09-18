using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tests.Sessions;

/// <summary>
/// Task 7：Core DI 必须注册会话组管理器与分支器，且为单例（组关系唯一权威）。
/// </summary>
public class SessionGroupDiTests
{
    private static ServiceProvider BuildProvider(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "seeing-grp-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dir = tempDir;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IWorkspaceProvider>(w => w.ProjectSeeingDirectory == dir));
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddSeeingCore_Registers_SessionGroupManager_As_Singleton()
    {
        var provider = BuildProvider(out var tempDir);
        try
        {
            var groupManager = provider.GetRequiredService<ISessionGroupManager>();
            var again = provider.GetRequiredService<ISessionGroupManager>();

            groupManager.Should().NotBeNull();
            groupManager.Should().BeSameAs(again);
            provider.GetRequiredService<SessionGroupManager>().Should().BeSameAs(groupManager);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AddSeeingCore_Registers_SessionForker_As_Singleton()
    {
        var provider = BuildProvider(out var tempDir);
        try
        {
            var forker = provider.GetRequiredService<SessionForker>();
            var again = provider.GetRequiredService<SessionForker>();

            forker.Should().NotBeNull();
            forker.Should().BeSameAs(again);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AddSeeingCore_SessionGroupManager_ShouldUseInjectedSessionForker()
    {
        var provider = BuildProvider(out var tempDir);
        try
        {
            var forker = provider.GetRequiredService<SessionForker>();
            var manager = provider.GetRequiredService<SessionGroupManager>();

            var field = typeof(SessionGroupManager)
                .GetField("_forker", BindingFlags.NonPublic | BindingFlags.Instance);

            field.Should().NotBeNull();
            field!.GetValue(manager).Should().BeSameAs(forker);
        }
        finally
        {
            provider.Dispose();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
