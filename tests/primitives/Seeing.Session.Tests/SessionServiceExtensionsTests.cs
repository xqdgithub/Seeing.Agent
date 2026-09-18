using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using System.Reflection;
using Xunit;

namespace Seeing.Session.Tests;

public class SessionServiceExtensionsTests
{
    [Fact]
    public void AddSessionManager_ConcreteAndInterface_ShouldBeSameInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), "seeing-session-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSessionManager(storagePath: path);

            using var sp = services.BuildServiceProvider();
            var concrete = sp.GetRequiredService<SessionManager>();
            var iface = sp.GetRequiredService<ISessionManager>();
            var store = sp.GetRequiredService<ISessionStore>();

            ReferenceEquals(concrete, iface).Should().BeTrue();
            store.Should().NotBeNull();
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void AddSessionManager_WhenAlreadyRegistered_ShouldBeNoOp()
    {
        var services = new ServiceCollection();
        var existing = new SessionManager(store: new InMemorySessionStore());
        services.AddSingleton<ISessionManager>(existing);

        services.AddSessionManager(storagePath: Path.GetTempPath());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISessionManager>().Should().BeSameAs(existing);
        services.Count(d => d.ServiceType == typeof(ISessionManager)).Should().Be(1);
    }

    [Fact]
    public void AddSessionManager_SessionGroupManager_ShouldUseInjectedSessionForker()
    {
        var path = Path.Combine(Path.GetTempPath(), "seeing-session-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddSessionManager(storagePath: path);

            using var sp = services.BuildServiceProvider();
            var forker = sp.GetRequiredService<SessionForker>();
            var manager = sp.GetRequiredService<SessionGroupManager>();

            var field = typeof(SessionGroupManager)
                .GetField("_forker", BindingFlags.NonPublic | BindingFlags.Instance);

            field.Should().NotBeNull();
            field!.GetValue(manager).Should().BeSameAs(forker);
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
