# Agent 配置系统实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 MD 文件格式的 Agent 配置系统，支持本地化配置覆盖和 Provider/Model 变体机制。

**Architecture:** 新增 `AgentConfigLoader` 组件，负责发现、解析、合并 MD 配置文件。配置优先级：代码内置 → 用户级 MD → 项目级 MD → seeing.json。变体选择基于当前 Provider/Model 自动匹配。

**Tech Stack:** C# 10, .NET 10, YamlDotNet (已安装)

---

## 文件结构

**新增文件：**
- `src/Seeing.Agent/Configuration/AgentConfigFile.cs` - MD 文件模型
- `src/Seeing.Agent/Configuration/AgentVariant.cs` - 变体模型
- `src/Seeing.Agent/Configuration/AgentDefinitionExtensions.cs` - 合并辅助方法
- `src/Seeing.Agent/Configuration/AgentConfigLoader.cs` - 核心加载器
- `tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs` - 单元测试
- `tests/Seeing.Agent.Tests/Configuration/AgentConfigIntegrationTests.cs` - 集成测试

**修改文件：**
- `src/Seeing.Agent/Core/Models/AgentDefinition.cs` - 添加 Variants 属性
- `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` - 注册 IAgentConfigLoader
- `src/Seeing.Agent/Core/AgentRegistry.cs` - 集成 MD 配置加载

---

### Task 1: 定义配置模型

**Files:**
- Create: `src/Seeing.Agent/Configuration/AgentConfigFile.cs`
- Create: `src/Seeing.Agent/Configuration/AgentVariant.cs`
- Modify: `src/Seeing.Agent/Core/Models/AgentDefinition.cs`

- [ ] **Step 1: 创建 AgentVariant 模型**

创建文件 `src/Seeing.Agent/Configuration/AgentVariant.cs`：

```csharp
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 变体配置 - 用于 Provider/Model 特定的配置覆盖
    /// </summary>
    public class AgentVariant
    {
        /// <summary>模型 ID</summary>
        public string? Model { get; set; }
        
        /// <summary>温度参数</summary>
        public double? Temperature { get; set; }
        
        /// <summary>TopP 参数</summary>
        public double? TopP { get; set; }
        
        /// <summary>最大输出 Token 数</summary>
        public int? MaxTokens { get; set; }
        
        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }
        
        /// <summary>权限规则</summary>
        public List<PermissionRuleEntry>? PermissionRules { get; set; }
        
        /// <summary>允许的工具（白名单）</summary>
        public List<string>? AllowedTools { get; set; }
        
        /// <summary>禁止的工具（黑名单）</summary>
        public List<string>? DeniedTools { get; set; }
        
        /// <summary>完全替换基础 SystemPrompt</summary>
        public string? SystemPrompt { get; set; }
        
        /// <summary>插入到基础 SystemPrompt 开头</summary>
        public string? SystemPromptPrepend { get; set; }
        
        /// <summary>追加到基础 SystemPrompt 末尾</summary>
        public string? SystemPromptAppend { get; set; }
    }
}
```

- [ ] **Step 2: 创建 AgentConfigFile 模型**

创建文件 `src/Seeing.Agent/Configuration/AgentConfigFile.cs`：

```csharp
using Seeing.Agent.Core.Permission;
using YamlDotNet.Serialization;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// MD 配置文件模型 - 对应 YAML Front Matter
    /// </summary>
    public class AgentConfigFile
    {
        // === 基础配置 ===
        
        /// <summary>Agent 名称（必需，与文件名一致）</summary>
        public string? Name { get; set; }
        
        /// <summary>Agent 描述</summary>
        public string? Description { get; set; }
        
        /// <summary>运行模式：Primary / SubAgent / All</summary>
        public string? Mode { get; set; }
        
        /// <summary>分类标签</summary>
        public string? Category { get; set; }
        
        /// <summary>最大执行步骤</summary>
        public int? MaxSteps { get; set; }
        
        /// <summary>运行时类型：Native / AcpPassthrough</summary>
        public string? Runtime { get; set; }
        
        /// <summary>ACP 后端标识</summary>
        public string? AcpBackend { get; set; }
        
        /// <summary>是否隐藏</summary>
        public bool? IsHidden { get; set; }
        
        // === 权限配置 ===
        
        /// <summary>权限规则列表</summary>
        public List<PermissionRuleEntry>? PermissionRules { get; set; }
        
        /// <summary>权限默认效果：Allow / Deny / Ask</summary>
        public string? PermissionDefaultEffect { get; set; }
        
        // === 工具限制 ===
        
        /// <summary>允许的工具列表（空数组表示全部允许）</summary>
        public List<string>? AllowedTools { get; set; }
        
        /// <summary>禁止的工具列表</summary>
        public List<string>? DeniedTools { get; set; }
        
        // === 模型配置 ===
        
        /// <summary>默认 Provider ID</summary>
        public string? Provider { get; set; }
        
        /// <summary>默认模型 ID</summary>
        public string? Model { get; set; }
        
        /// <summary>温度参数</summary>
        public double? Temperature { get; set; }
        
        /// <summary>TopP 参数</summary>
        public double? TopP { get; set; }
        
        /// <summary>最大输出 Token 数</summary>
        public int? MaxTokens { get; set; }
        
        // === 变体定义 ===
        
        /// <summary>Provider/Model 变体映射</summary>
        public Dictionary<string, AgentVariant>? Variants { get; set; }
        
        // === SystemPrompt（从 Markdown Body 解析）===
        
        /// <summary>系统提示词（Markdown Body 内容）</summary>
        [YamlIgnore]
        public string? SystemPrompt { get; set; }
    }
}
```

- [ ] **Step 3: 修改 AgentDefinition 添加 Variants 属性**

在 `src/Seeing.Agent/Core/Models/AgentDefinition.cs` 第 102 行后添加：

```csharp
        /// <summary>ACP 后端标识</summary>
        public string? AcpBackend { get; init; }

        /// <summary>权限默认效果（当没有匹配规则时，默认询问用户）</summary>
        public PermissionEffect PermissionDefaultEffect { get; init; } = PermissionEffect.Ask;

        /// <summary>
        /// Provider/Model 变体配置
        /// <para>
        /// 键格式：{provider} 或 {provider}.{model}
        /// 例如："openai"、"anthropic"、"openai.gpt-4o-mini"
        /// </para>
        /// </summary>
        public IReadOnlyDictionary<string, AgentVariant>? Variants { get; init; }
```

在文件顶部添加 using：

```csharp
using Seeing.Agent.Configuration;
```

- [ ] **Step 4: 验证编译**

```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj --no-restore
```

Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Configuration/AgentVariant.cs
git add src/Seeing.Agent/Configuration/AgentConfigFile.cs
git add src/Seeing.Agent/Core/Models/AgentDefinition.cs
git commit -m "feat(agent-config): add AgentVariant and AgentConfigFile models"
```

---

### Task 2: 实现合并辅助方法

**Files:**
- Create: `src/Seeing.Agent/Configuration/AgentDefinitionExtensions.cs`
- Create: `tests/Seeing.Agent.Tests/Configuration/AgentDefinitionExtensionsTests.cs`

- [ ] **Step 1: 编写合并测试**

创建文件 `tests/Seeing.Agent.Tests/Configuration/AgentDefinitionExtensionsTests.cs`：

```csharp
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

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

            Assert.Contains("Prepend", result.SystemPrompt);
            Assert.Contains("Base content", result.SystemPrompt);
            Assert.Contains("Append", result.SystemPrompt);
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
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentDefinitionExtensionsTests" --no-build
```

Expected: Test fails with "The type or namespace 'AgentDefinitionExtensions' could not be found"

- [ ] **Step 3: 实现 AgentDefinitionExtensions**

创建文件 `src/Seeing.Agent/Configuration/AgentDefinitionExtensions.cs`：

```csharp
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
            AgentConfigFile? override)
        {
            if (override == null)
                return baseDef;

            return new AgentDefinition
            {
                Name = baseDef.Name,
                Description = override.Description ?? baseDef.Description,
                Mode = ParseMode(override.Mode) ?? baseDef.Mode,
                Category = override.Category ?? baseDef.Category,
                SystemPrompt = override.SystemPrompt ?? baseDef.SystemPrompt,
                Model = MergeModelReference(baseDef.Model, override.Provider, override.Model),
                MaxSteps = override.MaxSteps ?? baseDef.MaxSteps,
                AllowedTools = override.AllowedTools?.AsReadOnly() ?? baseDef.AllowedTools,
                DeniedTools = override.DeniedTools?.AsReadOnly() ?? baseDef.DeniedTools,
                Temperature = override.Temperature ?? baseDef.Temperature,
                TopP = override.TopP ?? baseDef.TopP,
                MaxTokens = override.MaxTokens ?? baseDef.MaxTokens,
                IsHidden = override.IsHidden ?? baseDef.IsHidden,
                PermissionRules = override.PermissionRules?.AsReadOnly() ?? baseDef.PermissionRules,
                PermissionDefaultEffect = ParsePermissionEffect(override.PermissionDefaultEffect) ?? baseDef.PermissionDefaultEffect,
                Runtime = ParseRuntime(override.Runtime) ?? baseDef.Runtime,
                AcpBackend = override.AcpBackend ?? baseDef.AcpBackend,
                Variants = override.Variants != null
                    ? new Dictionary<string, AgentVariant>(override.Variants)
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
                    ? new ModelReference { ModelId = variant.Model }
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
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentDefinitionExtensionsTests"
```

Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Configuration/AgentDefinitionExtensions.cs
git add tests/Seeing.Agent.Tests/Configuration/AgentDefinitionExtensionsTests.cs
git commit -m "feat(agent-config): add AgentDefinitionExtensions for config merging"
```

---

### Task 3: 实现配置加载器

**Files:**
- Create: `src/Seeing.Agent/Configuration/AgentConfigLoader.cs`
- Create: `tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs`

- [ ] **Step 1: 编写加载器测试**

创建文件 `tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs`：

```csharp
using Seeing.Agent.Configuration;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Seeing.Agent.Tests.Configuration
{
    public class AgentConfigLoaderTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly string _userSeeingDir;
        private readonly string _projectSeeingDir;

        public AgentConfigLoaderTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), $"agent-config-tests-{Guid.NewGuid()}");
            _userSeeingDir = Path.Combine(_testDirectory, "user", ".seeing", "agents");
            _projectSeeingDir = Path.Combine(_testDirectory, "project", ".seeing", "agents");
            
            Directory.CreateDirectory(_userSeeingDir);
            Directory.CreateDirectory(_projectSeeingDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }

        [Fact]
        public async Task ParseFile_ValidYaml_ReturnsConfigFile()
        {
            var filePath = Path.Combine(_projectSeeingDir, "test-agent.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: test-agent
                description: Test agent
                maxSteps: 50
                ---
                You are a test agent.
                """);

            var result = AgentConfigLoader.ParseFile(filePath);

            Assert.Equal("test-agent", result!.Name);
            Assert.Equal("Test agent", result.Description);
            Assert.Equal(50, result.MaxSteps);
            Assert.Equal("You are a test agent.", result.SystemPrompt?.Trim());
        }

        [Fact]
        public async Task ParseFile_WithVariants_ReturnsVariants()
        {
            var filePath = Path.Combine(_projectSeeingDir, "test-agent.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: test-agent
                variants:
                  openai:
                    temperature: 0.7
                  anthropic:
                    temperature: 0.5
                    systemPromptAppend: |
                      Use XML tags.
                ---
                Base prompt.
                """);

            var result = AgentConfigLoader.ParseFile(filePath);

            Assert.NotNull(result!.Variants);
            Assert.Equal(2, result.Variants.Count);
            Assert.Equal(0.7, result.Variants["openai"].Temperature);
            Assert.Equal(0.5, result.Variants["anthropic"].Temperature);
            Assert.Contains("Use XML tags", result.Variants["anthropic"].SystemPromptAppend);
        }

        [Fact]
        public async Task ParseFile_InvalidYaml_ReturnsNull()
        {
            var filePath = Path.Combine(_projectSeeingDir, "invalid.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                name: [invalid yaml
                ---
                Content
                """);

            var result = AgentConfigLoader.ParseFile(filePath);

            Assert.Null(result);
        }

        [Fact]
        public async Task ParseFile_MissingName_ReturnsNull()
        {
            var filePath = Path.Combine(_projectSeeingDir, "no-name.md");
            await File.WriteAllTextAsync(filePath, """
                ---
                description: No name agent
                ---
                Content
                """);

            var result = AgentConfigLoader.ParseFile(filePath);

            Assert.Null(result);
        }

        [Fact]
        public async Task ParseFile_NoFrontMatter_ReturnsNull()
        {
            var filePath = Path.Combine(_projectSeeingDir, "no-frontmatter.md");
            await File.WriteAllTextAsync(filePath, "Just content without front matter");

            var result = AgentConfigLoader.ParseFile(filePath);

            Assert.Null(result);
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfigLoaderTests" --no-build
```

Expected: Test fails

- [ ] **Step 3: 实现 AgentConfigLoader**

创建文件 `src/Seeing.Agent/Configuration/AgentConfigLoader.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Configuration
{
    /// <summary>
    /// Agent 配置加载器接口
    /// </summary>
    public interface IAgentConfigLoader
    {
        /// <summary>
        /// 发现所有 Agent 配置文件
        /// </summary>
        Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default);

        /// <summary>
        /// 加载并合并 Agent 配置
        /// </summary>
        Task<AgentDefinition?> LoadAsync(
            string agentName,
            string? provider = null,
            string? model = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Agent 配置加载器 - 发现、解析、合并 MD 配置文件
    /// </summary>
    public class AgentConfigLoader : IAgentConfigLoader
    {
        private readonly IWorkspaceProvider _workspaceProvider;
        private readonly ILogger<AgentConfigLoader> _logger;
        private readonly IDeserializer _yamlDeserializer;

        public AgentConfigLoader(
            IWorkspaceProvider workspaceProvider,
            ILogger<AgentConfigLoader> logger)
        {
            _workspaceProvider = workspaceProvider;
            _logger = logger;
            _yamlDeserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default)
        {
            var files = new List<string>();

            // 用户级目录
            var userAgentsDir = Path.Combine(_workspaceProvider.UserSeeingDirectory, "agents");
            if (Directory.Exists(userAgentsDir))
            {
                files.AddRange(Directory.GetFiles(userAgentsDir, "*.md"));
            }

            // 项目级目录
            var projectAgentsDir = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "agents");
            if (Directory.Exists(projectAgentsDir))
            {
                files.AddRange(Directory.GetFiles(projectAgentsDir, "*.md"));
            }

            _logger.LogDebug("发现 {Count} 个 Agent 配置文件", files.Count);
            return await Task.FromResult(files.AsReadOnly());
        }

        /// <inheritdoc/>
        public async Task<AgentDefinition?> LoadAsync(
            string agentName,
            string? provider = null,
            string? model = null,
            CancellationToken ct = default)
        {
            var files = await DiscoverAsync(ct);
            
            // 按优先级分组：项目级 > 用户级
            var userFile = files.FirstOrDefault(f =>
                f.StartsWith(_workspaceProvider.UserSeeingDirectory) &&
                GetAgentNameFromPath(f).Equals(agentName, StringComparison.OrdinalIgnoreCase));
            
            var projectFile = files.FirstOrDefault(f =>
                f.StartsWith(_workspaceProvider.ProjectSeeingDirectory) &&
                GetAgentNameFromPath(f).Equals(agentName, StringComparison.OrdinalIgnoreCase));

            AgentConfigFile? userConfig = null;
            AgentConfigFile? projectConfig = null;

            if (userFile != null)
            {
                userConfig = ParseFile(userFile);
            }

            if (projectFile != null)
            {
                projectConfig = ParseFile(projectFile);
            }

            // 如果没有任何配置文件
            if (userConfig == null && projectConfig == null)
            {
                _logger.LogDebug("未找到 Agent '{AgentName}' 的 MD 配置文件", agentName);
                return null;
            }

            // 构建基础定义
            var baseDef = new AgentDefinition { Name = agentName };

            // 合并配置：用户级 → 项目级
            var result = AgentDefinitionExtensions.Merge(baseDef, userConfig);
            result = AgentDefinitionExtensions.Merge(result, projectConfig);

            // 应用变体
            if (!string.IsNullOrEmpty(provider))
            {
                result = AgentDefinitionExtensions.ApplyVariant(result, provider, model);
            }

            _logger.LogDebug("加载 Agent '{AgentName}' 配置完成", agentName);
            return result;
        }

        /// <summary>
        /// 解析 MD 配置文件
        /// </summary>
        public static AgentConfigFile? ParseFile(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);

                // 提取 YAML Front Matter
                var frontMatterEnd = content.IndexOf("---", 3);
                if (frontMatterEnd == -1)
                {
                    return null;
                }

                var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
                var bodyContent = content.Substring(frontMatterEnd + 3).Trim();

                var config = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build()
                    .Deserialize<AgentConfigFile>(yamlContent);

                if (string.IsNullOrEmpty(config?.Name))
                {
                    return null;
                }

                config.SystemPrompt = bodyContent;
                return config;
            }
            catch (Exception ex)
            {
                // 记录警告，返回 null 让调用方降级处理
                return null;
            }
        }

        private static string GetAgentNameFromPath(string path)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfigLoaderTests"
```

Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Configuration/AgentConfigLoader.cs
git add tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs
git commit -m "feat(agent-config): add AgentConfigLoader for MD file parsing"
```

---

### Task 4: 注册 DI 服务

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 添加 IAgentConfigLoader 注册**

在 `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` 的 `RegisterCoreServices` 方法中，在 `AgentRegistry` 创建之前添加：

```csharp
            // Agent 配置加载器（MD 文件）
            services.AddSingleton<IAgentConfigLoader, AgentConfigLoader>();
```

找到 `services.AddSingleton<AgentRegistry>(sp =>` 部分（约第 321 行），在其上方添加这行。

- [ ] **Step 2: 验证编译**

```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj
```

Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(agent-config): register IAgentConfigLoader in DI"
```

---

### Task 5: 集成到 AgentRegistry

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentRegistry.cs`
- Create: `tests/Seeing.Agent.Tests/Configuration/AgentConfigIntegrationTests.cs`

- [ ] **Step 1: 编写集成测试**

创建文件 `tests/Seeing.Agent.Tests/Configuration/AgentConfigIntegrationTests.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Seeing.Agent.Tests.Configuration
{
    public class AgentConfigIntegrationTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly string _projectSeeingDir;

        public AgentConfigIntegrationTests()
        {
            _testDirectory = Path.Combine(Path.GetTempPath(), $"agent-integration-tests-{Guid.NewGuid()}");
            _projectSeeingDir = Path.Combine(_testDirectory, ".seeing", "agents");
            Directory.CreateDirectory(_projectSeeingDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }

        [Fact]
        public async Task MDConfig_OverridesCodeDefinition()
        {
            // 创建 MD 配置文件
            var mdPath = Path.Combine(_projectSeeingDir, "sisyphus.md");
            await File.WriteAllTextAsync(mdPath, """
                ---
                name: sisyphus
                maxSteps: 999
                ---
                MD SystemPrompt content
                """);

            // 创建服务
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddConsole());
            
            var workspaceProvider = new TestWorkspaceProvider(_testDirectory);
            services.AddSingleton<IWorkspaceProvider>(workspaceProvider);
            services.AddSingleton<AgentConfigLoader>();
            services.AddSingleton<IAgentConfigLoader>(sp => sp.GetRequiredService<AgentConfigLoader>());

            var sp = services.BuildServiceProvider();
            var loader = sp.GetRequiredService<IAgentConfigLoader>();

            // 加载配置
            var result = await loader.LoadAsync("sisyphus");

            Assert.NotNull(result);
            Assert.Equal(999, result!.MaxSteps);
            Assert.Equal("MD SystemPrompt content", result.SystemPrompt);
        }

        [Fact]
        public async Task Variant_Selection_Works()
        {
            var mdPath = Path.Combine(_projectSeeingDir, "test-agent.md");
            await File.WriteAllTextAsync(mdPath, """
                ---
                name: test-agent
                temperature: 0.5
                variants:
                  openai:
                    temperature: 0.7
                  openai.gpt-4o-mini:
                    temperature: 0.3
                ---
                Base prompt
                """);

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddConsole());
            
            var workspaceProvider = new TestWorkspaceProvider(_testDirectory);
            services.AddSingleton<IWorkspaceProvider>(workspaceProvider);
            services.AddSingleton<AgentConfigLoader>();
            services.AddSingleton<IAgentConfigLoader>(sp => sp.GetRequiredService<AgentConfigLoader>());

            var sp = services.BuildServiceProvider();
            var loader = sp.GetRequiredService<IAgentConfigLoader>();

            // 测试精确匹配
            var exact = await loader.LoadAsync("test-agent", "openai", "gpt-4o-mini");
            Assert.Equal(0.3, exact!.Temperature);

            // 测试 Provider 级别匹配
            var provider = await loader.LoadAsync("test-agent", "openai", "other-model");
            Assert.Equal(0.7, provider!.Temperature);

            // 测试无匹配
            var noMatch = await loader.LoadAsync("test-agent", "anthropic", "claude");
            Assert.Equal(0.5, noMatch!.Temperature);
        }

        private class TestWorkspaceProvider : IWorkspaceProvider
        {
            private readonly string _root;

            public TestWorkspaceProvider(string root)
            {
                _root = root;
            }

            public string WorkspaceRoot => _root;
            public string UserSeeingDirectory => Path.Combine(_root, "user", ".seeing");
            public string ProjectSeeingDirectory => Path.Combine(_root, ".seeing");
            public string DefaultWorkspaceRoot => _root;
            public string GlobalWorkspaceRoot => _root;
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfigIntegrationTests" --no-build
```

Expected: Test fails

- [ ] **Step 3: 修改 AgentRegistry 集成 MD 配置**

在 `src/Seeing.Agent/Core/AgentRegistry.cs` 中：

1. 添加字段和构造函数参数：

```csharp
    public class AgentRegistry : IAgentRegistry
    {
        private readonly ILogger<AgentRegistry> _logger;
        private readonly IAgentStore _agentStore;
        private readonly IAgentRuntimeManager _runtimeManager;
        private readonly IOptions<SeeingAgentOptions>? _options;
        private readonly string? _fallbackDefaultAgentName;
        private readonly IAgentConfigLoader? _configLoader;  // 新增

        public AgentRegistry(
            ILogger<AgentRegistry> logger,
            IAgentStore agentStore,
            IAgentRuntimeManager runtimeManager,
            IEnumerable<AgentInfo>? builtInAgents = null,
            string? defaultAgent = null,
            IOptions<SeeingAgentOptions>? options = null,
            IAgentConfigLoader? configLoader = null)  // 新增参数
        {
            _logger = logger;
            _agentStore = agentStore;
            _runtimeManager = runtimeManager;
            _options = options;
            _fallbackDefaultAgentName = defaultAgent;
            _configLoader = configLoader;  // 新增
            // ... 其余代码不变
```

2. 添加新的 `GetAgentWithMergedConfigAsync` 方法：

在 `GetAgentAsync` 方法后添加：

```csharp
        /// <summary>
        /// 获取 Agent 并合并 MD 配置
        /// </summary>
        public async Task<AgentInfo?> GetAgentWithMergedConfigAsync(
            string name,
            string? provider = null,
            string? model = null)
        {
            var agentInfo = await GetAgentAsync(name);
            if (agentInfo == null)
                return null;

            // 加载 MD 配置
            if (_configLoader != null)
            {
                var mdConfig = await _configLoader.LoadAsync(name, provider, model);
                if (mdConfig != null)
                {
                    // 应用 MD 配置到 AgentInfo
                    agentInfo = ApplyMdConfig(agentInfo, mdConfig);
                }
            }

            // 应用 seeing.json 配置
            var jsonConfig = _options?.Value?.Agents?.GetValueOrDefault(name);
            if (jsonConfig != null)
            {
                agentInfo = ApplyJsonConfigToAgentInfo(agentInfo, jsonConfig);
            }

            return agentInfo;
        }

        private AgentInfo ApplyMdConfig(AgentInfo info, AgentDefinition mdConfig)
        {
            return new AgentInfo
            {
                Name = info.Name,
                Description = mdConfig.Description ?? info.Description,
                Mode = mdConfig.Mode,
                Category = mdConfig.Category ?? info.Category,
                SystemPrompt = mdConfig.SystemPrompt ?? info.SystemPrompt,
                Model = mdConfig.Model ?? info.Model,
                MaxSteps = mdConfig.MaxSteps ?? info.MaxSteps,
                AllowedTools = mdConfig.AllowedTools.Count > 0
                    ? mdConfig.AllowedTools.ToList()
                    : info.AllowedTools,
                DeniedTools = mdConfig.DeniedTools.Count > 0
                    ? mdConfig.DeniedTools.ToList()
                    : info.DeniedTools,
                Temperature = mdConfig.Temperature ?? info.Temperature,
                TopP = mdConfig.TopP ?? info.TopP,
                IsHidden = mdConfig.IsHidden,
                PermissionRules = mdConfig.PermissionRules.Count > 0
                    ? mdConfig.PermissionRules.ToList()
                    : info.PermissionRules,
                PermissionDefaultEffect = mdConfig.PermissionDefaultEffect,
                Runtime = mdConfig.Runtime,
                AcpBackend = mdConfig.AcpBackend ?? info.AcpBackend,
                IsNative = info.IsNative,
                AgentFactory = info.AgentFactory,
                Variants = mdConfig.Variants != null
                    ? new Dictionary<string, AgentVariant>(mdConfig.Variants)
                    : info.Variants
            };
        }

        private AgentInfo ApplyJsonConfigToAgentInfo(AgentInfo info, AgentConfig config)
        {
            // 复用现有的 ExtendFromConfig 逻辑
            // 这里简化实现，实际可以重构 ExtendFromConfig
            return new AgentInfo
            {
                Name = info.Name,
                Description = config.Description ?? info.Description,
                Mode = config.Mode ?? info.Mode,
                Category = info.Category,
                SystemPrompt = config.SystemPrompt ?? info.SystemPrompt,
                Model = config.Model != null
                    ? new ModelReference
                    {
                        ProviderId = config.Provider ?? string.Empty,
                        ModelId = config.Model
                    }
                    : info.Model,
                MaxSteps = config.MaxSteps ?? info.MaxSteps,
                AllowedTools = info.AllowedTools,
                DeniedTools = info.DeniedTools,
                Temperature = config.Temperature ?? info.Temperature,
                TopP = config.TopP ?? info.TopP,
                IsHidden = config.IsHidden ?? info.IsHidden,
                PermissionRules = MergePermissionRules(info.PermissionRules, config.PermissionRules),
                PermissionDefaultEffect = info.PermissionDefaultEffect,
                Runtime = config.Runtime ?? info.Runtime,
                AcpBackend = config.AcpBackend ?? info.AcpBackend,
                IsNative = info.IsNative,
                AgentFactory = info.AgentFactory,
                Variants = info.Variants
            };
        }

        private List<PermissionRuleEntry> MergePermissionRules(
            List<PermissionRuleEntry> baseRules,
            List<PermissionRuleEntry>? additionalRules)
        {
            if (additionalRules == null || additionalRules.Count == 0)
                return baseRules;

            var merged = new List<PermissionRuleEntry>(baseRules);
            merged.AddRange(additionalRules);
            return merged;
        }
```

- [ ] **Step 4: 运行测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfigIntegrationTests"
```

Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Core/AgentRegistry.cs
git add tests/Seeing.Agent.Tests/Configuration/AgentConfigIntegrationTests.cs
git commit -m "feat(agent-config): integrate MD config loading into AgentRegistry"
```

---

### Task 6: 更新 ServiceCollectionExtensions 使用新构造函数

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 更新 AgentRegistry 创建代码**

在 `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` 中找到 `services.AddSingleton<AgentRegistry>(sp =>` 部分（约第 321 行），修改为：

```csharp
            services.AddSingleton<AgentRegistry>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<AgentRegistry>>();
                var agentStore = sp.GetRequiredService<IAgentStore>();
                var runtimeManager = sp.GetRequiredService<IAgentRuntimeManager>();
                var discovery = sp.GetRequiredService<AgentDiscovery>();
                var options = sp.GetService<IOptions<SeeingAgentOptions>>();
                var configLoader = sp.GetService<IAgentConfigLoader>();  // 新增

                // 获取内置代理
                var builtInAgents = BuiltInAgents.GetBuiltInAgents();

                // 从文件系统发现代理
                var discoveredAgents = discovery.DiscoverAgentsAsync().GetAwaiter().GetResult();
                var allAgents = builtInAgents.Concat(discoveredAgents);

                var registry = new AgentRegistry(
                    logger,
                    agentStore,
                    runtimeManager,
                    allAgents,
                    defaultAgent: options?.Value?.DefaultAgent,
                    options: options,
                    configLoader: configLoader);  // 新增参数

                // 从配置扩展代理
                if (options?.Value?.Agents != null)
                {
                    registry.ExtendFromConfig(options.Value.Agents);
                }

                return registry;
            });
```

- [ ] **Step 2: 验证编译和测试**

```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj && dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfig"
```

Expected: Build and tests pass

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(agent-config): pass configLoader to AgentRegistry"
```

---

### Task 7: 创建示例配置文件

**Files:**
- Create: `.seeing/agents/sisyphus.md` (示例)

- [ ] **Step 1: 创建示例目录**

```bash
mkdir -p .seeing/agents
```

- [ ] **Step 2: 创建示例配置文件**

创建文件 `.seeing/agents/sisyphus.md`：

```markdown
---
name: sisyphus
description: 主工作代理，负责执行复杂任务和编排子代理
mode: Primary
category: orchestrator
maxSteps: 100
permissionRules:
  - kind: tool
    pattern: question
    effect: allow
    priority: 0
  - kind: tool
    pattern: call_omo_agent
    effect: deny
    priority: 100
variants:
  openai:
    model: gpt-4o
    temperature: 0.7
  anthropic:
    model: claude-sonnet-4-20250514
    temperature: 0.5
    systemPromptAppend: |
      ## Claude 特定指令
      使用 XML 标签结构化输出。
---

你是 Sisyphus，AI Agent 编排器，负责协调子代理完成复杂任务。

## 角色定位

你不是执行者，而是决策者和协调者。你的职责是：
- 解析用户的隐式需求
- 将专业工作委托给合适的子代理
- 并行执行以最大化吞吐量
- 遵循用户指令，不擅自开始实现
```

- [ ] **Step 3: Commit**

```bash
git add .seeing/agents/sisyphus.md
git commit -m "docs: add example sisyphus agent MD config"
```

---

### Task 8: 最终验证

- [ ] **Step 1: 运行所有相关测试**

```bash
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfig"
```

Expected: All tests pass

- [ ] **Step 2: 运行完整测试套件**

```bash
dotnet test
```

Expected: All tests pass

- [ ] **Step 3: 最终 Commit**

```bash
git add -A
git commit -m "feat(agent-config): complete MD-based agent configuration system

- Add AgentVariant and AgentConfigFile models
- Add AgentDefinitionExtensions for config merging  
- Add AgentConfigLoader for MD file discovery and parsing
- Integrate with AgentRegistry for runtime config loading
- Support Provider/Model variants with auto-selection
- Support dual-layer config (user-level + project-level)"
```

---

## 自我审查

### 1. Spec Coverage

| Spec Section | Task |
|-------------|------|
| 文件格式设计 | Task 1, Task 3 |
| 变体机制 | Task 2, Task 3 |
| 配置合并 | Task 2 |
| 加载器 | Task 3 |
| DI 注册 | Task 4 |
| AgentRegistry 集成 | Task 5, Task 6 |
| 示例文件 | Task 7 |

### 2. Placeholder Scan

- ✅ 无 TBD/TODO
- ✅ 所有代码步骤包含完整代码
- ✅ 所有命令包含具体参数

### 3. Type Consistency

- ✅ `AgentVariant` 在 Task 1 和 Task 2 中使用一致
- ✅ `AgentConfigFile` 在 Task 1 和 Task 3 中使用一致
- ✅ `IAgentConfigLoader` 在 Task 3, 4, 5, 6 中使用一致

---

**Plan complete and saved to `docs/superpowers/plans/2025-01-15-agent-config-system.md`.**
