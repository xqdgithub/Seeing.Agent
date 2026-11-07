using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Management;

/// <summary>
/// 会话组归档的失败语义：<b>部分失败必须上抛</b>，不能静默返回「有成员成功」。
/// <para>
/// 背景：<c>ArchiveGroupAsync</c> 此前逐个归档成员、吞掉每个异常只记 Warning，
/// 最后 <c>return archived &gt; 0</c>。调用方拿到 true 就认为组已归档，
/// 但实际可能是「2 个成员只归档了 1 个」——组内部分会话已归档、部分仍活跃，
/// <c>ListAnchorSummariesAsync</c> 与 <c>ListArchivedAsync</c> 会给出互相矛盾的视图。
/// 现在改为：任一成员失败即抛 <see cref="InvalidOperationException"/>，并保留组关系供重试。
/// </para>
/// </summary>
public class SessionGroupArchiveFailureTests
{
    [Fact]
    public async Task ArchiveGroupAsync_PartialMemberFailure_ShouldThrowAndPreserveGroup()
    {
        using var h = new SessionGroupTestHarness(
            decorateSessionStore: inner => new FailOneMemberStore(inner));

        var root = h.CreateRoot("g-root");
        await h.Manager.EnsureForSessionAsync(root.Id);

        var child = h.CreatePlainSession(SessionKind.SubAgent);
        await h.SessionStore.SaveAsync(child);

        var group = await h.Manager.GetGroupForSessionAsync(root.Id);
        await h.Manager.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = child.Id,
            Relation = SessionRelation.Child,
            ParentSessionId = root.Id
        });

        // 让 child 的归档失败（root 成功）→ 期望抛异常而不是返回 true
        FailOneMemberStore.FailFor(child.Id);

        var act = () => h.Manager.ArchiveGroupAsync(group.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未完成*");

        // 组关系必须保留，供重试归档
        var stillThere = await h.Manager.GetGroupAsync(group.Id);
        stillThere.Should().NotBeNull("部分失败时组关系不得被清除，否则无法重试归档");

        FailOneMemberStore.Reset();
    }

    [Fact]
    public async Task ArchiveGroupAsync_AllMembersSucceed_ShouldReturnTrue()
    {
        using var h = new SessionGroupTestHarness();
        var root = h.CreateRoot("g-root");
        await h.Manager.EnsureForSessionAsync(root.Id);

        var child = h.CreatePlainSession(SessionKind.SubAgent);
        await h.SessionStore.SaveAsync(child);

        var group = await h.Manager.GetGroupForSessionAsync(root.Id);
        await h.Manager.AddMemberAsync(group!.Id, new SessionGroupMember
        {
            SessionId = child.Id,
            Relation = SessionRelation.Child,
            ParentSessionId = root.Id
        });

        (await h.Manager.ArchiveGroupAsync(group.Id)).Should().BeTrue();
    }

    /// <summary>
    /// 会话存储装饰器：对指定会话 id 的归档抛异常，其余全部透传。
    /// </summary>
    private sealed class FailOneMemberStore(InMemorySessionStore inner)
        : ISessionStore, ISessionSummaryProvider, ISessionArchiveStore
    {
        private static string? s_failFor;

        public static void FailFor(string sessionId) => s_failFor = sessionId;
        public static void Reset() => s_failFor = null;

        public Task SaveAsync(SessionData data, CancellationToken ct = default) => inner.SaveAsync(data, ct);
        public Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default) => inner.LoadAsync(sessionId, ct);
        public Task DeleteAsync(string sessionId, CancellationToken ct = default) => inner.DeleteAsync(sessionId, ct);
        public IAsyncEnumerable<SessionData> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public IAsyncEnumerable<SessionData> QueryAsync(string partitionId, string agentId, CancellationToken ct = default)
            => inner.QueryAsync(partitionId, agentId, ct);
        public Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default) => inner.SaveAllAsync(data, ct);
        public IAsyncEnumerable<SessionData> LoadAllAsync(CancellationToken ct = default) => inner.LoadAllAsync(ct);

        public IAsyncEnumerable<SessionSummary> ListSummariesAsync(CancellationToken ct = default)
            => inner.ListSummariesAsync(ct);

        public Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.Equals(sessionId, s_failFor, StringComparison.Ordinal))
                throw new IOException($"模拟归档失败: {sessionId}");

            return inner.ArchiveAsync(sessionId, ct);
        }

        public Task<bool> RestoreAsync(string sessionId, CancellationToken ct = default) => inner.RestoreAsync(sessionId, ct);
        public IAsyncEnumerable<SessionSummary> ListArchivedAsync(CancellationToken ct = default) => inner.ListArchivedAsync(ct);
    }
}
