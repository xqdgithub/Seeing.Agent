using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using System.Text.Json;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 将已弃用的 Gateway 节 Agent/Model 默认项迁移到根级 <see cref="SeeingAgentOptions"/>。
/// </summary>
public static class LegacyGatewayConfigMigrator
{
    /// <summary>将已弃用的 Gateway 默认 Agent/Model 迁移到根级配置项。</summary>
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

        // 旧 Gateway.DefaultModelId 迁移到按能力默认模型的 chat 槽位（决议 11）。
        // 值必须带 provider/ 前缀（决议 21）：历史值多为裸 ID，故迁移时显式提示核对。
        if (gateway.TryGetProperty("DefaultModelId", out var legacyModel) &&
            legacyModel.ValueKind == JsonValueKind.String)
        {
            var legacyModelId = legacyModel.GetString();
            var chatKey = LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.Chat);
            if (!string.IsNullOrWhiteSpace(legacyModelId) &&
                !options.DefaultModels.ContainsKey(chatKey))
            {
                options.DefaultModels[chatKey] = legacyModelId;
                logger?.LogWarning(
                    "Gateway.DefaultModelId 已弃用，已迁移到 SeeingAgent.DefaultModels[{ChatKey}]={ModelId}。" +
                    "请确认其为 provider/模型 形式并更新 .seeing/seeing.json",
                    chatKey,
                    legacyModelId);
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
