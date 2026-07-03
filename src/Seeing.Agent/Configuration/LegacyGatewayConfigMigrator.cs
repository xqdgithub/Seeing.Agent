using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 将已弃用的 Gateway 节 Agent/Model 默认项迁移到根级 <see cref="SeeingAgentOptions"/>。
/// </summary>
public static class LegacyGatewayConfigMigrator
{
    public static void Apply(JsonElement gateway, SeeingAgentOptions options, ILogger? logger = null)
    {
        if (gateway.TryGetProperty("DefaultAgentId", out var legacyAgent) &&
            legacyAgent.ValueKind == JsonValueKind.String)
        {
            var legacyAgentId = legacyAgent.GetString();
            if (!string.IsNullOrWhiteSpace(legacyAgentId) && string.IsNullOrWhiteSpace(options.DefaultAgent))
            {
                options.DefaultAgent = legacyAgentId;
                logger?.LogWarning(
                    "Gateway.DefaultAgentId 已弃用，已迁移到 SeeingAgent.DefaultAgent。请更新 .seeing/seeing.json");
            }
        }

        if (gateway.TryGetProperty("DefaultModelId", out var legacyModel) &&
            legacyModel.ValueKind == JsonValueKind.String)
        {
            var legacyModelId = legacyModel.GetString();
            if (!string.IsNullOrWhiteSpace(legacyModelId) && string.IsNullOrWhiteSpace(options.DefaultModel))
            {
                options.DefaultModel = legacyModelId;
                logger?.LogWarning(
                    "Gateway.DefaultModelId 已弃用，已迁移到 SeeingAgent.DefaultModel。请更新 .seeing/seeing.json");
            }
        }

        if (gateway.TryGetProperty("DefaultAcpBackend", out var legacyAcp) &&
            legacyAcp.ValueKind == JsonValueKind.String)
        {
            var backend = legacyAcp.GetString();
            if (!string.IsNullOrWhiteSpace(backend) && string.IsNullOrWhiteSpace(options.DefaultAgent))
            {
                options.DefaultAgent = "acp-" + backend;
                logger?.LogWarning(
                    "Gateway.DefaultAcpBackend 已弃用，已迁移为 DefaultAgent=acp-{Backend}。请更新 .seeing/seeing.json",
                    backend);
            }
        }
    }
}
