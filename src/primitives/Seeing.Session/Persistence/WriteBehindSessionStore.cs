using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;
using Seeing.Session.Storage;

namespace Seeing.Session.Persistence;

/// <summary>
/// 会话写回装饰器：将高频 <see cref="SaveAsync"/> 去抖合并后异步写入内层存储，
/// 并提供显式持久化屏障与可重定位能力转发。设计规格 §5.1。
/// </summary>
/// <remarks>
/// <para><b>快照契约：</b><see cref="SaveAsync"/> 不克隆，直接入队调用方移交的私有快照（§3.1）。</para>
/// <para><b>读路径活性：</b><see cref="LoadAsync"/>/<see cref="ListAsync"/>/<see cref="QueryAsync"/>/<see cref="LoadAllAsync"/>
/// 先做有界 flush，超时或内层持续失败时继续读取并记 Warning，绝不挂起。</para>
/// <para><b>删除时序：</b><see cref="DeleteAsync"/> 先经调度器 <c>DiscardAsync</c> 等待在途写并丢弃待写，再删除后端，消除"删除复活"竞态。</para>
/// </remarks>
public sealed class WriteBehindSessionStore :
    ISessionStore,
    IRelocatableSessionStore,
    IWriteBehindSessionStore,
    IPersistenceFlusher,
    IDisposable,
    IAsyncDisposable
{
    private readonly ISessionStore _inner;
    private readonly SessionPersistenceOptions _options;
    private readonly ILogger? _logger;
    private readonly CoalescingWriteScheduler<string, SessionData> _scheduler;

    /// <summary>
    /// 创建写回装饰器。
    /// </summary>
    /// <param name="inner">内层存储后端。</param>
    /// <param name="options">写回配置。</param>
    /// <param name="logger">可选日志记录器。</param>
    public WriteBehindSessionStore(ISessionStore inner, SessionPersistenceOptions options, ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        _scheduler = new CoalescingWriteScheduler<string, SessionData>(
            options.DebounceWindow,
            options.MaxFlushDelay,
            options.MaxRetryBackoff,
            options.ShutdownFlushTimeout,
            (snapshot, ct) => _inner.SaveAsync(snapshot, ct),
            logger);
    }

    /// <summary>当前会话存储基础目录；内层不支持重定位时为 null。</summary>
    public string? BaseDirectory => (_inner as IRelocatableSessionStore)?.BaseDirectory;

    /// <summary>切换基础目录；内层不支持重定位时为 no-op。</summary>
    public void SetBaseDirectory(string baseDirectory)
    {
        if (_inner is IRelocatableSessionStore relocatable)
            relocatable.SetBaseDirectory(baseDirectory);
    }

    /// <inheritdoc />
    public Task SaveAsync(SessionData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // 立即入队，不克隆、不阻塞；实际落盘由调度器去抖合并
        _scheduler.Enqueue(data.Id, data);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SessionData?> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        // 有界刷新（不抛）保证"读己所写"；内层持续失败时不挂起，退化为读取旧值
        var flushed = await _scheduler.TryFlushAsync(sessionId, _options.ReadFlushTimeout, ct).ConfigureAwait(false);
        if (!flushed)
        {
            _logger?.LogWarning(
                "读取会话 {SessionId} 前写回未在 {Timeout} 内完成，可能读到旧值",
                sessionId,
                _options.ReadFlushTimeout);
        }

        return await _inner.LoadAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        // 先丢弃待写并等待在途写完成，再删除后端，避免删除后被缓冲写复活
        await _scheduler.DiscardAsync(sessionId, ct).ConfigureAwait(false);
        await _inner.DeleteAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionData> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await TryFlushAllForReadAsync("ListAsync", ct).ConfigureAwait(false);

        await foreach (var item in _inner.ListAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionData> QueryAsync(
        string partitionId,
        string agentId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await TryFlushAllForReadAsync("QueryAsync", ct).ConfigureAwait(false);

        await foreach (var item in _inner.QueryAsync(partitionId, agentId, ct).ConfigureAwait(false))
            yield return item;
    }

    /// <inheritdoc />
    public async Task SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        foreach (var item in data)
        {
            ArgumentNullException.ThrowIfNull(item);
            _scheduler.Enqueue(item.Id, item);
        }

        await _scheduler.FlushAllAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SessionData> LoadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await TryFlushAllForReadAsync("LoadAllAsync", ct).ConfigureAwait(false);

        await foreach (var item in _inner.LoadAllAsync(ct).ConfigureAwait(false))
            yield return item;
    }

    /// <inheritdoc />
    public Task FlushAsync(string sessionId, CancellationToken ct = default)
        => _scheduler.FlushAsync(sessionId, ct);

    /// <inheritdoc />
    public Task<bool> TryFlushAsync(string sessionId, TimeSpan timeout, CancellationToken ct = default)
        => _scheduler.TryFlushAsync(sessionId, timeout, ct);

    /// <inheritdoc />
    public Task FlushAllAsync(CancellationToken ct = default)
        => _scheduler.FlushAllAsync(ct);

    /// <inheritdoc />
    public Task<bool> TryFlushAllAsync(TimeSpan timeout, CancellationToken ct = default)
        => _scheduler.TryFlushAllAsync(timeout, ct);

    /// <summary>同步释放：触发最终 flush 并停止后台循环（超时仅记 Warning）。</summary>
    public void Dispose() => _scheduler.Dispose();

    /// <summary>异步释放：触发最终 flush 并停止后台循环。</summary>
    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();

    /// <summary>读路径的统一有界 flush，超时/失败仅记 Warning 并继续委托内层。</summary>
    private async Task TryFlushAllForReadAsync(string operation, CancellationToken ct)
    {
        var flushed = await _scheduler.TryFlushAllAsync(_options.ReadFlushTimeout, ct).ConfigureAwait(false);
        if (!flushed)
        {
            _logger?.LogWarning(
                "{Operation} 前写回未在 {Timeout} 内全部完成，可能读到旧值",
                operation,
                _options.ReadFlushTimeout);
        }
    }
}
