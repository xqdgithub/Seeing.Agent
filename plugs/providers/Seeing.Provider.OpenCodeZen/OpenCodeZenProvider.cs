using Seeing.Agent.Abstractions.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;

namespace Seeing.Provider.OpenCodeZen;

/// <summary>
/// OpenCode Zen LLM Provider：OpenAI 兼容网关，免费模型无需 API Key。
/// </summary>
public sealed class OpenCodeZenProvider : LlmProviderBase, IConfigurableLlmProvider, IAsyncDisposable
{
    public const string ExtensionId = "seeing.provider.opencodezen";
    public static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly OpenCodeZenConfigStore _store;
    private readonly ILlmClientFactory _factory;
    private readonly IProviderRegistry _registry;
    private readonly OpenCodeZenModelsClient _modelsClient;
    private readonly ILogger<OpenCodeZenProvider> _logger;
    private readonly object _gate = new();
    private string? _apiKey;
    private ILlmClient? _client;
    private IReadOnlyList<ModelConfig>? _modelsCache;
    private DateTimeOffset _modelsCachedAt;
    private int _disposed;

    public OpenCodeZenProvider(
        OpenCodeZenConfigStore store,
        ILlmClientFactory factory,
        IProviderRegistry registry,
        OpenCodeZenModelsClient modelsClient,
        ILogger<OpenCodeZenProvider> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _modelsClient = modelsClient ?? throw new ArgumentNullException(nameof(modelsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override string Id => "opencode-zen";

    public override string? Name => "OpenCode Zen";

    public async Task WarmupAsync(CancellationToken ct = default)
    {
        var options = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _apiKey = options.ApiKey;
        }
    }

    public override ILlmClient GetClient()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _client ??= CreateClient();
        }
    }

    /// <summary>
    /// 拉取 OpenCode Zen 全量模型目录（免认证），并应用内置预置与用户自定义覆盖。
    /// 未配置 API Key 时仅返回免费模型。
    /// </summary>
    public override async Task<IReadOnlyList<ModelConfig>> GetModelsAsync(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_modelsCache is not null &&
                DateTimeOffset.Now - _modelsCachedAt < ModelsCacheTtl)
            {
                return _modelsCache;
            }
        }

        // 快照 Key：过滤决策与客户端（CreateClient）保持一致；
        // 若在途请求期间 Key 被保存，放弃写回，避免旧过滤覆盖新缓存。
        string? apiKeySnapshot;
        lock (_gate)
        {
            apiKeySnapshot = _apiKey;
        }

        var options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var models = await _modelsClient.ListModelsAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (string.Equals(apiKeySnapshot, _apiKey, StringComparison.Ordinal))
            {
                // 未配置 API Key 时仅展示免费模型，避免全量（含付费）列表过大
                var visible = string.IsNullOrWhiteSpace(apiKeySnapshot)
                    ? models.Where(m => m.IsFree)
                    : models;

                _modelsCache = visible
                    .Select(m => OpenCodeZenModelCatalog.ApplyOverrides(m, options.ModelCapabilities))
                    .Select(ToModelConfig)
                    .ToList();
                _modelsCachedAt = DateTimeOffset.Now;
                return _modelsCache;
            }
        }

        // Key 已变更：本次结果作废（保存时会清空缓存，交由下次刷新重新拉取）
        return _modelsCache ?? Array.Empty<ModelConfig>();
    }

    /// <summary>
    /// ApiKey 可选：免费模型无需配置。手动构造 Schema 以表达可选性
    /// （OptionsSchemaBuilder 会把 ApiKey 隐式标记为必填）。
    /// </summary>
    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema()
        => new[]
        {
            new ConfigFieldSchema(
                Name: "ApiKey",
                Label: "API Key",
                Description: "OpenCode Zen API Key（可选）：免费模型无需配置，仅付费模型需要。可在 https://opencode.ai/auth 获取。",
                Type: ConfigFieldType.Secret,
                Required: false,
                DefaultValue: null)
        };

    public async Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default)
    {
        var options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>
        {
            ["ApiKey"] = options.ApiKey
        };
    }

    public async Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        _ = level;

        var apiKey = GetString(values, "ApiKey");

        // 合并保存：保留已有的 modelCapabilities 等扩展配置，避免 WebUI 保存时静默清空
        var options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        options.ApiKey = apiKey;
        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);

        ILlmClient? oldClient;
        lock (_gate)
        {
            _apiKey = apiKey;
            _modelsCache = null;
            _modelsCachedAt = default;
            oldClient = InvalidateClient();
        }

        await DisposeClientAsync(oldClient).ConfigureAwait(false);
        _registry.Register(this, ExtensionId);
    }

    public override Task<bool> TestConnectionAsync(
        string modelId,
        CancellationToken cancellationToken)
        => GetClient().TestConnectionAsync(modelId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        ILlmClient? client;
        lock (_gate)
        {
            if (_disposed != 0)
                return;

            Volatile.Write(ref _disposed, 1);
            client = InvalidateClient();
            _modelsCache = null;
        }

        await DisposeClientAsync(client).ConfigureAwait(false);
    }

    private ILlmClient CreateClient()
    {
        try
        {
            Dictionary<string, string>? headers = null;
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                // OpenCode Zen 免费模型无需 API Key，但 OpenAiChatClient 要求存在 Authorization 头；
                // 服务端对免费模型不校验 Key，用占位头满足客户端校验。
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer opencode-zen"
                };
            }

            return CreateBuiltInClient(_factory, new ProviderConfig
            {
                Id = Id,
                Type = ProviderType.OpenAI,
                Name = Name,
                BaseUrl = OpenCodeZenModelsClient.DefaultBaseUrl,
                ApiKey = _apiKey,
                Headers = headers
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建 OpenCode Zen Provider 客户端失败");
            throw;
        }
    }

    private ILlmClient? InvalidateClient()
    {
        var oldClient = _client;
        _client = null;
        return oldClient;
    }

    private async ValueTask DisposeClientAsync(ILlmClient? client)
    {
        if (client is null)
            return;

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
            _logger.LogWarning(ex, "释放 OpenCode Zen Provider 客户端失败");
        }
    }

    private static ModelConfig ToModelConfig(OpenCodeZenModel model)
        => new()
        {
            Id = model.Id,
            Name = model.Name,
            Provider = "opencode-zen",
            Types = [ModelType.Text],
            Modalities = new ModelModalities
            {
                Input = model.SupportsImage ? ["text", "image"] : ["text"],
                Output = ["text"]
            },
            Limit = new ModelLimits { Context = model.Context, Output = model.Output },
            Pricing = model.IsFree
                ? new ModelPricing { Input = 0, Output = 0 }
                : model.InputPrice is null || model.OutputPrice is null
                    ? null
                    : new ModelPricing { Input = model.InputPrice.Value, Output = model.OutputPrice.Value },
            Metadata = model.IsFree
                ? new Dictionary<string, object?> { [ModelMetadataKeys.IsFree] = true }
                : null
        };

    private static string? GetString(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => raw.ToString()
        };
    }
}
