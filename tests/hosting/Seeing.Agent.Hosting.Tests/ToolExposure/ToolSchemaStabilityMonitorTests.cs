using FluentAssertions;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

/// <summary>
/// 前缀缓存运行时守门：同一会话连续两轮的 <c>tools[]</c> 若发生变化，必须能被执行链观测到
/// （日志 + <c>SchemaSnapshotEvent</c> 的增删字段），以便快速定位是哪类变化击穿了缓存。
/// </summary>
public class ToolSchemaStabilityMonitorTests
{
    [Fact]
    public void 首轮_无基线_应视为未变化()
    {
        var monitor = new ToolSchemaStabilityMonitor();

        var diff = monitor.Track("s1", ["read", "grep"]);

        diff.Changed.Should().BeFalse();
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
    }

    [Fact]
    public void 连续两轮相同_应视为未变化()
    {
        var monitor = new ToolSchemaStabilityMonitor();
        monitor.Track("s1", ["read", "grep"]);

        var diff = monitor.Track("s1", ["read", "grep"]);

        diff.Changed.Should().BeFalse();
    }

    [Fact]
    public void 新增工具_应报告新增项()
    {
        var monitor = new ToolSchemaStabilityMonitor();
        monitor.Track("s1", ["read"]);

        var diff = monitor.Track("s1", ["read", "write"]);

        diff.Changed.Should().BeTrue();
        diff.Added.Should().Equal("write");
        diff.Removed.Should().BeEmpty();
    }

    [Fact]
    public void 移除工具_应报告移除项()
    {
        var monitor = new ToolSchemaStabilityMonitor();
        monitor.Track("s1", ["read", "write"]);

        var diff = monitor.Track("s1", ["read"]);

        diff.Changed.Should().BeTrue();
        diff.Removed.Should().Equal("write");
        diff.Added.Should().BeEmpty();
    }

    [Fact]
    public void 仅顺序变化_也算击穿缓存()
    {
        // 缓存按前缀工作：元素顺序变化等价于从变化点起全量失效。
        var monitor = new ToolSchemaStabilityMonitor();
        monitor.Track("s1", ["read", "write"]);

        var diff = monitor.Track("s1", ["write", "read"]);

        diff.Changed.Should().BeTrue("顺序变化同样击穿前缀");
    }

    [Fact]
    public void 不同会话_互不影响()
    {
        var monitor = new ToolSchemaStabilityMonitor();
        monitor.Track("s1", ["read"]);

        var diff = monitor.Track("s2", ["read", "write"]);

        diff.Changed.Should().BeFalse("每个会话独立基线");
    }
}
