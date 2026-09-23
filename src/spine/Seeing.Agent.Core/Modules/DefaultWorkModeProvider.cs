using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

namespace Seeing.Agent.Core.Modules;

/// <summary>
/// 从 seeing.json <c>Scenario</c> 与 Host Shape <see cref="ProcessSettlementOptions.HostDefaultScenario"/> 解析进程默认工作模式。
/// </summary>
public sealed class DefaultWorkModeProvider : IDefaultWorkModeProvider
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly ProcessSettlementOptions? _settlementOptions;

    /// <summary>初始化默认工作模式提供者，注入配置监视器与进程结算选项。</summary>
    public DefaultWorkModeProvider(
        IOptionsMonitor<SeeingAgentOptions> options,
        ProcessSettlementOptions? settlementOptions = null)
    {
        _options = options;
        _settlementOptions = settlementOptions;
    }

    /// <inheritdoc />
    public string? GetDefaultScenario()
    {
        var configured = _options.CurrentValue.Scenario;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        var hostDefault = _settlementOptions?.HostDefaultScenario;
        return string.IsNullOrWhiteSpace(hostDefault) ? null : hostDefault.Trim();
    }
}
