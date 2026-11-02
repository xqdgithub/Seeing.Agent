using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Reflection.Configuration;

/// <summary>
/// 从 <see cref="IConfigSectionStore"/> 提供 <see cref="ReflectionOptions"/>
/// （<see cref="IOptions{T}"/> / <see cref="IOptionsMonitor{T}"/>）。
/// <para>
/// <b>必须走 <see cref="IConfigSectionStore"/> 而不是 <see cref="Microsoft.Extensions.Configuration.IConfiguration"/></b>：
/// 本仓的能力包配置统一存放在 <c>~/.seeing/seeing.json</c> 与 <c>.seeing/seeing.json</c>，
/// 由 <c>UnifiedConfigManager</c> 解析，**不进入** Microsoft 的 <c>IConfiguration</c> 管道。
/// 若从 <c>IConfiguration.GetSection("SeeingAgent:Reflection")</c> 读，用户在 seeing.json 里
/// 写的任何配置都不会生效——能力会永久停在默认值（<c>Enabled = false</c>）而毫无提示。
/// </para>
/// <para>
/// 配置变更经 <see cref="IConfigSectionStore.ConfigChanged"/> 事件重新绑定，
/// 与 <see cref="Seeing.Agent.Memory.Configuration.MemoryOptionsProvider"/> 同构。
/// </para>
/// </summary>
public sealed class ReflectionOptionsProvider : IOptionsMonitor<ReflectionOptions>, IDisposable
{
    /// <summary>配置节键名（seeing.json 顶层键）。</summary>
    public const string SectionName = "Reflection";

    /// <summary>配置节存储文件名。</summary>
    public const string FileName = "reflection.json";

    /// <summary>配置节元信息：登记后 WebUI 配置页可见、统一配置校验覆盖该节。</summary>
    public static ConfigSectionMeta Meta { get; } = new(
        SectionName,
        FileName,
        ConfigScope.Both,
        typeof(ReflectionOptions));

    private readonly IConfigSectionStore? _store;
    private readonly ILogger<ReflectionOptionsProvider>? _logger;
    private ReflectionOptions _current;
    private event Action<ReflectionOptions, string?>? _listeners;

    /// <summary>DI 构造：从配置节存储读取；未注入存储时退回默认值（便于独立装配与测试）。</summary>
    public ReflectionOptionsProvider(
        IConfigSectionStore? store = null,
        ILogger<ReflectionOptionsProvider>? logger = null)
    {
        _store = store;
        _logger = logger;
        _current = Load();

        if (store is not null)
            store.ConfigChanged += OnConfigChanged;
    }

    /// <inheritdoc />
    public ReflectionOptions CurrentValue => _current;

    /// <inheritdoc />
    public ReflectionOptions Get(string? name) => _current;

    /// <summary>无参取值（供内部与测试使用）。</summary>
    public ReflectionOptions Get() => _current;

    /// <inheritdoc />
    public IDisposable OnChange(Action<ReflectionOptions, string?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listeners += listener;
        return new ChangeSubscription(() => _listeners -= listener);
    }

    /// <summary>重新读取配置节并通知监听者（配置热重载入口）。</summary>
    public void Reload()
    {
        _current = Load();
        _logger?.LogDebug(
            "Reflection options reloaded (Enabled={Enabled}, Mode={Mode}, TurnNudgeInterval={TurnNudgeInterval})",
            _current.Enabled,
            _current.Mode,
            _current.TurnNudgeInterval);
        _listeners?.Invoke(_current, Options.DefaultName);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_store is not null)
            _store.ConfigChanged -= OnConfigChanged;
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        // 只关心本节：其它能力包改配置不应触发复盘选项重建。
        // 空数组表示全量重载（初始化 / 外部文件变更），此时必须重建。
        if (!e.ContainsSection(SectionName))
            return;

        Reload();
    }

    private ReflectionOptions Load()
    {
        if (_store is null)
            return new ReflectionOptions();

        try
        {
            return _store.GetSection<ReflectionOptions>(SectionName);
        }
        catch (Exception ex)
        {
            // 配置损坏不应让宿主起不来：退回默认值（Enabled=false 即"不工作"）并留痕。
            _logger?.LogWarning(ex, "读取复盘配置失败，已退回默认值（Enabled=false）");
            return new ReflectionOptions();
        }
    }

    private sealed class ChangeSubscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

/// <summary><see cref="IOptions{T}"/> 适配（与 monitor 共用同一快照）。</summary>
public sealed class ReflectionOptionsAccessor : IOptions<ReflectionOptions>
{
    private readonly ReflectionOptionsProvider _provider;

    public ReflectionOptionsAccessor(ReflectionOptionsProvider provider) => _provider = provider;

    public ReflectionOptions Value => _provider.CurrentValue;
}
