using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Acp.Transport;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Acp;

/// <summary>
/// ACP 插件入口。
/// <para>
/// 服务注册与运行时初始化由 <see cref="AcpServiceCollectionExtensions.AddSeeingAcp"/> 负责；
/// 本扩展仅提供 <see cref="IExtension"/> 生命周期（工具导出、连接清理）。
/// </para>
/// </summary>
public sealed class AcpExtension : IExtension
{
    /// <inheritdoc />
    public string? Id => "seeing.agent.acp";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public string Name => "Seeing.Agent ACP";

    /// <inheritdoc />
    public string Description => "ACP 双模式集成（Passthrough 透传 + acp 工具委派）";

    /// <inheritdoc />
    public string Target => "server";

    private AcpConnectionManager? _connectionManager;
    private AcpTool? _acpTool;
    private AcpStatusTool? _acpStatusTool;
    private ILogger? _logger;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services) => services.AddSeeingAcp();

    /// <inheritdoc />
    public Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
    {
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        _logger = loggerFactory.CreateLogger<AcpExtension>();

        var options = context.Services.GetRequiredService<IOptions<SeeingAgentOptions>>().Value;
        _connectionManager = context.Services.GetService<AcpConnectionManager>();
        _acpTool = context.Services.GetService<AcpTool>();
        _acpStatusTool = context.Services.GetService<AcpStatusTool>();

        if (!options.Acp.Enabled)
        {
            _logger.LogInformation("{Name} loaded; ACP is disabled in configuration", Name);
            return Task.CompletedTask;
        }

        if (_connectionManager == null || _acpTool == null)
        {
            _logger.LogWarning(
                "{Name} plugin loaded without AddSeeingAcp() DI integration. " +
                "Call services.AddSeeingAcp() during host startup.",
                Name);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "{Name} extension active ({BackendCount} backend(s)); " +
            "agent/hook/router registration is handled by AddSeeingAcp hosted services",
            Name,
            options.Acp.Backends.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<ITool> GetTools()
    {
        if (_acpTool != null)
            yield return _acpTool;
        if (_acpStatusTool != null)
            yield return _acpStatusTool;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _logger?.LogInformation("Stopping {Name}", Name);

        if (_connectionManager != null)
            await _connectionManager.StopAllAsync().ConfigureAwait(false);
    }
}
