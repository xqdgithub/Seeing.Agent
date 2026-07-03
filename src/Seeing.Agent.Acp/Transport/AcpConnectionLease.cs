namespace Seeing.Agent.Acp.Transport;

/// <summary>
/// ACP 子进程连接租约。
/// </summary>
public sealed class AcpConnectionLease : IAsyncDisposable
{
    private readonly AcpConnectionManager _manager;
    private readonly SemaphoreSlim _gate;
    private int _disposed;

    internal AcpConnectionLease(
        AcpConnectionManager manager,
        string leaseKey,
        Client.SeeingAcpClient client,
        SemaphoreSlim gate)
    {
        _manager = manager;
        LeaseKey = leaseKey;
        Client = client;
        _gate = gate;
    }

    public string LeaseKey { get; }

    public Client.SeeingAcpClient Client { get; }

    public async Task<IDisposable> AcquirePromptLockAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new PromptLock(_gate);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return ValueTask.CompletedTask;

        _manager.ReturnLease(LeaseKey, this);
        return ValueTask.CompletedTask;
    }

    private sealed class PromptLock(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
