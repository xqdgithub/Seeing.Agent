using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Agent.Skills.Commands;
using Seeing.Agent.Skills.Sources;
using Xunit;

namespace Seeing.Agent.Skills.Tests;

/// <summary>
/// 技能上下文源锁定：字节稳定基线（单一 <c>## Skills</c> 区块）/ 无描述条目不渲染 /
/// 增量叙述（新增 / 说明变更 / 移除）/ 移除文案含 <c>must not be used</c> /
/// 工具按执行级钉住快照解析 / 清单文本只有 <see cref="SkillCatalogFormatter"/> 一个出口。
/// </summary>
public sealed class SkillsContextSourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"seeing-skills-source-{Guid.NewGuid():N}");

    public SkillsContextSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    // ===== 基线 =====

    [Fact]
    public async Task Baseline_ShouldSortById()
    {
        var source = CreateSource(
            Entry("zeta", "z 说明"),
            Entry("alpha", "a 说明"),
            Entry("mike", "m 说明"));

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        text.Should().StartWith("## Skills");
        var alphaIndex = text.IndexOf("- alpha:", StringComparison.Ordinal);
        var mikeIndex = text.IndexOf("- mike:", StringComparison.Ordinal);
        var zetaIndex = text.IndexOf("- zeta:", StringComparison.Ordinal);

        alphaIndex.Should().BeGreaterThan(-1);
        mikeIndex.Should().BeGreaterThan(alphaIndex, "条目必须按名称 ordinal 排序（字节稳定的前提）");
        zetaIndex.Should().BeGreaterThan(mikeIndex);

        // 同值重复渲染必须逐字节相等（缓存不被逐请求击穿）
        source.RenderInitial(value).Should().Be(text);
    }

    [Fact]
    public async Task Baseline_ShouldSkipEntriesWithoutDescription()
    {
        var source = CreateSource(
            Entry("with-desc", "有说明"),
            Entry("no-desc", string.Empty));

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        text.Should().Contain("- with-desc: 有说明");
        text.Should().NotContain("no-desc", "无 description 的条目不参与渲染");

        // 但 canonical 仍记录全部条目（diff 只看值，不看渲染）
        var canonical = value.Canonical!.Value.GetRawText();
        canonical.Should().Contain("no-desc");
    }

    [Fact]
    public async Task Delta_ShouldNarrateAddRenameDescriptionAndRemoval()
    {
        var previous = await ReadAsync(
            Entry("alpha", "旧说明"),
            Entry("gone", "将被删除"));
        var current = await ReadAsync(
            Entry("alpha", "新说明"),
            Entry("fresh", "新增技能"));

        var source = CreateSource();
        var text = source.Render(previous, current);

        text.Should().NotBeNull();
        text!.Should().Contain("fresh", "新增必须被叙述");
        text.Should().Contain("alpha", "说明变更必须被叙述");
        text.Should().Contain("新说明");
        text.Should().Contain("gone", "移除必须被叙述");
    }

    [Fact]
    public async Task Removal_ShouldSayMustNotBeUsed()
    {
        var previous = await ReadAsync(Entry("alpha", "旧技能"));
        var current = new ContextSourceValue(ContextValueState.Removed, null, null);

        var source = CreateSource();
        var text = source.Render(previous, current);

        text.Should().NotBeNull();
        text!.Should().Contain("must not be used");
    }

    [Fact]
    public async Task Baseline_ShouldNotDuplicateSkillsHeading()
    {
        var source = CreateSource(Entry("alpha", "说明"));
        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        // 基线拥有整个 ## Skills 区块：标题只出现一次
        text.Split('\n').Count(line => line.Trim() == "## Skills").Should().Be(1);
        text.Split('\n')[0].Should().Be("## Skills");

        // 列表形态（/skills、错误提示）不得再自带同名标题，避免拼接后出现两个
        SkillCatalogFormatter.RenderList([Entry("alpha", "说明")])
            .Should().NotContain("## Skills");
    }

    // ===== 执行级钉住 =====

    [Fact]
    public async Task SkillTool_ShouldResolveFromPinnedSnapshot()
    {
        var skillDirectory = Path.Combine(_root, "pinned-skill");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "extra.md"),
            "附加文件",
            TestContext.Current.CancellationToken);

        var entry = new SkillCatalogEntry(
            new SkillInfo
            {
                Name = "pinned",
                Description = "钉住的技能",
                Content = "钉住正文",
                Location = Path.Combine(skillDirectory, "SKILL.md")
            },
            SkillSource.ProjectFile,
            null,
            skillDirectory);

        // 目录里已经找不到该技能（模拟安装中途被删除 / 执行期间目录漂移）
        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance);
        catalog.SetRoots([Path.Combine(_root, "empty-root")]);
        var manager = new SkillManager(NullLogger<SkillManager>.Instance, catalog);
        manager.GetSkillInfo("pinned").Should().BeNull();

        var tool = new SkillTool(manager, NullLogger<SkillTool>.Instance);
        var context = new ToolContext
        {
            SessionId = "s1",
            SkillSnapshot = Snapshot(entry)
        };

        var result = await tool.ExecuteAsync(Arguments("pinned"), context);

        result.Success.Should().BeTrue("工具必须按执行级钉住快照解析，而不是当前目录");
        result.Output.Should().Contain("钉住正文");
        result.Metadata!["dir"].Should().Be(skillDirectory);
        result.Metadata["fileCount"].Should().Be(1);
    }

    // ===== 格式化单点 =====

    [Fact]
    public async Task Formatter_ShouldBeSingleSource_ForListAndErrors()
    {
        var entries = new[] { Entry("zeta", "z 说明"), Entry("alpha", "a 说明") };
        var expected = SkillCatalogFormatter.RenderList(entries);

        var catalog = new SkillCatalogCore(NullLogger<SkillCatalogCore>.Instance);
        foreach (var entry in entries)
        {
            await catalog.RegisterAsync(
                entry.Info, "test", TestContext.Current.CancellationToken);
        }

        // /skills 与 /skill（无参数）都复用同一份清单文本
        var listCommand = new SkillsListCommand(catalog);
        var listResult = await listCommand.ExecuteAsync(
            new Abstractions.Commands.CommandContext(),
            TestContext.Current.CancellationToken);
        listResult.Message.Should().Be(expected);

        var loadCommand = new SkillLoadCommand(catalog, Mock.Of<Seeing.Session.Core.ISessionManager>());
        var loadResult = await loadCommand.ExecuteAsync(
            new Abstractions.Commands.CommandContext { Arguments = string.Empty },
            TestContext.Current.CancellationToken);
        loadResult.Message.Should().Be(expected);

        // 工具的「未找到」提示复用同一份清单文本
        var manager = new SkillManager(NullLogger<SkillManager>.Instance, catalog);
        var tool = new SkillTool(manager, NullLogger<SkillTool>.Instance);
        var result = await tool.ExecuteAsync(
            Arguments("missing"),
            new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Output.Should().Contain(expected);
    }

    // ===== 辅助 =====

    private static SkillCatalogEntry Entry(string name, string description, string? directory = null) =>
        new(
            new SkillInfo
            {
                Name = name,
                Description = description,
                Content = $"{name} 正文"
            },
            SkillSource.Registered,
            "test",
            directory ?? string.Empty);

    private static SkillCatalogSnapshot Snapshot(params SkillCatalogEntry[] entries) =>
        new(
            1,
            entries,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase),
            [],
            []);

    private static SkillsContextSource CreateSource(params SkillCatalogEntry[] entries) =>
        new(new FakeExecutionScope(Snapshot(entries)));

    private static async Task<ContextSourceValue> ReadAsync(params SkillCatalogEntry[] entries)
    {
        var source = CreateSource(entries);
        return await source.ReadAsync(TestContext.Current.CancellationToken);
    }

    private static JsonElement Arguments(string name) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["name"] = name });

    /// <summary>测试用执行作用域：钉住快照即构造时给定的事实。</summary>
    private sealed class FakeExecutionScope(SkillCatalogSnapshot snapshot) : IContextExecutionScope
    {
        public string SessionId { get; set; } = string.Empty;

        public string Cwd { get; set; } = string.Empty;

        public string WorkspaceRoot { get; set; } = string.Empty;

        public SkillCatalogSnapshot? SkillSnapshot { get; set; } = snapshot;
    }

}
