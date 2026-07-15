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
                AllowedTools = overrideConfig.AllowedTools?.AsReadOnly() ?? baseDef.AllowedTools,
                DeniedTools = overrideConfig.DeniedTools?.AsReadOnly() ?? baseDef.DeniedTools,
                Temperature = overrideConfig.Temperature ?? baseDef.Temperature,
                TopP = overrideConfig.TopP ?? baseDef.TopP,
                MaxTokens = overrideConfig.MaxTokens ?? baseDef.MaxTokens,
                IsHidden = overrideConfig.IsHidden ?? baseDef.IsHidden,
                PermissionRules = overrideConfig.PermissionRules?.AsReadOnly() ?? baseDef.PermissionRules,
                PermissionDefaultEffect = ParsePermissionEffect(overrideConfig.PermissionDefaultEffect) ?? baseDef.PermissionDefaultEffect,
                Runtime = ParseRuntime(overrideConfig.Runtime) ?? baseDef.Runtime,
                AcpBackend = overrideConfig.AcpBackend ?? baseDef.AcpBackend,
                Variants = overrideConfig.Variants != null
                    ? new Dictionary<string, AgentVariant>(overrideConfig.Variants)
                    : baseDef.Variants
            };
        }

        /// <summary>
        /// 应用 seeing.json AgentConfig 到 AgentDefinition
        /// </summary>
        public static AgentDefinition ApplyJsonConfig(
            AgentDefinition baseDef,
            AgentConfig jsonConfig)
        {
            return new AgentDefinition
            {
                Name = baseDef.Name,
                Description = jsonConfig.Description ?? baseDef.Description,
                Mode = jsonConfig.Mode ?? baseDef.Mode,
                Category = baseDef.Category,
                SystemPrompt = jsonConfig.SystemPrompt ?? baseDef.SystemPrompt,
                Model = jsonConfig.Model != null
                    ? new ModelReference
                    {
                        ProviderId = jsonConfig.Provider ?? string.Empty,
                        ModelId = jsonConfig.Model
                    }
                    : baseDef.Model,
                MaxSteps = jsonConfig.MaxSteps ?? baseDef.MaxSteps,
                AllowedTools = baseDef.AllowedTools,
                DeniedTools = baseDef.DeniedTools,
                Temperature = jsonConfig.Temperature ?? baseDef.Temperature,
                TopP = jsonConfig.TopP ?? baseDef.TopP,
                MaxTokens = jsonConfig.MaxTokens ?? baseDef.MaxTokens,
                IsHidden = jsonConfig.IsHidden ?? baseDef.IsHidden,
                PermissionRules = MergePermissionRules(baseDef.PermissionRules, jsonConfig.PermissionRules),
                PermissionDefaultEffect = baseDef.PermissionDefaultEffect,
                Runtime = jsonConfig.Runtime ?? baseDef.Runtime,
                AcpBackend = jsonConfig.AcpBackend ?? baseDef.AcpBackend,
                Variants = baseDef.Variants
            };
        }

        /// <summary>
        /// 应用 Provider/Model 变体
        /// </summary>
        public static AgentDefinition ApplyVariant(
            AgentDefinition baseDef,
            string? provider,
            string? model)
        {
            if (baseDef.Variants == null || provider == null)
                return baseDef;

            // 尝试精确匹配 provider.model
            if (model != null)
            {
                var exactKey = $"{provider}.{model}";
                if (baseDef.Variants.TryGetValue(exactKey, out var exactVariant))
                {
                    return ApplyVariantToDefinition(baseDef, exactVariant);
                }
            }

            // 尝试 Provider 级别匹配
            if (baseDef.Variants.TryGetValue(provider, out var providerVariant))
            {
                return ApplyVariantToDefinition(baseDef, providerVariant);
            }

            return baseDef;
        }

        private static AgentDefinition ApplyVariantToDefinition(
            AgentDefinition baseDef,
            AgentVariant variant)
        {
            // 处理 SystemPrompt 特殊合并
            var systemPrompt = baseDef.SystemPrompt ?? string.Empty;
            
            if (!string.IsNullOrEmpty(variant.SystemPromptPrepend))
                systemPrompt = variant.SystemPromptPrepend + "\n\n" + systemPrompt;
            
            if (!string.IsNullOrEmpty(variant.SystemPromptAppend))
                systemPrompt = systemPrompt + "\n\n" + variant.SystemPromptAppend;
            
            // 完全覆盖优先级最高
            if (!string.IsNullOrEmpty(variant.SystemPrompt))
                systemPrompt = variant.SystemPrompt;

            return new AgentDefinition
            {
                Name = baseDef.Name,
                Description = baseDef.Description,
                Mode = baseDef.Mode,
                Category = baseDef.Category,
                SystemPrompt = systemPrompt,
                Model = variant.Model != null
                    ? new ModelReference
                    {
                        ProviderId = baseDef.Model?.ProviderId ?? string.Empty,
                        ModelId = variant.Model
                    }
                    : baseDef.Model,
                MaxSteps = variant.MaxSteps ?? baseDef.MaxSteps,
                AllowedTools = variant.AllowedTools?.AsReadOnly() ?? baseDef.AllowedTools,
                DeniedTools = variant.DeniedTools?.AsReadOnly() ?? baseDef.DeniedTools,
                Temperature = variant.Temperature ?? baseDef.Temperature,
                TopP = variant.TopP ?? baseDef.TopP,
                MaxTokens = variant.MaxTokens ?? baseDef.MaxTokens,
                IsHidden = baseDef.IsHidden,
                PermissionRules = variant.PermissionRules?.AsReadOnly() ?? baseDef.PermissionRules,
                PermissionDefaultEffect = baseDef.PermissionDefaultEffect,
                Runtime = baseDef.Runtime,
                AcpBackend = baseDef.AcpBackend,
                Variants = baseDef.Variants
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

        private static AgentRuntime? ParseRuntime(string? runtime)
        {
            if (string.IsNullOrEmpty(runtime))
                return null;
            return Enum.TryParse<AgentRuntime>(runtime, ignoreCase: true, out var result)
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
