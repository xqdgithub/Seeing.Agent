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
        public void Merge_ModelReference_SetsCorrectly()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test"
            };

            var overrideConfig = new AgentConfigFile
            {
                Provider = "openai",
                Model = "gpt-4o"
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.NotNull(result.Model);
            Assert.Equal("openai", result.Model.ProviderId);
            Assert.Equal("gpt-4o", result.Model.ModelId);
        }

        [Fact]
        public void Merge_Temperature_Overrides()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Temperature = 0.5
            };

            var overrideConfig = new AgentConfigFile
            {
                Temperature = 0.7
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.Equal(0.7, result.Temperature);
        }

        [Fact]
        public void Merge_Category_Overrides()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Category = "base-category"
            };

            var overrideConfig = new AgentConfigFile
            {
                Category = "override-category"
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.Equal("override-category", result.Category);
        }

        [Fact]
        public void Merge_Description_Overrides()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                Description = "Base description"
            };

            var overrideConfig = new AgentConfigFile
            {
                Description = "Override description"
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.Equal("Override description", result.Description);
        }

        [Fact]
        public void Merge_IsHidden_Overrides()
        {
            var baseDef = new AgentDefinition
            {
                Name = "test",
                IsHidden = false
            };

            var overrideConfig = new AgentConfigFile
            {
                IsHidden = true
            };

            var result = AgentDefinitionExtensions.Merge(baseDef, overrideConfig);

            Assert.True(result.IsHidden);
        }
    }
}
