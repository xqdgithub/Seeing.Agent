using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Providers;
using Seeing.Agent.Llm.SystemOne.Clients;
using Seeing.Agent.Llm.SystemOne.Configuration;

namespace Seeing.Agent.Llm.SystemOne;

/// <summary>
/// SystemOne（TypeSafe / Jev）内置 Provider：装完即用，同时供出 decision 能力与动态模型目录。
/// <para><b>为什么它是独立 Provider 而不是一条种子配置</b>：它有自己的端点缺省（10s 超时、
/// 不走代理）与独立的动态目录来源，配置形状虽简单但与「用户手写的配置驱动 Provider」语义不同——
/// 后者是用户提供的连接信息，前者是内置能力包的默认连接。</para>
/// <para><b>与 <c>Providers.typesafe</c> 的关系</b>：用户若在 <c>Providers</c> 节显式配置了同 id 的
/// 配置驱动 Provider，<see cref="ProviderRegistry"/> 的后注册者覆盖会让用户配置生效（用户优先）。
/// 本 Provider 只负责「没有用户配置时也能用」。</para>
/// </summary>
public sealed class SystemOneLlmProvider : ConfigurableProviderBase<SystemOneOptions>
{
    private readonly SystemOneConfigStore _store;
    private readonly IProviderRegistry _registry;
    private readonly ILogger<SystemOneLlmProvider> _logger;

    /// <summary>构造内置 SystemOne Provider。</summary>
    public SystemOneLlmProvider(
        SystemOneConfigStore store,
        ILlmClientFactoryResolver factories,
        IProviderRegistry registry,
        ILogger<SystemOneLlmProvider> logger)
        : base(factories, logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override string Id => Options.Id;

    /// <inheritdoc />
    public override string? Name => Options.Name;

    /// <inheritdoc />
    public override string Protocol => ProviderTypes.TypeSafe;

    /// <summary>TypeSafe 协议只实现 decision；声明其它能力会在 Router 第 5 层报错。</summary>
    protected override LlmCapabilityKind DefaultCapabilities => LlmCapabilityKind.Decision;

    /// <inheritdoc />
    protected override IReadOnlyList<string>? ReadCapabilityOverride(SystemOneOptions options)
        => options.Capabilities;

    /// <inheritdoc />
    protected override ProviderConfig BuildProviderConfig()
    {
        var options = Options;

        return new ProviderConfig
        {
            Id = options.Id,
            Type = ProviderTypes.TypeSafe,
            Name = options.Name,
            BaseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? null : options.BaseUrl,
            ApiKey = options.ApiKey,
            UseProxy = options.UseProxy,
            Timeout = options.Timeout,
            ListModels = options.ListModels,
            Capabilities = options.Capabilities is null ? null : new List<string>(options.Capabilities),
            Models = options.Models,
        };
    }

    /// <inheritdoc />
    protected override ILlmCapabilityClient? CreateClient(
        ProviderConfig config,
        LlmCapabilityKind capability)
        => CreateClientViaFactory(config, capability);

    /// <inheritdoc />
    protected override async Task<SystemOneOptions> LoadOptionsAsync(CancellationToken cancellationToken)
        => await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override async Task SaveOptionsAsync(SystemOneOptions options, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        _registry.Register(this, ownerExtensionId: null);
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyDictionary<string, object?>> ExportOptionsAsync(
        SystemOneOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["Id"] = options.Id,
            ["Name"] = options.Name,
            ["BaseUrl"] = options.BaseUrl,
            ["ApiKey"] = options.ApiKey,
            ["Capabilities"] = options.Capabilities is null ? null : new List<string>(options.Capabilities),
            ["ListModels"] = options.ListModels,
            ["Timeout"] = options.Timeout,
            ["UseProxy"] = options.UseProxy,
        };

        return Task.FromResult(values);
    }

    /// <summary>
    /// 模型目录 = 静态 <c>Models</c> ∪（<c>ListModels</c> 时的动态目录）。
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

        if (!options.ListModels)
            return models;

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
            _logger.LogWarning(ex, "拉取 SystemOne 远端目录时创建客户端失败");
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
            _logger.LogWarning(ex, "SystemOne 远端目录拉取失败，保留静态模型表");
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
