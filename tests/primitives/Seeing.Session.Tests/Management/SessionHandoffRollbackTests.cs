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

        public Task<SessionGroup?> LoadAsync(string groupId) => _inner.LoadAsync(groupId);

        public Task<SessionGroup?> FindBySessionAsync(string sessionId) => _inner.FindBySessionAsync(sessionId);

        public Task SaveAsync(SessionGroup group) =>
            _shouldFail(group)
                ? Task.FromException(new IOException("注入的组保存失败"))
                : _inner.SaveAsync(group);

        public Task DeleteAsync(string groupId) => _inner.DeleteAsync(groupId);

        public IAsyncEnumerable<SessionGroup> ListAsync() => _inner.ListAsync();
    }
}
