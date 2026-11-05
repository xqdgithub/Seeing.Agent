using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Providers;

namespace Seeing.Provider.TypeSafeApi;

/// <summary>
/// TypeSafe（Decision）端点 Provider：装完即用，同时供出 decision 能力与动态模型目录。
/// <para><b>为什么它是插件</b>：能力包 <c>llm.typesafe</c> 只负责「typesafe 协议怎么说话」，
/// 而「连哪个端点、凭据从哪来、模型目录从哪拉」是某个具体端点的事——那是插件的职责。
/// 早先这段逻辑放在能力包里，导致设置页把一个不是用户配置的 Provider
/// 当成「配置型」渲染，还给了它一份用户看不见的私有配置文件。</para>
/// <para><b>为什么只有 ApiKey 可配</b>：这是预置 client。端点、能力、超时、代理、
/// 目录行为都是插件已知的事实，写在 <see cref="BuildProviderConfig"/> 里；
/// 让用户可改只会制造必然失败的配置（例如给 TypeSafe 声明 chat 能力，
/// 会在 Router 第 5 层直接报错）。要连自建端点请另建配置型 Provider 选 <c>typesafe</c> 协议。</para>
/// </summary>
public sealed class TypeSafeApiProvider : ConfigurableProviderBase<TypeSafeApiOptions>
{
    /// <summary>扩展标识（同时作为注册表里的归属 id）。</summary>
    public const string ExtensionId = "seeing.provider.typesafe-api";

    /// <summary>Provider id（同时是模型引用前缀，如 <c>typesafe-api/jev-latest</c>）。</summary>
    public const string ProviderId = "typesafe-api";

    /// <summary>TypeSafe 协议的缺省基址。</summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";

    /// <summary>请求超时（毫秒）。决策调用不应吃满框架 5 分钟缺省超时。</summary>
    public const int DefaultTimeoutMs = 10_000;

    private readonly TypeSafeApiConfigStore _store;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<TypeSafeApiProvider> _logger;

    /// <summary>构造 TypeSafe Provider。</summary>
    public TypeSafeApiProvider(
        TypeSafeApiConfigStore store,
        ILlmClientFactoryResolver factories,
        IProviderRegistry registry,
        ILogger<TypeSafeApiProvider> logger)
        : base(factories, logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// 固定为 <see cref="ProviderId"/>，**不随配置变化**：
    /// <see cref="IProviderRegistry"/> 按 id 索引，若 id 可改，改配置后旧键会永久残留，
    /// 且模块 Deactivate 用新 id 注销会失败并泄漏实例。
    /// </remarks>
    public override string Id => ProviderId;

    /// <inheritdoc />
    public override string? Name => "TypeSafe";

    /// <inheritdoc />
    public override string Protocol => ProviderTypes.TypeSafe;

    /// <summary>TypeSafe 协议只实现 decision；声明其它能力会在 Router 第 5 层报错。</summary>
    protected override LlmCapabilityKind DefaultCapabilities => LlmCapabilityKind.Decision;

    /// <inheritdoc />
    /// <remarks>
    /// 恒为 <c>null</c>：能力是协议事实，不由配置覆盖。
    /// 继承来的「配置覆盖」入口在这里被主动关掉，避免配置里出现一个能改坏能力的字段。
    /// </remarks>
    protected override IReadOnlyList<string>? ReadCapabilityOverride(TypeSafeApiOptions options) => null;

    /// <inheritdoc />
    protected override ProviderConfig BuildProviderConfig() => new()
    {
        Id = ProviderId,
        Type = ProviderTypes.TypeSafe,
        Name = Name,
        BaseUrl = DefaultBaseUrl,
        ApiKey = Options.ApiKey,
        // UseProxy 取框架默认值（true = 跟随系统代理），与 openai / anthropic /
        // 配置驱动 Provider 完全一致。决策端点是公网服务，与其它 Provider 无区别；
        // 早先 SystemOne 时代写死 false（「决策服务通常在内网」）的假设在此不成立，
        // 直连会让按来源 IP 判定地域的服务返回 451。
        Timeout = DefaultTimeoutMs,
        // /v1/models 是协议标准端点；拉取失败会自动回退静态表
        ListModels = true,
        Models = Options.Models,
    };

    /// <inheritdoc />
    protected override ILlmCapabilityClient? CreateClient(
        ProviderConfig config,
        LlmCapabilityKind capability)
        => CreateClientViaFactory(config, capability);

    /// <inheritdoc />
    protected override async Task<TypeSafeApiOptions> LoadOptionsAsync(CancellationToken cancellationToken)
        => await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task SaveOptionsAsync(TypeSafeApiOptions options, CancellationToken cancellationToken)
        => await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override Task OnOptionsSavedAsync(CancellationToken cancellationToken)
    {
        // 重新注册让 ProviderInfo 的能力投影跟上新配置（注册表内部是幂等替换）。
        RegisterSelf();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把自己注册为 <see cref="ProviderOrigin.Extension"/>。
    /// <para><b>生命周期不归注册表</b>（<c>DisposeByRegistry: false</c>）：本实例来自 DI 容器，
    /// 注册表若在注销时释放它，下一次 Activate 之后取客户端会撞 <c>ObjectDisposedException</c>。</para>
    /// </summary>
    internal void RegisterSelf()
        => _registry.Register(new ProviderRegistration(
            this,
            ProviderOrigin.Extension,
            OwnerId: ExtensionId,
            DisposeByRegistry: false));

    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, object?>> ExportOptionsAsync(
        TypeSafeApiOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["ApiKey"] = options.ApiKey,
        };

        return Task.FromResult(values);
    }

    /// <summary>
    /// 模型目录 = 静态 <c>Models</c> ∪ 远端动态目录。
    /// <para>静态条目优先：用户显式写的限额/定价不被远端裸条目冲掉。
    /// 动态目录经客户端 <see cref="IModelCatalogClient"/> 取，失败只记警告、保留静态表。</para>
    /// </summary>
    public override async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var options = Options;
        var models = options.Models is null
            ? new List<ModelConfig>()
            : options.Models.Select(pair => Clone(pair.Key, pair.Value)).ToList();

        var remote = await TryListRemoteModelsAsync(cancellationToken).ConfigureAwait(false);
        if (remote is null || remote.Count == 0)
            return models;

        var known = new HashSet<string>(
            options.Models?.Keys ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in remote)
        {
            var modelId = string.IsNullOrWhiteSpace(entry.Id) ? entry.Name : entry.Id;
            if (string.IsNullOrWhiteSpace(modelId) || !known.Add(modelId))
                continue;

            models.Add(Clone(modelId, entry));
        }

        return models;
    }

    private async Task<IReadOnlyList<ModelConfig>?> TryListRemoteModelsAsync(CancellationToken cancellationToken)
    {
        ILlmCapabilityClient? client;
        try
        {
            client = GetClient(LlmCapabilityKind.Decision);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "拉取 TypeSafe 远端目录时创建客户端失败");
            return null;
        }

        if (client is not IModelCatalogClient catalog)
            return null;

        try
        {
            return await catalog.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 远端目录不可用不能让整张模型表消失：保留静态表。
            _logger.LogWarning(ex, "TypeSafe 远端目录拉取失败，保留静态模型表");
            return null;
        }
    }

    private ModelConfig Clone(string modelId, ModelConfig model) => new()
    {
        Id = string.IsNullOrWhiteSpace(model.Id) ? modelId : model.Id,
        Name = model.Name,
        Provider = Id,
        Types = model.Types,
        Modalities = model.Modalities,
        Limit = model.Limit,
        Options = model.Options,
        Pricing = model.Pricing,
        Metadata = model.Metadata is null ? null : new Dictionary<string, object?>(model.Metadata),
    };
}
