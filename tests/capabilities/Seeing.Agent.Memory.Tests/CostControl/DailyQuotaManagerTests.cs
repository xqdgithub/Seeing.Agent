using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.CostControl;
using Xunit;

namespace Seeing.Agent.Memory.Tests.CostControl;

public class DailyQuotaManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DailyQuotaManager _manager;

    public DailyQuotaManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _manager = new DailyQuotaManager(_connection, new SqliteConnectionGate());
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task ConsumeAsync_累加当日用量()
    {
        await _manager.ConsumeAsync(100, ct: TestContext.Current.CancellationToken);
        await _manager.ConsumeAsync(250, ct: TestContext.Current.CancellationToken);

        var usage = await _manager.GetUsageAsync("daily", TestContext.Current.CancellationToken);

        usage.Used.Should().Be(350);
        usage.UsageRate.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SetLimitAsync_更新限额()
    {
        await _manager.SetLimitAsync("daily", 1000, TestContext.Current.CancellationToken);

        var usage = await _manager.GetUsageAsync("daily", TestContext.Current.CancellationToken);

        usage.Limit.Should().Be(1000);
    }

    [Fact]
    public async Task IsQuotaAvailableAsync_超限返回false()
    {
        await _manager.SetLimitAsync("daily", 10, TestContext.Current.CancellationToken);
        await _manager.ConsumeAsync(10, ct: TestContext.Current.CancellationToken);

        var available = await _manager.IsQuotaAvailableAsync("daily", TestContext.Current.CancellationToken);

        available.Should().BeFalse();
    }

    [Fact]
    public async Task 并发操作_不破坏共享连接()
    {
        var tasks = Enumerable.Range(0, 64)
            .Select(_ => _manager.ConsumeAsync(1, ct: TestContext.Current.CancellationToken));

        await Task.WhenAll(tasks);

        var usage = await _manager.GetUsageAsync("daily", TestContext.Current.CancellationToken);
        usage.Used.Should().Be(64);
    }
}
