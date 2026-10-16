using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Persistence;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Session;

/// <summary>
/// 启动扫描归档服务行为：阈值判定、活跃组跳过、消息数下限、无组独立会话。
/// </summary>
public sealed class SessionArchivalScanServiceTests
{
    private static (ServiceProvider Provider, InMemorySessionStore Store) BuildProvider(
        SessionArchivalOptions options)
    {
        var services = new ServiceCollection();
        var store = new InMemorySessionStore();
        var sessionManager = new SessionManager(store: store, logger: NullLogger<SessionManager>.Instance);
        var forker = new SessionForker(NullLogger<SessionForker>.Instance, sessionManager);
        var groupStore = new InMemoryGroupStore();
        var groupManager = new SessionGroupManager(
            sessionManager, groupStore, forker, NullLogger<SessionGroupManager>.Instance);

        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton<ISessionGroupManager>(groupManager);
        services.AddSingleton(options);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<SessionArchivalScanService>>(
            NullLogger<SessionArchivalScanService>.Instance);

        return (services.BuildServiceProvider(), store);
    }

    private static async Task<SessionData> SeedAsync(
        InMemorySessionStore store, string title, DateTime lastActive, int messageCount, string? groupId = null)
    {
        var session = SessionData.Create(selectedAgent: "build");
        session.Title = title;
        session.GroupId = groupId;
        session.LastActiveAt = lastActive;
        for (var i = 0; i < messageCount; i++)
            session.AddMessage(new SessionMessage { Role = MessageRole.User, Content = $"m{i}" });
        session.LastActiveAt = lastActive;
        await store.SaveAsync(session);
        return session;
    }

    private static async Task RunScanAsync(ServiceProvider provider, SessionArchivalOptions options)
    {
        var service = new SessionArchivalScanService(
            provider, options, NullLogger<SessionArchivalScanService>.Instance);
        await service.StartAsync(CancellationToken.None);
        // BackgroundService 的 ExecuteAsync 是 fire-and-forget；给它一点时间完成
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Scan_ShouldArchiveInactiveLooseSession()
    {
        var options = new SessionArchivalOptions { InactivityThreshold = TimeSpan.FromDays(30) };
        var (provider, store) = BuildProvider(options);
        using var _ = provider;
        var old = await SeedAsync(store, "old", DateTime.Now.AddDays(-60), 3);

        await RunScanAsync(provider, options);

        (await store.LoadAsync(old.Id)).Should().BeNull();
        var archived = new List<SessionSummary>();
        await foreach (var s in store.ListArchivedAsync())
            archived.Add(s);
        archived.Should().ContainSingle(s => s.Id == old.Id);
    }

    [Fact]
    public async Task Scan_ShouldKeepActiveSession()
    {
        var options = new SessionArchivalOptions { InactivityThreshold = TimeSpan.FromDays(30) };
        var (provider, store) = BuildProvider(options);
        using var _ = provider;
        var recent = await SeedAsync(store, "recent", DateTime.Now.AddDays(-1), 3);

        await RunScanAsync(provider, options);

        (await store.LoadAsync(recent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Scan_ShouldRespectMinMessageCount()
    {
        var options = new SessionArchivalOptions
        {
            InactivityThreshold = TimeSpan.FromDays(30),
            MinMessageCount = 5
        };
        var (provider, store) = BuildProvider(options);
        using var _ = provider;
        var tiny = await SeedAsync(store, "tiny", DateTime.Now.AddDays(-60), 1);

        await RunScanAsync(provider, options);

        // 消息数低于下限 → 不归档
        (await store.LoadAsync(tiny.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Scan_ShouldBeDisabled_WhenDisabled()
    {
        var options = new SessionArchivalOptions
        {
            Enabled = false,
            InactivityThreshold = TimeSpan.FromDays(30)
        };
        var (provider, store) = BuildProvider(options);
        using var _ = provider;
        var old = await SeedAsync(store, "old", DateTime.Now.AddDays(-60), 3);

        await RunScanAsync(provider, options);

        (await store.LoadAsync(old.Id)).Should().NotBeNull();
    }

    private sealed class InMemoryGroupStore : ISessionGroupStore
    {
        private readonly Dictionary<string, SessionGroup> _groups = new();

        public Task<SessionGroup?> LoadAsync(string groupId, CancellationToken ct = default) =>
            Task.FromResult(_groups.TryGetValue(groupId, out var g) ? g : null);

        public Task<SessionGroup?> FindBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_groups.Values.FirstOrDefault(g => g.Members.Any(m => m.SessionId == sessionId)));

        public Task SaveAsync(SessionGroup group, CancellationToken ct = default)
        {
            _groups[group.Id] = group;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string groupId, CancellationToken ct = default)
        {
            _groups.Remove(groupId);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionGroup> ListAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var g in _groups.Values.ToList())
                yield return g;
        }
    }
}
