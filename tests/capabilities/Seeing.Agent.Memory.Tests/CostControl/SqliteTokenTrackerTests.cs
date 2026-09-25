using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.CostControl;
using Xunit;

namespace Seeing.Agent.Memory.Tests.CostControl;

public class SqliteTokenTrackerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTokenTracker _tracker;

    public SqliteTokenTrackerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _tracker = new SqliteTokenTracker(_connection, new SqliteConnectionGate());
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task TrackAsync_记录消耗()
    {
        await _tracker.TrackAsync(new TokenUsage(100, 50, 150, 1), TestContext.Current.CancellationToken);

        var usage = await _tracker.GetTodayUsageAsync(TestContext.Current.CancellationToken);

        usage.InputTokens.Should().Be(100);
        usage.OutputTokens.Should().Be(50);
        usage.TotalTokens.Should().Be(150);
        usage.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOperationUsageAsync_按操作筛选()
    {
        await _tracker.TrackAsync(new TokenUsage(10, 5, 15, 1), TestContext.Current.CancellationToken);

        var usage = await _tracker.GetOperationUsageAsync("embedding", TestContext.Current.CancellationToken);

        usage.RequestCount.Should().Be(1);
        usage.InputTokens.Should().Be(10);
    }

    [Fact]
    public async Task 并发操作_不破坏共享连接()
    {
        // 共享 SqliteConnection 非线程安全；经 SqliteConnectionGate 串行化后并发操作应全部成功
        var tasks = Enumerable.Range(0, 64)
            .Select(_ => _tracker.TrackAsync(new TokenUsage(1, 1, 2, 1), TestContext.Current.CancellationToken));

        await Task.WhenAll(tasks);

        var usage = await _tracker.GetTodayUsageAsync(TestContext.Current.CancellationToken);
        usage.RequestCount.Should().Be(64);
        usage.InputTokens.Should().Be(64);
    }
}
