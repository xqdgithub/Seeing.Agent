# Tool & Skill 禁用配置设计

**日期:** 2026-07-21  
**状态:** 已批准  
**分支:** master

---

## 概述

完善 Tool 和 Skill 的启用/禁用配置机制，将现有 WebUI 中的纯装饰性 UI 升级为核心框架功能，确保禁用状态在所有使用场景中生效。

## 目标

1. 禁用的 Tool/Skill 完全对 LLM 隐藏（不发送 schema）
2. 双层级配置：用户级 `~/.seeing/` + 项目级 `.seeing/`，项目级覆盖用户级
3. 全局禁用粒度：一个 ID 禁用后所有 Agent 都无法使用
4. 内置工具与 MCP 工具一视同仁
5. 集成到现有管理类（ToolInvoker、SkillManager），不创建新服务

## 设计

### 状态文件格式

```json
// ~/.seeing/tool-state.json (用户级)
{
  "DisabledTools": ["webfetch", "websearch"]
}

// .seeing/tool-state.json (项目级)
{
  "DisabledTools": ["bash", "write"]
}
```

```json
// ~/.seeing/skill-state.json (用户级)
{
  "DisabledSkills": ["browser-use"]
}

// .seeing/skill-state.json (项目级)
{
  "DisabledSkills": ["playwright-cli"]
}
```

### 双层级合并规则

- **加载时:** 先加载用户级，再加载项目级，项目级覆盖用户级
- **保存时:** 默认保存到用户级 `~/.seeing/`。项目级 `.seeing/` 中已存在的禁用条目继续保存到项目级。新增的禁用条目保存到用户级。
- **默认行为:** 所有工具/技能默认启用

### ToolInvoker 集成

**新增字段:**
```csharp
private HashSet<string> _userDisabledTools = new(StringComparer.OrdinalIgnoreCase);
private HashSet<string> _projectDisabledTools = new(StringComparer.OrdinalIgnoreCase);
public IReadOnlySet<string> DisabledTools => _userDisabledTools
    .Union(_projectDisabledTools)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

**新增方法:**
- `LoadToolStateAsync()` — 加载双层级状态
- `SaveToolStateAsync(toolId, level)` — 保存到指定级别
- `IsToolEnabled(toolId)` — 查询启用状态
- `SetToolEnabledAsync(toolId, enabled)` — 设置并保存
- `GetToolsIncludingDisabled()` — 返回所有工具（UI 用）
- `GetTools()` — 只返回启用的工具（消费者用）

**修改方法:**
- `GetToolSchemasAsync()` — 过滤禁用工具
- `ExecuteAsync()` — 拒绝执行禁用工具

**构造函数变更:**
- 新增 `IWorkspaceProvider? workspace = null` 参数
- `workspace` 为 null 时跳过状态加载（兼容测试场景）

### SkillManager 集成

**构造函数变更:**
- 新增 `IWorkspaceProvider? workspace = null` 参数
- `workspace` 为 null 时跳过状态加载（兼容测试场景）

**新增字段:**
```csharp
private HashSet<string> _userDisabledSkills = new(StringComparer.OrdinalIgnoreCase);
private HashSet<string> _projectDisabledSkills = new(StringComparer.OrdinalIgnoreCase);
public IReadOnlySet<string> DisabledSkills => _userDisabledSkills
    .Union(_projectDisabledSkills)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
```

**新增方法:**
- `LoadSkillStateAsync()` — 加载双层级状态
- `SaveSkillStateAsync(skillName, level)` — 保存到指定级别
- `IsSkillEnabled(skillName)` — 查询启用状态
- `SetSkillEnabledAsync(skillName, enabled)` — 设置并保存
- `GetAllSkillInfosIncludingDisabled()` — 返回所有技能（UI 用）

**修改方法:**
- `GetAllSkillInfos()` — 只返回启用的技能（自动过滤下游消费者）
- `GetSkillInfo()` — 返回 null 如果禁用

### SkillTool 集成

**修改方法:**
- `ExecuteAsync()` — 添加 `IsSkillEnabled()` 显式检查
- `BuildDescription()` — 自动生效（调用 `GetAllSkillInfos()`）

### 初始化流程

```csharp
// ServiceCollectionExtensions.InitializeSeeingAgentAsync() 中添加：
var toolInvoker = sp.GetRequiredService<ToolInvoker>();
await toolInvoker.LoadToolStateAsync(ct);

var skillManager = sp.GetRequiredService<SkillManager>();
await skillManager.LoadSkillStateAsync(ct);
```

### WebUI 迁移

**删除文件:**
- `samples/Seeing.Agent.WebUI/Services/ToolStateService.cs`
- `samples/Seeing.Agent.WebUI/Services/SkillStateService.cs`

**修改文件:**
- `Program.cs` — 移除旧服务注册
- `ToolsPage.razor` — 使用 `ToolInvoker.IsToolEnabled()`
- `SkillsPage.razor` — 使用 `SkillManager.IsSkillEnabled()`
- `SkillDetailPage.razor` — 使用 `SkillManager.IsSkillEnabled()`

### 自动生效的消费者（无需修改）

| 消费者 | 调用方法 | 说明 |
|--------|----------|------|
| PromptBuilder.BuildSkillSection() | `GetAllSkillInfos()` | 自动过滤 |
| SkillCommands.LoadSkill() | `GetSkillInfo()` | 自动过滤 |
| SkillCommands.ListSkills() | `GetAllSkillInfos()` | 自动过滤 |
| ChatOrchestratorServiceExtensions | `GetAllSkillInfos()` | 自动过滤 |

### 配置页面专用方法

| 方法 | 用途 |
|------|------|
| `GetAllSkillInfosIncludingDisabled()` | WebUI 显示所有技能+状态标签 |
| `GetToolsIncludingDisabled()` | WebUI 显示所有工具+状态标签 |

### 线程安全与事件

- `HashSet` 操作使用 `lock` 保护
- 状态变更触发 `OnToolStateChanged` / `OnSkillStateChanged` 事件
- WebUI 订阅事件更新 UI

## 变更清单

| 文件 | 变更类型 | 说明 |
|------|----------|------|
| `src/Seeing.Agent/Tools/ToolInvoker.cs` | 修改 | 添加状态管理、过滤逻辑 |
| `src/Seeing.Agent/Skills/SkillManager.cs` | 修改 | 添加状态管理、过滤逻辑 |
| `src/Seeing.Agent/Tools/SkillTool.cs` | 修改 | ExecuteAsync 添加检查 |
| `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | 修改 | 初始化加载状态 |
| `samples/Seeing.Agent.WebUI/Pages/ToolsPage.razor` | 修改 | 使用核心库 API |
| `samples/Seeing.Agent.WebUI/Pages/SkillsPage.razor` | 修改 | 使用核心库 API |
| `samples/Seeing.Agent.WebUI/Pages/SkillDetailPage.razor` | 修改 | 使用核心库 API |
| `samples/Seeing.Agent.WebUI/Services/ToolStateService.cs` | 删除 | 被核心库替代 |
| `samples/Seeing.Agent.WebUI/Services/SkillStateService.cs` | 删除 | 被核心库替代 |
| `samples/Seeing.Agent.WebUI/Program.cs` | 修改 | 移除旧服务注册 |
