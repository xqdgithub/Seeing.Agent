using FluentAssertions;
using Seeing.Agent.Abstractions.Ui.Workbench;

namespace Seeing.Agent.Workbench.Tests;

/// <summary>快照存储：整批替换、相同快照静默、清空、事件隔离。</summary>
public class WorkbenchSessionStateStoreTests
{
    private static WorkbenchSessionSnapshot Snapshot(
        string id, bool active = true, string? summary = null, WorkbenchDock dock = WorkbenchDock.Right)
        => new(new WorkbenchInstanceKey(dock, id), id, active, summary);

    [Fact]
    public void GetSnapshot_UnknownSession_ShouldReturnEmptyNotNull()
        => new WorkbenchSessionStateStore().GetSnapshot("nope").Should().NotBeNull().And.BeEmpty();

    [Fact]
    public void Update_ThenGet_ShouldReturnSnapshot()
    {
        var store = new WorkbenchSessionStateStore();

        store.Update("s1", [Snapshot("files"), Snapshot("markdown", active: false)]);

        var snapshot = store.GetSnapshot("s1");
        snapshot.Should().HaveCount(2);
        snapshot[0].Key.WorkbenchId.Should().Be("files");
        snapshot[1].Key.WorkbenchId.Should().Be("markdown");
    }

    [Fact]
    public void Update_SameSnapshotTwice_ShouldRaiseChangedOnce()
    {
        var store = new WorkbenchSessionStateStore();
        var count = 0;
        store.Changed += _ => count++;

        store.Update("s1", [Snapshot("files")]);
        store.Update("s1", [Snapshot("files")]);

        count.Should().Be(1, "相同快照重复上报不得触发上下文源重建");
    }

    [Fact]
    public void Update_DifferentContent_ShouldRaiseChanged()
    {
        var store = new WorkbenchSessionStateStore();
        var count = 0;
        string? observed = null;
        store.Changed += id => { count++; observed = id; };

        store.Update("s1", [Snapshot("files")]);
        store.Update("s1", [Snapshot("files", summary: "正在看 a.md")]);

        count.Should().Be(2);
        observed.Should().Be("s1");
    }

    [Fact]
    public void Update_EmptyList_ShouldRemoveSession()
    {
        var store = new WorkbenchSessionStateStore();
        store.Update("s1", [Snapshot("files")]);

        store.Update("s1", []);

        store.GetSnapshot("s1").Should().BeEmpty();
    }

    [Fact]
    public void Clear_ShouldRemoveAndNotify()
    {
        var store = new WorkbenchSessionStateStore();
        store.Update("s1", [Snapshot("files")]);
        var notified = 0;
        store.Changed += _ => notified++;

        store.Clear("s1");

        store.GetSnapshot("s1").Should().BeEmpty();
        notified.Should().Be(1);
    }

    [Fact]
    public void Clear_UnknownSession_ShouldNotNotify()
    {
        var store = new WorkbenchSessionStateStore();
        var notified = 0;
        store.Changed += _ => notified++;

        store.Clear("unknown");

        notified.Should().Be(0);
    }

    [Fact]
    public void Sessions_ShouldBeIsolated()
    {
        var store = new WorkbenchSessionStateStore();

        store.Update("s1", [Snapshot("files")]);
        store.Update("s2", [Snapshot("markdown")]);

        store.GetSnapshot("s1").Single().Key.WorkbenchId.Should().Be("files");
        store.GetSnapshot("s2").Single().Key.WorkbenchId.Should().Be("markdown");
    }

    [Fact]
    public void Update_SubscriberThrows_ShouldNotBreakOtherSubscribers()
    {
        var store = new WorkbenchSessionStateStore();
        var secondCalled = false;

        store.Changed += _ => throw new InvalidOperationException("boom");
        store.Changed += _ => secondCalled = true;

        var act = () => store.Update("s1", [Snapshot("files")]);

        act.Should().NotThrow("订阅者异常必须隔离：写者是 UI 渲染线程");
        secondCalled.Should().BeTrue();
    }

    [Fact]
    public void Update_BlankSessionId_ShouldBeIgnored()
    {
        var store = new WorkbenchSessionStateStore();
        var notified = 0;
        store.Changed += _ => notified++;

        store.Update("", [Snapshot("files")]);
        store.Update("   ", [Snapshot("files")]);

        notified.Should().Be(0);
    }
}
