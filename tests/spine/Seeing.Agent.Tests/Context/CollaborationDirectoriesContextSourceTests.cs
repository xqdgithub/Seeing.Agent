using FluentAssertions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Directories;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Core.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 协同目录上下文源：空列表必须建立基线并渲染"未授权"；成员/存在性/备注变化各产生一条增量；
/// 无变化返回 null；不渲染 cwd（由 Environment 分节每请求实时渲染）。
/// </summary>
public class CollaborationDirectoriesContextSourceTests : IDisposable
{
    private readonly List<string> _tempDirectories = new();

    [Fact]
    public async Task ReadAsync_EmptyList_ShouldReturnValueAndRenderNone()
    {
        var source = CreateSource(new FakeDirectories());

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        // 空列表是确定事实，不是"源失效"：必须 Value（Removed 会在首次装配被直接跳过）
        value.State.Should().Be(ContextValueState.Value);
        value.Canonical.Should().NotBeNull();
        value.Canonical!.Value.GetProperty("directories").GetArrayLength().Should().Be(0);

        var text = source.RenderInitial(value);
        text.Should().StartWith("## 协同工作目录");
        text.Should().Contain("未授权任何协同目录");
        text.Should().NotContain("（无）");
    }

    [Fact]
    public async Task RenderInitial_ShouldNotRenderWorkingDirectory()
    {
        var collaboration = CreateDirectory();
        var cwd = CreateDirectory();
        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(collaboration, "报表输出"));
        var source = CreateSource(directories, cwd);

        var text = source.RenderInitial(await source.ReadAsync(TestContext.Current.CancellationToken));

        text.Should().Contain(collaboration);
        // 只声明"与工作目录平级、访问用绝对路径"，不重复渲染 cwd（系统基线是快照，会与实时分节矛盾）
        text.Should().StartWith("## 协同工作目录");
        text.Should().Contain("绝对路径");
        text.Should().Contain("workdir");
        text.Should().Contain("Environment");
        text.Should().NotContain("以 Environment 分节的工作目录为基准");
        text.Should().NotContain(cwd);
    }

    [Fact]
    public async Task RenderInitial_WithDirectory_ShouldListOneEntryPerLine()
    {
        var directory = CreateDirectory();
        var spaced = Path.Combine(directory, "报表 输出");
        Directory.CreateDirectory(spaced);

        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(directory, "报表\n输出"));
        directories.Entries.Add(new CollaborationDirectory(spaced));
        var source = CreateSource(directories);

        var text = source.RenderInitial(await source.ReadAsync(TestContext.Current.CancellationToken));

        text.Should().StartWith("## 协同工作目录");
        text.Should().Contain($"\n- {directory}（报表 输出）");
        text.Should().Contain($"\n- {spaced}");
        text.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Render_NoChange_ShouldReturnNull()
    {
        var directory = CreateDirectory();
        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(directory, "备注"));
        var source = CreateSource(directories);

        var previous = await source.ReadAsync(TestContext.Current.CancellationToken);
        var current = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(previous, current).Should().BeNull();
    }

    [Fact]
    public async Task Render_AddedAndRemoved_ShouldNarrateBoth()
    {
        var first = CreateDirectory();
        var second = CreateDirectory();
        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(first, "报表输出"));
        var source = CreateSource(directories);

        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        directories.Entries.Add(new CollaborationDirectory(second));
        var added = await source.ReadAsync(TestContext.Current.CancellationToken);

        var addedText = source.Render(before, added);
        addedText.Should().Contain($"协同目录已添加：{second}");

        // 再移除第一个
        directories.Entries.RemoveAll(d => d.Path == first);
        var removed = await source.ReadAsync(TestContext.Current.CancellationToken);

        var removedText = source.Render(added, removed);
        removedText.Should().Contain($"协同目录已移除：{first}");

        // 增量叙述不是初始渲染：不得重复整份列表
        removedText.Should().NotContain("## 协同工作目录");
        removedText.Should().NotContain("（无）");
    }

    [Fact]
    public async Task Render_UnavailableThenRestored_ShouldNarrateBothTransitions()
    {
        var directory = CreateDirectory();
        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(directory));
        var source = CreateSource(directories);

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
        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(directory, "旧备注"));
        var source = CreateSource(directories);

        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        directories.Entries[0] = new CollaborationDirectory(directory, "新备注");
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(before, after).Should().Contain($"协同目录备注已更新：{directory}（新备注）");

        directories.Entries[0] = new CollaborationDirectory(directory, null);
        var cleared = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(after, cleared).Should().Contain($"协同目录备注已清空：{directory}");
    }

    [Fact]
    public async Task ReadAsync_ShouldReflectLatestDirectoryState()
    {
        // 源每次读端口取当前值：上一轮之后的新增，下一轮装配必须看到
        var first = CreateDirectory();
        var second = CreateDirectory();
        var directories = new FakeDirectories();
        directories.Entries.Add(new CollaborationDirectory(first));
        var source = CreateSource(directories);

        var before = await source.ReadAsync(TestContext.Current.CancellationToken);
        directories.Entries.Add(new CollaborationDirectory(second));
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        after.Canonical!.Value.GetProperty("directories").GetArrayLength().Should().Be(2);
        source.Render(before, after).Should().Contain(second);
    }

    private CollaborationDirectoriesContextSource CreateSource(FakeDirectories directories, string cwd = "")
        => new(new FakeExecutionScope { Cwd = cwd, WorkspaceRoot = cwd }, directories);

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

    /// <summary>测试用执行作用域：只提供会话 id / cwd，目录内容走端口。</summary>
    private sealed class FakeExecutionScope : IContextExecutionScope
    {
        public string SessionId { get; set; } = "s1";

        public string Cwd { get; set; } = string.Empty;

        public string WorkspaceRoot { get; set; } = string.Empty;

        public SkillCatalogSnapshot? SkillSnapshot { get; set; }

        public string? UserQuery { get; set; }

        public SessionToolSchemaResolution? ToolSettlement { get; set; }
    }

    private sealed class FakeDirectories : ICollaborationDirectories
    {
        public List<CollaborationDirectory> Entries { get; } = [];

        public Task<IReadOnlyList<CollaborationDirectory>> GetAsync(
            string sessionId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CollaborationDirectory>>(Entries.ToArray());

        public Task AddAsync(
            string sessionId, string absolutePath, string? note = null, CancellationToken ct = default)
        {
            Entries.Add(new CollaborationDirectory(absolutePath, note));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, string absolutePath, CancellationToken ct = default)
        {
            Entries.RemoveAll(entry => entry.Path == absolutePath);
            return Task.CompletedTask;
        }
    }
}
