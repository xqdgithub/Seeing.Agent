using FluentAssertions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Core.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 协同目录上下文源：空列表必须建立基线并渲染"（无）"；成员/存在性/备注变化各产生一条增量；
/// 无变化返回 null；不渲染 cwd（由 Environment 分节每请求实时渲染）。
/// </summary>
public class CollaborationDirectoriesContextSourceTests : IDisposable
{
    private readonly List<string> _tempDirectories = new();

    [Fact]
    public async Task ReadAsync_EmptyList_ShouldReturnValueAndRenderNone()
    {
        var source = new CollaborationDirectoriesContextSource(new FakeExecutionScope([]));

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        // 空列表是确定事实，不是"源失效"：必须 Value（Removed 会在首次装配被直接跳过）
        value.State.Should().Be(ContextValueState.Value);
        value.Canonical.Should().NotBeNull();
        value.Canonical!.Value.GetProperty("directories").GetArrayLength().Should().Be(0);

        var text = source.RenderInitial(value);
        text.Should().Contain("（无）");
        text.Should().Contain("协同工作目录");
    }

    [Fact]
    public async Task ReadAsync_PinnedSnapshotNull_ShouldReturnUnavailable()
    {
        var scope = new FakeExecutionScope([])
        {
            CollaborationDirectories = null!
        };
        var source = new CollaborationDirectoriesContextSource(scope);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        // 宿主接线缺失：Unavailable（不参与 diff，保留上次已叙述内容）
        value.State.Should().Be(ContextValueState.Unavailable);
        value.Canonical.Should().BeNull();
    }

    [Fact]
    public async Task RenderInitial_ShouldNotRenderWorkingDirectory()
    {
        var collaboration = CreateDirectory();
        var cwd = CreateDirectory();
        var scope = new FakeExecutionScope([new CollaborationDirectory(collaboration, "报表输出")])
        {
            Cwd = cwd,
            WorkspaceRoot = cwd
        };
        var source = new CollaborationDirectoriesContextSource(scope);

        var text = source.RenderInitial(await source.ReadAsync(TestContext.Current.CancellationToken));

        text.Should().Contain(collaboration);
        // 只声明"以 Environment 分节为基准"，不重复渲染 cwd（系统基线是快照，会与实时分节矛盾）
        text.Should().Contain("Environment");
        text.Should().NotContain(cwd);
    }

    [Fact]
    public async Task Render_NoChange_ShouldReturnNull()
    {
        var directory = CreateDirectory();
        var source = new CollaborationDirectoriesContextSource(
            new FakeExecutionScope([new CollaborationDirectory(directory, "备注")]));

        var previous = await source.ReadAsync(TestContext.Current.CancellationToken);
        var current = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(previous, current).Should().BeNull();
    }

    [Fact]
    public async Task Render_AddedAndRemoved_ShouldNarrateBoth()
    {
        var first = CreateDirectory();
        var second = CreateDirectory();
        var scope = new FakeExecutionScope([new CollaborationDirectory(first, "报表输出")]);
        var source = new CollaborationDirectoriesContextSource(scope);

        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        scope.CollaborationDirectories =
        [
            new CollaborationDirectory(first, "报表输出"),
            new CollaborationDirectory(second)
        ];
        var added = await source.ReadAsync(TestContext.Current.CancellationToken);

        var addedText = source.Render(before, added);
        addedText.Should().Contain($"协同目录已添加：{second}");

        // 再移除第一个
        scope.CollaborationDirectories = [new CollaborationDirectory(second)];
        var removed = await source.ReadAsync(TestContext.Current.CancellationToken);

        var removedText = source.Render(added, removed);
        removedText.Should().Contain($"协同目录已移除：{first}");

        // 增量叙述不是初始渲染：不得重复整份列表
        removedText.Should().NotContain("（无）");
    }

    [Fact]
    public async Task Render_UnavailableThenRestored_ShouldNarrateBothTransitions()
    {
        var directory = CreateDirectory();
        var source = new CollaborationDirectoriesContextSource(
            new FakeExecutionScope([new CollaborationDirectory(directory)]));

        var available = await source.ReadAsync(TestContext.Current.CancellationToken);
        available.State.Should().Be(ContextValueState.Value);

        // 成员未变、目录被删：只能靠 exists 探针产生增量
        Directory.Delete(directory);
        var missing = await source.ReadAsync(TestContext.Current.CancellationToken);
        source.Render(available, missing).Should().Contain($"协同目录不可用：{directory}");

        Directory.CreateDirectory(directory);
        var restored = await source.ReadAsync(TestContext.Current.CancellationToken);
        source.Render(missing, restored).Should().Contain($"协同目录已恢复：{directory}");
    }

    [Fact]
    public async Task Render_NoteChanged_ShouldNarrateUpdate()
    {
        var directory = CreateDirectory();
        var scope = new FakeExecutionScope([new CollaborationDirectory(directory, "旧备注")]);
        var source = new CollaborationDirectoriesContextSource(scope);

        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        scope.CollaborationDirectories = [new CollaborationDirectory(directory, "新备注")];
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(before, after).Should().Contain($"协同目录备注已更新：{directory}（新备注）");

        scope.CollaborationDirectories = [new CollaborationDirectory(directory, null)];
        var cleared = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(after, cleared).Should().Contain($"协同目录备注已清空：{directory}");
    }

    /// <summary>创建本次测试独占的临时目录（Dispose 统一清理）。</summary>
    private string CreateDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "seeing-collab-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>测试用执行作用域：协同目录快照即构造时给定的事实。</summary>
    private sealed class FakeExecutionScope(IReadOnlyList<CollaborationDirectory> directories)
        : IContextExecutionScope
    {
        public string SessionId { get; set; } = "s1";

        public string Cwd { get; set; } = string.Empty;

        public string WorkspaceRoot { get; set; } = string.Empty;

        public SkillCatalogSnapshot? SkillSnapshot { get; set; }

        public string? UserQuery { get; set; }

        public SessionToolSchemaResolution? ToolSettlement { get; set; }

        public IReadOnlyList<CollaborationDirectory> CollaborationDirectories { get; set; } = directories;
    }
}
