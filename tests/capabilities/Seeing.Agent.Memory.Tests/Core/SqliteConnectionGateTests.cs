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
/// <para>
/// <b>两个重载必须分别覆盖</b>：非泛型 <c>RunAsync</c> 与泛型 <c>RunAsync&lt;T&gt;</c> 各自持有独立锁路径。
/// 全包共 26 处调用点：泛型 12 处、非泛型 14 处（按「lambda 体是否产出值」逐点判定，并经编译器改名探针复核）。
/// 泛型 12 处：<c>KeywordIndex.cs:157/190</c>、<c>VectorIndex.cs:184/226/260</c>、
/// <c>SqliteMemoryGraph.cs:191/236/269/291</c>、<c>SqliteEmbeddingCache.cs:65/144/159</c>
/// ——其中多数是表达式体方法（签名返回 <c>Task&lt;T&gt;</c>，不出现 <c>return</c> 关键字）。
/// 只覆盖其中一个会让另一个的锁失效在测试中隐形——这正是变异 B 暴露的问题。
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
    public async Task RunAsync_NonGeneric_Under64WayConcurrency_ShouldNeverAllowTwoInside()
    {
        // 非泛型重载：临界区同一时刻只允许一个执行体。
        var gate = new SqliteConnectionGate();
        var ct = TestContext.Current.CancellationToken;
        var inside = 0;
        var maxObserved = 0;
        var completed = 0;

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

        maxObserved.Should().Be(1, "非泛型 RunAsync 的临界区必须互斥");
        completed.Should().Be(Concurrency);
    }

    [Fact]
    public async Task RunAsync_Generic_Under64WayConcurrency_ShouldNeverAllowTwoInside()
    {
        // 泛型重载 RunAsync<T>：独立锁路径，必须单独覆盖。
        // KeywordIndex.cs:157 / VectorIndex.cs:184 / VectorIndex.cs:226 走的就是这条。
        var gate = new SqliteConnectionGate();
        var ct = TestContext.Current.CancellationToken;
        var inside = 0;
        var maxObserved = 0;
        var completed = 0;

        var tasks = Enumerable.Range(0, Concurrency).Select(async _ =>
        {
            var projected = await gate.RunAsync(async token =>
            {
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxObserved, now);

                await Task.Yield();
                await Task.Delay(1, token);

                Interlocked.Decrement(ref inside);
                Interlocked.Increment(ref completed);
                return 0;
            }, ct);

            projected.Should().Be(0);
        }).ToArray();

        await Task.WhenAll(tasks);

        maxObserved.Should().Be(1, "泛型 RunAsync<T> 的临界区必须互斥");
        completed.Should().Be(Concurrency);
    }

    [Fact]
    public async Task RunAsync_With64ConcurrentWrites_ShouldNotCorruptSharedConnection()
    {
        // 泛型重载的"连接不被破坏"弱断言（保留作为补充；判别力靠上面两条互斥用例）。
        var gate = new SqliteConnectionGate();
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable.Range(0, Concurrency).Select(i => gate.RunAsync(async token =>
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO gate_probe (value) VALUES (@v)";
            cmd.Parameters.AddWithValue("@v", i);
            await cmd.ExecuteNonQueryAsync(token);
            return i;
        }, ct)).ToArray();

        var written = await Task.WhenAll(tasks);

        written.Should().HaveCount(Concurrency);
        (await CountRowsAsync()).Should().Be(Concurrency);
    }

    [Fact]
    public async Task RunAsync_ShouldPropagateCancellationToCaller()
    {
        var gate = new SqliteConnectionGate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await gate.RunAsync(_ => Task.CompletedTask, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_Generic_ShouldPropagateCancellationToCaller()
    {
        var gate = new SqliteConnectionGate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await gate.RunAsync(_ => Task.FromResult(0), cts.Token);

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
