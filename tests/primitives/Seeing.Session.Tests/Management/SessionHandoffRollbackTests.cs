using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// 交接后继创建中途失败时，管理器必须自清理，避免孤儿会话 / 组成员泄漏。
/// </summary>
public class SessionHandoffRollbackTests
{
    [Fact]
    public async Task CreateHandoffSuccessorAsync_WhenGroupSaveFails_ShouldCleanUpOrphanSessionAndMember()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing-handoff-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sessions = new SessionManager(store: new InMemorySessionStore(), logger: NullLogger<SessionManager>.Instance);
            var inner = new FileSessionGroupStore(dir);
            var failing = new FailingGroupStore(
                inner, g => g.Members.Any(m => m.Relation == SessionRelation.HandoffSuccessor));
            var forker = new SessionForker(NullLogger<SessionForker>.Instance, sessions);
            var manager = new SessionGroupManager(sessions, failing, forker, NullLogger<SessionGroupManager>.Instance);

            var root = sessions.Create(partitionId: "p1", selectedAgent: "build");
            root.Title = "root";
            await manager.EnsureForSessionAsync(root.Id);

            var group = await manager.GetGroupForSessionAsync(root.Id);
            var membersBefore = await manager.ListMembersAsync(group!.Id);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                manager.CreateHandoffSuccessorAsync(root.Id, null, "successor", null));

            var membersAfter = await manager.ListMembersAsync(group.Id);
            membersAfter.Should().HaveCount(membersBefore.Count);
            membersAfter.Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);

            // 无孤儿会话：仅源会话保留
            sessions.List().Should().ContainSingle(s => s.Id == root.Id);

            // 持久化组中也不含后继成员
            var persisted = await manager.GetGroupAsync(group.Id);
            persisted!.Members.Should().NotContain(m => m.Relation == SessionRelation.HandoffSuccessor);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 链 A→B→C（C 为锚点）执行 C→D 交接时若保存失败，清理后继后必须恢复原锚点 C，
    /// 而不是按 Order 回退到链首 A（A/B 关系保持不变）。
    /// </summary>
    [Fact]
    public async Task Handoff_MidChainStorageFailure_ShouldRestoreOriginalAnchor()
    {
        // 仅当组内出现第 4 个成员（即 C→D 交接写入后继）时注入保存失败
        using var h = new SessionGroupTestHarness(
            inner => new FailingGroupStore(inner, g => g.Members.Count == 4));

        var a = h.CreateRoot("A");
        var group = await h.Manager.EnsureForSessionAsync(a.Id);
        var b = await h.Manager.CreateHandoffSuccessorAsync(a.Id, null, "B", null); // A→B, B 锚点
        var c = await h.Manager.CreateHandoffSuccessorAsync(b.Id, null, "C", null); // A→B→C, C 锚点

        var sessionsBefore = h.Sessions.List().Select(s => s.Id).OrderBy(x => x).ToList();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            h.Manager.CreateHandoffSuccessorAsync(c.Id, null, "D", null));

        // 无孤儿会话：D 不存在（会话集合与失败前一致）
        h.Sessions.List().Select(s => s.Id).OrderBy(x => x).Should().Equal(sessionsBefore);

        var g = await h.Manager.GetGroupAsync(group.Id);
        g!.Members.Should().HaveCount(3);
        g.AnchorSessionId.Should().Be(c.Id);

        var cMember = g.Members.Single(m => m.SessionId == c.Id);
        cMember.IsAnchor.Should().BeTrue();
        cMember.Relation.Should().Be(SessionRelation.HandoffSuccessor);

        // A/B 主线历史关系不变
        g.Members.Single(m => m.SessionId == b.Id).Relation.Should().Be(SessionRelation.HandoffPredecessor);
        g.Members.Single(m => m.SessionId == a.Id).Relation.Should().Be(SessionRelation.HandoffPredecessor);
        g.Members.Single(m => m.SessionId == b.Id).ParentSessionId.Should().Be(a.Id);
        g.Members.Single(m => m.SessionId == c.Id).ParentSessionId.Should().Be(b.Id);
    }

    /// <summary>按谓词在保存组时注入失败的存储装饰器。</summary>
    private sealed class FailingGroupStore : ISessionGroupStore
    {
        private readonly ISessionGroupStore _inner;
        private readonly Func<SessionGroup, bool> _shouldFail;

        public FailingGroupStore(ISessionGroupStore inner, Func<SessionGroup, bool> shouldFail)
        {
            _inner = inner;
            _shouldFail = shouldFail;
        }

        public Task<SessionGroup?> LoadAsync(string groupId, CancellationToken ct = default) => _inner.LoadAsync(groupId, ct);

        public Task<SessionGroup?> FindBySessionAsync(string sessionId, CancellationToken ct = default) => _inner.FindBySessionAsync(sessionId, ct);

        public Task SaveAsync(SessionGroup group, CancellationToken ct = default) =>
            _shouldFail(group)
                ? Task.FromException(new IOException("注入的组保存失败"))
                : _inner.SaveAsync(group, ct);

        public Task DeleteAsync(string groupId, CancellationToken ct = default) => _inner.DeleteAsync(groupId, ct);

        public IAsyncEnumerable<SessionGroup> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
    }
}
