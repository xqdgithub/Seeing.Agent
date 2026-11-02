using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Seeing.Agent.Reflection.Configuration;

/// <summary>
/// 从 <see cref="IConfiguration"/>（配置节 <c>SeeingAgent:Reflection</c>）提供
/// <see cref="ReflectionOptions"/>（<see cref="IOptions{T}"/> / <see cref="IOptionsMonitor{T}"/>）。
/// <para>
/// 与 Memory 模块的 provider 同构：自身即 monitor，配置变更时重建快照并通知监听者，
/// 未注入 <see cref="IConfiguration"/> 时退回默认值（能力包可独立装配、测试友好）。
/// </para>
/// </summary>
public sealed class ReflectionOptionsProvider : IOptionsMonitor<ReflectionOptions>, IDisposable
{
    /// <summary>配置节键名（位于 <c>SeeingAgent</c> 之下）。</summary>
    public const string SectionName = "Reflection";

    /// <summary>完整配置路径。</summary>
    public const string ConfigurationPath = "SeeingAgent:" + SectionName;

    private readonly IConfiguration? _configuration;
    private readonly ILogger<ReflectionOptionsProvider>? _logger;
    private readonly IDisposable? _reloadRegistration;
    private ReflectionOptions _current;
    private event Action<ReflectionOptions, string?>? _listeners;

    /// <summary>DI 构造：绑定 <see cref="IConfiguration"/> 的 <c>SeeingAgent:Reflection</c> 节。</summary>
    public ReflectionOptionsProvider(
        IConfiguration? configuration = null,
        ILogger<ReflectionOptionsProvider>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
        _current = Bind(configuration);
        _reloadRegistration = configuration is null
            ? null
            : ChangeToken.OnChange(configuration.GetReloadToken, Reload);
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

    /// <summary>重新绑定配置节并通知监听者（配置热重载入口）。</summary>
    public void Reload()
    {
        _current = Bind(_configuration);
        _logger?.LogDebug(
            "Reflection options reloaded (Enabled={Enabled}, Mode={Mode}, TurnNudgeInterval={TurnNudgeInterval})",
            _current.Enabled,
            _current.Mode,
            _current.TurnNudgeInterval);
        _listeners?.Invoke(_current, Options.DefaultName);
    }

    /// <inheritdoc />
    public void Dispose() => _reloadRegistration?.Dispose();

    private static ReflectionOptions Bind(IConfiguration? configuration)
    {
        if (configuration is null)
            return new ReflectionOptions();

        var section = configuration.GetSection(ConfigurationPath);
        if (!section.Exists())
            return new ReflectionOptions();

        var options = new ReflectionOptions();
        section.Bind(options);
        return options;
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
