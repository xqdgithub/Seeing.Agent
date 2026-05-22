using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Seeing.Agent.Core.Generation
{
    /// <summary>
    /// Agent 定义验证器
    /// </summary>
    public class AgentValidator
    {
        private static readonly Regex NamePattern = new(@"^[a-zA-Z][a-zA-Z0-9_\-\.]{1,63}$", RegexOptions.Compiled);
        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "admin", "root", "default", "config", "debug", "test"
        };

        /// <summary>验证 Agent 定义</summary>
        public AgentValidationResult Validate(AgentDefinition definition)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            // 名称验证
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                errors.Add("Agent name is required");
            }
            else if (!NamePattern.IsMatch(definition.Name))
            {
                errors.Add($"Agent name '{definition.Name}' is invalid. Must start with a letter, contain only alphanumeric, underscore, hyphen, or dot. Length: 2-64");
            }
            else if (ReservedNames.Contains(definition.Name))
            {
                errors.Add($"Agent name '{definition.Name}' is reserved");
            }

            // 系统提示词验证
            if (string.IsNullOrWhiteSpace(definition.SystemPrompt))
            {
                warnings.Add("System prompt is empty - agent may not behave as expected");
            }
            else if (definition.SystemPrompt.Length > 32000)
            {
                warnings.Add($"System prompt is very long ({definition.SystemPrompt.Length} chars) - may impact token usage");
            }

            // 工具列表验证
            var toolConflicts = definition.AllowedTools.Intersect(definition.DeniedTools).ToList();
            if (toolConflicts.Any())
            {
                errors.Add($"Tools appear in both allowed and denied lists: {string.Join(", ", toolConflicts)}");
            }

            // 迭代次数验证
            if (definition.MaxIterations <= 0)
            {
                errors.Add("MaxIterations must be positive");
            }
            else if (definition.MaxIterations > 100)
            {
                warnings.Add($"MaxIterations is very high ({definition.MaxIterations}) - may lead to excessive API usage");
            }

            // 超时验证
            if (definition.TimeoutSeconds <= 0)
            {
                errors.Add("TimeoutSeconds must be positive");
            }
            else if (definition.TimeoutSeconds > 3600)
            {
                warnings.Add($"TimeoutSeconds is very high ({definition.TimeoutSeconds}s) - agent may run for over an hour");
            }

            // 模型配置验证
            if (definition.ModelConfig != null)
            {
                ValidateModelConfig(definition.ModelConfig, errors, warnings);
            }

            // 标签验证
            var duplicateTags = definition.Tags.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicateTags.Any())
            {
                warnings.Add($"Duplicate tags: {string.Join(", ", duplicateTags)}");
            }

            return new AgentValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings
            };
        }

        private void ValidateModelConfig(ModelConfigOverride config, List<string> errors, List<string> warnings)
        {
            if (config.Temperature.HasValue && (config.Temperature.Value < 0 || config.Temperature.Value > 2))
            {
                errors.Add($"Temperature must be between 0 and 2, got {config.Temperature.Value}");
            }

            if (config.MaxTokens.HasValue && config.MaxTokens.Value <= 0)
            {
                errors.Add($"MaxTokens must be positive, got {config.MaxTokens.Value}");
            }

            if (config.TopP.HasValue && (config.TopP.Value < 0 || config.TopP.Value > 1))
            {
                errors.Add($"TopP must be between 0 and 1, got {config.TopP.Value}");
            }

            if (config.Temperature.HasValue && config.TopP.HasValue)
            {
                if (config.Temperature.Value > 0 && config.TopP.Value < 0.1)
                {
                    warnings.Add("Both Temperature and TopP are set - they may interact unexpectedly");
                }
            }
        }
    }

    /// <summary>验证结果</summary>
    public class AgentValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
