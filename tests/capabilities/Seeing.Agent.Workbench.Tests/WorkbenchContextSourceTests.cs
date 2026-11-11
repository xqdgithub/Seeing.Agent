using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.Workbench.Sources;
using System.Text.Json;

namespace Seeing.Agent.Workbench.Tests;

/// <summary>
/// 工作台上下文源的契约：
/// ① 无会话 / 空快照 → <see cref="ContextValueState.Removed"/>（是事实，不是「暂时不可读」）；
/// ② 初始渲染逐字节稳定，含不可信内容声明；
/// ③ 无变化时 <c>Render</c> 返回 null；
/// ④ 增量只叙述打开/关闭/激活/摘要变更。
/// </summary>
public class WorkbenchContextSourceTests
{
    private static WorkbenchSessionSnapshot Snapshot(
        string id,
        bool active = false,
        string? summary = null,
        WorkbenchDock dock = WorkbenchDock.Right,
        string? title = null)
        => new(new WorkbenchInstanceKey(dock, id), title ?? id, active, summary);

    private static (WorkbenchContextSource Source, WorkbenchSessionStateStore Store) Build(string? sessionId = "s1")
    {
        var scope = new Mock<IContextExecutionScope>();
        scope.SetupGet(s => s.SessionId).Returns(sessionId ?? string.Empty);

        var store = new WorkbenchSessionStateStore();
        var source = new WorkbenchContextSource(scope.Object, store, NullLogger<WorkbenchContextSource>.Instance);
        return (source, store);
    }

    [Fact]
    public async Task ReadAsync_NoSession_ShouldBeRemoved()
    {
        var (source, _) = Build(sessionId: null);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Removed);
        value.Canonical.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_EmptySnapshot_ShouldBeRemoved()
    {
        var (source, _) = Build();

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Removed,
            "没有打开的工作台是事实，不是暂时不可读");
    }

    [Fact]
    public async Task ReadAsync_WithWorkbenches_ShouldProduceStableCanonical()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files", active: true, summary: "正在看 a.md")]);

        var first = await source.ReadAsync(TestContext.Current.CancellationToken);
        var second = await source.ReadAsync(TestContext.Current.CancellationToken);

        first.State.Should().Be(ContextValueState.Value);
        first.Canonical!.Value.GetRawText().Should().Be(second.Canonical!.Value.GetRawText(),
            "同一快照重复读取必须逐字节相等，否则基线会无故抖动");
    }

    [Fact]
    public async Task ReadAsync_ShouldOrderByDockThenId()
    {
        var (source, store) = Build();
        store.Update("s1",
        [
            Snapshot("video", dock: WorkbenchDock.Right),
            Snapshot("files", dock: WorkbenchDock.Left),
            Snapshot("alpha", dock: WorkbenchDock.Right)
        ]);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var ids = value.Canonical!.Value.GetProperty("workbenches")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();

        ids.Should().Equal(["files", "alpha", "video"], "左 dock 在前，同 dock 按 id 序数序");
    }

    [Fact]
    public async Task RenderInitial_ShouldMentionUntrustedData()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files", active: true, summary: "正在看 a.md", title: "文件")]);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        text.Should().Contain("不可信");
        text.Should().Contain("文件");
        text.Should().Contain("正在看 a.md");
        text.Should().Contain("当前激活");
    }

    [Fact]
    public async Task Render_NoChange_ShouldReturnNull()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files", active: true)]);

        var first = await source.ReadAsync(TestContext.Current.CancellationToken);
        var second = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(first, second).Should().BeNull("无变化不得推进叙述");
    }

    [Fact]
    public async Task Render_Opened_ShouldNarrate()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files")]);
        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        store.Update("s1", [Snapshot("files"), Snapshot("video")]);
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(before, after).Should().Contain("打开了").And.Contain("video");
    }

    [Fact]
    public async Task Render_Closed_ShouldNarrate()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files", title: "文件"), Snapshot("video")]);
        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        store.Update("s1", [Snapshot("video")]);
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(before, after).Should().Contain("关闭了").And.Contain("文件");
    }

    [Fact]
    public async Task Render_AllClosed_ShouldNarrateClosures()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files")]);
        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        store.Update("s1", []);
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        after.State.Should().Be(ContextValueState.Removed);
        source.Render(before, after).Should().Contain("关闭了");
    }

    [Fact]
    public async Task Render_ActiveToggle_ShouldNarrate()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files", active: true), Snapshot("video", active: false)]);
        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        store.Update("s1", [Snapshot("files", active: false), Snapshot("video", active: true)]);
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        var text = source.Render(before, after);
        text.Should().Contain("切到").And.Contain("video");
    }

    [Fact]
    public async Task Render_SummaryChange_ShouldNarrate()
    {
        var (source, store) = Build();
        store.Update("s1", [Snapshot("files", active: true, summary: "正在看 a.md")]);
        var before = await source.ReadAsync(TestContext.Current.CancellationToken);

        store.Update("s1", [Snapshot("files", active: true, summary: "正在看 b.md")]);
        var after = await source.ReadAsync(TestContext.Current.CancellationToken);

        source.Render(before, after).Should().Contain("查看内容变为").And.Contain("b.md");
    }

    [Fact]
    public void RenderInitial_ShouldTruncateToBaselineLimit()
    {
        var (source, _) = Build();
        var many = Enumerable.Range(0, 200)
            .Select(i => Snapshot($"wb{i:000}", summary: new string('x', 200)))
            .ToArray();

        var canonical = JsonSerializer.SerializeToElement(new
        {
            workbenches = many.Select(s => new
            {
                dock = s.Key.Dock.ToString(),
                id = s.Key.WorkbenchId,
                title = s.Title,
                active = s.Active,
                summary = s.ContextSummary
            }).ToList()
        });

        var text = source.RenderInitial(new ContextSourceValue(ContextValueState.Value, canonical, many));

        text.Length.Should().BeLessThanOrEqualTo(WorkbenchContextSource.BaselineCharLimit + 40);
        text.Should().Contain("已按上限截断");
    }

    [Fact]
    public void KeyAndScope_ShouldBeSessionScopedSystemTarget()
    {
        var (source, _) = Build();

        source.Scope.Should().Be(ContextSourceScope.Session);
        source.Target.Should().Be(ContextSourceTarget.System);
        source.ValueSchemaVersion.Should().Be(1);
        source.Key.Value.Should().Contain(nameof(WorkbenchContextSource));
    }
}
