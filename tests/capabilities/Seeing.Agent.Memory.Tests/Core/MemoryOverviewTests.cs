using FluentAssertions;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

// Core.MemoryType（Semantic/Episodic/...）是死枚举；测试命名空间 ...Tests.Core
// 会让两个 MemoryType 都进作用域。显式消歧义到真正在用的那个。
using MemoryType = Seeing.Agent.Memory.Core.Models.MemoryType;

namespace Seeing.Agent.Memory.Tests.Core;

/// <summary>总览聚合：类型分布恒三键、时间分布恒 N 项、窗口内外语义分离。</summary>
public class MemoryOverviewTests
{
    private static FileNode Node(string path, MemoryType type, DateTimeOffset createdAt)
        => FileNode.Create(path, "正文", FileMetadata.Create("id", type, "t") with { CreatedAt = createdAt });

    private static MemoryService Build(IReadOnlyList<FileNode> files)
    {
        var store = new Mock<IFileStore>();
        store.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(files);

        var index = new Mock<IMemoryIndex>();
        index.Setup(i => i.GetStatsAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IndexStats(0, 0, 0, DateTimeOffset.UtcNow));

        var graph = new Mock<IMemoryGraph>();
        graph.Setup(g => g.GetStatsAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new GraphStats(0, 0, 0));

        return new MemoryService(store.Object, index.Object, graph.Object);
    }

    [Fact]
    public async Task GetOverview_CountByType_Should_Include_Zero_Value_Types()
    {
        // 只有 session 数据，daily/digest 也必须在字典里且为 0，前端四张卡片才稳定。
        var overview = await Build([Node("session/s1/index.md", MemoryType.Session, DateTimeOffset.Now)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.CountByType.Should().HaveCount(3);
        overview.CountByType[MemoryType.Session].Should().Be(1);
        overview.CountByType[MemoryType.Daily].Should().Be(0);
        overview.CountByType[MemoryType.Digest].Should().Be(0);
    }

    [Fact]
    public async Task GetOverview_DailyCounts_Should_Have_Exactly_N_Items()
    {
        var overview = await Build([]).GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.DailyCounts.Should().HaveCount(14, "空库也要给满 14 个槽，前端不必补零");
        overview.DailyCounts.Should().OnlyContain(d => d.Count == 0);
    }

    [Fact]
    public async Task GetOverview_DailyCounts_Should_Place_Today_In_Last_Slot()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var overview = await Build([Node("daily/t/a.md", MemoryType.Daily, DateTimeOffset.Now)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.DailyCounts[^1].Date.Should().Be(today, "最后一根柱子是今天");
        overview.DailyCounts[^1].Count.Should().Be(1);
        overview.DailyCounts[0].Date.Should().Be(today.AddDays(-13));
    }

    [Fact]
    public async Task GetOverview_Should_Exclude_Entries_Outside_Window_From_Daily_But_Keep_In_Total()
    {
        var old = DateTimeOffset.Now.AddDays(-30);
        var overview = await Build([Node("daily/old/a.md", MemoryType.Daily, old)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.TotalCount.Should().Be(1, "总数不受窗口限制");
        overview.CountByType[MemoryType.Daily].Should().Be(1);
        overview.DailyCounts.Sum(d => d.Count).Should().Be(0, "30 天前不进 14 天窗口");
    }

    [Fact]
    public async Task GetOverview_Window_Boundary_Should_Include_Days_Minus_One_And_Exclude_Days()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var offset = DateTimeOffset.Now.Offset;

        // 窗口首日（today - 13）中午：必须计入。
        var boundary = new DateTimeOffset(today.AddDays(-13).ToDateTime(new TimeOnly(12, 0)), offset);
        // 窗口外一天（today - 14）中午：必须不计入。
        var outside = new DateTimeOffset(today.AddDays(-14).ToDateTime(new TimeOnly(12, 0)), offset);

        var overview = await Build([
                Node("daily/in/a.md", MemoryType.Daily, boundary),
                Node("daily/out/b.md", MemoryType.Daily, outside)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.DailyCounts[0].Count.Should().Be(1, "days-1 天前属于窗口首日");
        overview.DailyCounts.Sum(d => d.Count).Should().Be(1, "正好 days 天前不算");
    }

    [Fact]
    public async Task GetOverview_DailyCounts_Should_Group_By_Local_Date()
    {
        // 必须构造一个「本地日 ≠ UTC 日」的时刻，否则这条测试无法区分本地归日与 UTC 归日。
        // 非 UTC 时区下取本地 00:30：此时 UTC 通常是前一天。
        var offset = DateTimeOffset.Now.Offset;
        if (offset == TimeSpan.Zero)
            return;   // UTC 时区下两者相同，本用例无从区分（CI 若为 UTC 会跳过验证）

        var localNow = DateTimeOffset.Now;
        var localEarlyMorning = new DateTimeOffset(
            localNow.Date.AddMinutes(30), offset);   // 本地 00:30

        var overview = await Build([Node("daily/x/a.md", MemoryType.Daily, localEarlyMorning)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        var expectedLocal = DateOnly.FromDateTime(localEarlyMorning.LocalDateTime);
        var expectedUtc = DateOnly.FromDateTime(localEarlyMorning.UtcDateTime);
        expectedLocal.Should().NotBe(expectedUtc,
            "前置条件：本地 00:30 在非 UTC 时区下必然与前一天的 UTC 日期不同");

        var actual = overview.DailyCounts.Single(d => d.Count == 1).Date;
        actual.Should().Be(expectedLocal, "按用户本地日历归日，不能用 UTC");
    }
}
