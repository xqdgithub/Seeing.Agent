using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Seeing.Session.Tests.Management;
using Xunit;

namespace Seeing.Session.Tests.Core;

/// <summary>
/// <see cref="SessionOrigin"/> 正交维度测试：来源（Origin）与类型（Kind）互不干扰。
/// <para>Kind 被组不变量结构性使用（Child ⇔ SubAgent），因此平台内部会话仍必须是 SubAgent，
/// 只通过 Origin 区分用途。</para>
/// </summary>
public class SessionOriginTests : IDisposable
{
    /// <summary>文件存储临时目录（真实 JSON 落盘用，非内存字典）。</summary>
    private readonly string _storeDir =
        Path.Combine(Path.GetTempPath(), "seeing-origin-" + Guid.NewGuid().ToString("N"));

    /// <summary>构造根会话夹具并返回组管理器与根会话。</summary>
    private static async Task<(SessionGroupManager Group, SessionData Root)> SeedRootAsync(
        SessionGroupTestHarness harness)
    {
        var root = harness.CreateRoot();
        await harness.Manager.EnsureForSessionAsync(root.Id, TestContext.Current.CancellationToken);
        return (harness.Manager, root);
    }

    /// <summary>
    /// 默认值必须经反序列化行为断言：<c>default(SessionOrigin) == User == 0</c> 使得直接断言
    /// <c>new SessionData().Origin == User</c> 在被删掉属性初始化器时仍然通过（无护栏价值）。
    /// <para>本用例覆盖的是<b>存量兼容</b>：不含 <c>origin</c> 字段的旧格式 JSON 反序列化必须得到
    /// <see cref="SessionOrigin.User"/>，不报错、不强制写值。覆盖边界：由于 <c>User == 0</c>，
    /// 本用例<b>无法</b>区分「初始化器存在」与「反序列化回落 default」——这是枚举零值语义的固有局限
    /// （已实测：删初始化器本条仍绿）。数值契约由 <see cref="SessionOrigin_Values_Are_Stable"/> 锁定。</para>
    /// </summary>
    [Fact]
    public void Deserialize_Should_Default_Origin_To_User()
    {
        const string legacyJson = """
            {
              "id": "ses_legacy_origin",
              "title": "Old",
              "partitionId": "default",
              "messages": []
            }
            """;

        var loaded = System.Text.Json.JsonSerializer.Deserialize<SessionData>(legacyJson, CreateSessionJsonOptions());

        loaded.Should().NotBeNull();
        loaded!.Origin.Should().Be(SessionOrigin.User);
    }

    /// <summary>
    /// 枚举数值是对外序列化契约（会话文件里存的是数字）：改值会让既有会话文件被读成错误来源。
    /// <para>本用例也守住 <c>User == 0</c> —— 即「缺字段 → User」的兼容语义前提。</para>
    /// </summary>
    [Fact]
    public void SessionOrigin_Values_Are_Stable()
    {
        ((int)SessionOrigin.User).Should().Be(0);
        ((int)SessionOrigin.SubAgentTask).Should().Be(1);
        ((int)SessionOrigin.Reflection).Should().Be(2);
        ((int)SessionOrigin.Scheduler).Should().Be(3);
        ((int)SessionOrigin.Heartbeat).Should().Be(4);
        Enum.GetValues<SessionOrigin>().Should().HaveCount(5);
    }

    [Fact]
    public void Clone_Should_Preserve_Origin()
    {
        // Clone 是落盘与分叉的主要快照点；丢字段会让 Origin 在首存后退化为 User
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

    /// <summary>
    /// Origin 必须在首次 <see cref="ISessionManager.SaveAsync"/> 之前写入：该保存移交的是
    /// 保存时刻的克隆快照，保存后再赋值只改内存。
    /// <para>注意本用例只守「最终落盘值正确」，<b>不单独区分</b>首次/二次落盘：
    /// 入组后的 <c>UpdateSessionAsync(GroupId)</c> 会再保存一次，因此把赋值挪到
    /// 首次 SaveAsync 之后、AddMemberAsync 之前仍会通过（已用变异实验确认）。
    /// 「写入时机」的守卫落在文件存储往返用例与 <see cref="Clone_Should_Preserve_Origin"/> 上。</para>
    /// </summary>
    [Fact]
    public async Task CreateInternalAsync_Should_Persist_Origin_To_Store()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        var child = await group.CreateInternalAsync(
            root.Id, "reflection", "复盘", new List<SessionPermissionRule>(),
            null, SessionOrigin.Reflection, TestContext.Current.CancellationToken);

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

    // ============================ 真实文件存储往返（JSON 契约护栏） ============================

    /// <summary>
    /// <see cref="FileSessionStore"/> 真实 JSON 序列化/反序列化往返：会话文件格式是对外契约，
    /// 换序列化器、加 <c>JsonStringEnumConverter</c>、改枚举数值都会在此变红。
    /// </summary>
    [Fact]
    public async Task FileSessionStore_Should_RoundTrip_Origin()
    {
        var store = new FileSessionStore(_storeDir, NullLogger<FileSessionStore>.Instance);
        var session = SessionData.Create(partitionId: "p1");
        session.Origin = SessionOrigin.Reflection;

        await store.SaveAsync(session, TestContext.Current.CancellationToken);

        // 落盘 JSON 必须含数值形式的 origin（默认 System.Text.Json 枚举语义）
        var json = await File.ReadAllTextAsync(
            Path.Combine(_storeDir, $"{session.Id}.json"), TestContext.Current.CancellationToken);
        json.Should().Contain("\"origin\": 2");

        // 冷读（新建 store 实例，不走任何内存缓存）必须还原 Reflection
        var coldStore = new FileSessionStore(_storeDir, NullLogger<FileSessionStore>.Instance);
        var loaded = await coldStore.LoadAsync(session.Id, TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Origin.Should().Be(SessionOrigin.Reflection);
    }

    /// <summary>
    /// 存量兼容：无 <c>origin</c> 字段的旧会话文件必须读成 <see cref="SessionOrigin.User"/>，
    /// 且不得因缺字段报错或强制写值。
    /// </summary>
    [Fact]
    public async Task FileSessionStore_LegacyJson_WithoutOrigin_Should_Load_As_User()
    {
        Directory.CreateDirectory(_storeDir);
        var legacyPath = Path.Combine(_storeDir, "ses_legacy_origin.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            {
              "id": "ses_legacy_origin",
              "title": "Old",
              "partitionId": "default",
              "messages": []
            }
            """, TestContext.Current.CancellationToken);

        var store = new FileSessionStore(_storeDir, NullLogger<FileSessionStore>.Instance);
        var loaded = await store.LoadAsync("ses_legacy_origin", TestContext.Current.CancellationToken);

        loaded.Should().NotBeNull();
        loaded!.Origin.Should().Be(SessionOrigin.User);
    }

    // ============================ 入口参数校验 ============================

    [Fact]
    public async Task CreateInternalAsync_With_User_Origin_Should_Throw()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        // Kind=SubAgent 却标成 User 的会话会在任务视图白名单下整体消失，必须在入口拒绝
        var act = () => group.CreateInternalAsync(
            root.Id, "reflection", "复盘", Array.Empty<SessionPermissionRule>(),
            null, SessionOrigin.User, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("origin");
    }

    [Fact]
    public async Task CreateInternalAsync_With_Undefined_Origin_Should_Throw()
    {
        using var h = new SessionGroupTestHarness();
        var (group, root) = await SeedRootAsync(h);

        var act = () => group.CreateInternalAsync(
            root.Id, "reflection", "复盘", Array.Empty<SessionPermissionRule>(),
            null, (SessionOrigin)99, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("origin");
    }

    /// <summary>与 <see cref="FileSessionStore"/> 实际使用的序列化选项保持一致。</summary>
    private static System.Text.Json.JsonSerializerOptions CreateSessionJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
            Directory.Delete(_storeDir, recursive: true);
    }
}
