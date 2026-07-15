using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Configuration
{
    public class AgentDefinitionExtensionsTests
    {
        [Fact]
        public void Merge_WithNullOverride_ReturnsBase()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                MaxSteps = 10
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, (AgentConfigFile?)null);

            Assert.Equal("test", result.Name);
            Assert.Equal(10, result.MaxSteps);
        }

        [Fact]
        public void Merge_OverrideMaxSteps_ReturnsMerged()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                MaxSteps = 10
            };

            var overrideConfig = new AgentConfigFile
            {
                MaxSteps = 20
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.Equal("test", result.Name);
            Assert.Equal(20, result.MaxSteps);
        }

        [Fact]
        public void Merge_OverrideSystemPrompt_ReturnsMerged()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                SystemPrompt = "Base prompt"
            };

            var overrideConfig = new AgentConfigFile
            {
                SystemPrompt = "Override prompt"
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.Equal("Override prompt", result.SystemPrompt);
        }

        [Fact]
        public void Merge_PermissionRules_Replaces()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "tool1")
                }
            };

            var overrideConfig = new AgentConfigFile
            {
                PermissionRules = new List<PermissionRuleEntry>
                {
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "tool2")
                }
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.Single(result.PermissionRules);
            Assert.Equal(PermissionEffect.Deny, result.PermissionRules[0].Effect);
        }

        [Fact]
        public void ApplyVariant_ExactMatch_TakesPrecedence()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Temperature = 0.5,
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["openai"] = new() { Temperature = 0.7 },
                    ["openai.gpt-4o-mini"] = new() { Temperature = 0.3 }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "openai", "gpt-4o-mini");

            Assert.Equal(0.3, result.Temperature);
        }

        [Fact]
        public void ApplyVariant_ProviderOnlyMatch_Works()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Temperature = 0.5,
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["anthropic"] = new() { Temperature = 0.2 }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "anthropic", "claude-sonnet-4");

            Assert.Equal(0.2, result.Temperature);
        }

        [Fact]
        public void ApplyVariant_NoMatch_ReturnsBase()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Temperature = 0.5,
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["openai"] = new() { Temperature = 0.7 }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "anthropic", "claude-sonnet-4");

            Assert.Equal(0.5, result.Temperature);
        }

        [Fact]
        public void ApplyVariant_SystemPromptAppend_Works()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                SystemPrompt = "Base content",
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["openai"] = new()
                    {
                        SystemPromptPrepend = "Prepend",
                        SystemPromptAppend = "Append"
                    }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "openai", null);

            // Verify the exact format and order: "Prepend\n\nBase content\n\nAppend"
            Assert.Equal("Prepend\n\nBase content\n\nAppend", result.SystemPrompt);
        }

        [Fact]
        public void ApplyVariant_SystemPromptFullReplace_Works()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                SystemPrompt = "Base content",
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["openai"] = new()
                    {
                        SystemPrompt = "Full replacement"
                    }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "openai", null);

            Assert.Equal("Full replacement", result.SystemPrompt);
        }

        [Fact]
        public void ApplyVariant_ModelOverride_PreservesProviderId()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Model = new ModelReference { ProviderId = "openai", ModelId = "gpt-4o" },
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["openai"] = new() { Model = "gpt-4o-mini" }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "openai", null);

            Assert.NotNull(result.Model);
            Assert.Equal("openai", result.Model.ProviderId);
            Assert.Equal("gpt-4o-mini", result.Model.ModelId);
        }

        [Fact]
        public void ApplyVariant_ModelOverride_NoBaseModel_UsesEmptyProviderId()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Model = null,
                Variants = new Dictionary<string, AgentVariant>
                {
                    ["anthropic"] = new() { Model = "claude-sonnet-4" }
                }
            };

            var result = AgentDefinitionExtensions.ApplyVariant(baseDef, "anthropic", null);

            Assert.NotNull(result.Model);
            Assert.Equal(string.Empty, result.Model.ProviderId);
            Assert.Equal("claude-sonnet-4", result.Model.ModelId);
        }

        #region ApplyJsonConfig Tests

        [Fact]
        public void ApplyJsonConfig_WithNoOverrides_ReturnsBase()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Description = "Base description",
                MaxSteps = 10
            };

            var jsonConfig = new AgentConfig();

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.Equal("test", result.Name);
            Assert.Equal("Base description", result.Description);
            Assert.Equal(10, result.MaxSteps);
        }

        [Fact]
        public void ApplyJsonConfig_OverridesProperties()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Description = "Base description",
                SystemPrompt = "Base prompt",
                MaxSteps = 10,
                Temperature = 0.5
            };

            var jsonConfig = new AgentConfig
            {
                Description = "Override description",
                SystemPrompt = "Override prompt",
                MaxSteps = 20,
                Temperature = 0.7
            };

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.Equal("test", result.Name);
            Assert.Equal("Override description", result.Description);
            Assert.Equal("Override prompt", result.SystemPrompt);
            Assert.Equal(20, result.MaxSteps);
            Assert.Equal(0.7, result.Temperature);
        }

        [Fact]
        public void ApplyJsonConfig_ModelProviderConfiguration_SetsModelReference()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Model = new ModelReference { ProviderId = "base-provider", ModelId = "base-model" }
            };

            var jsonConfig = new AgentConfig
            {
                Provider = "openai",
                Model = "gpt-4o"
            };

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.NotNull(result.Model);
            Assert.Equal("openai", result.Model.ProviderId);
            Assert.Equal("gpt-4o", result.Model.ModelId);
        }

        [Fact]
        public void ApplyJsonConfig_ModelOnly_UsesEmptyProvider()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Model = new ModelReference { ProviderId = "base-provider", ModelId = "base-model" }
            };

            var jsonConfig = new AgentConfig
            {
                Model = "gpt-4o"
            };

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.NotNull(result.Model);
            Assert.Equal(string.Empty, result.Model.ProviderId);
            Assert.Equal("gpt-4o", result.Model.ModelId);
        }

        [Fact]
        public void ApplyJsonConfig_NoModelChange_KeepsBaseModel()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Model = new ModelReference { ProviderId = "base-provider", ModelId = "base-model" }
            };

            var jsonConfig = new AgentConfig
            {
                MaxSteps = 20
            };

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.NotNull(result.Model);
            Assert.Equal("base-provider", result.Model.ProviderId);
            Assert.Equal("base-model", result.Model.ModelId);
        }

        [Fact]
        public void ApplyJsonConfig_PermissionRules_MergesWithBase()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "tool1")
                }
            };

            var jsonConfig = new AgentConfig
            {
                PermissionRules = new List<PermissionRuleEntry>
                {
                    PermissionRuleEntry.Deny(PermissionKind.Tool, "tool2")
                }
            };

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.Equal(2, result.PermissionRules.Count);
            Assert.Equal(PermissionEffect.Allow, result.PermissionRules[0].Effect);
            Assert.Equal(PermissionEffect.Deny, result.PermissionRules[1].Effect);
        }

        [Fact]
        public void ApplyJsonConfig_NoAdditionalPermissionRules_KeepsBaseRules()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                PermissionRules = new List<PermissionRuleEntry>
                {
                    PermissionRuleEntry.Allow(PermissionKind.Tool, "tool1")
                }
            };

            var jsonConfig = new AgentConfig
            {
                MaxSteps = 20
            };

            var result = AgentDefinitionExtensions.ApplyJsonConfig(baseDef, jsonConfig);

            Assert.Single(result.PermissionRules);
            Assert.Equal(PermissionEffect.Allow, result.PermissionRules[0].Effect);
        }

        #endregion
    }
}
