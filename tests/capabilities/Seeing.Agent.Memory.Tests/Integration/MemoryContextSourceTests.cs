using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Memory.Integration;
using Seeing.Agent.Memory.Integration.Hosting;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

/// <summary>
/// <see cref="MemoryContextSource"/> 锁定：召回经 <see cref="IContextSource"/> 管道注入，
/// <c>Target = Message</c>（内容随轮次以消息形式进入历史）、无命中/不可读一律 <see cref="ContextValueState.Unavailable"/>
/// （绝不叙述「记忆已移除」）、<c>Canonical</c> 必须是 JSON 对象且不含正文、值不变不叙述。
/// </summary>
public class MemoryContextSourceTests
{
    [Fact]
    public async Task ReadAsync_Should_Return_Unavailable_When_UserQuery_Empty()
    {
        // Review Focus #3
        var source = Build(userQuery: null, hits: []);
        (await source.ReadAsync(TestContext.Current.CancellationToken)).State
            .Should().Be(ContextValueState.Unavailable);
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
        var v = BuildValue(("a.md", 0.9));
        source.Render(v, v).Should().BeNull();
    }

    [Fact]
    public void Render_Should_Narrate_When_Canonical_Changed()
    {
        var source = Build(userQuery: "问题", hits: []);
        source.Render(BuildValue(("a.md", 0.9)), BuildValue(("b.md", 0.7)))
            .Should().Contain("b.md");
    }

    [Fact]
    public void Target_Should_Be_Message()
    {
        // 裁决 R4：System 源只在首次建立时渲染基线，UserQuery 每轮变会让系统提示永久停在第一轮
        Build(userQuery: null, hits: []).Target.Should().Be(ContextSourceTarget.Message);
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

    private static MemoryContextSource Build(
        string? userQuery,
        IReadOnlyList<SearchHit> hits,
        bool active = true,
        MemoryRetrievalMode mode = MemoryRetrievalMode.Both)
    {
        var recall = new Mock<IMemoryRecallService>();
        recall.Setup(r => r.RecallAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hits);

        var activity = new MemoryModuleActivity();
        if (!active)
            activity.MarkInactiveAndWake();

        return new MemoryContextSource(
            new FakeExecutionScope { UserQuery = userQuery },
            recall.Object,
            MemoryTestOptions.Monitor(new MemoryOptions
            {
                Retrieval = new MemoryRetrievalOptions { Mode = mode }
            }),
            activity);
    }

    private static SearchHit Hit(string path, double score, string? title = null, string? content = null) =>
        new(
            FileNode.Create(path, content ?? "正文", FileMetadata.Create("id", MemoryType.Daily, title)),
            score,
            score,
            0);

    private static ContextSourceValue BuildValue(params (string Path, double Score)[] hits)
    {
        var canonical = JsonSerializer.SerializeToElement(new
        {
            hits = hits
                .OrderBy(hit => hit.Path, StringComparer.Ordinal)
                .Select(hit => new { path = hit.Path, score = hit.Score })
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
    }
}
