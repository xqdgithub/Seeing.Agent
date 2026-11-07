using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Context;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Hosting;
using Seeing.Session.Core;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

/// <summary>
/// <see cref="MemoryContextSource"/> 锁定：召回经 <see cref="IContextSource"/> 管道注入，
/// <c>Target = Message</c>（内容随轮次以消息形式进入历史）、无命中/不可读一律 <see cref="ContextValueState.Unavailable"/>
/// （绝不叙述「记忆已移除」）、<c>Canonical</c> 必须是 JSON 对象且含内容指纹、值不变不叙述、
/// 初始渲染消息带「机器注入」元数据。
/// </summary>
public class MemoryContextSourceTests
{
    [Fact]
    public async Task ReadAsync_Should_Return_Unavailable_When_UserQuery_Empty()
    {
        // 反脆化：mock 在 query 为空时**照常返回命中**。若空查询检查被删掉，
        // 会读到命中并返回 Value —— 这条测试才具备判别力。
        var recall = new Mock<IMemoryRecallService>();
        recall.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Hit("a.md", 0.9)]);
        var source = Build(userQuery: null, hits: [], recall: recall.Object);

        (await source.ReadAsync(TestContext.Current.CancellationToken)).State
            .Should().Be(ContextValueState.Unavailable);
    }

    [Fact]
    public async Task ReadAsync_Should_Not_Call_Recall_When_UserQuery_Empty()
    {
        // 同一门禁的第二重保证：空查询时根本不该发生召回（省 IO，也避免记录垃圾查询）
        var recall = new Mock<IMemoryRecallService>();
        recall.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Hit("a.md", 0.9)]);
        var source = Build(userQuery: "   ", hits: [], recall: recall.Object);

        await source.ReadAsync(TestContext.Current.CancellationToken);

        recall.Verify(
            r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "空白查询没有检索依据，不应调用召回");
    }

    [Fact]
    public async Task ReadAsync_Should_Return_Unavailable_When_No_Hits()
    {
        // 裁决 R5：无命中也是 Unavailable（不叙述 Removed）
        var source = Build(userQuery: "问题", hits: []);
        (await source.ReadAsync(TestContext.Current.CancellationToken)).State
            .Should().Be(ContextValueState.Unavailable);
    }

    [Fact]
    public async Task ReadAsync_Should_Return_Unavailable_When_Module_Inactive()
    {
        var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9)], active: false);
        (await source.ReadAsync(TestContext.Current.CancellationToken)).State
            .Should().Be(ContextValueState.Unavailable);
    }

    [Fact]
    public async Task ReadAsync_Should_Return_Unavailable_In_ToolsOnly_Mode()
    {
        var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9)], mode: MemoryRetrievalMode.ToolsOnly);
        (await source.ReadAsync(TestContext.Current.CancellationToken)).State
            .Should().Be(ContextValueState.Unavailable);
    }

    [Fact]
    public async Task ReadAsync_Should_Return_Value_With_Stable_Object_Canonical()
    {
        var source = Build(userQuery: "问题", hits: [Hit("b.md", 0.9), Hit("a.md", 0.8)]);
        var v1 = await source.ReadAsync(TestContext.Current.CancellationToken);
        var v2 = await source.ReadAsync(TestContext.Current.CancellationToken);
        v1.State.Should().Be(ContextValueState.Value);

        var json = v1.Canonical!.Value;
        json.ValueKind.Should().Be(JsonValueKind.Object);          // 必须是对象
        v1.Canonical.Value.GetRawText().Should().Be(v2.Canonical!.Value.GetRawText());
        v1.Canonical.Value.GetRawText().Should().Contain("\"a.md\"").And.Contain("\"b.md\"");
        v1.Canonical.Value.GetRawText().Should().NotContain("内容");  // 不含正文
    }

    [Fact]
    public async Task ReadAsync_Should_Include_ContentHash_In_Canonical()
    {
        // Important 1：Canonical 必须带内容指纹，否则正文更新不改变值 → 永久不可见
        var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9, "标题A", "内容A")]);
        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        var hit = value.Canonical!.Value.GetProperty("hits")[0];
        hit.TryGetProperty("contentHash", out var hash).Should().BeTrue(
            "没有内容指纹时，正文更新后 RRF 分数不变 → 装配器判定「值未变」→ 模型永远看到旧正文");
        hash.GetString().Should().NotBeNullOrWhiteSpace();
        hash.GetString()!.Length.Should().Be(16);
    }

    [Fact]
    public async Task Render_Should_Narrate_When_Content_Changed_But_Score_Same()
    {
        // Important 1 的核心判别：分数逐位相同、正文不同 → 必须叙述内容更新。
        // previous 按装配器口径重建（Payload 恒为 null），只留 Canonical。
        var source = Build(userQuery: "问题", hits: []);

        var previous = WithoutPayload(await BuildWithHitsAsync("a.md", 0.9, "标题A", "旧内容"));
        var current = await BuildWithHitsAsync("a.md", 0.9, "标题A", "新内容");

        var text = source.Render(previous, current);

        text.Should().NotBeNull(
            "正文变了但 RRF 分数不变，只有内容指纹能感知这个变化");
        text!.Should().Contain("标题A", "该条路径仍有当前正文可用，叙述应带上标题");
        text.Should().Contain("新内容", "正文更新后模型应立即看到新内容，无需再调工具");
    }

    [Fact]
    public async Task Render_Should_Narrate_When_Only_Title_Changed()
    {
        // B3 守门：contentHash 必须含标题。只改标题时 title 变了但正文没变，
        // 若哈希只算正文，装配器会判「值未变」→ 每轮「同值不同文本」告警且基线永久停在旧标题。
        var source = Build(userQuery: "问题", hits: []);

        var previous = WithoutPayload(await BuildWithHitsAsync("a.md", 0.9, "旧标题", "同一正文"));
        var current = await BuildWithHitsAsync("a.md", 0.9, "新标题", "同一正文");

        source.Render(previous, current).Should().NotBeNull(
            "标题变化必须被感知，否则会触发装配器的同值不同文本告警");
    }

    [Fact]
    public async Task Summarize_Should_Skip_Yaml_Frontmatter()
    {
        // B2 守门：真实记忆文件带 YAML 头（约 200 字符），240 字符预算会被吃光。
        var content = """
            ---
            id: explicit-abc
            type: daily
            title: "用户偏好"
            tags: [偏好]
            importance: 1.0
            created_at: 2026-11-02T12:14:01.5282104+00:00
            ---

            用户偏好使用 .NET 10 与 xUnit v3。
            """;

        var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9, "用户偏好", content)]);
        var text = source.RenderInitial(await source.ReadAsync(TestContext.Current.CancellationToken));

        text.Should().Contain("用户偏好使用 .NET 10 与 xUnit v3");
        text.Should().NotContain("created_at", "摘要必须剥离 frontmatter，否则元数据吃掉全部字符预算");
        text.Should().NotContain("importance");
    }

    [Fact]
    public async Task ReadAsync_Should_Skip_Placeholder_Node_Instead_Of_Reporting_Content_Change()
    {
        // B1 守门：索引层读失败时返回占位节点（Content = ""，标题取文件名）。
        // 若让它进入 Canonical，一次瞬时 IO 失败会被 contentHash 判成「记忆内容已更新」，
        // 产生只有路径没有正文的伪造叙述。正确行为是整条跳过。
        var placeholder = new SearchHit(
            FileNode.Create("a.md", string.Empty,
                FileMetadata.Create(Guid.NewGuid().ToString("N")[..8], MemoryType.Session, "a")),
            0.9, 0.9, 0);

        var source = Build(userQuery: "问题", hits: [placeholder]);
        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Unavailable,
            "占位节点表示本次读不到内容，应视为不可用而不是产出一条「已更新」的记忆");
    }

    [Fact]
    public async Task Render_Should_Return_Null_When_Nothing_Changed()
    {
        var source = Build(userQuery: "问题", hits: []);
        var previous = WithoutPayload(await BuildWithHitsAsync("a.md", 0.9, "标题A", "同一内容"));
        var current = await BuildWithHitsAsync("a.md", 0.9, "标题A", "同一内容");

        source.Render(previous, current).Should().BeNull();
    }

    [Fact]
    public async Task RenderInitial_Should_Produce_Relevant_Memories_Section()
    {
        var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.92, "标题A", "内容A")]);
        var text = source.RenderInitial(await source.ReadAsync(TestContext.Current.CancellationToken));
        text.Should().StartWith("## Relevant memories");
        text.Should().Contain("标题A");
    }

    [Fact]
    public void Render_Should_Return_Null_When_Canonical_Unchanged()
    {
        // Review Focus #4：值不变不叙述
        var source = Build(userQuery: "问题", hits: []);
        var v = BuildValue(("a.md", 0.9, "hash-a"));
        source.Render(v, v).Should().BeNull();
    }

    [Fact]
    public void Render_Should_Narrate_When_Canonical_Changed()
    {
        var source = Build(userQuery: "问题", hits: []);
        source.Render(BuildValue(("a.md", 0.9, "hash-a")), BuildValue(("b.md", 0.7, "hash-b")))
            .Should().Contain("b.md");
    }

    [Fact]
    public void Render_Should_Not_Throw_On_Duplicate_Path()
    {
        // Minor 3：重复 path 不得抛 ArgumentException
        var source = Build(userQuery: "问题", hits: []);
        var previous = BuildValue(("a.md", 0.9, "h1"), ("a.md", 0.8, "h1"));
        var current = BuildValue(("a.md", 0.9, "h2"), ("a.md", 0.8, "h2"));

        var act = () => source.Render(previous, current);

        act.Should().NotThrow();
        act().Should().Contain("a.md");
    }

    [Fact]
    public void Target_Should_Be_Message()
    {
        // 裁决 R4：System 源只在首次建立时渲染基线，UserQuery 每轮变会让系统提示永久停在第一轮
        Build(userQuery: null, hits: []).Target.Should().Be(ContextSourceTarget.Message);
    }

    [Fact]
    public async Task ReadAsync_Should_Skip_Bad_Hits_Without_Failing_Whole_Source()
    {
        // Minor 2：坏命中只影响该条，不得让整个源本轮失效。
        // 缺节点 → 跳过该条；NaN 分数 → 归零保留（内容是真实记忆，只有分数是垃圾）。
        var bad = new SearchHit(null!, 1.0, 0, 0);
        var nanScore = new SearchHit(
            FileNode.Create("b.md", "正文B", FileMetadata.Create("id", MemoryType.Daily, "标题B")),
            double.NaN, 0, 0);
        var good = Hit("c.md", 0.8, "标题C", "内容C");

        var source = Build(userQuery: "问题", hits: [bad, nanScore, good]);
        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Value);
        var raw = value.Canonical!.Value.GetRawText();
        raw.Should().Contain("c.md");
        raw.Should().Contain("b.md", "NaN 分数归零后该条仍可用（内容是真实记忆）");
        raw.Should().NotContain("NaN", "NaN 无法序列化为 JSON，必须在进入 Canonical 前归零");
    }

    // ===== 消息元数据（Important 2）=====

    [Fact]
    public async Task GetMessageMetadata_Should_Mark_Rendered_Message_As_Machine_Injected()
    {
        var source = Build(userQuery: "问题", hits: [Hit("a.md", 0.9, "标题A", "内容A")]);
        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        var metadata = source.GetMessageMetadata(text, value);

        metadata.Should().NotBeNull(
            "未实现元数据时记忆块会是一条裸 user 消息，被标题服务计为意图用户消息");
        metadata.Should().ContainKey(SessionMetadataKeys.ContextUpdate);
        metadata![SessionMetadataKeys.ContextUpdate].Should().Be(true);
        metadata.Should().ContainKey(SessionMetadataKeys.ContextUpdateSources);
        metadata[SessionMetadataKeys.ContextUpdateSources].Should().BeAssignableTo<string[]>()
            .Which.Should().BeEquivalentTo([source.Key.Value]);

        // 既有判据立刻生效：标题服务不再把记忆块当用户发言
        var message = SessionMessage.UserMessage(text);
        foreach (var (key, metadataValue) in metadata)
            message.WithMetadata(key, metadataValue);

        IsContextUpdateMessage(message).Should().BeTrue(
            "ContextUpdate / ContextUpdateSources 是仓内既有的机器注入判据（SessionTitleService + WebUI 共用）");
    }

    [Fact]
    public async Task GetMessageMetadata_Should_Work_On_Replay_Without_Payload()
    {
        // 重放路径下 Payload 不可用；元数据只依赖源键，因此仍然成立
        var source = Build(userQuery: "问题", hits: []);
        var value = new ContextSourceValue(
            ContextValueState.Value,
            BuildValue(("a.md", 0.9, "h")).Canonical,
            null);

        source.GetMessageMetadata("## Relevant memories\n\n- (0.90) a.md", value)
            .Should().ContainKey(SessionMetadataKeys.ContextUpdate);
    }

    // ===== 装配器接线（Important 1 的端到端证据）=====

    /// <summary>
    /// 真实 <see cref="ContextAssembler"/> + <see cref="ContextAssemblyMode.Persist"/>：
    /// 正文更新（分数不变）必须被感知为值变化并叙述，且**不得**产生「同值不同文本」Issue。
    /// </summary>
    [Fact]
    public async Task Assembler_Should_Narrate_Content_Update_Without_Instability_Issue()
    {
        var recall = new Mock<IMemoryRecallService>();
        var current = Hit("daily/a.md", 0.90, "标题A", "内容A");
        recall.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([current]);

        var source = Build(userQuery: "问题", hits: [], recall: recall.Object);
        var session = new SessionData { Id = "s-memory-1" };
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var assembler = new ContextAssembler(
            [source], manager.Object, NullLogger<ContextAssembler>.Instance);

        // R1：建立基线（插入初始渲染消息）
        var r1 = await assembler.EnsureFreshAsync(
            session, ContextAssemblyMode.Persist, null, null,
            TestContext.Current.CancellationToken);
        r1.Issues.Should().BeEmpty();
        var countAfterBaseline = session.MessageCount;

        // R2：召回内容不变（分数相同）→ 不叙述、不新增消息、无告警
        var r2 = await assembler.EnsureFreshAsync(
            session, ContextAssemblyMode.Persist, null, null,
            TestContext.Current.CancellationToken);
        r2.DeltaText.Should().BeNull("值未变不得叙述");
        r2.Issues.Should().BeEmpty("值未变时不得出现「同值不同文本」告警");
        session.MessageCount.Should().Be(countAfterBaseline);

        // R3：正文更新、RRF 分数逐位相同 → 必须叙述，且仍然无告警
        recall.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Hit("daily/a.md", 0.90, "标题A", "内容A-已更新")]);

        var r3 = await assembler.EnsureFreshAsync(
            session, ContextAssemblyMode.Persist, null, null,
            TestContext.Current.CancellationToken);

        r3.Issues.Should().BeEmpty(
            "内容指纹让「正文变了」成为值变化，不再触发同值不同文本的兜底告警");
        r3.DeltaText.Should().NotBeNull("正文更新必须被叙述，否则模型永远看到旧正文");
        r3.DeltaText!.Should().Contain("内容A-已更新");
        (r3.DeltaText.Contains("daily/a.md", StringComparison.Ordinal)
            || r3.DeltaText.Contains("标题A", StringComparison.Ordinal)).Should().BeTrue(
            "叙述必须指明是哪条记忆");
    }

    /// <summary>
    /// 初始渲染消息必须带机器注入元数据（Important 2 的端到端证据）。
    /// </summary>
    [Fact]
    public async Task Assembler_Initial_Message_Should_Carry_Machine_Injected_Metadata()
    {
        var source = Build(userQuery: "问题", hits: [Hit("daily/a.md", 0.9, "标题A", "内容A")]);
        var session = new SessionData { Id = "s-memory-2" };
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        var assembler = new ContextAssembler(
            [source], manager.Object, NullLogger<ContextAssembler>.Instance);

        await assembler.EnsureFreshAsync(
            session, ContextAssemblyMode.Persist, null, null,
            TestContext.Current.CancellationToken);

        var initial = session.Messages.Should().ContainSingle().Subject;
        initial.Role.Should().Be(MessageRole.User);
        initial.Content.Should().StartWith("## Relevant memories");

        initial.Metadata.Should().NotBeNull();
        initial.Metadata!.Should().ContainKey(SessionMetadataKeys.ContextUpdate,
            "缺该键时记忆块是裸 user 消息，标题模型会把它当用户发言");
        initial.Metadata.Should().ContainKey(SessionMetadataKeys.ContextUpdateSources);

        IsContextUpdateMessage(initial).Should().BeTrue(
            "必须命中 SessionTitleService / WebUI 的既有判定");
    }

    // ===== 注册 =====
    /// <summary>
    /// 注册必须用 <c>TryAddEnumerable</c>：<c>TryAddScoped&lt;IContextSource, T&gt;()</c> 按 ServiceType 去重，
    /// 在已有其它源（如 <c>ProjectInstructionsContextSource</c> / <c>SkillsContextSource</c>）时会整条静默跳过。
    /// </summary>
    [Fact]
    public void Di_Registration_Should_Add_Source_Beside_Existing_Ones()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfigSectionStore>(new StubConfigSectionStore());
        services.AddSingleton(Mock.Of<IFileStore>());
        services.AddSingleton(Mock.Of<Seeing.Agent.Abstractions.Llm.ITextCompletion>());
        services.AddSingleton(Mock.Of<IMemoryRecallService>());
        // 先登记的其它源：若注册用到 TryAddScoped，MemoryContextSource 会被整条跳过
        services.AddScoped<IContextSource>(_ => new OtherSource());
        services.AddScoped<IContextExecutionScope>(_ => new FakeExecutionScope());

        services.AddMemoryServices(new StubConfigSectionRegistry(), "Data Source=:memory:");

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sources = scope.ServiceProvider.GetServices<IContextSource>().ToList();

        sources.OfType<MemoryContextSource>().Should().ContainSingle(
            "既有源不得让记忆源整条静默跳过");
        sources.OfType<OtherSource>().Should().ContainSingle("既有源本身必须保留");
    }

    // ===== 辅助 =====

    /// <summary>与 <c>SessionTitleService.IsIntentionalUserMessage</c> / WebUI 同口径的判据（只认元数据）。</summary>
    private static bool IsContextUpdateMessage(SessionMessage message) =>
        message.Metadata is { } metadata
        && (metadata.ContainsKey(SessionMetadataKeys.ContextUpdate)
            || metadata.ContainsKey(SessionMetadataKeys.ContextUpdateSources));

    /// <summary>占位的既有上下文源（验证注册不与其它源互斥）。</summary>
    private sealed class OtherSource : IContextSource
    {
        public ContextSourceKey Key => new("other");

        public ContextSourceScope Scope => ContextSourceScope.Project;

        public int ValueSchemaVersion => 1;

        public ContextSourceTarget Target => ContextSourceTarget.System;

        public int MaxBaselineChars => 0;

        public Task<ContextSourceValue> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new ContextSourceValue(ContextValueState.Unavailable, null, null));

        public string RenderInitial(ContextSourceValue value) => string.Empty;

        public string? Render(ContextSourceValue previous, ContextSourceValue current) => null;
    }

    private sealed class StubConfigSectionStore : IConfigSectionStore
    {
        private readonly Dictionary<string, object> _sections = new(StringComparer.OrdinalIgnoreCase);

        public T GetSection<T>(string sectionName) where T : class, new() =>
            _sections.TryGetValue(sectionName, out var value) && value is T typed ? typed : new T();

        public Task SaveSectionAsync<T>(
            string sectionName,
            T value,
            ConfigLevel level = ConfigLevel.Project,
            CancellationToken ct = default) where T : class
        {
            _sections[sectionName] = value;
            return Task.CompletedTask;
        }

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>构造被测源。<paramref name="recall"/> 用于注入自定义召回 mock（反脆化用）。</summary>
    private static MemoryContextSource Build(
        string? userQuery,
        IReadOnlyList<SearchHit> hits,
        bool active = true,
        MemoryRetrievalMode mode = MemoryRetrievalMode.Both,
        IMemoryRecallService? recall = null)
    {
        var recallService = recall ?? BuildRecall(hits);

        var activity = new MemoryModuleActivity();
        if (!active)
            activity.MarkInactiveAndWake();

        return new MemoryContextSource(
            new FakeExecutionScope { UserQuery = userQuery },
            recallService,
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Retrieval = new MemoryRetrievalOptions { Mode = mode }
            }),
            activity);
    }

    private static IMemoryRecallService BuildRecall(IReadOnlyList<SearchHit> hits)
    {
        var recall = new Mock<IMemoryRecallService>();
        recall.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hits);
        return recall.Object;
    }

    /// <summary>走真实 <see cref="MemoryContextSource.ReadAsync"/> 产生一个值（含内容指纹）。</summary>
    private static async Task<ContextSourceValue> BuildWithHitsAsync(
        string path,
        double score,
        string? title,
        string? content)
    {
        var source = Build(userQuery: "问题", hits: [Hit(path, score, title, content)]);
        return await source.ReadAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>抹掉 Payload：装配器的 <c>previous</c> 就是这个形状（只有 Canonical）。</summary>
    private static ContextSourceValue WithoutPayload(ContextSourceValue value) =>
        value with { Payload = null };

    private static SearchHit Hit(string path, double score, string? title = null, string? content = null) =>
        new(
            FileNode.Create(path, content ?? "正文", FileMetadata.Create("id", MemoryType.Daily, title)),
            score,
            score,
            0);

    private static ContextSourceValue BuildValue(params (string Path, double Score, string Hash)[] hits)
    {
        var canonical = JsonSerializer.SerializeToElement(new
        {
            hits = hits
                .OrderBy(hit => hit.Path, StringComparer.Ordinal)
                .Select(hit => new { path = hit.Path, score = hit.Score, contentHash = hit.Hash })
                .ToList()
        });

        return new ContextSourceValue(ContextValueState.Value, canonical, null);
    }

    private sealed class FakeExecutionScope : IContextExecutionScope
    {
        public string SessionId { get; set; } = string.Empty;
        public string Cwd { get; set; } = string.Empty;
        public string WorkspaceRoot { get; set; } = string.Empty;
        public Seeing.Agent.Abstractions.Skills.SkillCatalogSnapshot? SkillSnapshot { get; set; }
        public string? UserQuery { get; set; }
        public Seeing.Agent.Abstractions.Execution.SessionToolSchemaResolution? ToolSettlement { get; set; }
    }
}
