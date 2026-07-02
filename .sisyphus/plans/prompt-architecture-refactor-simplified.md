# Seeing.Agent 提示词构建架构重构设计（精简版）

## 问题分析

### 现有代码问题

1. **DynamicPromptBuilder** (312行)
   - ✅ 占位符替换逻辑清晰
   - ❌ 缺少 Provider 特定模板
   - ❌ 缺少环境信息注入
   - ❌ 缺少 AGENTS.md 指令加载

2. **AgentExecutor.InjectDynamicContextAsync** 
   - ❌ 在执行器中处理提示词（违反单一职责）
   - ❌ 重复的占位符替换逻辑

### 真正需要的改进

| 需求 | 当前状态 | 改进方案 |
|------|----------|----------|
| Provider 特定模板 | ❌ 缺失 | 添加 `SystemPromptProvider` |
| 环境信息注入 | ❌ 缺失 | 扩展 `PromptContext` |
| AGENTS.md 加载 | ✅ 有 `IInstructionLoader` | 在构建时调用 |
| 占位符替换 | ✅ 已有 | 保持现有逻辑 |

## 精简架构

```
┌─────────────────────────────────────────────────────────────┐
│                    PromptBuilder (统一类)                    │
│  - BuildAsync(context) → string                             │
│  - 内部编排所有格式化逻辑                                      │
├─────────────────────────────────────────────────────────────┤
│  SystemPromptProvider (单类)                                 │
│  - GetTemplate(providerId, modelId) → string                │
│  - 嵌入资源模板管理                                           │
└─────────────────────────────────────────────────────────────┘
```

**不引入的过度设计**：
- ❌ `IPromptContributor` 接口 + 7个实现类 → 保持内聚在 PromptBuilder 中
- ❌ `PromptSection` 类 → 直接返回 string
- ❌ `IPromptBuilderService` 接口 → 单一实现无需接口
- ❌ `ISystemPromptService` 接口 → 单一实现无需接口

## 文件结构（3个文件）

```
Core/Prompts/
├── PromptBuilder.cs          # 主构建器（扩展 DynamicPromptBuilder）
├── SystemPromptProvider.cs   # Provider 模板选择器
├── PromptContext.cs          # 已存在，扩展属性
└── Templates/                # 嵌入资源
    ├── default.txt
    ├── anthropic.txt
    ├── gpt.txt
    ├── gemini.txt
    └── beast.txt
```

## 实现代码

### 1. SystemPromptProvider.cs（新增）

```csharp
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 系统提示词提供者 - 根据 Provider/Model 选择模板
/// </summary>
public class SystemPromptProvider
{
    private readonly ILogger<SystemPromptProvider> _logger;
    private readonly Dictionary<string, string> _templates = new();
    private readonly Dictionary<string, string> _customTemplates = new();

    public SystemPromptProvider(ILogger<SystemPromptProvider> logger)
    {
        _logger = logger;
        LoadEmbeddedTemplates();
    }

    private void LoadEmbeddedTemplates()
    {
        var assembly = typeof(SystemPromptProvider).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Prompts.Templates") && n.EndsWith(".txt"));

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            
            // 从资源名提取模板名称（如 "Core.Prompts.Templates.default.txt" → "default"）
            var parts = name.Split('.');
            var templateName = parts.Length >= 2 ? parts[^2] : name;
            _templates[templateName] = content;
        }
    }

    /// <summary>
    /// 获取 Provider 特定的系统提示词模板
    /// </summary>
    public string GetTemplate(string providerId, string modelId)
    {
        // 检查自定义模板
        foreach (var (pattern, template) in _customTemplates)
        {
            if (MatchesPattern(modelId, pattern) || MatchesPattern(providerId, pattern))
            {
                return template;
            }
        }

        // 根据 Model ID 选择模板
        var templateName = SelectTemplateName(modelId);
        
        return _templates.TryGetValue(templateName, out var content) 
            ? content 
            : _templates.GetValueOrDefault("default", string.Empty);
    }

    /// <summary>
    /// 注册自定义模板
    /// </summary>
    public void RegisterTemplate(string pattern, string template)
    {
        _customTemplates[pattern] = template;
    }

    private static string SelectTemplateName(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        
        if (lower.Contains("gpt-4") || lower.Contains("o1") || lower.Contains("o3"))
            return "beast";
        if (lower.Contains("gpt"))
            return "gpt";
        if (lower.Contains("gemini-"))
            return "gemini";
        if (lower.Contains("claude"))
            return "anthropic";
        
        return "default";
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
            return value.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith("*"))
            return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith("*"))
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
```

### 2. PromptContext.cs（扩展现有）

```csharp
// 在现有 PromptContext.cs 中添加以下属性

/// <summary>Provider ID</summary>
public string? ProviderId { get; set; }

/// <summary>模型变体</summary>
public string? ModelVariant { get; set; }

/// <summary>工作区根目录</summary>
public string? WorkspaceRoot { get; set; }

/// <summary>平台信息</summary>
public string? Platform { get; set; }

/// <summary>Agent 定义</summary>
public AgentDefinition? Agent { get; set; }

/// <summary>服务提供者（用于获取依赖）</summary>
public IServiceProvider? Services { get; set; }
```

### 3. PromptBuilder.cs（重构 DynamicPromptBuilder）

```csharp
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Skills;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Core.Prompts;

/// <summary>
/// 提示词构建器 - 统一构建系统提示词
/// <para>
/// 支持的占位符：
/// - {{tools}} - 工具列表
/// - {{agents}} - 代理列表  
/// - {{skills}} - 技能列表
/// - {{environment}} - 环境信息（工作目录、平台、时间）
/// - {{instructions}} - AGENTS.md 指令
/// - 自定义变量 {{variable_name}}
/// </para>
/// </summary>
public class PromptBuilder
{
    private const string ToolsPlaceholder = "{{tools}}";
    private const string AgentsPlaceholder = "{{agents}}";
    private const string SkillsPlaceholder = "{{skills}}";
    private const string EnvironmentPlaceholder = "{{environment}}";
    private const string InstructionsPlaceholder = "{{instructions}}";

    private readonly SystemPromptProvider _systemPromptProvider;
    private readonly IInstructionLoader _instructionLoader;
    private readonly IAgentRegistry _agentRegistry;
    private readonly SkillManager _skillManager;

    public PromptBuilder(
        SystemPromptProvider systemPromptProvider,
        IInstructionLoader instructionLoader,
        IAgentRegistry agentRegistry,
        SkillManager skillManager)
    {
        _systemPromptProvider = systemPromptProvider;
        _instructionLoader = instructionLoader;
        _agentRegistry = agentRegistry;
        _skillManager = skillManager;
    }

    /// <summary>
    /// 构建完整的系统提示词
    /// </summary>
    /// <param name="context">提示词构建上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>构建后的完整提示词</returns>
    public async Task<string> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
    {
        // 1. 获取 Provider 特定模板（如果有）
        var basePrompt = context.Agent?.SystemPrompt;
        
        // 如果 Agent 没有定义系统提示词，使用 Provider 默认模板
        if (string.IsNullOrEmpty(basePrompt) && !string.IsNullOrEmpty(context.ProviderId) && !string.IsNullOrEmpty(context.ModelName))
        {
            basePrompt = _systemPromptProvider.GetTemplate(context.ProviderId, context.ModelName);
        }

        if (string.IsNullOrEmpty(basePrompt))
        {
            return string.Empty;
        }

        var result = basePrompt;

        // 2. 替换工具占位符
        if (result.Contains(ToolsPlaceholder) && context.Tools != null)
        {
            result = result.Replace(ToolsPlaceholder, BuildToolSection(context.Tools));
        }

        // 3. 替换代理占位符
        if (result.Contains(AgentsPlaceholder))
        {
            var agents = await _agentRegistry.GetAgentsAsync();
            result = result.Replace(AgentsPlaceholder, BuildAgentSection(agents, context.Agent?.Name));
        }

        // 4. 替换技能占位符
        if (result.Contains(SkillsPlaceholder))
        {
            var skills = _skillManager.GetAllSkillInfos().Values.ToList();
            result = result.Replace(SkillsPlaceholder, BuildSkillSection(skills));
        }

        // 5. 替换环境信息占位符
        if (result.Contains(EnvironmentPlaceholder))
        {
            result = result.Replace(EnvironmentPlaceholder, BuildEnvironmentSection(context));
        }

        // 6. 替换指令占位符
        if (result.Contains(InstructionsPlaceholder))
        {
            var instructions = await _instructionLoader.DiscoverAsync(context.WorkingDirectory ?? "", cancellationToken);
            var mergedInstructions = _instructionLoader.Merge(instructions);
            result = result.Replace(InstructionsPlaceholder, mergedInstructions);
        }

        // 7. 替换自定义变量
        foreach (var (key, value) in context.Variables)
        {
            result = result.Replace($"{{{{{key}}}}}", value);
        }

        // 8. 替换内置变量
        result = ReplaceBuiltinVariables(result, context);

        return result.Trim();
    }

    /// <summary>
    /// 同步构建（向后兼容）
    /// <para>注意：不支持 {{agents}}（需异步）、{{instructions}}（需异步）占位符</para>
    /// </summary>
    public string Build(string basePrompt, PromptContext context)
    {
        if (string.IsNullOrEmpty(basePrompt))
            return string.Empty;

        var result = basePrompt;

        if (context.Tools != null)
            result = result.Replace(ToolsPlaceholder, BuildToolSection(context.Tools));

        if (context.Agents != null)
            result = result.Replace(AgentsPlaceholder, BuildAgentSection(context.Agents, null));

        if (context.Skills != null)
            result = result.Replace(SkillsPlaceholder, BuildSkillSection(context.Skills.ToList()));

        // 环境信息同步可用
        result = result.Replace(EnvironmentPlaceholder, BuildEnvironmentSection(context));

        foreach (var (key, value) in context.Variables)
            result = result.Replace($"{{{{{key}}}}}", value);

        result = ReplaceBuiltinVariables(result, context);

        return result.Trim();
    }

    #region 内容构建方法（保持内聚）

    private string BuildToolSection(IEnumerable<FunctionSchema> tools)
    {
        var toolList = tools.ToList();
        if (toolList.Count == 0)
            return "暂无可用工具。";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用工具");
        sb.AppendLine();
        sb.AppendLine("以下工具可供调用：");
        sb.AppendLine();

        foreach (var tool in toolList)
        {
            sb.AppendLine($"### {tool.Name}");
            if (!string.IsNullOrEmpty(tool.Description))
                sb.AppendLine(tool.Description);
            sb.AppendLine();

            if (tool.Parameters.HasValue)
            {
                var parameters = tool.Parameters.Value;
                if (parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("properties", out var properties))
                {
                    sb.AppendLine("**参数：**");
                    foreach (var prop in properties.EnumerateObject())
                    {
                        sb.AppendLine($"- `{prop.Name}`: {GetPropertyDescription(prop.Value)}");
                    }
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    private string BuildAgentSection(IEnumerable<AgentInfo> agents, string? currentAgentName)
    {
        var agentList = agents.ToList();
        var subAgents = agentList
            .Where(a => a.Mode == AgentMode.SubAgent && a.Name != currentAgentName)
            .ToList();

        if (subAgents.Count == 0)
            return "暂无可用子代理。";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用代理");
        sb.AppendLine();
        sb.AppendLine("以下代理可供委托：");
        sb.AppendLine();

        foreach (var agent in subAgents)
        {
            var desc = agent.Description ?? "无描述";
            var shortDesc = desc.Split('.')[0];
            sb.AppendLine($"- **{agent.Name}**: {shortDesc}");
        }

        return sb.ToString();
    }

    private string BuildSkillSection(List<SkillInfo> skills)
    {
        if (skills.Count == 0)
            return "暂无可用技能。";

        var sb = new StringBuilder();
        sb.AppendLine("## 可用技能");
        sb.AppendLine();
        sb.AppendLine("以下技能可供使用：");
        sb.AppendLine();

        foreach (var skill in skills)
        {
            sb.AppendLine($"### {skill.Name}");
            sb.AppendLine(skill.Description);

            if (skill.Tags.Count > 0)
                sb.AppendLine($"**标签**: {string.Join(", ", skill.Tags)}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string BuildEnvironmentSection(PromptContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<env>");
        sb.AppendLine($"Working directory: {context.WorkingDirectory ?? "unknown"}");
        if (!string.IsNullOrEmpty(context.WorkspaceRoot))
            sb.AppendLine($"Workspace root: {context.WorkspaceRoot}");
        if (!string.IsNullOrEmpty(context.Platform))
            sb.AppendLine($"Platform: {context.Platform}");
        sb.AppendLine($"Today's date: {context.Timestamp:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(context.ModelName))
            sb.AppendLine($"Model: {context.ModelName}");
        sb.AppendLine("</env>");

        return sb.ToString();
    }

    private string ReplaceBuiltinVariables(string prompt, PromptContext context)
    {
        var result = prompt;

        if (!string.IsNullOrEmpty(context.ModelName))
            result = result.Replace("{{model}}", context.ModelName);

        if (!string.IsNullOrEmpty(context.SessionId))
            result = result.Replace("{{session_id}}", context.SessionId);

        if (!string.IsNullOrEmpty(context.WorkingDirectory))
            result = result.Replace("{{working_directory}}", context.WorkingDirectory);

        result = result.Replace("{{timestamp}}", context.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));

        return result;
    }

    private static string GetPropertyDescription(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return "未知类型";

        var sb = new StringBuilder();

        if (property.TryGetProperty("type", out var typeElement))
            sb.Append(typeElement.GetString() ?? "unknown");

        if (property.TryGetProperty("description", out var descElement))
        {
            var desc = descElement.GetString();
            if (!string.IsNullOrEmpty(desc))
                sb.Append($" - {desc}");
        }

        if (property.TryGetProperty("required", out var requiredElement) && requiredElement.GetBoolean())
            sb.Append(" (必需)");

        return sb.ToString();
    }

    #endregion
}
```

### 4. DI 扩展（添加到现有 Extensions）

```csharp
// 在 ServiceCollectionExtensions.cs 中添加

/// <summary>
/// 添加提示词构建服务
/// </summary>
public static IServiceCollection AddPromptBuilder(this IServiceCollection services)
{
    services.AddSingleton<SystemPromptProvider>();
    services.AddSingleton<PromptBuilder>();
    
    return services;
}
```

### 5. AgentExecutor 集成

```csharp
// 修改 AgentExecutor 中的 BuildSystemPromptAsync

private async Task<string?> BuildSystemPromptAsync(AgentDefinition agent, AgentContext context)
{
    var promptBuilder = context.Services?.GetService(typeof(PromptBuilder)) as PromptBuilder;
    
    if (promptBuilder != null)
    {
        var model = agent.Model;
        var modelId = model?.ModelId ?? ResolveModelId(agent);
        var providerId = model?.ProviderId ?? string.Empty;
        
        var promptContext = new PromptContext
        {
            Agent = agent,
            ModelName = modelId,
            ProviderId = providerId,
            WorkingDirectory = context.WorkingDirectory,
            // WorkspaceRoot 需在 AgentContext 中添加（如添加可设为 context.WorkingDirectory 作为兜底）
            Platform = Environment.OSVersion.Platform.ToString(),
            SessionId = context.SessionId,
            Services = context.Services,
            Tools = GetToolSchemas(agent).Select(s => s.Function)
        };
        
        return await promptBuilder.BuildAsync(promptContext);
    }
    
    // 向后兼容：使用旧逻辑
    return agent.SystemPrompt;
}
```

## 提示词模板

### Templates/default.txt

```
You are an AI coding assistant. Help the user with software engineering tasks.

# Tone and style
- Be concise and direct
- Use GitHub-flavored markdown
- Only use emojis if explicitly requested

# Following conventions
- Understand existing code conventions before making changes
- Mimic code style and patterns
- Follow security best practices

# Tool usage
- Use available tools to complete tasks
- Run tests and linting after making changes
- Never commit unless explicitly asked
```

### Templates/anthropic.txt

```
You are an AI coding assistant powered by Claude.

# Task Management
Use TodoWrite tools frequently to track progress and give visibility.

# Tone
- Be concise and direct
- Prioritize technical accuracy
- Use GitHub-flavored markdown

# Tool usage
- Call multiple tools in parallel when possible
- Use specialized tools instead of bash when available
```

### Templates/gpt.txt

```
You are an AI coding assistant.

# Editing Approach
- The best changes are often the smallest correct changes
- Keep things in one function unless composable
- Do not add backward-compatibility code without concrete need

# Autonomy
- Assume the user wants code changes or tool execution
- Persist until the task is fully handled

# Formatting
- Never use nested bullets, keep lists flat
- Use inline code blocks for commands and paths
```

### Templates/gemini.txt

```
You are an AI coding assistant.

# Core Mandates
- Rigorously adhere to project conventions
- Never assume a library is available without verification
- Mimic the style of existing code

# Tone
- Be concise and direct
- Keep responses under 3 lines when practical
- No conversational filler

# Security
- Never expose, log, or commit secrets
```

### Templates/beast.txt

```
You are an AI coding assistant with deep reasoning capabilities.

# Workflow
1. Understand the problem deeply before coding
2. Investigate the codebase thoroughly
3. Develop a clear step-by-step plan
4. Implement incrementally
5. Debug as needed
6. Test frequently
7. Iterate until complete

# Persistence
You MUST keep working until the problem is completely solved.
Do not end your turn until all tasks are finished.

# Communication
Be thorough in thinking but concise in output.
```

## csproj 更新

```xml
<ItemGroup>
  <EmbeddedResource Include="Core\Prompts\Templates\*.txt" />
</ItemGroup>
```

## 高精度审查结果

### 🔴 已修复的 Bug

| # | 位置 | 问题 | 修复 |
|---|------|------|------|
| 1 | `SelectTemplateName` | `o1-`/`o3-` 会遗漏 `o1`、`o1-mini` 等 | 改为 `o1`/`o3`（不含 `-`） |
| 2 | `PromptBuilder` 构造函数 | `_toolInvoker` 注入后从未使用 | 移除该依赖 |
| 3 | `AgentExecutor` 集成 | `ModelName` 混入 `ProviderId/ModelId` 组合值 | 分别设置 `ModelName` 和 `ProviderId` |
| 4 | `Build()` 同步方法 | 未处理 `{{environment}}`（数据已可用） | 添加环境信息替换 |
| 5 | 模板解析 | `Split('.').Reverse()` 对复杂命名空间脆弱 | 改用 `EndsWith` 匹配 |

### ✅ 验证通过

| 验证项 | 状态 | 说明 |
|--------|------|------|
| `ModelReference.ProviderId` | ✅ | 存在，string 类型 |
| `ModelReference.ModelId` | ✅ | 存在，string 类型 |
| `AgentContext.Services` | ✅ | 存在，`IServiceProvider?` |
| `AgentContext.WorkingDirectory` | ✅ | 存在 |
| `FunctionToolSchema.Function` | ✅ | 类型为 `FunctionSchema` |
| `PromptContext` 现有属性 | ✅ | `Tools`, `Agents`, `Skills`, `Variables`, `ModelName`, `SessionId` |
| `IInstructionLoader` | ✅ | 存在，`DiscoverAsync(baseDirectory, ct)` |
| `IAgentRegistry` | ✅ | 存在，`GetAgentsAsync()` |
| `SkillManager.GetAllSkillInfos()` | ✅ | 返回 `IReadOnlyDictionary<string, SkillInfo>` |

### ⚠️ 需注意的集成约束

| # | 约束 | 说明 |
|---|------|------|
| 1 | `AgentContext` 无 `WorkspaceRoot` 属性 | 需在 `AgentContext` 中添加此属性 |
| 2 | `SeeingAgentOptions` 需验证 `DefaultProvider` | 若不存在用空字符串替代 |

## 对比总结

| 方面 | 原设计（过度） | 精简设计 |
|------|---------------|----------|
| 文件数 | 19个 | 5个（1新增+1扩展+1重构+2模板） |
| 接口数 | 3个 | 0个 |
| 抽象层 | Contributor 模式 | 直接方法调用 |
| 内聚性 | 分散在多个类 | 保持在 PromptBuilder 内 |
| 复杂度 | 高 | 低 |
| 可维护性 | 需要理解多个类 | 单一入口点 |

## 精简原则

1. **不为扩展而过度抽象**：当前只有一种实现，不需要接口
2. **保持内聚**：格式化逻辑保持在构建器内部，不拆分
3. **最小改动**：扩展现有类而非创建新体系
4. **向后兼容**：保留同步 `Build()` 方法
