using Acp.Messages;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Acp.Client;

namespace Seeing.Agent.Acp.Session;

/// <summary>
/// 将 Gateway / 会话层解析的 ACP mode、model 应用到 spawn client（session/set_config_option）。
/// </summary>
public sealed class AcpSessionConfigApplier
{
    private readonly ILogger<AcpSessionConfigApplier> _logger;

    public AcpSessionConfigApplier(ILogger<AcpSessionConfigApplier> logger)
    {
        _logger = logger;
    }

    public async Task ApplyAsync(
        IAcpSessionConfigClient client,
        string acpSessionId,
        IReadOnlyList<SessionConfigOption>? configOptions,
        string? desiredModeId,
        string? desiredModelId,
        CancellationToken cancellationToken = default)
    {
        var options = configOptions ?? Array.Empty<SessionConfigOption>();

        if (options.Count > 0)
        {
            await ApplyViaConfigOptionsAsync(client, acpSessionId, options, desiredModeId, desiredModelId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await ApplyViaLegacyAsync(client, acpSessionId, desiredModeId, desiredModelId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyViaConfigOptionsAsync(
        IAcpSessionConfigClient client,
        string acpSessionId,
        IReadOnlyList<SessionConfigOption> options,
        string? desiredModeId,
        string? desiredModelId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(desiredModeId))
        {
            var modeOption = SessionConfigOptionsHelper.FindByCategory(options, SessionConfigOptionCategories.Mode)
                ?? SessionConfigOptionsHelper.FindByKey(options, SessionConfigOptionCategories.Mode);
            if (modeOption != null)
                await SetConfigOptionIfNeededAsync(client, acpSessionId, modeOption, desiredModeId, cancellationToken)
                    .ConfigureAwait(false);
            else
                _logger.LogDebug("ACP backend has no mode config option; skipping mode={ModeId}", desiredModeId);
        }

        if (!string.IsNullOrWhiteSpace(desiredModelId))
        {
            var modelOption = SessionConfigOptionsHelper.FindByCategory(options, SessionConfigOptionCategories.Model)
                ?? SessionConfigOptionsHelper.FindByKey(options, SessionConfigOptionCategories.Model);
            if (modelOption != null)
                await SetConfigOptionIfNeededAsync(client, acpSessionId, modelOption, desiredModelId, cancellationToken)
                    .ConfigureAwait(false);
            else
                _logger.LogDebug("ACP backend has no model config option; skipping model={ModelId}", desiredModelId);
        }
    }

    private async Task SetConfigOptionIfNeededAsync(
        IAcpSessionConfigClient client,
        string acpSessionId,
        SessionConfigOption option,
        string configuredValue,
        CancellationToken cancellationToken)
    {
        var resolved = SessionConfigOptionsHelper.ResolveValue(option, configuredValue);
        if (resolved == null)
        {
            _logger.LogDebug(
                "ACP config option {OptionId} already at {CurrentValue}; skip set",
                option.Id,
                option.CurrentValue);
            return;
        }

        _logger.LogInformation(
            "ACP session/set_config_option session={AcpSessionId} option={OptionId} value={Value}",
            acpSessionId,
            option.Id,
            resolved);

        await client.SetConfigOptionAsync(acpSessionId, option.Id, resolved, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ApplyViaLegacyAsync(
        IAcpSessionConfigClient client,
        string acpSessionId,
        string? desiredModeId,
        string? desiredModelId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(desiredModeId))
        {
            _logger.LogInformation(
                "ACP session/set_mode (legacy) session={AcpSessionId} mode={ModeId}",
                acpSessionId,
                desiredModeId);
            await client.SetModeAsync(acpSessionId, desiredModeId.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(desiredModelId))
        {
            _logger.LogInformation(
                "ACP session/set_model (legacy) session={AcpSessionId} model={ModelId}",
                acpSessionId,
                desiredModelId);
            await client.SetModelAsync(acpSessionId, desiredModelId.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
