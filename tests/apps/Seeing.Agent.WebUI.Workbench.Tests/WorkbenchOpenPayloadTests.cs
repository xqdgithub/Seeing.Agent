using FluentAssertions;
using Seeing.Agent.Abstractions.Ui.Workbench;
using Seeing.Agent.WebUI.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench.Tests;

/// <summary>
/// 打开载荷的<b>一次性</b>语义。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这条必须钉住</b>：<c>OpenPayload</c> 的契约是「一次性指令」，但早期实现只存不清，
/// 组件每次挂载都重放它。浏览器工作台的重放动作是「导航到该地址」，于是
/// <b>切到别的标签再切回来、或刷新页面后，页面整页重新加载</b> ——
/// 滚动位置、表单内容、视频进度全部归零。用户实测报障：「切换回来浏览器会整个刷新」。
/// </para>
/// <para>
/// 这类缺陷靠肉眼审查极难发现：代码注释写的是「一次性」，字段却一直是旧值，
/// 编译、其他测试全绿，只有真人在界面上切标签才会看到。
/// </para>
/// </remarks>
public class WorkbenchOpenPayloadTests
{
    private static WorkbenchContribution Wb(string id) => new()
    {
        Point = WorkbenchPoints.DockId,
        Id = id,
        Title = id,
        ViewKey = WorkbenchPoints.ViewKeyFor(id)
    };

    private static JsonElement Payload(string url)
        => JsonSerializer.SerializeToElement(new { url });

    [Fact]
    public void TakeOpenPayload_FirstCall_ShouldReturnPayload()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://example.com")).Value;

        var taken = state.TakeOpenPayload("s1", key);

        taken.Should().NotBeNull();
        taken!.Value.TryGetProperty("url", out var url).Should().BeTrue();
        url.GetString().Should().Be("https://example.com");
    }

    [Fact]
    public void TakeOpenPayload_SecondCall_ShouldReturnNull()
    {
        // ── 这就是「切 tab 回来页面重载」的修复点 ──
        // 组件销毁重建后会再次调用它；第二次必须是 null，否则会重放导航。
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://example.com")).Value;

        state.TakeOpenPayload("s1", key).Should().NotBeNull();
        state.TakeOpenPayload("s1", key).Should().BeNull(
            "载荷是一次性指令；重复消费会导致每次重挂载都重新导航（整页刷新）");
    }

    [Fact]
    public void TakeOpenPayload_InstanceMetadata_ShouldBeCleared()
    {
        // 取走之后，布局投影里也必须为空 —— 否则 WorkbenchViewContext.OpenPayload
        // 仍是旧值，任何直接读它的代码又会重放一次。
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://example.com")).Value;

        state.TakeOpenPayload("s1", key);

        state.GetLayout("s1").Right.Single().OpenPayload.Should().BeNull(
            "已消费的载荷不应残留在实例投影里");
    }

    [Fact]
    public void TakeOpenPayload_NoPayload_ShouldReturnNull()
    {
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("files")).Value;

        state.TakeOpenPayload("s1", key).Should().BeNull();
    }

    [Fact]
    public void TakeOpenPayload_UnknownSession_ShouldReturnNull()
    {
        var state = new WorkbenchDockState();

        state.TakeOpenPayload("nope", new WorkbenchInstanceKey(WorkbenchDock.Right, "browser"))
            .Should().BeNull();
    }

    [Fact]
    public void TakeOpenPayload_WrongDock_ShouldReturnNull()
    {
        // 同一工作台 id 在另一侧 dock 并不存在：不能误取
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://example.com")).Value;

        state.TakeOpenPayload("s1", new WorkbenchInstanceKey(WorkbenchDock.Left, key.WorkbenchId))
            .Should().BeNull();
    }

    [Fact]
    public void Open_AgainWithNewPayload_ShouldReplaceConsumedOne()
    {
        // agent 再调一次 browser_open（真的换了地址）时，新载荷必须生效 ——
        // 「一次性」不等于「只接受一次」，而是「消费过就不再重放」。
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://a.example")).Value;
        state.TakeOpenPayload("s1", key).Should().NotBeNull();

        var again = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://b.example")).Value;

        var taken = state.TakeOpenPayload("s1", again);
        taken.Should().NotBeNull("agent 重新打开并给了新地址时必须能生效");
        taken!.Value.GetProperty("url").GetString().Should().Be("https://b.example");
    }

    [Fact]
    public void TakeOpenPayload_AfterMoveToOtherDock_ShouldFollowInstance()
    {
        // 换边搬运实例时载荷也要跟着走（MoveToOtherDock 会带上 OpenPayload）
        var state = new WorkbenchDockState();
        var key = state.Open("s1", WorkbenchDock.Right, Wb("browser"), Payload("https://example.com")).Value;

        var moved = state.MoveToOtherDock("s1", key);
        moved.Should().NotBeNull();

        state.TakeOpenPayload("s1", moved!.Value).Should().NotBeNull(
            "未消费的载荷应随实例一起换边");
        state.TakeOpenPayload("s1", moved.Value).Should().BeNull();
    }

    [Fact]
    public void TakeOpenPayload_AfterRestore_ShouldReturnNull()
    {
        // localStorage 恢复时载荷为 null（不持久化）：恢复后不该触发任何导航
        var state = new WorkbenchDockState();
        var restored = new WorkbenchInstance(
            new WorkbenchInstanceKey(WorkbenchDock.Right, "browser"),
            "浏览器",
            WorkbenchPoints.ViewKeyFor("browser"),
            null,
            null);

        state.Restore("s1", new WorkbenchSessionLayout([], [restored], null, null, 45, 45));

        state.TakeOpenPayload("s1", restored.Key).Should().BeNull(
            "刷新恢复不应重放历史导航（ViewState 里的地址由组件自己判断是否需要补导航）");
    }
}
