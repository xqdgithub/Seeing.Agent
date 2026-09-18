using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// 会话组测试夹具：共享会话存储 + 文件组存储，便于构造冷缓存管理器。
/// </summary>
internal sealed class SessionGroupTestHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(
        Path.GetTempPath(), "seeing-grp-" + Guid.NewGuid().ToString("N"));

    public InMemorySessionStore SessionStore { get; } = new();
    public FileSessionGroupStore GroupStore { get; }
    public SessionManager Sessions { get; }
    public SessionGroupManager Manager { get; }

    public SessionGroupTestHarness()
    {
        GroupStore = new FileSessionGroupStore(Dir);
        Sessions = new SessionManager(store: SessionStore, logger: new NullLogger<SessionManager>());
        var forker = new SessionForker(new NullLogger<SessionForker>(), Sessions);
        Manager = new SessionGroupManager(Sessions, GroupStore, forker, NullLogger<SessionGroupManager>.Instance);
    }

    /// <summary>构造共享同一后端存储、但缓存为空的组管理器（用于冷兜底测试）。</summary>
    public SessionGroupManager NewColdManager()
    {
        var sessions = new SessionManager(store: SessionStore, logger: new NullLogger<SessionManager>());
        var groupStore = new FileSessionGroupStore(Dir);
        var forker = new SessionForker(new NullLogger<SessionForker>(), sessions);
        return new SessionGroupManager(sessions, groupStore, forker, NullLogger<SessionGroupManager>.Instance);
    }

    /// <summary>创建并注册一个根会话（未入组）。</summary>
    public SessionData CreateRoot(string? title = null)
    {
        var session = Sessions.Create(partitionId: "p1", selectedAgent: "build");
        session.Title = title ?? "root-" + session.Id;
        Sessions.Register(session);
        return session;
    }

    /// <summary>创建并注册一个普通会话（未入组）。</summary>
    public SessionData CreatePlainSession(SessionKind kind = SessionKind.Root)
    {
        var session = Sessions.Create(partitionId: "p1", selectedAgent: "build");
        session.Kind = kind;
        Sessions.Register(session);
        return session;
    }

    public void Dispose()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
    }
}
