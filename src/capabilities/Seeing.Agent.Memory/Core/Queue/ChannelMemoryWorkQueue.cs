using System.Threading.Channels;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Queue;

public sealed class ChannelMemoryWorkQueue : IMemoryWorkQueue
{
    private readonly Channel<MemoryBatch> _channel;
    private int _count;

    public ChannelMemoryWorkQueue(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _channel = Channel.CreateBounded<MemoryBatch>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int Count => Volatile.Read(ref _count);

    public bool TryEnqueue(MemoryBatch batch)
    {
        if (!_channel.Writer.TryWrite(batch))
            return false;

        Interlocked.Increment(ref _count);
        return true;
    }

    public async IAsyncEnumerable<MemoryBatch> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _count);
            yield return item;
        }
    }
}
