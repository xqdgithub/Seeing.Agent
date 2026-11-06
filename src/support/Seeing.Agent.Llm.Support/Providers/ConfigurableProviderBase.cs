using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.ConfigSchema;
using System.Text.Json;

namespace Seeing.Agent.Llm.Support.Providers;

/// <summary>
/// 插件 Provider 的公共骨架：能力解析（作者默认值 + 配置覆盖）、能力束生命周期、释放。
/// <para><b>它不决定协议，也不决定客户端怎么造</b>：子类给出 <see cref="Protocol"/> 与
/// <see cref="CreateClient"/>，客户端可以是框架工厂产物，也完全可以自己 new——
/// 契约上 Provider 只需能从 <see cref="GetClient"/> 交出 <see cref="ILlmCapabilityClient"/>。</para>
/// <para><b>能力为什么由子类声明而不是工厂推导</b>：同一个 OpenAI 兼容工厂能造 chat/embedding，
/// 不代表某个具体端点实现了 <c>/v1/embeddings</c>。只有插件作者知道自家端点支持什么，
/// 所以默认值写在代码里、配置可覆盖。装完即可用，无需先改配置。</para>
/// </summary>
/// <typeparam name="TOptions">插件自己的配置形状（可用 <see cref="OptionsSchemaBuilder"/> 生成表单）。</typeparam>
public abstract class ConfigurableProviderBase<TOptions> : ILlmProvider, IConfigurableLlmProvider, IAsyncDisposable
    where TOptions : class, new()
{
    private readonly ILogger _logger;
    private readonly ILlmClientFactoryResolver _factories;
    private readonly object _gate = new();

    private CapabilityClientBundle? _clients;
    private TOptions _options = new();
    private IReadOnlyList<string>? _configuredCapabilities;
    private LlmCapabilityKind _capabilities;
    private int _disposed;

    /// <summary>注入客户端工厂解析器与日志。</summary>
    protected ConfigurableProviderBase(ILlmClientFactoryResolver factories, ILogger logger)
    {
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capabilities = DefaultCapabilities;

        // 构造期就建好能力束（内部全是 Lazy）：GetClient 的语义保持「按需创建」，
        // 不要求宿主先调 Warmup 才能取客户端。Warmup/保存配置会整体替换这个束。
        _clients = BuildBundleLocked();
    }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string? Name { get; }

    /// <inheritdoc />
    public abstract string Protocol { get; }

    /// <summary>
    /// 作者声明的默认能力集：配置未覆盖时生效。装完即可用，不必先改配置。
    /// </summary>
    protected abstract LlmCapabilityKind DefaultCapabilities { get; }

    /// <inheritdoc />
    public LlmCapabilityKind Capabilities
    {
        get
        {
            lock (_gate)
            {
                return _capabilities;
            }
        }
    }

    /// <inheritdoc />
    public virtual int MaxRetries => 3;

    /// <summary>按能力创建客户端；不支持该组合返回 <c>null</c>。子类可自行 new，也可经 <see cref="_factories"/> 取框架工厂。</summary>
    protected abstract ILlmCapabilityClient? CreateClient(ProviderConfig config, LlmCapabilityKind capability);

    /// <summary>用当前配置构造传给工厂的 <see cref="ProviderConfig"/>（端点、密钥、自定义头）。</summary>
    protected abstract ProviderConfig BuildProviderConfig();

    /// <summary>读取插件自己的配置（首次 Warmup 与每次保存后调用）。</summary>
    protected abstract Task<TOptions> LoadOptionsAsync(CancellationToken cancellationToken);

    /// <summary>把配置表单的键值写回插件自己的存储。</summary>
    protected abstract Task SaveOptionsAsync(TOptions options, CancellationToken cancellationToken);

    /// <summary>配置表单的默认导出（子类可覆盖以补自定义字段）。</summary>
    protected virtual async Task<IReadOnlyDictionary<string, object?>> ExportOptionsAsync(
        TOptions options,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return new Dictionary<string, object?>();
    }

    /// <summary>
    /// 插件自己的配置节 → 能力覆盖。返回 <c>null</c> 表示「用作者默认值」。
    /// 默认实现读 <c>Capabilities</c> 属性（子类 Options 常带该字段）。
    /// </summary>
    protected virtual IReadOnlyList<string>? ReadCapabilityOverride(TOptions options) => null;

    /// <summary>
    /// 装饰器（重试等）由工厂在返回前应用（spec §7.3）；本方法只做「取工厂 + 传配置」。
    /// 子类若要完全自建客户端，可覆盖 <see cref="CreateClient"/> 而不调用本方法。
    /// </summary>
    protected ILlmCapabilityClient? CreateClientViaFactory(ProviderConfig config, LlmCapabilityKind capability)
        => _factories.Require(Protocol).Create(config, capability);

    /// <summary>
    /// 应用一份新配置：重建能力集与能力束，返回需要释放的旧束。
    /// <para>调用方负责在锁外释放旧束（异步释放不能在锁内做）。</para>
    /// </summary>
    protected CapabilityClientBundle? ApplyOptions(TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _options = options;
            _configuredCapabilities = ReadCapabilityOverride(options);
            _capabilities = ResolveCapabilitiesLocked(_configuredCapabilities);

            var previous = _clients;
            _clients = BuildBundleLocked();
            return previous;
        }
    }

    /// <summary>当前配置快照（子类读自己的字段）。</summary>
    protected TOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }
    }

    /// <summary>推导当前应供出的能力；配置覆盖优先，未识别名字记警告并忽略。</summary>
    private LlmCapabilityKind ResolveCapabilitiesLocked(IReadOnlyList<string>? configured)
    {
        if (configured is null)
            return DefaultCapabilities;

        var result = LlmCapabilityKind.None;
        foreach (var name in configured)
        {
            if (LlmCapabilityMap.TryParseConfigKey(name, out var kind))
            {
                result |= kind;
                continue;
            }

            // 不静默当成有效能力，但也绝不因此让 Provider 整体不可用：记警告即可。
            _logger.LogWarning(
                "Provider {ProviderId} 的配置里出现未识别的能力键 '{Capability}'，已忽略。合法值：{Valid}",
                Id,
                name,
                string.Join(", ", LlmCapabilityMap.ConfigKeys));
        }

        // 显式配置了就严格按配置走：即使全都不认识也视为「不供出任何能力」，
        // 绝不悄悄回落默认值——否则用户以为已关掉的能力仍然在生效。
        return result;
    }

    private CapabilityClientBundle BuildBundleLocked()
        => new(
            _capabilities,
            capability => CreateClient(BuildProviderConfig(), capability),
            onCreateFailed: ex => _logger.LogError(
                ex, "创建 Provider 客户端失败: {ProviderId}", Id),
            onDisposeFailed: ex => _logger.LogWarning(
                ex, "释放 Provider 客户端失败: {ProviderId}", Id));

    /// <inheritdoc />
    public ILlmCapabilityClient? GetClient(LlmCapabilityKind capability)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _clients?.Get(capability);
        }
    }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<ModelConfig>> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual async Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        CancellationToken cancellationToken = default)
    {
        // 未配置凭据时直接 false，且**不得**为它创建客户端（创建会触发工厂与网络客户端构造）。
        if (!HasUsableCredentials)
            return false;

        var client = GetClient(capability);
        if (client is null)
            return false;

        return await client.TestConnectionAsync(modelId, capability, call: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 是否具备可用凭据：为 <c>false</c> 时连接测试直接失败且不创建客户端。
    /// 子类按自己的配置实现（例如 API Key 为空即不可用）。
    /// </summary>
    protected virtual bool HasUsableCredentials => true;

    /// <inheritdoc />
    public virtual IReadOnlyList<ConfigFieldSchema>? GetConfigSchema()
        => WithCapabilityChoices(OptionsSchemaBuilder.FromType(typeof(TOptions)), DefaultCapabilities);

    /// <summary>
    /// 给能力清单字段补上合法候选值，并说明「留空」的含义。
    /// <para><b>为什么在这一层做</b>：候选值来自 <see cref="LlmCapabilityMap.ConfigKeys"/>，
    /// 是 LLM 领域知识，<c>Seeing.ConfigSchema</c> 原语层不该知道。
    /// 每个 Provider 插件都继承本基类，因此在这里补一次即可覆盖全部插件——</para>
    /// <para><b>为什么还要占位文案</b>：能力清单留空<em>不是</em>「没有能力」，而是「用默认能力」。
    /// 用户打开一个未覆盖的 Provider 时看到空下拉，很容易读成「这个 Provider 什么都没声明」；
    /// 占位文案是他在这个控件上唯一能看到的解释。</para>
    /// <para>只补「名为 Capabilities 且尚无候选」的字段；插件可自行提供别的候选。</para>
    /// </summary>
    /// <param name="fields">反射生成的字段。</param>
    /// <param name="defaults">留空时生效的默认能力集（用于生成占位文案）。</param>
    protected static IReadOnlyList<ConfigFieldSchema>? WithCapabilityChoices(
        IReadOnlyList<ConfigFieldSchema>? fields,
        LlmCapabilityKind defaults = LlmCapabilityKind.None)
    {
        if (fields is null)
            return null;

        var placeholder = defaults == LlmCapabilityKind.None
            ? "留空 = 使用默认能力"
            : $"留空 = 使用默认能力（{DescribeCapabilities(defaults)}）";

        List<ConfigFieldSchema>? patched = null;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (field.Type != ConfigFieldType.StringList ||
                field.Choices is { Count: > 0 } ||
                !string.Equals(field.Name, "Capabilities", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            patched ??= [.. fields];
            patched[i] = field with
            {
                Choices = LlmCapabilityMap.ConfigKeys,
                Placeholder = field.Placeholder ?? placeholder,
            };
        }

        return patched ?? fields;
    }

    /// <summary>能力位集 → 配置键串（<c>chat, embedding</c>），用于界面文案。</summary>
    private static string DescribeCapabilities(LlmCapabilityKind mask) =>
        string.Join(", ", LlmCapabilityMap.SingleFlags(mask).Select(LlmCapabilityMap.ToConfigKey));

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, object?>> LoadConfigAsync(
        CancellationToken cancellationToken = default)
    {
        var options = await LoadOptionsAsync(cancellationToken).ConfigureAwait(false);
        return await ExportOptionsAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 配置写盘后调用的钩子。默认空操作；插件可在此把自身重新注册到
    /// <see cref="IProviderRegistry"/>，让 <c>ProviderInfo</c>（能力投影）反映新配置。
    /// </summary>
    protected virtual Task OnOptionsSavedAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async Task SaveConfigAsync(
        IReadOnlyDictionary<string, object?> values,
        ConfigLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        _ = level;

        var options = await LoadOptionsAsync(cancellationToken).ConfigureAwait(false);
        ApplyValues(options, values);
        await SaveOptionsAsync(options, cancellationToken).ConfigureAwait(false);

        var previous = ApplyOptions(options);
        await DisposeBundleAsync(previous).ConfigureAwait(false);

        // 能力集可能已变（配置覆盖了 capabilities），ProviderInfo 的能力投影需要同步刷新。
        await OnOptionsSavedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>首次装载配置（宿主 Activate 时调用）。</summary>
    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        var options = await LoadOptionsAsync(cancellationToken).ConfigureAwait(false);
        var previous = ApplyOptions(options);
        await DisposeBundleAsync(previous).ConfigureAwait(false);
    }

    /// <summary>把配置表单的键值写回 <typeparamref name="TOptions"/>；默认按同名属性反射赋值。</summary>
    protected virtual void ApplyValues(TOptions options, IReadOnlyDictionary<string, object?> values)
    {
        var properties = typeof(TOptions).GetProperties()
            .Where(p => p.CanWrite)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, raw) in values)
        {
            if (!properties.TryGetValue(key, out var property))
                continue;

            if (raw is null)
            {
                property.SetValue(options, null);
                continue;
            }

            try
            {
                if (property.PropertyType == typeof(List<string>))
                {
                    property.SetValue(options, ToStringList(raw));
                    continue;
                }

                if (property.PropertyType == typeof(string))
                {
                    property.SetValue(options, raw switch
                    {
                        string text => text,
                        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                        _ => raw.ToString(),
                    });
                    continue;
                }

                property.SetValue(options, Convert.ChangeType(raw, property.PropertyType));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException)
            {
                _logger.LogWarning(
                    ex,
                    "Provider {ProviderId} 的配置项 {Key} 无法转换为 {Type}，已保留原值",
                    Id,
                    key,
                    property.PropertyType.Name);
            }
        }
    }

    /// <summary>把表单值归一为字符串列表（接受 JSON 数组、字符串集合、逗号分隔文本）。</summary>
    protected static List<string>? ToStringList(object raw)
    {
        IEnumerable<string?> candidates = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } array
                => array.EnumerateArray().Select(x => x.ToString()),
            IEnumerable<string> strings => strings,
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Enumerable.Repeat<string?>(raw.ToString(), 1),
        };

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim())
            .ToList();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CapabilityClientBundle? clients;
        lock (_gate)
        {
            if (_disposed != 0)
                return;

            Volatile.Write(ref _disposed, 1);
            clients = _clients;
            _clients = null;
        }

        await DisposeBundleAsync(clients).ConfigureAwait(false);
        await OnDisposingAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 释放前钩子：子类在此退订事件、清理外部引用。默认空操作。
    /// </summary>
    protected virtual ValueTask OnDisposingAsync() => ValueTask.CompletedTask;

    private static async ValueTask DisposeBundleAsync(CapabilityClientBundle? bundle)
    {
        if (bundle is null)
            return;

        await bundle.DisposeAsync().ConfigureAwait(false);
    }
}
