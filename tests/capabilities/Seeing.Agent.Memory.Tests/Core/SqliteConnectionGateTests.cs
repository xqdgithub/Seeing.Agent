using FluentAssertions;
using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Core;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Core;

/// <summary>
/// <see cref="SqliteConnectionGate"/> 并发串行化守门测试。
/// <para>
/// Microsoft.Data.Sqlite 的 <see cref="SqliteConnection"/> 非线程安全：并发
/// CreateCommand/Dispose 会破坏内部命令链表并抛 ArgumentOutOfRangeException。
/// <see cref="SqliteConnectionGate"/> 被 4 个生产类共用（VectorIndex / KeywordIndex /
/// SqliteMemoryGraph / SqliteEmbeddingCache），本测试是它唯一的并发覆盖。
/// </para>
/// </summary>
public sealed class SqliteConnectionGateTests : IDisposable
{
    private const int Concurrency = 64;

    private readonly SqliteConnection _connection;

    public SqliteConnectionGateTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var init = _connection.CreateCommand();
        init.CommandText = "CREATE TABLE gate_probe (id INTEGER PRIMARY KEY, value INTEGER NOT NULL)";
        init.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task RunAsync_Under64WayConcurrency_ShouldNeverAllowTwoActionsInsideCriticalSection()
    {
        // Arrange：直接测门的语义——临界区同一时刻只允许一个执行体。
        // 去掉锁后此断言必然失败（maxObserved 会接近 64），因此它对锁的实现有判别力。
        var gate = new SqliteConnectionGate();
        var ct = TestContext.Current.CancellationToken;
        var inside = 0;
        var maxObserved = 0;
        var completed = 0;

        // Act
        var tasks = Enumerable.Range(0, Concurrency).Select(async _ =>
        {
            await gate.RunAsync(async token =>
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxObserved, now);

                // 主动让出调度并拉长临界区，确保竞争窗口真实存在
                await Task.Yield();
                await Task.Delay(1, token);

                Interlocked.Decrement(ref inside);
                Interlocked.Increment(ref completed);
            }, ct);
        }).ToArray();

        await Task.WhenAll(tasks);

        // Assert
        maxObserved.Should().Be(1, "临界区必须互斥：任何时刻只允许一个执行体进入");
        completed.Should().Be(Concurrency);
    }

    [Fact]
    public async Task RunAsync_With64ConcurrentWrites_ShouldNotCorruptSharedConnection()
    {
        // Arrange
        var gate = new SqliteConnectionGate();
        var ct = TestContext.Current.CancellationToken;

        // Act：64 个并发写入，全部经同一 gate 串行访问同一连接
        var tasks = Enumerable.Range(0, Concurrency).Select(i => gate.RunAsync(async token =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO gate_probe (value) VALUES (@v)";
            cmd.Parameters.AddWithValue("@v", i);
            await cmd.ExecuteNonQueryAsync(token);
            return i;
        }, ct)).ToArray();

        var written = await Task.WhenAll(tasks);

        // Assert：无异常抛出，且 64 行全部落盘（连接未被并发破坏）
        written.Should().HaveCount(Concurrency);
        (await CountRowsAsync()).Should().Be(Concurrency);
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateCancellationToCaller()
    {
        // Arrange
        var gate = new SqliteConnectionGate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var act = async () => await gate.RunAsync(_ => Task.CompletedTask, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<long> CountRowsAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM gate_probe";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                break;
        }
    }
}
