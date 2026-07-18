namespace Seeing.Agent.Memory.Core;

/// <summary>
/// 保护共享 <see cref="Microsoft.Data.Sqlite.SqliteConnection"/> 的互斥门闩。
/// Microsoft.Data.Sqlite 连接非线程安全，并发 CreateCommand/Dispose 会触发
/// ArgumentOutOfRangeException（RemoveCommand index）。
/// </summary>
public sealed class SqliteConnectionGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
