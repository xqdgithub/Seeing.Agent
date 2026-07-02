# Seeing.Agent 提示词构建架构重构设计

## 背景

当前 `DynamicPromptBuilder` 存在以下问题：

1. **单一方法集中**：所有逻辑集中在 `Build()` 方法，职责不清晰
2. **注入信息有限**：仅支持工具、代理、技能、变量，缺少：
   - Provider 特定的系统提示词
   - 环境信息（工作目录、平台、时间等）
   - 项目指令（AGENTS.md）
   - 动态上下文注入
3. **扩展困难**：添加新内容源需要修改核心类
4. **测试困难**：难以独立测试各部分逻辑

## 设计目标

参考 opencode 仓库的架构设计：

1. **分层架构**：SystemPrompt → Instruction → PromptBuilder → AgentExecutor
2. **服务化**：每个关注点独立服务
3. **可扩展**：通过 Contributor 模式注入内容
4. **Provider 适配**：支持不同 LLM Provider 的系统提示词

## 架构设计

### 整体架构图

```
┌─────────────────────────────────────────────────────────────────┐
│                      PromptBuilderService                        │
│  (主编排器 - 协调各贡献者，组装最终提示词)                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐│
│  │ SystemPrompt     │  │ Instruction      │  │ Environment    ││
│  │ Service          │  │ Service          │  │ Contributor    ││
│  │ (Provider特定)   │  │ (AGENTS.md)      │  │ (工作目录等)   ││
│  └──────────────────┘  └──────────────────┘  └────────────────┘│
│                                                                  │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐│
│  │ Tools            │  │ Agents           │  │ Skills         ││
│  │ Contributor      │  │ Contributor      │  │ Contributor    ││
│  └──────────────────┘  └──────────────────┘  └────────────────┘│
│                                                                  │
│  ┌──────────────────┐  ┌──────────────────┐                     │
│  │ Custom Variables │  │ Reminders        │                     │
│  │ Contributor      │  │ Contributor      │                     │
│  └──────────────────┘  └──────────────────┘                     │
├─────────────────────────────────────────────────────────────────┤
│                     IPromptContributor 接口                      │
└─────────────────────────────────────────────────────────────────┘
```

### 核心接口和模型

#### 1. PromptBuildContext - 构建上下文

```csharp
namespace Seeing.Agent.Core.Prompts.Models;

/// <summary>
/// 提示词构建上下文 - 包含构建所需的所有信息
/// </summary>
public class PromptBuildContext
{
    // 会话信息
    public string SessionId { get; init; } = string.Empty;
    public string? ParentSessionId { get; init; }
    
    // Agent 信息
    public AgentDefinition Agent { get; init; } = null!;
    public string AgentName => Agent.Name;
    
    // 模型信息
    public string ModelId { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string? ModelVariant { get; init; }
    
    // 环境信息
    public string WorkingDirectory { get; init; } = string.Empty;
    public string WorkspaceRoot { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    
    // 服务提供者（用于依赖注入）
    public IServiceProvider Services { get; init; } = null!;
    
    // 动态数据（可扩展）
    public Dictionary<string, object> Data { get; init; } = new();
}
```

#### 2. PromptSection - 内容片段

```csharp
namespace Seeing.Agent.Core.Prompts.Models;

/// <summary>
/// 提示词内容片段
/// </summary>
public class PromptSection
{
    /// <summary>片段名称（用于调试）</summary>
    public string Name { get; init; } = string.Empty;
    
    /// <summary>片段内容</summary>
    public string Content { get; init; } = string.Empty;
    
    /// <summary>优先级（排序用）</summary>
    public int Priority { get; init; }
    
    /// <summary>是否必需（即使为空也要包含）</summary>
    public bool Required { get; init; }
    
    /// <summary>元数据</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}
```

#### 3. IPromptContributor - 贡献者接口

```csharp
namespace Seeing.Agent.Core.Prompts.Abstractions;

/// <summary>
/// 提示词贡献者接口 - 用于向系统提示词注入内容
/// </summary>
public interface IPromptContributor
{
    /// <summary>贡献者名称</summary>
    string Name { get; }
    
    /// <summary>优先级（数值越小越先执行）</summary>
    int Priority { get; }
    
    /// <summary>是否适用于当前上下文</summary>
    ValueTask<bool> ShouldContributeAsync(PromptBuildContext context);
    
    /// <summary>贡献提示词内容</summary>
    ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default);
}
```

### 服务层设计

#### 1. SystemPromptService

负责根据 Provider/Model 选择基础系统提示词模板。

```csharp
namespace Seeing.Agent.Core.Prompts.Services;

public interface ISystemPromptService
{
    /// <summary>获取 Provider 特定的系统提示词模板</summary>
    Task<string> GetProviderPromptAsync(
        string providerId, 
        string modelId, 
        CancellationToken cancellationToken = default);
    
    /// <summary>注册自定义提示词模板</summary>
    void RegisterTemplate(string providerPattern, string template);
}
```

实现逻辑参考 opencode：
```typescript
// opencode/src/session/system.ts
export function provider(model: Provider.Model) {
  if (model.api.id.includes("gpt-4") || model.api.id.includes("o1") || model.api.id.includes("o3"))
    return [PROMPT_BEAST]
  if (model.api.id.includes("gpt")) return [PROMPT_GPT]
  if (model.api.id.includes("gemini-")) return [PROMPT_GEMINI]
  if (model.api.id.includes("claude")) return [PROMPT_ANTHROPIC]
  return [PROMPT_DEFAULT]
}
```

#### 2. PromptBuilderService

主编排器，协调所有贡献者。

```csharp
namespace Seeing.Agent.Core.Prompts.Services;

public interface IPromptBuilderService
{
    /// <summary>构建完整的系统提示词</summary>
    Task<string> BuildAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default);
    
    /// <summary>注册贡献者</summary>
    void RegisterContributor(IPromptContributor contributor);
    
    /// <summary>移除贡献者</summary>
    bool RemoveContributor(string name);
}
```

### 贡献者实现

#### 1. EnvironmentContributor

注入环境信息（工作目录、平台、时间等）。

```csharp
public class EnvironmentContributor : IPromptContributor
{
    public string Name => "Environment";
    public int Priority => 10; // 最先执行
    
    public ValueTask<bool> ShouldContributeAsync(PromptBuildContext context) 
        => ValueTask.FromResult(true);
    
    public ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var content = $"""
            <env>
              Working directory: {context.WorkingDirectory}
              Workspace root: {context.WorkspaceRoot}
              Platform: {context.Platform}
              Today's date: {context.Timestamp:yyyy-MM-dd}
            </env>
            """;
        
        return ValueTask.FromResult<IReadOnlyList<PromptSection>>(
            new[] { new PromptSection { Name = "Environment", Content = content, Priority = 10 } });
    }
}
```

#### 2. SystemPromptContributor

注入 Provider 特定的系统提示词。

```csharp
public class SystemPromptContributor : IPromptContributor
{
    private readonly ISystemPromptService _systemPromptService;
    
    public string Name => "SystemPrompt";
    public int Priority => 0; // 最基础
    
    // 从 _systemPromptService 获取模板...
}
```

#### 3. InstructionContributor

注入项目指令（AGENTS.md 等）。

```csharp
public class InstructionContributor : IPromptContributor
{
    private readonly IInstructionLoader _instructionLoader;
    
    public string Name => "Instructions";
    public int Priority => 100;
    
    // 从 IInstructionLoader 加载并合并指令...
}
```

#### 4. ToolsContributor

注入可用工具列表。

```csharp
public class ToolsContributor : IPromptContributor
{
    private readonly ToolInvoker _toolInvoker;
    
    public string Name => "Tools";
    public int Priority => 200;
    
    // 格式化工具列表...
}
```

#### 5. AgentsContributor

注入可用 Agent 列表。

#### 6. SkillsContributor

注入可用技能列表。

### 文件结构

```
Core/Prompts/
├── Abstractions/
│   ├── IPromptContributor.cs       # 贡献者接口
│   └── IPromptBuilderService.cs    # 构建器服务接口
├── Models/
│   ├── PromptBuildContext.cs       # 构建上下文
│   ├── PromptSection.cs            # 内容片段
│   └── PromptBuildOptions.cs       # 构建选项
├── Services/
│   ├── PromptBuilderService.cs     # 主构建服务
│   ├── SystemPromptService.cs      # Provider 特定提示词
│   └── PromptContributorRegistry.cs # 贡献者注册表
├── Contributors/
│   ├── EnvironmentContributor.cs   # 环境信息
│   ├── SystemPromptContributor.cs  # 基础提示词
│   ├── InstructionContributor.cs   # 指令加载
│   ├── ToolsContributor.cs         # 工具列表
│   ├── AgentsContributor.cs        # Agent 列表
│   ├── SkillsContributor.cs        # 技能列表
│   └── VariablesContributor.cs     # 自定义变量
├── Templates/
│   ├── default.txt                 # 默认提示词模板
│   ├── anthropic.txt               # Claude 特定
│   ├── gpt.txt                     # GPT 特定
│   ├── gemini.txt                  # Gemini 特定
│   └── beast.txt                   # GPT-4/o1/o3 特定
└── Extensions/
    └── PromptServiceExtensions.cs  # DI 扩展
```

### 集成到 AgentExecutor

修改 `AgentExecutor.BuildSystemPromptAsync`:

```csharp
private async Task<string?> BuildSystemPromptAsync(AgentDefinition agent, AgentContext context)
{
    var promptBuilder = context.Services.GetRequiredService<IPromptBuilderService>();
    
    var buildContext = new PromptBuildContext
    {
        SessionId = context.SessionId,
        ParentSessionId = context.ParentSessionId,
        Agent = agent,
        ModelId = ResolveModelId(agent),
        ProviderId = agent.Model?.ProviderId ?? _options.DefaultProvider,
        WorkingDirectory = context.WorkingDirectory,
        WorkspaceRoot = context.WorkspaceRoot,
        Platform = Environment.OSVersion.Platform.ToString(),
        Services = context.Services!
    };
    
    return await promptBuilder.BuildAsync(buildContext);
}
```

### 迁移计划

#### 阶段 1：基础设施（不破坏现有代码）

1. 创建新的 `Abstractions/` 接口
2. 创建新的 `Models/` 数据模型
3. 创建 `PromptBuilderService` 和 `SystemPromptService`
4. 创建所有 `Contributors/`

#### 阶段 2：并行运行

1. 添加配置开关切换新旧实现
2. 验证新实现功能完整

#### 阶段 3：迁移

1. 更新 `AgentExecutor` 使用新服务
2. 移除旧的 `DynamicPromptBuilder`
3. 更新测试

### 关键设计决策

#### 为什么保留内聚性

1. **Contributor 内聚**：每个 Contributor 只负责一种内容类型，但包含完整的格式化逻辑
2. **服务内聚**：`SystemPromptService` 只负责模板选择，不涉及其他逻辑

#### 为什么不拆分

1. **PromptSection 足够简单**：不需要进一步抽象
2. **Contributor 模式已足够灵活**：无需额外的策略模式或责任链

#### 扩展点

1. **新增内容源**：实现 `IPromptContributor`，注册到 DI
2. **Provider 适配**：继承 `SystemPromptService` 或添加模板文件
3. **动态内容**：在 `PromptBuildContext.Data` 中传递数据

## 测试策略

1. **单元测试**：每个 Contributor 独立测试
2. **集成测试**：验证完整构建流程
3. **对比测试**：新旧实现输出对比

## 性能考虑

1. **Contributor 缓存**：对于不常变化的内容（如工具列表）可缓存
2. **并行执行**：Contributor 可并行执行（无依赖时）
3. **延迟加载**：指令文件只在需要时加载

## 实现步骤

### 第一步：创建核心接口和模型

- [ ] `Abstractions/IPromptContributor.cs`
- [ ] `Abstractions/IPromptBuilderService.cs`
- [ ] `Models/PromptBuildContext.cs`
- [ ] `Models/PromptSection.cs`

### 第二步：实现服务层

- [ ] `Services/SystemPromptService.cs`
- [ ] `Services/PromptBuilderService.cs`
- [ ] `Services/PromptContributorRegistry.cs`

### 第三步：实现贡献者

- [ ] `Contributors/EnvironmentContributor.cs`
- [ ] `Contributors/SystemPromptContributor.cs`
- [ ] `Contributors/InstructionContributor.cs`
- [ ] `Contributors/ToolsContributor.cs`
- [ ] `Contributors/AgentsContributor.cs`
- [ ] `Contributors/SkillsContributor.cs`
- [ ] `Contributors/VariablesContributor.cs`

### 第四步：创建模板

- [ ] 从 opencode 移植提示词模板
- [ ] 添加嵌入资源支持

### 提示词模板内容

从 opencode 仓库移植以下模板（存储为嵌入资源）：

#### 1. default.txt - 默认模板
适用于所有未匹配的 Provider，包含：
- CLI 工具基本行为规范
- 代码风格要求
- 工具使用策略
- 简洁输出要求

#### 2. anthropic.txt - Claude 特定
针对 Claude 模型优化：
- 强调 TodoWrite 工具使用
- 任务管理指导
- 并行工具调用支持
- 专业客观性强调

#### 3. gpt.txt - GPT 系列特定
针对 OpenAI GPT 模型：
- commentary/final 双通道输出
- 编辑约束说明
- Git 工作流规范
- 前端任务指导

#### 4. gemini.txt - Gemini 特定
针对 Google Gemini 模型：
- 安全执行流程
- 路径构建规则
- 详细示例
- 交互细节

#### 5. beast.txt - 高推理模型（GPT-4/o1/o3）
针对需要深度推理的任务：
- 迭代工作流
- 互联网研究要求
- 详细计划和验证步骤
- 调试指南

#### Provider 匹配逻辑

```csharp
public string GetTemplateForModel(string modelId)
{
    if (modelId.Contains("gpt-4") || modelId.Contains("o1") || modelId.Contains("o3"))
        return "beast";
    if (modelId.Contains("gpt"))
        return "gpt";
    if (modelId.Contains("gemini-"))
        return "gemini";
    if (modelId.Contains("claude"))
        return "anthropic";
    return "default";
}
```

### 第五步：集成

- [ ] 更新 `AgentExecutor`
- [ ] 更新 DI 扩展
- [ ] 移除旧代码

### 第六步：测试

- [ ] 单元测试
- [ ] 集成测试
- [ ] 性能测试

---

## 完整实现代码

以下是需要创建的所有文件的完整代码实现。

### 核心接口和模型

#### Abstractions/IPromptContributor.cs

```csharp
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Abstractions;

/// <summary>
/// 提示词贡献者接口 - 用于向系统提示词注入内容
/// <para>
/// 实现此接口以贡献提示词片段。所有贡献者按优先级排序执行，
/// 通过 PromptBuilderService 协调组装最终提示词。
/// </para>
/// </summary>
public interface IPromptContributor
{
    /// <summary>贡献者名称（用于调试和日志）</summary>
    string Name { get; }

    /// <summary>
    /// 优先级（数值越小越先执行）
    /// <para>
    /// 建议优先级范围：
    /// - 0-99: 核心系统信息（环境、模型信息）
    /// - 100-199: 指令和规则
    /// - 200-299: 工具和能力
    /// - 300-399: Agent 和技能
    /// - 400+: 自定义扩展
    /// </para>
    /// </summary>
    int Priority { get; }

    /// <summary>判断是否适用于当前上下文</summary>
    ValueTask<bool> ShouldContributeAsync(PromptBuildContext context);

    /// <summary>贡献提示词内容</summary>
    ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default);
}
```

#### Abstractions/IPromptBuilderService.cs

```csharp
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Abstractions;

/// <summary>
/// 提示词构建服务接口 - 主编排器
/// <para>
/// 协调所有 IPromptContributor，按优先级组装最终的系统提示词。
/// </para>
/// </summary>
public interface IPromptBuilderService
{
    /// <summary>构建完整的系统提示词</summary>
    Task<string> BuildAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default);

    /// <summary>注册贡献者</summary>
    void RegisterContributor(IPromptContributor contributor);

    /// <summary>移除贡献者</summary>
    bool RemoveContributor(string name);

    /// <summary>获取所有已注册的贡献者</summary>
    IReadOnlyList<IPromptContributor> GetContributors();
}
```

#### Models/PromptBuildContext.cs

```csharp
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Prompts.Models;

/// <summary>
/// 提示词构建上下文 - 包含构建所需的所有信息
/// </summary>
public class PromptBuildContext
{
    #region 会话信息

    /// <summary>会话 ID</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>父会话 ID（子 Agent 场景）</summary>
    public string? ParentSessionId { get; init; }

    #endregion

    #region Agent 信息

    /// <summary>Agent 定义</summary>
    public AgentDefinition Agent { get; init; } = null!;

    /// <summary>Agent 名称（便捷访问）</summary>
    public string AgentName => Agent.Name;

    #endregion

    #region 模型信息

    /// <summary>模型 ID</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Provider ID</summary>
    public string ProviderId { get; init; } = string.Empty;

    /// <summary>模型变体（可选）</summary>
    public string? ModelVariant { get; init; }

    #endregion

    #region 环境信息

    /// <summary>工作目录</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>工作区根目录</summary>
    public string WorkspaceRoot { get; init; } = string.Empty;

    /// <summary>平台信息</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    #endregion

    #region 服务提供者

    /// <summary>服务提供者（用于依赖注入）</summary>
    public IServiceProvider Services { get; init; } = null!;

    #endregion

    #region 动态数据

    /// <summary>动态数据（可扩展）</summary>
    public Dictionary<string, object> Data { get; init; } = new();

    #endregion
}
```

#### Models/PromptSection.cs

```csharp
namespace Seeing.Agent.Core.Prompts.Models;

/// <summary>
/// 提示词内容片段
/// </summary>
public class PromptSection
{
    /// <summary>片段名称（用于调试和日志）</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>片段内容</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>优先级（排序用，数值越小越靠前）</summary>
    public int Priority { get; init; }

    /// <summary>是否必需（即使内容为空也要包含）</summary>
    public bool Required { get; init; }

    /// <summary>元数据（用于传递额外信息）</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}
```

### 服务层实现

#### Services/SystemPromptService.cs

```csharp
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Prompts.Services;

/// <summary>
/// 系统提示词服务 - Provider 特定提示词模板管理
/// </summary>
public interface ISystemPromptService
{
    /// <summary>获取 Provider 特定的系统提示词模板</summary>
    Task<string> GetProviderPromptAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>注册自定义提示词模板</summary>
    void RegisterTemplate(string providerPattern, string template);
}

/// <summary>
/// 系统提示词服务实现
/// </summary>
public class SystemPromptService : ISystemPromptService
{
    private readonly ILogger<SystemPromptService> _logger;
    private readonly Dictionary<string, string> _customTemplates = new();
    private readonly Dictionary<string, string> _templateCache = new();

    public SystemPromptService(ILogger<SystemPromptService> logger)
    {
        _logger = logger;
        LoadEmbeddedTemplates();
    }

    private void LoadEmbeddedTemplates()
    {
        var assembly = typeof(SystemPromptService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.Contains("Prompts.Templates") && n.EndsWith(".txt"));

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            
            // 提取模板名称（去掉命名空间前缀和扩展名）
            var templateName = name.Split('.').Reverse().Skip(1).First();
            _templateCache[templateName] = content;
            
            _logger.LogDebug("加载嵌入模板: {TemplateName}, 长度: {Length}", templateName, content.Length);
        }
    }

    public Task<string> GetProviderPromptAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        // 检查自定义模板
        foreach (var (pattern, template) in _customTemplates)
        {
            if (MatchesPattern(modelId, pattern) || MatchesPattern(providerId, pattern))
            {
                _logger.LogDebug("使用自定义模板: {Pattern}", pattern);
                return Task.FromResult(template);
            }
        }

        // 根据 Model ID 选择模板（参考 opencode 逻辑）
        var templateName = GetTemplateNameForModel(modelId);
        
        if (_templateCache.TryGetValue(templateName, out var content))
        {
            return Task.FromResult(content);
        }

        // 回退到默认模板
        if (_templateCache.TryGetValue("default", out var defaultContent))
        {
            return Task.FromResult(defaultContent);
        }

        _logger.LogWarning("未找到匹配的模板，ModelId: {ModelId}, ProviderId: {ProviderId}", modelId, providerId);
        return Task.FromResult(string.Empty);
    }

    public void RegisterTemplate(string providerPattern, string template)
    {
        _customTemplates[providerPattern] = template;
        _logger.LogInformation("注册自定义模板: {Pattern}", providerPattern);
    }

    private static string GetTemplateNameForModel(string modelId)
    {
        var lowerModelId = modelId.ToLowerInvariant();
        
        // 高推理模型（GPT-4, o1, o3）
        if (lowerModelId.Contains("gpt-4") || lowerModelId.Contains("o1-") || lowerModelId.Contains("o3-"))
            return "beast";
        
        // GPT 系列
        if (lowerModelId.Contains("gpt"))
            return "gpt";
        
        // Gemini
        if (lowerModelId.Contains("gemini-"))
            return "gemini";
        
        // Claude
        if (lowerModelId.Contains("claude"))
            return "anthropic";
        
        // 默认
        return "default";
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
        {
            return value.Contains(pattern.Trim('*'), StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith("*"))
        {
            return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.EndsWith("*"))
        {
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
```

#### Services/PromptBuilderService.cs

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Services;

/// <summary>
/// 提示词构建服务 - 主编排器实现
/// </summary>
public class PromptBuilderService : IPromptBuilderService
{
    private readonly ILogger<PromptBuilderService> _logger;
    private readonly ISystemPromptService _systemPromptService;
    private readonly List<IPromptContributor> _contributors = new();

    public PromptBuilderService(
        ILogger<PromptBuilderService> logger,
        ISystemPromptService systemPromptService)
    {
        _logger = logger;
        _systemPromptService = systemPromptService;
    }

    public async Task<string> BuildAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var sections = new List<PromptSection>();
        var sortedContributors = _contributors.OrderBy(c => c.Priority).ToList();

        _logger.LogDebug("开始构建提示词，已注册 {Count} 个贡献者", sortedContributors.Count);

        foreach (var contributor in sortedContributors)
        {
            try
            {
                if (!await contributor.ShouldContributeAsync(context))
                {
                    _logger.LogDebug("贡献者 {Name} 不适用于当前上下文，跳过", contributor.Name);
                    continue;
                }

                var contributedSections = await contributor.ContributeAsync(context, cancellationToken);
                
                if (contributedSections.Count > 0)
                {
                    sections.AddRange(contributedSections);
                    _logger.LogDebug("贡献者 {Name} 贡献了 {Count} 个片段", 
                        contributor.Name, contributedSections.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "贡献者 {Name} 执行失败", contributor.Name);
            }
        }

        // 按优先级排序所有片段
        var sortedSections = sections.OrderBy(s => s.Priority).ToList();

        // 组装最终提示词
        var builder = new StringBuilder();
        
        foreach (var section in sortedSections)
        {
            if (string.IsNullOrEmpty(section.Content) && !section.Required)
            {
                continue;
            }

            builder.AppendLine(section.Content);
            builder.AppendLine(); // 添加空行分隔
        }

        var result = builder.ToString().Trim();
        
        _logger.LogInformation("提示词构建完成，总长度: {Length}，片段数: {Count}", 
            result.Length, sortedSections.Count);

        return result;
    }

    public void RegisterContributor(IPromptContributor contributor)
    {
        if (_contributors.Any(c => c.Name == contributor.Name))
        {
            _logger.LogWarning("贡献者 {Name} 已存在，跳过注册", contributor.Name);
            return;
        }

        _contributors.Add(contributor);
        _logger.LogDebug("注册贡献者: {Name}, 优先级: {Priority}", contributor.Name, contributor.Priority);
    }

    public bool RemoveContributor(string name)
    {
        var contributor = _contributors.FirstOrDefault(c => c.Name == name);
        if (contributor != null)
        {
            _contributors.Remove(contributor);
            _logger.LogDebug("移除贡献者: {Name}", name);
            return true;
        }
        return false;
    }

    public IReadOnlyList<IPromptContributor> GetContributors()
    {
        return _contributors.OrderBy(c => c.Priority).ToList().AsReadOnly();
    }
}
```

### 贡献者实现

#### Contributors/EnvironmentContributor.cs

```csharp
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// 环境信息贡献者 - 注入工作目录、平台、时间等环境信息
/// </summary>
public class EnvironmentContributor : IPromptContributor
{
    public string Name => "Environment";
    public int Priority => 10;

    public ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        return ValueTask.FromResult(true);
    }

    public ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var content = $"""
            <env>
            Working directory: {context.WorkingDirectory}
            Workspace root: {context.WorkspaceRoot}
            Platform: {context.Platform}
            Today's date: {context.Timestamp:yyyy-MM-dd}
            Model: {context.ProviderId}/{context.ModelId}
            </env>
            """;

        return ValueTask.FromResult<IReadOnlyList<PromptSection>>(
            new[]
            {
                new PromptSection
                {
                    Name = "Environment",
                    Content = content,
                    Priority = Priority
                }
            });
    }
}
```

#### Contributors/SystemPromptContributor.cs

```csharp
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;
using Seeing.Agent.Core.Prompts.Services;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// 系统提示词贡献者 - 注入 Provider 特定的基础系统提示词
/// </summary>
public class SystemPromptContributor : IPromptContributor
{
    private readonly ISystemPromptService _systemPromptService;

    public string Name => "SystemPrompt";
    public int Priority => 0;

    public SystemPromptContributor(ISystemPromptService systemPromptService)
    {
        _systemPromptService = systemPromptService;
    }

    public ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        return ValueTask.FromResult(true);
    }

    public async ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var template = await _systemPromptService.GetProviderPromptAsync(
            context.ProviderId,
            context.ModelId,
            cancellationToken);

        if (string.IsNullOrEmpty(template))
        {
            return Array.Empty<PromptSection>();
        }

        return new[]
        {
            new PromptSection
            {
                Name = "SystemPrompt",
                Content = template,
                Priority = Priority,
                Required = true
            }
        };
    }
}
```

#### Contributors/InstructionContributor.cs

```csharp
using Seeing.Agent.Core.Instructions;
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// 指令贡献者 - 注入 AGENTS.md 等项目指令
/// </summary>
public class InstructionContributor : IPromptContributor
{
    private readonly IInstructionLoader _instructionLoader;

    public string Name => "Instructions";
    public int Priority => 100;

    public InstructionContributor(IInstructionLoader instructionLoader)
    {
        _instructionLoader = instructionLoader;
    }

    public async ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        var files = await _instructionLoader.DiscoverAsync(context.WorkingDirectory);
        return files.Count > 0;
    }

    public async ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var files = await _instructionLoader.DiscoverAsync(context.WorkingDirectory, cancellationToken);
        
        if (files.Count == 0)
        {
            return Array.Empty<PromptSection>();
        }

        var mergedContent = _instructionLoader.Merge(files);
        
        return new[]
        {
            new PromptSection
            {
                Name = "Instructions",
                Content = mergedContent,
                Priority = Priority
            }
        };
    }
}
```

#### Contributors/ToolsContributor.cs

```csharp
using System.Text;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// 工具列表贡献者 - 注入可用工具列表
/// </summary>
public class ToolsContributor : IPromptContributor
{
    private readonly ToolInvoker _toolInvoker;

    public string Name => "Tools";
    public int Priority => 200;

    public ToolsContributor(ToolInvoker toolInvoker)
    {
        _toolInvoker = toolInvoker;
    }

    public ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        return ValueTask.FromResult(true);
    }

    public ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var agentInfo = new AgentInfo
        {
            Name = context.AgentName,
            Mode = context.Agent.Mode,
            AllowedTools = context.Agent.AllowedTools,
            DeniedTools = context.Agent.DeniedTools
        };

        var schemas = _toolInvoker.GetToolSchemasForAgent(agentInfo);
        
        if (schemas.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<PromptSection>>(
                new[]
                {
                    new PromptSection
                    {
                        Name = "Tools",
                        Content = "暂无可用工具。",
                        Priority = Priority
                    }
                });
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 可用工具");
        sb.AppendLine();
        sb.AppendLine("以下工具可供调用：");
        sb.AppendLine();

        foreach (var tool in schemas)
        {
            sb.AppendLine($"### {tool.Function.Name}");
            if (!string.IsNullOrEmpty(tool.Function.Description))
            {
                sb.AppendLine(tool.Function.Description);
            }
            sb.AppendLine();
        }

        return ValueTask.FromResult<IReadOnlyList<PromptSection>>(
            new[]
            {
                new PromptSection
                {
                    Name = "Tools",
                    Content = sb.ToString(),
                    Priority = Priority
                }
            });
    }
}
```

#### Contributors/AgentsContributor.cs

```csharp
using System.Text;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// Agent 列表贡献者 - 注入可用子代理列表
/// </summary>
public class AgentsContributor : IPromptContributor
{
    private readonly IAgentRegistry _agentRegistry;

    public string Name => "Agents";
    public int Priority => 300;

    public AgentsContributor(IAgentRegistry agentRegistry)
    {
        _agentRegistry = agentRegistry;
    }

    public async ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        var agents = await _agentRegistry.GetAgentsAsync();
        return agents.Any(a => a.Mode == AgentMode.SubAgent && !a.Name.Equals(context.AgentName, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var agents = await _agentRegistry.GetAgentsAsync();
        
        var subAgents = agents
            .Where(a => a.Mode == AgentMode.SubAgent && !a.Name.Equals(context.AgentName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (subAgents.Count == 0)
        {
            return Array.Empty<PromptSection>();
        }

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

        return new[]
        {
            new PromptSection
            {
                Name = "Agents",
                Content = sb.ToString(),
                Priority = Priority
            }
        };
    }
}
```

#### Contributors/SkillsContributor.cs

```csharp
using System.Text;
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;
using Seeing.Agent.Skills;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// 技能列表贡献者 - 注入可用技能列表
/// </summary>
public class SkillsContributor : IPromptContributor
{
    private readonly SkillManager _skillManager;

    public string Name => "Skills";
    public int Priority => 310;

    public SkillsContributor(SkillManager skillManager)
    {
        _skillManager = skillManager;
    }

    public ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        var skills = _skillManager.GetAllSkillInfos();
        return ValueTask.FromResult(skills.Count > 0);
    }

    public ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        var skills = _skillManager.GetAllSkillInfos();
        
        if (skills.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<PromptSection>>(Array.Empty<PromptSection>());
        }

        var sb = new StringBuilder();
        sb.AppendLine("## 可用技能");
        sb.AppendLine();
        sb.AppendLine("以下技能可供使用：");
        sb.AppendLine();

        foreach (var kvp in skills)
        {
            var skill = kvp.Value;
            sb.AppendLine($"### {skill.Name}");
            sb.AppendLine(skill.Description);
            
            if (skill.Tags.Count > 0)
            {
                sb.AppendLine($"**标签**: {string.Join(", ", skill.Tags)}");
            }
            
            sb.AppendLine();
        }

        return ValueTask.FromResult<IReadOnlyList<PromptSection>>(
            new[]
            {
                new PromptSection
                {
                    Name = "Skills",
                    Content = sb.ToString(),
                    Priority = Priority
                }
            });
    }
}
```

#### Contributors/VariablesContributor.cs

```csharp
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Models;

namespace Seeing.Agent.Core.Prompts.Contributors;

/// <summary>
/// 自定义变量贡献者 - 注入用户定义的变量
/// </summary>
public class VariablesContributor : IPromptContributor
{
    public string Name => "Variables";
    public int Priority => 400;

    public ValueTask<bool> ShouldContributeAsync(PromptBuildContext context)
    {
        var hasVariables = context.Data.TryGetValue("Variables", out var vars) 
            && vars is Dictionary<string, string> dict 
            && dict.Count > 0;
        
        return ValueTask.FromResult(hasVariables);
    }

    public ValueTask<IReadOnlyList<PromptSection>> ContributeAsync(
        PromptBuildContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Data.TryGetValue("Variables", out var vars) || vars is not Dictionary<string, string> variables)
        {
            return ValueTask.FromResult<IReadOnlyList<PromptSection>>(Array.Empty<PromptSection>());
        }

        // 变量替换由 PromptBuilderService 在最后阶段处理
        // 这里只返回变量数据供后续使用
        return ValueTask.FromResult<IReadOnlyList<PromptSection>>(
            new[]
            {
                new PromptSection
                {
                    Name = "Variables",
                    Content = string.Empty,
                    Priority = Priority,
                    Metadata = { ["Variables"] = variables }
                }
            });
    }
}
```

### DI 扩展

#### Extensions/PromptServiceExtensions.cs

```csharp
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Prompts.Abstractions;
using Seeing.Agent.Core.Prompts.Contributors;
using Seeing.Agent.Core.Prompts.Services;

namespace Seeing.Agent.Core.Prompts.Extensions;

/// <summary>
/// 提示词服务 DI 扩展
/// </summary>
public static class PromptServiceExtensions
{
    /// <summary>
    /// 添加提示词构建服务
    /// </summary>
    public static IServiceCollection AddPromptBuilder(this IServiceCollection services)
    {
        // 注册服务
        services.AddSingleton<ISystemPromptService, SystemPromptService>();
        services.AddSingleton<IPromptBuilderService, PromptBuilderService>();

        // 注册默认贡献者
        services.AddSingleton<IPromptContributor, EnvironmentContributor>();
        services.AddSingleton<IPromptContributor, SystemPromptContributor>();
        services.AddSingleton<IPromptContributor, InstructionContributor>();
        services.AddSingleton<IPromptContributor, ToolsContributor>();
        services.AddSingleton<IPromptContributor, AgentsContributor>();
        services.AddSingleton<IPromptContributor, SkillsContributor>();
        services.AddSingleton<IPromptContributor, VariablesContributor>();

        return services;
    }
}
```

---

## 执行计划

1. 创建目录结构 ✅
2. 实现核心接口和模型（4个文件）- 进行中
3. 实现服务层（2个文件）- 进行中
4. 实现贡献者（7个文件）- 进行中
5. 创建提示词模板嵌入资源
6. 更新 DI 扩展 - 进行中
7. 重构 AgentExecutor 使用新服务
8. 移除旧的 DynamicPromptBuilder
9. 添加单元测试

---

## 提示词模板文件（嵌入资源）

需要在 `Core/Prompts/Templates/` 目录下创建以下文件，并设置为嵌入资源（EmbeddedResource）。

### Templates/default.txt

```
You are Seeing.Agent, an interactive CLI tool that helps users with software engineering tasks. Use the instructions below and the tools available to you to assist the user.

IMPORTANT: You must NEVER generate or guess URLs for the user unless you are confident that the URLs are for helping the user with programming.

# Tone and style
You should be concise, direct, and to the point. Remember that your output will be displayed on a command line interface. Your responses can use GitHub-flavored markdown for formatting.

Only use emojis if the user explicitly requests it. Avoid using emojis in all communication unless asked.

IMPORTANT: You should minimize output tokens as much as possible while maintaining helpfulness, quality, and accuracy.

# Following conventions
When making changes to files, first understand the file's code conventions. Mimic code style, use existing libraries and utilities, and follow existing patterns.

# Code style
- IMPORTANT: DO NOT ADD ***ANY*** COMMENTS unless asked

# Doing tasks
The user will primarily request you perform software engineering tasks. For these tasks:
- Use the available search tools to understand the codebase
- Implement the solution using all tools available
- Verify the solution if possible with tests
- NEVER commit changes unless the user explicitly asks you to

# Tool usage policy
- When doing file search, prefer to use the Task tool in order to reduce context usage
- You have the capability to call multiple tools in a single response
```

### Templates/anthropic.txt

```
You are Seeing.Agent, the best coding agent on the planet.

You are an interactive CLI tool that helps users with software engineering tasks. Use the instructions below and the tools available to you to assist the user.

# Tone and style
- Only use emojis if the user explicitly requests it
- Your output will be displayed on a command line interface. Keep responses short and concise

# Task Management
You have access to the TodoWrite tools to help you manage and plan tasks. Use these tools VERY frequently to ensure that you are tracking your tasks and giving the user visibility into your progress.

It is critical that you mark todos as completed as soon as you are done with a task.

# Professional objectivity
Prioritize technical accuracy and truthfulness over validating the user's beliefs. Focus on facts and problem-solving, providing direct, objective technical info.

# Tool usage policy
- When doing file search, prefer to use the Task tool in order to reduce context usage
- You should proactively use the Task tool with specialized agents when the task at hand matches the agent's description
- You can call multiple tools in a single response

IMPORTANT: Always use the TodoWrite tool to plan and track tasks throughout the conversation.
```

### Templates/gpt.txt

```
You are Seeing.Agent, You and the user share the same workspace and collaborate to achieve the user's goals.

You are a deeply pragmatic, effective software engineer. You take engineering quality seriously.

- When searching for text or files, prefer using Glob and Grep tools
- Parallelize tool calls whenever possible - especially file reads

## Editing Approach
- The best changes are often the smallest correct changes
- Keep things in one function unless composable or reusable
- Do not add backward-compatibility code unless there is a concrete need

## Autonomy and persistence
Unless the user explicitly asks for a plan, assume the user wants you to make code changes or run tools to solve the problem.

## Formatting rules
- Never use nested bullets. Keep lists flat
- Use inline code blocks for commands, paths, function names
- Don't use emojis or em dashes unless explicitly instructed
```

### Templates/gemini.txt

```
You are Seeing.Agent, an interactive CLI agent specializing in software engineering tasks.

# Core Mandates
- **Conventions:** Rigorously adhere to existing project conventions when reading or modifying code
- **Libraries/Frameworks:** NEVER assume a library/framework is available or appropriate. Verify its usage first
- **Style & Structure:** Mimic the style (formatting, naming), structure of existing code
- **Comments:** Add code comments sparingly. Focus on *why* something is done

# Primary Workflows
## Software Engineering Tasks
1. **Understand:** Use search tools to understand file structures and existing patterns
2. **Plan:** Build a coherent plan for how to resolve the task
3. **Implement:** Use available tools to act on the plan
4. **Verify:** Verify the changes using testing procedures
5. **Verify (Standards):** Execute build, linting and type-checking commands

# Operational Guidelines
## Tone and Style (CLI Interaction)
- **Concise & Direct:** Adopt a professional, direct tone suitable for CLI
- **Minimal Output:** Aim for fewer than 3 lines of text output per response
- **No Chitchat:** Avoid conversational filler

## Security and Safety Rules
- **Security First:** Never introduce code that exposes, logs, or commits secrets
```

### Templates/beast.txt

```
You are Seeing.Agent, an agent - please keep going until the user's query is completely resolved, before ending your turn.

Your thinking should be thorough and so it's fine if it's very long. However, avoid unnecessary repetition.

You MUST iterate and keep going until the problem is solved.

You have everything you need to resolve this problem. I want you to fully solve this autonomously before coming back to me.

Only terminate your turn when you are sure that the problem is solved.

# Workflow
1. Understand the problem deeply
2. Investigate the codebase
3. Research the problem if needed
4. Develop a clear, step-by-step plan with a todo list
5. Implement incrementally
6. Debug as needed
7. Test frequently
8. Iterate until complete

Take your time and think through every step - remember to check your solution rigorously.

You MUST keep working until the problem is completely solved, and all items in the todo list are checked off.

# Communication Guidelines
Always communicate clearly and concisely in a casual, friendly yet professional tone.
```

### 项目文件更新

需要在 `Seeing.Agent.csproj` 中添加嵌入资源配置：

```xml
<ItemGroup>
  <EmbeddedResource Include="Core\Prompts\Templates\*.txt" />
</ItemGroup>
```

---

## 实现总结

### 架构优势

1. **分层清晰**：SystemPrompt → Contributors → PromptBuilder → AgentExecutor
2. **可扩展**：通过 `IPromptContributor` 接口轻松添加新内容源
3. **Provider 适配**：支持不同 LLM Provider 的系统提示词模板
4. **关注点分离**：每个 Contributor 只负责一种内容类型
5. **可测试**：每个组件可独立测试

### 文件清单（19个文件）

| 类别 | 文件数 | 文件列表 |
|------|--------|----------|
| Abstractions | 2 | IPromptContributor.cs, IPromptBuilderService.cs |
| Models | 2 | PromptBuildContext.cs, PromptSection.cs |
| Services | 2 | SystemPromptService.cs, PromptBuilderService.cs |
| Contributors | 7 | Environment, SystemPrompt, Instruction, Tools, Agents, Skills, Variables |
| Extensions | 1 | PromptServiceExtensions.cs |
| Templates | 5 | default.txt, anthropic.txt, gpt.txt, gemini.txt, beast.txt |

### 下一步行动

由构建代理（build agent）执行：

1. **读取设计文档**：`E:\Projects\CSharp\Seeing.Agent\.sisyphus\prompt-architecture-refactor.md`
2. **创建文件**：按照"完整实现代码"部分的代码创建所有文件
3. **更新 csproj**：添加 EmbeddedResource 配置
4. **验证编译**：确保无编译错误
5. **运行测试**：验证功能正确性

### 参考资源

- OpenCode 仓库：`E:\Projects\Ts\opencode`
- OpenCode 系统提示词架构：`packages/opencode/src/session/system.ts`
- OpenCode 提示词模板：`packages/opencode/src/session/prompt/*.txt`
- OpenCode 指令加载：`packages/opencode/src/session/instruction.ts`
