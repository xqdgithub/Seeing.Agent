# Agent 配置系统设计文档

**版本:** 1.0.0
**创建日期:** 2025-01-15
**状态:** 待实现

---

## 一、背景与目标

### 1.1 问题陈述

当前 Seeing.Agent 框架的 Agent 配置存在以下问题：

1. **提示词嵌入代码**：Agent 的 SystemPrompt 硬编码在 C# 类中，修改需要重新编译
2. **配置界面受限**：WebUI 的配置编辑器功能有限，无法编辑所有 AgentConfig 属性
3. **Provider/Model 优化逻辑不可配置**：针对特定 Provider 或 Model 的优化（如 Temperature 调整、特殊指令）无法通过配置修改
4. **缺乏本地化文件支持**：没有 MD 文件格式支持本地配置覆盖和自动加载

### 1.2 目标

设计一个 Agent 配置系统，实现：

- **全量配置支持**：覆盖 AgentConfig 所有属性（SystemPrompt、权限规则、工具限制、模型参数等）
- **MD 文件配置**：使用 YAML Front Matter + Markdown 格式，便于版本控制和编辑
- **双层目录结构**：用户级 + 项目级配置，支持团队共享和个人定制
- **变体机制**：支持 Provider/Model 级别的配置覆盖
- **与现有系统集成**：复用 seeing.json 配置、Hook 系统等

---

## 二、架构设计

### 2.1 整体架构

```
┌─────────────────────────────────────────────────────────────────┐
│                     Agent Configuration System                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Configuration Sources (优先级从低到高):                          │
│                                                                  │
│  ┌──────────────────┐   ┌──────────────────┐   ┌──────────────┐ │
│  │ Code Built-in    │ → │ User-level MD    │ → │ Project MD   │ │
│  │ AgentDefinition  │   │ ~/.seeing/agents │   │ .seeing/     │ │
│  └──────────────────┘   └──────────────────┘   └──────────────┘ │
│           │                      │                     │        │
│           └──────────────────────┴─────────────────────┘        │
│                                  ↓                               │
│                    ┌──────────────────────┐                      │
│                    │ AgentConfigLoader    │                      │
│                    │ - 发现 MD 文件       │                      │
│                    │ - 解析 Front Matter  │                      │
│                    │ - 选择变体          │                      │
│                    │ - 合并配置          │                      │
│                    └──────────────────────┘                      │
│                                  ↓                               │
│                    ┌──────────────────────┐                      │
│                    │ AgentDefinition      │                      │
│                    │ (运行时配置对象)      │                      │
│                    └──────────────────────┘                      │
│                                  ↓                               │
│                    ┌──────────────────────┐                      │
│                    │ AgentRegistry        │                      │
│                    │ (注册 & 执行)        │                      │
│                    └──────────────────────┘                      │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 配置优先级

合并优先级从低到高：

```
1. 代码内置 AgentDefinition（AgentBase.Definition）
2. 用户级 MD 文件（~/.seeing/agents/{agent-name}.md）
3. 项目级 MD 文件（./.seeing/agents/{agent-name}.md）
4. 项目级 seeing.json（SeeingAgent.Agents.{agent-name}）
```

**合并策略**：属性级深度合并（非整体替换）

---

## 三、文件格式设计

### 3.1 文件位置

```
~/.seeing/agents/{agent-name}.md          # 用户级配置
./.seeing/agents/{agent-name}.md          # 项目级配置
```

### 3.2 文件格式

使用 YAML Front Matter + Markdown 格式：

```markdown
---
# === 基础配置（所有变体继承）===
name: sisyphus
description: 主工作代理，负责执行复杂任务和编排子代理
mode: Primary
category: orchestrator
maxSteps: 100

# === 权限规则 ===
permissionRules:
  - kind: tool
    pattern: question
    effect: allow
    priority: 0
  - kind: tool
    pattern: call_omo_agent
    effect: deny
    priority: 100

# === 工具限制 ===
allowedTools: []
deniedTools: []

# === 变体定义（Provider/Model 特定覆盖）===
variants:
  openai:
    model: gpt-4o
    temperature: 0.7
    maxTokens: 4096
  
  anthropic:
    model: claude-sonnet-4-20250514
    temperature: 0.5
    systemPromptAppend: |
      ## Claude 特定指令
      使用 XML 标签结构化输出。
  
  openai.gpt-4o-mini:
    model: gpt-4o-mini
    temperature: 0.3
    maxSteps: 50
---

你是 Sisyphus，AI Agent 编排器，负责协调子代理完成复杂任务。

## 角色定位

你不是执行者，而是决策者和协调者。你的职责是：
- 解析用户的隐式需求
- 将专业工作委托给合适的子代理
- 并行执行以最大化吞吐量
- 遵循用户指令，不擅自开始实现

...（其余 SystemPrompt 内容）
```

### 3.3 Front Matter 字段定义

#### 基础字段

| 字段 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `name` | string | ✓ | Agent 唯一标识，与文件名一致 |
| `description` | string | | Agent 描述，用于 UI 显示 |
| `mode` | string | | 运行模式：Primary / SubAgent / All |
| `category` | string | | 分类标签，用于 UI 分组 |
| `maxSteps` | int | | 最大执行步骤数 |
| `runtime` | string | | 运行时类型：Native / AcpPassthrough |
| `acpBackend` | string | | ACP 后端标识（Runtime 为 AcpPassthrough 时使用） |

#### 权限配置

| 字段 | 类型 | 说明 |
|------|------|------|
| `permissionRules` | array | 权限规则列表 |
| `permissionDefaultEffect` | string | 默认效果：Allow / Deny / Ask |

**permissionRules 条目格式**：

```yaml
permissionRules:
  - kind: tool          # 权限类型：tool / file / command
    pattern: question   # 匹配模式（支持通配符）
    effect: allow       # 效果：allow / deny / ask
    priority: 0         # 优先级（数字越大越优先）
```

#### 工具限制

| 字段 | 类型 | 说明 |
|------|------|------|
| `allowedTools` | array | 允许的工具列表（空数组表示全部允许） |
| `deniedTools` | array | 禁止的工具列表 |

#### 模型配置

| 字段 | 类型 | 说明 |
|------|------|------|
| `provider` | string | 默认 Provider ID |
| `model` | string | 默认模型 ID |
| `temperature` | double | 温度参数 |
| `topP` | double | Top-P 参数 |
| `maxTokens` | int | 最大输出 Token 数 |

#### 变体定义

| 字段 | 类型 | 说明 |
|------|------|------|
| `variants` | object | Provider/Model 变体映射 |

**variants 键格式**：

- `{provider}` - 匹配该 Provider 下所有模型
- `{provider}.{model}` - 匹配特定模型

**变体继承规则**：

- 变体继承基础配置的所有字段
- 变体中定义的字段覆盖基础配置
- `systemPromptAppend` 追加到基础 SystemPrompt 末尾
- `systemPromptPrepend` 插入到基础 SystemPrompt 开头

### 3.4 变体配置字段

变体支持所有基础字段，以及两个特殊的 SystemPrompt 操作字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `systemPrompt` | string | 完全替换基础 SystemPrompt |
| `systemPromptPrepend` | string | 插入到基础 SystemPrompt 开头 |
| `systemPromptAppend` | string | 追加到基础 SystemPrompt 末尾 |

---

## 四、核心组件设计

### 4.1 新增文件结构

```
src/Seeing.Agent/Configuration/
├── AgentConfigLoader.cs         # 核心：MD 文件发现、解析、合并
├── AgentConfigFile.cs           # MD 文件模型
├── AgentVariant.cs              # 变体模型
└── AgentDefinitionExtensions.cs # 合并辅助方法
```

### 4.2 AgentConfigLoader

```csharp
/// <summary>
/// Agent 配置加载器 - 负责发现、解析、合并 MD 配置文件
/// </summary>
public interface IAgentConfigLoader
{
    /// <summary>
    /// 发现所有 Agent 配置文件
    /// </summary>
    /// <returns>配置文件路径列表</returns>
    Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct = default);
    
    /// <summary>
    /// 加载并合并 Agent 配置
    /// </summary>
    /// <param name="agentName">Agent 名称</param>
    /// <param name="provider">当前 Provider（用于变体选择）</param>
    /// <param name="model">当前 Model（用于变体选择）</param>
    /// <returns>合并后的 AgentDefinition，如果未找到则返回 null</returns>
    Task<AgentDefinition?> LoadAsync(
        string agentName, 
        string? provider = null,
        string? model = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// 启用热重载（监听文件变化）
    /// </summary>
    void EnableHotReload();
}
```

### 4.3 AgentConfigFile 模型

```csharp
/// <summary>
/// MD 配置文件模型
/// </summary>
public class AgentConfigFile
{
    // 基础配置
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Mode { get; set; }
    public string? Category { get; set; }
    public int? MaxSteps { get; set; }
    public string? Runtime { get; set; }
    public string? AcpBackend { get; set; }
    
    // 权限配置
    public List<PermissionRuleEntry>? PermissionRules { get; set; }
    public string? PermissionDefaultEffect { get; set; }
    
    // 工具限制
    public List<string>? AllowedTools { get; set; }
    public List<string>? DeniedTools { get; set; }
    
    // 模型配置
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    
    // 变体定义
    public Dictionary<string, AgentVariant>? Variants { get; set; }
    
    // SystemPrompt（从 Markdown Body 解析）
    [YamlIgnore]
    public string? SystemPrompt { get; set; }
}
```

### 4.4 AgentVariant 模型

```csharp
/// <summary>
/// Agent 变体配置
/// </summary>
public class AgentVariant
{
    // 模型配置
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    public int? MaxSteps { get; set; }
    
    // 权限配置
    public List<PermissionRuleEntry>? PermissionRules { get; set; }
    public List<string>? AllowedTools { get; set; }
    public List<string>? DeniedTools { get; set; }
    
    // SystemPrompt 操作
    public string? SystemPrompt { get; set; }
    public string? SystemPromptPrepend { get; set; }
    public string? SystemPromptAppend { get; set; }
}
```

### 4.5 与 AgentRegistry 集成

修改现有 `AgentRegistry.GetAgentAsync` 方法：

```csharp
public async Task<IAgent> GetAgentAsync(
    string name, 
    CancellationToken ct = default)
{
    // 1. 尝试从 DI 容器获取代码定义的 Agent
    var codeAgent = _services.KeyedServices<IAgent>(name).FirstOrDefault();
    
    // 2. 获取 Provider/Model（从 seeing.json Agent 配置）
    var jsonConfig = _options.Agents.GetValueOrDefault(name);
    var provider = jsonConfig?.Provider ?? _options.DefaultProvider;
    var model = jsonConfig?.Model ?? _options.DefaultModel;
    
    // 3. 加载 MD 文件配置
    var mdConfig = await _configLoader.LoadAsync(name, provider, model, ct);
    
    // 4. 按优先级合并配置
    if (codeAgent?.Definition != null)
    {
        // 代码内置配置作为基础
        var definition = codeAgent.Definition with { };
        
        // 应用 MD 配置
        if (mdConfig != null)
        {
            definition = AgentDefinitionExtensions.Merge(definition, mdConfig);
        }
        
        // 应用 seeing.json 配置（最高优先级）
        if (jsonConfig != null)
        {
            definition = AgentDefinitionExtensions.ApplyJsonConfig(definition, jsonConfig);
        }
        
        // 更新 Agent 配置
        ApplyDefinitionToAgent(codeAgent, definition);
    }
    
    return codeAgent ?? throw new InvalidOperationException($"Agent '{name}' not found");
}
```

### 4.6 与 Hook 系统集成

在执行流程中，合并后的 SystemPrompt 可被 `llm.system_prompt` Hook 动态修改：

```csharp
// AgentExecutor 或执行流程中
var finalSystemPrompt = agent.SystemPrompt;

// Hook 点: llm.system_prompt
var hookContext = new HookContext
{
    HookPoint = HookRegistry.LlmSystemPrompt,
    SessionId = context.SessionId,
    Data = new Dictionary<string, object?>
    {
        ["agentName"] = agent.Name,
        ["prompt"] = finalSystemPrompt
    }
};

var hookResult = await _hookManager.TriggerBlockingAsync(hookContext);
if (hookResult.Data?.TryGetValue("prompt", out var modifiedPrompt) == true)
{
    finalSystemPrompt = modifiedPrompt as string;
}
```

---

## 五、配置合并算法

### 5.1 合并优先级

```
代码内置 → 用户级 MD → 项目级 MD → seeing.json
```

### 5.2 属性级合并

```csharp
public static AgentDefinition Merge(AgentDefinition baseDef, AgentConfigFile override)
{
    var result = baseDef with { };  // 浅拷贝
    
    // 逐属性覆盖（非 null 时覆盖）
    if (override.Description != null)
        result = result with { Description = override.Description };
    
    if (override.Mode != null)
        result = result with { Mode = ParseEnum<AgentMode>(override.Mode) };
    
    if (override.MaxSteps != null)
        result = result with { MaxSteps = override.MaxSteps };
    
    if (override.PermissionRules != null)
        result = result with { PermissionRules = override.PermissionRules };
    
    if (override.SystemPrompt != null)
        result = result with { SystemPrompt = override.SystemPrompt };
    
    // ... 其他字段
    
    return result;
}
```

### 5.3 变体选择

```csharp
public static AgentDefinition ApplyVariant(
    AgentDefinition baseDef, 
    string? provider, 
    string? model)
{
    if (baseDef.Variants == null || provider == null)
        return baseDef;
    
    // 优先级：provider.model > provider
    var exactKey = model != null ? $"{provider}.{model}" : null;
    
    // 尝试精确匹配
    if (exactKey != null && baseDef.Variants.TryGetValue(exactKey, out var exactVariant))
    {
        return MergeVariant(baseDef, exactVariant);
    }
    
    // 尝试 Provider 级别匹配
    if (baseDef.Variants.TryGetValue(provider, out var providerVariant))
    {
        return MergeVariant(baseDef, providerVariant);
    }
    
    return baseDef;
}

private static AgentDefinition MergeVariant(AgentDefinition baseDef, AgentVariant variant)
{
    var result = baseDef with { };
    
    if (variant.Model != null)
        result = result with { Model = variant.Model };
    
    if (variant.Temperature != null)
        result = result with { Temperature = variant.Temperature };
    
    // SystemPrompt 特殊合并
    var systemPrompt = baseDef.SystemPrompt ?? "";
    
    if (!string.IsNullOrEmpty(variant.SystemPromptPrepend))
        systemPrompt = variant.SystemPromptPrepend + "\n\n" + systemPrompt;
    
    if (!string.IsNullOrEmpty(variant.SystemPromptAppend))
        systemPrompt = systemPrompt + "\n\n" + variant.SystemPromptAppend;
    
    if (!string.IsNullOrEmpty(variant.SystemPrompt))
        systemPrompt = variant.SystemPrompt;
    
    result = result with { SystemPrompt = systemPrompt };
    
    return result;
}
```

---

## 六、错误处理与验证

### 6.1 文件发现阶段

| 错误情况 | 处理方式 |
|---------|---------|
| .seeing/agents 目录不存在 | 跳过，使用默认配置 |
| MD 文件语法错误（无效 YAML） | 记录警告，跳过该文件 |
| 缺少必需字段（name） | 记录警告，跳过该文件 |
| Agent 名称冲突（多个文件同名） | 项目级覆盖用户级 |

### 6.2 验证器

```csharp
public class AgentConfigValidator
{
    public ValidationResult Validate(AgentConfigFile config)
    {
        var result = new ValidationResult();
        
        // 必需字段
        if (string.IsNullOrEmpty(config.Name))
            result.Errors.Add("name is required");
        
        // mode 验证
        if (config.Mode != null && !Enum.TryParse<AgentMode>(config.Mode, out _))
            result.Errors.Add($"invalid mode: {config.Mode}");
        
        // variants 键格式验证
        if (config.Variants != null)
        {
            foreach (var key in config.Variants.Keys)
            {
                if (!Regex.IsMatch(key, @"^[\w-]+(\.[\w-]+)?$"))
                    result.Warnings.Add($"variant key '{key}' may not match provider.model format");
            }
        }
        
        return result;
    }
}
```

### 6.3 运行时错误处理

```csharp
public async Task<AgentDefinition?> LoadAsync(string agentName, ...)
{
    try
    {
        var files = await DiscoverAsync(ct);
        var matchingFiles = files.Where(f => 
            Path.GetFileNameWithoutExtension(f).Equals(agentName, StringComparison.OrdinalIgnoreCase));
        
        if (!matchingFiles.Any())
        {
            _logger.LogDebug("No MD config found for agent '{AgentName}'", agentName);
            return null;
        }
        
        // 解析和合并...
    }
    catch (YamlException ex)
    {
        _logger.LogWarning(ex, "Failed to parse agent config: {Message}", ex.Message);
        return null;  // 降级到默认配置
    }
    catch (IOException ex)
    {
        _logger.LogError(ex, "Failed to read agent config file");
        throw;  // 文件系统错误应该传播
    }
}
```

---

## 七、测试策略

### 7.1 单元测试

**解析测试**：
- 基础配置解析
- 变体定义解析
- SystemPrompt 提取
- 无效 YAML 处理

**合并测试**：
- 用户级 + 项目级合并
- 变体继承
- SystemPrompt 追加/前置/覆盖
- 权限规则合并

**变体选择测试**：
- 精确匹配优先
- Provider 级别匹配
- 无匹配时使用基础配置

### 7.2 集成测试

**端到端测试**：
- MD 配置覆盖代码定义
- seeing.json 覆盖 MD 配置
- Hook 动态修改 SystemPrompt
- 热重载功能

**性能测试**：
- 大量配置文件加载
- 并发访问
- 文件监听开销

---

## 八、迁移与兼容性

### 8.1 现有代码兼容

- 现有 `AgentBase.Definition` 返回的配置继续生效
- MD 文件可覆盖代码定义的任意字段
- `seeing.json` 中的 `Agents` 配置保持最高优先级

### 8.2 迁移路径

1. **阶段 1**：实现 `AgentConfigLoader`，支持 MD 文件加载
2. **阶段 2**：修改 `AgentRegistry`，集成 MD 配置
3. **阶段 3**：将内置 Agent 的 SystemPrompt 迁移到 MD 文件
4. **阶段 4**：实现热重载和 WebUI 集成

---

## 九、依赖项

### 9.1 NuGet 包

- `YamlDotNet` - YAML Front Matter 解析

### 9.2 现有组件复用

- `WorkspaceProvider` - 获取用户级/项目级目录
- `UnifiedConfigManager` - 配置加载模式参考
- `HookManager` - Hook 系统集成

---

## 十、附录

### A. 完整示例文件

```markdown
---
name: metis
description: 预规划顾问，用于分析任务意图、识别风险、给出指令
mode: SubAgent
maxSteps: 1
allowedTools:
  - read
  - grep
  - glob
  - lsp_*
  - ast_grep_*
deniedTools:
  - write
  - edit
  - bash
  - task
  - apply_patch
variants:
  openai:
    temperature: 0.3
  anthropic:
    temperature: 0.2
    systemPromptAppend: |
      ## Claude 特定指令
      使用 XML 标签结构化输出，如 <intent>、<analysis>、<questions>。
---

你是 Metis，预规划顾问，在规划前分析用户请求以预防 AI 失败。

## 角色定位

- 识别隐藏意图和未说明的需求
- 检测可能导致实现失败的模糊之处
- 标记潜在 AI-slop 模式（过度工程、范围蔓延）
- 生成澄清问题供用户确认
- 为规划者 Agent 准备指令

## 约束

- **只读权限**：你负责分析、提问、建议，不实施或修改文件
- **输出目标**：你的分析输出给规划者，必须可行动

## 第 0 阶段：意图分类（必须首先执行）

### 步骤 1：识别意图类型

| 类型 | 触发词 | 关键策略 |
|-----|-------|---------|
| **重构** | refactor、restructure、clean up | 安全：回归预防、行为保留 |
| **从头构建** | create new、add feature | 发现：先探索模式，提出针对性问题 |
| **中等任务** | 范围明确的特性 | 约束：精确交付物、明确排除项 |
| **协作** | help me plan、let's figure out | 交互：通过对话逐步明确 |
| **架构** | how should we structure | 战略：长期影响、Oracle 推荐 |
| **研究** | 需要调查 | 调查：退出标准、并行探针 |
```

### B. 配置合并示例

**场景**：sisyphus Agent 配置合并

```
代码定义:
  maxSteps: 100
  permissionRules: [{kind: tool, pattern: question, effect: allow}]

用户级 MD:
  maxSteps: 150
  temperature: 0.7

项目级 MD:
  maxSteps: 200
  permissionRules: [{kind: tool, pattern: dangerous_*, effect: deny}]

seeing.json:
  maxSteps: 50

最终结果:
  maxSteps: 50           # seeing.json 最高优先级
  temperature: 0.7       # 项目级 MD 未覆盖，保留用户级
  permissionRules: [...]  # 项目级 MD 覆盖代码定义
```
