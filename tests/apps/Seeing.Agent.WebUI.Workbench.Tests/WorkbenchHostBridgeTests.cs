using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Workbench;

namespace Seeing.Agent.WebUI.Workbench.Tests;

/// <summary>
/// 快照上报桥：去抖、只上报稳定事实、按声明的 ContributesContext 过滤摘要。
/// <para>用 <see cref="FakeTimeProvider"/> 推进虚拟时间，不做固定等待。</para>
/// </summary>
public class WorkbenchHostBridgeTests
{
    private static WorkbenchContribution Wb(string id, bool contributesContext = true) => new()
    {
        Point = WorkbenchPoints.DockId,
        Id = id,
        Title = id,
        ViewKey = WorkbenchPoints.ViewKeyFor(id),
        ContributesContext = contributesContext
    };

    private static (WorkbenchDockState State, FakeStore Store, WorkbenchHostBridge Bridge,
        FakeTimeProvider Clock) Build(TimeSpan? debounce = null)
    {
        var state = new WorkbenchDockState();
        var store = new FakeStore();
        var clock = new FakeTimeProvider();
        var bridge = new WorkbenchHostBridge(state, store, clock, debounce ?? TimeSpan.FromMilliseconds(250));
        return (state, store, bridge, clock);
    }

    /// <summary>测试替身：只需要「最后一次写入的整批快照」。</summary>
    private sealed class FakeStore : IWorkbenchSessionStateStore
    {
        private readonly Dictionary<string, IReadOnlyList<WorkbenchSessionSnapshot>> _bySession =
            new(StringComparer.Ordinal);

        public event Action<string>? Changed;

        public void Update(string sessionId, IReadOnlyList<WorkbenchSessionSnapshot> snapshots)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return;

            if (snapshots.Count == 0)
                _bySession.Remove(sessionId);
            else
                _bySession[sessionId] = snapshots.ToArray();

            Changed?.Invoke(sessionId);
        }

        public void Clear(string sessionId) => _bySession.Remove(sessionId);

        public IReadOnlyList<WorkbenchSessionSnapshot> GetSnapshot(string sessionId)
            => _bySession.TryGetValue(sessionId, out var snapshots) ? snapshots : [];
    }

    [Fact]
    public void Flush_ShouldPublishImmediately()
    {
        var (state, store, bridge, _) = Build();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        bridge.Flush("s1", [Wb("files")]);

        store.GetSnapshot("s1").Should().ContainSingle();
    }

    [Fact]
    public void Changed_ShouldNotPublishBeforeDebounce()
    {
        var (state, store, bridge, clock) = Build();
        bridge.Flush("s1", [Wb("files")]);
        store.Clear("s1");

        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        clock.Advance(TimeSpan.FromMilliseconds(100));

        store.GetSnapshot("s1").Should().BeEmpty("去抖窗口内不得上报");
    }

    [Fact]
    public void Changed_ShouldPublishAfterDebounce()
    {
        var (state, store, _, clock) = Build();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));

        clock.Advance(TimeSpan.FromMilliseconds(300));

        store.GetSnapshot("s1").Should().ContainSingle();
    }

    [Fact]
    public void Snapshot_ShouldCarryDockTitleAndActive()
    {
        var (state, store, bridge, _) = Build();
        state.Open("s1", WorkbenchDock.Left, Wb("files"));

        bridge.Flush("s1", [Wb("files")]);

        var snapshot = store.GetSnapshot("s1").Single();
        snapshot.Key.Dock.Should().Be(WorkbenchDock.Left);
        snapshot.Title.Should().Be("files");
        snapshot.Active.Should().BeTrue();
    }

    [Fact]
    public void SetSummary_ShouldBeIncludedForContextContributors()
    {
        var (state, store, bridge, _) = Build();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        bridge.SetSummary("s1", key, "正在看 a.md");
        bridge.Flush("s1", [Wb("files")]);

        store.GetSnapshot("s1").Single().ContextSummary.Should().Be("正在看 a.md");
    }

    [Fact]
    public void SetSummary_ShouldBeDroppedForNonContextContributors()
    {
        var (state, store, bridge, _) = Build();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        bridge.SetSummary("s1", key, "正在看 a.md");
        bridge.Flush("s1", [Wb("files", contributesContext: false)]);

        store.GetSnapshot("s1").Single().ContextSummary.Should().BeNull(
            "摘要可能含用户内容，必须由工作台显式开启后才进入上下文");
    }

    [Fact]
    public void SetSummary_ShouldTruncateToLimit()
    {
        var (state, store, bridge, _) = Build();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        bridge.SetSummary("s1", key, new string('x', 500));
        bridge.Flush("s1", [Wb("files")]);

        store.GetSnapshot("s1").Single().ContextSummary!.Length
            .Should().BeLessThanOrEqualTo(WorkbenchContextLimits.SummaryCharLimit + 1);
    }

    [Fact]
    public void SetSummary_ShouldFlattenNewlines()
    {
        var (state, store, bridge, _) = Build();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files"))!.Value;

        bridge.SetSummary("s1", key, "第一行\r\n第二行");
        bridge.Flush("s1", [Wb("files")]);

        store.GetSnapshot("s1").Single().ContextSummary.Should().NotContain("\n");
    }

    [Fact]
    public void Snapshot_ShouldBeOrderedByDockThenId()
    {
        var (state, store, bridge, _) = Build();
        state.Open("s1", WorkbenchDock.Right, Wb("markdown"));
        state.Open("s1", WorkbenchDock.Left, Wb("files"));

        bridge.Flush("s1", [Wb("files"), Wb("markdown")]);

        store.GetSnapshot("s1")
            .Select(s => $"{s.Key.Dock}:{s.Key.WorkbenchId}")
            .Should().Equal(["Left:files", "Right:markdown"]);
    }

    [Fact]
    public void Clear_ShouldEmptyStore()
    {
        var (state, store, bridge, _) = Build();
        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        bridge.Flush("s1", [Wb("files")]);

        bridge.Clear("s1");

        store.GetSnapshot("s1").Should().BeEmpty();
    }

    [Fact]
    public void Dispose_ShouldStopPublishing()
    {
        var (state, store, bridge, clock) = Build();
        bridge.Flush("s1", [Wb("files")]);
        store.Clear("s1");
        bridge.Dispose();

        state.Open("s1", WorkbenchDock.Right, Wb("files"));
        clock.Advance(TimeSpan.FromMilliseconds(500));

        store.GetSnapshot("s1").Should().BeEmpty("释放后不得再写服务端快照");
    }

    [Fact]
    public void BlankSessionId_ShouldBeIgnored()
    {
        var (_, store, bridge, _) = Build();

        bridge.Flush("");
        bridge.Flush("   ");
        bridge.Clear("");

        store.GetSnapshot("").Should().BeEmpty();
    }
}
