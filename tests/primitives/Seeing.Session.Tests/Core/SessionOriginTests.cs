using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Tests.Management;
using Xunit;

namespace Seeing.Session.Tests.Core;

/// <summary>
/// <see cref="SessionOrigin"/> 正交维度测试：来源（Origin）与类型（Kind）互不干扰。
/// <para>Kind 被组不变量结构性使用（Child ⇔ SubAgent），因此平台内部会话仍必须是 SubAgent，
/// 只通过 Origin 区分用途。</para>
/// </summary>
public class SessionOriginTests
{
    /// <summary>构造根会话夹具并返回组管理器与根会话。</summary>
    private static async Task<(SessionGroupManager Group, SessionData Root)> SeedRootAsync(
        SessionGroupTestHarness harness)
    {
        var root = harness.CreateRoot();
        await harness.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        return (harness.Manager, root);
    }

    [Fact]
    public void SessionData_Should_Default_Origin_To_User()
    {
        // 存量会话与用户新建会话默认即 User，无需显式赋值
        new SessionData().Origin.Should().Be(SessionOrigin.User);
    }

    [Fact]
    public void Clone_Should_Preserve_Origin()
    {
        // Clone 是落盘与分叉的唯一快照点，丢字段会导致 Origin 退化为 User
        var session = new SessionData { Origin = SessionOrigin.Reflection };

        session.Clone().Origin.Should().Be(SessionOrigin.Reflection);
    }

    [Fact]
    public async Task CreateInternalAsync_Should_Produce_SubAgent_With_Reflection_Origin()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        var child = await group.CreateInternalAsync(
            root.Id, "reflection", "复盘", new List<SessionPermissionRule>(),
            null, SessionOrigin.Reflection, TestContext.Current.CancellationToken);

        // 满足组不变量：Relation=Child ⇔ Kind=SubAgent
        child.Kind.Should().Be(SessionKind.SubAgent);
        child.Origin.Should().Be(SessionOrigin.Reflection);
    }

    [Fact]
    public async Task CreateInternalAsync_Should_Not_Generate_Title()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        var child = await group.CreateInternalAsync(
            root.Id, "reflection", "复盘", new List<SessionPermissionRule>(),
            null, SessionOrigin.Reflection, TestContext.Current.CancellationToken);

        // 保留调用方显式标题，不得退化为 "Session ses_..."
        child.Title.Should().Be("复盘");
    }

    [Fact]
    public async Task CreateInternalAsync_Should_Persist_Origin_To_Store()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        var child = await group.CreateInternalAsync(
            root.Id, "reflection", "复盘", new List<SessionPermissionRule>(),
            null, SessionOrigin.Reflection, TestContext.Current.CancellationToken);

        // 锁住「Origin 必须在 SaveAsync 之前设置」：存储保存的是保存时刻的克隆快照，
        // 若实现把赋值放到 SaveAsync 之后，这里读回的仍是 User。
        var reloaded = await h.SessionStore.LoadAsync(child.Id, TestContext.Current.CancellationToken);

        reloaded.Should().NotBeNull();
        reloaded!.Origin.Should().Be(SessionOrigin.Reflection);
    }

    [Fact]
    public async Task CreateChildAsync_Should_Mark_Origin_As_SubAgentTask()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        // Task 工具委派入口的来源语义必须稳定：后续视图过滤依赖该白名单
        var child = await group.CreateChildAsync(
            root.Id, "build", "子任务", Array.Empty<SessionPermissionRule>(),
            null, TestContext.Current.CancellationToken);

        child.Origin.Should().Be(SessionOrigin.SubAgentTask);
    }
}
