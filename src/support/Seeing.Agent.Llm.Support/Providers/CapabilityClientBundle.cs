using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Llm.Support.Providers;

/// <summary>
/// 能力 → 客户端束：按 Provider 声明的能力位集各建一个延迟客户端，并统一释放。
/// <para><b>为什么需要它</b>：Provider 声明的能力可以多于一个（例如 SiliconFlow 同时供 chat 与 embedding）。
/// 只用一个 <c>_client</c> 字段时，第二个能力要么取不到实例，要么与第一个共用错误类型的客户端。
/// 缓存键是「能力」，与 providerId 一起构成 <c>(providerId, capability)</c> 的实例粒度。</para>
/// <para>放在 Support 层而非 Abstractions：它是实现（含 Lazy 与释放策略），
/// 而 Abstractions 只承载契约。插件已依赖 Support，无需改 csproj。</para>
/// </summary>
public sealed class CapabilityClientBundle : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<LlmCapabilityKind, Lazy<ILlmCapabilityClient?>> _clients;
    private readonly Action<Exception>? _onCreateFailed;
    private readonly Action<Exception>? _onDisposeFailed;
    private int _disposed;

    /// <summary>按能力位集建束；每个单值能力一个延迟工厂。</summary>
    /// <param name="capabilities">Provider 声明供出的能力位集。</param>
    /// <param name="factory">按能力创建客户端；不支持的组合应返回 <c>null</c>。</param>
    /// <param name="onCreateFailed">客户端创建抛异常时的记录回调（不含重抛，仍会重抛）。</param>
    /// <param name="onDisposeFailed">单个客户端释放失败时的记录回调。</param>
    public CapabilityClientBundle(
        LlmCapabilityKind capabilities,
        Func<LlmCapabilityKind, ILlmCapabilityClient?> factory,
        Action<Exception>? onCreateFailed = null,
        Action<Exception>? onDisposeFailed = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _onCreateFailed = onCreateFailed;
        _onDisposeFailed = onDisposeFailed;
        Capabilities = capabilities;
        _clients = new Dictionary<LlmCapabilityKind, Lazy<ILlmCapabilityClient?>>();

        foreach (var kind in SingleFlags(capabilities))
        {
            _clients[kind] = new Lazy<ILlmCapabilityClient?>(
                () =>
                {
                    try
                    {
                        return factory(kind);
                    }
                    catch (Exception ex)
                    {
                        _onCreateFailed?.Invoke(ex);
                        throw;
                    }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    /// <summary>本束覆盖的能力位集。</summary>
    public LlmCapabilityKind Capabilities { get; }

    /// <summary>
    /// 取指定能力的客户端；未声明该能力返回 <c>null</c>（不抛、不回退）。
    /// </summary>
    public ILlmCapabilityClient? Get(LlmCapabilityKind capability)
        => _clients.TryGetValue(capability, out var lazy) ? lazy.Value : null;

    /// <summary>已创建的客户端实例（含 Lazy 已求值的项）。</summary>
    private IEnumerable<KeyValuePair<LlmCapabilityKind, ILlmCapabilityClient>> CreatedClients()
    {
        foreach (var (kind, lazy) in _clients)
        {
            if (lazy.IsValueCreated && lazy.Value is { } client)
                yield return new KeyValuePair<LlmCapabilityKind, ILlmCapabilityClient>(kind, client);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var (_, client) in CreatedClients())
        {
            try
            {
                switch (client)
                {
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                _onDisposeFailed?.Invoke(ex);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var (_, client) in CreatedClients())
        {
            try
            {
                switch (client)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception ex)
            {
                _onDisposeFailed?.Invoke(ex);
            }
        }
    }

    /// <summary>把位集拆成单值能力（跳过 <see cref="LlmCapabilityKind.None"/>），顺序稳定。</summary>
    private static IEnumerable<LlmCapabilityKind> SingleFlags(LlmCapabilityKind capabilities)
        => LlmCapabilityMap.SingleFlags(capabilities);
}
