using FluentAssertions;
using Seeing.Agent.WebUI.Services;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 会话加载状态机：把「是否需要显示骨架」从一组互相牵制的布尔标志，
/// 收敛成「目标会话 vs 已装载会话」这一唯一判定。
/// </summary>
public class SessionLoadStateTests
{
    [Fact]
    public void Initially_ShouldNotBeLoading()
    {
        var s = new SessionLoadState();

        s.IsLoading.Should().BeFalse("尚未请求任何会话");
        s.ShowSkeleton.Should().BeFalse();
    }

    /// <summary>首次进入：没有旧内容，不该显示骨架（否则空闪一下）。</summary>
    [Fact]
    public void BeginFirstLoad_ShouldNotShowSkeleton()
    {
        var s = new SessionLoadState();

        s.Begin("s1");

        s.IsLoading.Should().BeTrue();
        s.ShowSkeleton.Should().BeFalse("首次加载没有旧内容需要遮挡");
    }

    /// <summary>已有内容时切到另一会话：必须显示骨架。</summary>
    [Fact]
    public void BeginSwitchAfterLoaded_ShouldShowSkeleton()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");

        s.Begin("s2");

        s.IsLoading.Should().BeTrue();
        s.ShowSkeleton.Should().BeTrue("旧会话内容仍在，需要骨架遮挡");
    }

    [Fact]
    public void Complete_ShouldClearLoading()
    {
        var s = new SessionLoadState();
        s.Begin("s1");

        s.Complete("s1");

        s.IsLoading.Should().BeFalse();
        s.ShowSkeleton.Should().BeFalse();
        s.LoadedSessionId.Should().Be("s1");
    }

    /// <summary>重复 Begin 同一会话属于幂等（重复导航/参数重放）。</summary>
    [Fact]
    public void BeginSameSessionTwice_ShouldStayIdempotent()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");

        s.Begin("s1");

        s.IsLoading.Should().BeFalse("目标与已装载一致，无需重新加载");
        s.ShowSkeleton.Should().BeFalse();
    }

    /// <summary>关键：加载期间又切到第三个会话，骨架应持续显示，不能因为中间态而闪烁。</summary>
    [Fact]
    public void BeginDuringLoading_ShouldKeepSkeletonVisible()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");
        s.Begin("s2");

        s.Begin("s3");

        s.ShowSkeleton.Should().BeTrue("期间始终有旧内容，骨架不应中断");
    }

    /// <summary>Complete 的会话与当前目标不符（过期完成）：不能误清加载态。</summary>
    [Fact]
    public void CompleteStaleSession_ShouldNotClearLoading()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");

        s.Begin("s2");
        s.Complete("s1"); // s1 的迟到完成

        s.IsLoading.Should().BeTrue("目标仍是 s2，不能被 s1 的迟到完成清掉");
        s.ShowSkeleton.Should().BeTrue();
    }

    [Fact]
    public void CompleteAfterLoad_ShouldRecordLoadedSession()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");

        s.LoadedSessionId.Should().Be("s1");
    }

    [Fact]
    public void EmptySessionId_ShouldNotEnterLoading()
    {
        var s = new SessionLoadState();

        s.Begin("");

        s.IsLoading.Should().BeFalse();
        s.ShowSkeleton.Should().BeFalse();
    }

    /// <summary>加载失败：撤掉骨架但不记录已装载（下次参数变更应重试）。</summary>
    [Fact]
    public void Fail_ShouldClearLoadingWithoutRecordingLoaded()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");
        s.Begin("s2");

        s.Fail("s2");

        s.IsLoading.Should().BeFalse();
        s.ShowSkeleton.Should().BeFalse();
        s.LoadedSessionId.Should().Be("s1", "失败不能把目标当作已装载");
    }

    /// <summary>
    /// 回归（骨架常驻）：迟到的失败同样必须退出加载态。
    /// <para>
    /// 切换会话时组事件回推会让 <see cref="SessionLoadState.Begin"/> 交错，
    /// 若 <c>Fail</c> 因「不是当前目标」提前返回，<c>IsLoading</c> 永久为 true，
    /// 而 <c>ShowSkeleton == IsLoading</c>，表现为骨架永远挂着、页面再也刷不出来。
    /// </para>
    /// </summary>
    [Fact]
    public void FailStaleSession_ShouldStillExitLoading()
    {
        var s = new SessionLoadState();
        s.Begin("s1");
        s.Complete("s1");

        s.Begin("s2");
        s.Begin("s3");   // 交错：目标被改成 s3
        s.Fail("s2");    // s2 的迟到失败

        s.IsLoading.Should().BeFalse("迟到的失败也必须退出加载态，否则骨架常驻");
        s.ShowSkeleton.Should().BeFalse();
        s.LoadedSessionId.Should().Be("s1");
    }

    /// <summary>重复 Fail 不应产生异常或副作用。</summary>
    [Fact]
    public void FailTwice_ShouldBeIdempotent()
    {
        var s = new SessionLoadState();
        s.Begin("s1");

        s.Fail("s1");
        s.Fail("s1");

        s.IsLoading.Should().BeFalse();
        s.LoadedSessionId.Should().BeNull();
    }
}
