using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// AgentDefinition 合并扩展方法
    /// </summary>
    public static class AgentDefinitionExtensions
    {
        /// <summary>
        /// 将 AgentConfigFile 合并到 AgentDefinition
        /// </summary>
        public static AgentDefinition Merge(
            AgentDefinition baseDef,
            AgentConfigFile? overrideConfig)
        {
            if (overrideConfig == null)
                return baseDef;

            return new AgentDefinition
            {
                Name = baseDef.Name,
                Description = overrideConfig.Description ?? baseDef.Description,
                Mode = ParseMode(overrideConfig.Mode) ?? baseDef.Mode,
                Category = overrideConfig.Category ?? baseDef.Category,
                SystemPrompt = overrideConfig.SystemPrompt ?? baseDef.SystemPrompt,
                Model = MergeModelReference(baseDef.Model, overrideConfig.Provider, overrideConfig.Model),
                MaxSteps = overrideConfig.MaxSteps ?? baseDef.MaxSteps,
                AllowedTools = overrideConfig.AllowedTools ?? baseDef.AllowedTools,
                DeniedTools = overrideConfig.DeniedTools ?? baseDef.DeniedTools,
                Temperature = overrideConfig.Temperature ?? baseDef.Temperature,
                TopP = overrideConfig.TopP ?? baseDef.TopP,
                MaxTokens = overrideConfig.MaxTokens ?? baseDef.MaxTokens,
                IsHidden = overrideConfig.IsHidden ?? baseDef.IsHidden,
                Disabled = overrideConfig.Disabled ?? baseDef.Disabled,
                PermissionRules = overrideConfig.PermissionRules ?? baseDef.PermissionRules,
                PermissionDefaultEffect = ParsePermissionEffect(overrideConfig.PermissionDefaultEffect) ?? baseDef.PermissionDefaultEffect,
                AcpBackend = overrideConfig.AcpBackend ?? baseDef.AcpBackend
            };
        }

        private static AgentMode? ParseMode(string? mode)
        {
            if (string.IsNullOrEmpty(mode))
                return null;
            return Enum.TryParse<AgentMode>(mode, ignoreCase: true, out var result)
                ? result
                : null;
        }

        private static PermissionEffect? ParsePermissionEffect(string? effect)
        {
            if (string.IsNullOrEmpty(effect))
                return null;
            return Enum.TryParse<PermissionEffect>(effect, ignoreCase: true, out var result)
                ? result
                : null;
        }

        private static ModelReference? MergeModelReference(
            ModelReference? baseRef,
            string? provider,
            string? model)
        {
            if (!string.IsNullOrEmpty(model))
            {
                return new ModelReference
                {
                    ProviderId = provider ?? baseRef?.ProviderId ?? string.Empty,
                    ModelId = model
                };
            }
            return baseRef;
        }

        private static IReadOnlyList<PermissionRuleEntry> MergePermissionRules(
            IReadOnlyList<PermissionRuleEntry> baseRules,
            List<PermissionRuleEntry>? additionalRules)
        {
            if (additionalRules == null || additionalRules.Count == 0)
                return baseRules;

            var merged = new List<PermissionRuleEntry>(baseRules);
            merged.AddRange(additionalRules);
            return merged.AsReadOnly();
        }
    }
}
