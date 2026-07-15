# WebUI Agent MD 配置管理设计规格

## 概述

为 WebUI 添加 Agent MD 配置文件的完整管理功能（CRUD），集成 Monaco Editor 提供良好的编辑体验，并确保数据一致性和内聚性。

## 需求

- **管理范围**：完整 CRUD（创建、查看、编辑、删除 `.seeing/agents/*.md` 文件）
- **编辑方式**：原始 MD 编辑器（Monaco Editor）
- **UI 集成**：扩展现有 `/agents` 页面
- **配置层级**：支持用户级（`~/.seeing/agents/`）和项目级（`.seeing/agents/`）

## 架构设计

### 整体架构

```
┌─────────────────────────────────────────────────────────────┐
│                   AgentConfigLoader                          │
│  (Agent MD 配置的唯一文件访问层)                               │
│                                                             │
│  读取:                                                       │
│  ├── ParseFile(path) → AgentConfigFile                     │
│  ├── DiscoverAsync(level) → List<AgentConfigFile>          │
│  └── LoadAllAsync() → Dictionary<string, AgentConfigFile>  │
│                                                             │
│  写入 (新增):                                                 │
│  ├── CreateAsync(name, level, content)                     │
│  ├── SaveAsync(name, level, content)                       │
│  └── DeleteAsync(name, level)                              │
│                                                             │
│  缓存 + 事件:                                                │
│  ├── _cache: Dictionary<string, AgentConfigFile>           │
│  └── OnChanged: EventHandler<AgentConfigChangedEventArgs>  │
└─────────────────────────────────────────────────────────────┘
         │                                    │
         │ 读取                               │ 读写
         ▼                                    ▼
┌─────────────────────┐          ┌─────────────────────────┐
│   AgentRegistry     │          │  AgentMdConfigService   │
│   (运行时聚合)       │          │  (UI 层封装，委托调用)    │
│                     │          │                         │
│  - 不直接访问文件    │◄─────────│  - 不直接访问文件        │
│  - 从 Loader 获取   │   通知    │  - 委托 Loader 操作     │
└─────────────────────┘          └─────────────────────────┘
```

### 职责边界

| 组件 | 职责 | 文件访问 |
|------|------|---------|
| `AgentConfigLoader` | MD 文件唯一读写入口 | ✅ 唯一 |
| `AgentRegistry` | 运行时 Agent 聚合 | ❌ 禁止 |
| `AgentMdConfigService` | UI 层封装，格式验证，业务逻辑 | ❌ 委托 Loader |

### 数据流

```
UI 保存
    │
    ▼
AgentMdConfigService.SaveAsync()
    │
    ├── 验证 YAML 格式
    │
    ▼
AgentConfigLoader.SaveAsync()  ← 唯一写入点
    │
    ├── 写入文件
    ├── 更新缓存
    └── 触发 OnChanged 事件
                │
                ▼
        AgentRegistry 收到通知
                │
                ▼
        刷新运行时 Agent 信息
```

### AgentRegistry 事件订阅

`AgentRegistry` 需要订阅 `AgentConfigLoader.ConfigChanged` 事件，在 MD 配置变更时刷新运行时缓存：

```csharp
// AgentRegistry 构造函数中
_configLoader.ConfigChanged += OnAgentConfigChanged;

private void OnAgentConfigChanged(object? sender, AgentConfigChangedEventArgs e)
{
    // 根据变更类型刷新对应 Agent
    switch (e.Action)
    {
        case ConfigChangeAction.Created:
        case ConfigChangeAction.Updated:
            // 重新加载 Agent 配置
            ReloadAgentFromMdConfig(e.Name, e.Level);
            break;
        case ConfigChangeAction.Deleted:
            // 移除 Agent 配置（如果是 MD 定义的）
            RemoveAgentFromMdConfig(e.Name, e.Level);
            break;
    }
}
```

## 接口设计

### IAgentConfigLoader 扩展

> **注意**：当前 `AgentConfigLoader` 是具体类，需要提取接口 `IAgentConfigLoader`。已在 Task 4 完成接口提取和 DI 注册。

```csharp
public interface IAgentConfigLoader
{
    // 已有读取方法
    AgentConfigFile? ParseFile(string path);
    Task<IReadOnlyList<AgentConfigFile>> DiscoverAsync(ConfigLevel level, CancellationToken ct = default);
    Task<Dictionary<string, AgentConfigFile>> LoadAllAsync(CancellationToken ct = default);
    
    // 新增写入方法
    Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, string? template = null, CancellationToken ct = default);
    Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default);
    Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default);
    
    // 新增：获取所有层级的 MD 信息（合并用户级和项目级）
    Task<IReadOnlyList<AgentMdInfo>> GetAllWithLevelAsync(CancellationToken ct = default);
    
    // 新增事件
    event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;
}

public class AgentConfigChangedEventArgs : EventArgs
{
    public string Name { get; init; } = "";
    public ConfigLevel Level { get; init; }
    public ConfigChangeAction Action { get; init; }
}

public enum ConfigChangeAction { Created, Updated, Deleted }
```

### AgentMdConfigService

```csharp
public class AgentMdConfigService
{
    private readonly IAgentConfigLoader _loader;
    
    // 查询（委托给 Loader）
    public Task<AgentConfigFile?> GetAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => Task.FromResult(_loader.ParseFile(GetFilePath(name, level)));
    
    public Task<IReadOnlyList<AgentMdInfo>> GetAllAsync(CancellationToken ct = default)
        => _loader.GetAllWithLevelAsync(ct);
    
    // 操作（委托给 Loader）
    public Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => _loader.CreateAsync(name, level, null, ct);
    
    public Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default)
        => _loader.SaveAsync(name, level, content, ct);
    
    public Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => _loader.DeleteAsync(name, level, ct);
    
    // 辅助（UI 层业务逻辑）
    public string GetDefaultTemplate(string agentName);
    public ValidationResult ValidateContent(string content);
    public string GetFilePath(string name, ConfigLevel level);
}

public class AgentMdInfo
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ConfigLevel Level { get; set; }
    public string FilePath { get; set; } = "";
    public int VariantCount { get; set; }
    public DateTimeOffset LastModified { get; set; }
}
```

## UI 设计

### AgentsPage.razor 改动

**新增元素：**

1. 顶部操作栏
   - 层级选择器（项目级/用户级）
   - "新增 MD 配置" 按钮
   - "刷新" 按钮

2. 表格新增列
   - "来源" 列：显示 `MD(项目)`、`MD(用户)`、`内置`、`JSON`
   - "变体数" 列：显示 variants 数量

3. 操作列
   - MD 来源：显示 "编辑MD"、"删除" 按钮
   - 其他来源：无操作按钮或 "查看MD"

**代码示例：**

```razor
@* 顶部操作栏 *@
<AntDesign.Space>
    <AntDesign.Select @bind-Value="_selectedLevel" Options="@_levelOptions" />
    <AntDesign.Button Type="primary" OnClick="OpenCreateDrawer">新增 MD 配置</AntDesign.Button>
    <AntDesign.Button OnClick="RefreshList">刷新</AntDesign.Button>
</AntDesign.Space>

@* 表格操作列 *@
<AntDesign.Column Title="操作">
    @if (context.Source == "MD")
    {
        <AntDesign.Button Size="small" OnClick="() => OpenEditDrawer(context)">编辑MD</AntDesign.Button>
        <AntDesign.Popconfirm Title="确定删除？" OnConfirm="() => DeleteAgent(context.Name)">
            <AntDesign.Button Size="small" Danger>删除</AntDesign.Button>
        </AntDesign.Popconfirm>
    }
</AntDesign.Column>

@* 编辑器抽屉 *@
<AgentMdEditorDrawer @bind-Visible="_drawerVisible"
                      AgentName="_currentAgentName"
                      Level="_selectedLevel"
                      OnSaved="HandleSaved" />
```

### AgentMdEditorDrawer.razor

**功能：**

- Drawer 容器，宽度 800px
- Monaco Editor 组件，语言设置为 markdown
- 实时验证 YAML Front Matter 格式
- 显示验证错误
- 保存/取消按钮

**代码示例：**

```razor
<AntDesign.Drawer Title="@_drawerTitle" @bind-Visible="Visible" Width="800">
    <div class="editor-container" style="height: 500px;">
        <MonacoEditor Value="_content"
                      ValueChanged="OnContentChanged"
                      Language="markdown"
                      Options="@_editorOptions" />
    </div>
    
    @if (_validationErrors.Any())
    {
        <AntDesign.Alert Type="error" Message="@string.Join("\n", _validationErrors)" />
    }
    
    <div class="drawer-footer">
        <AntDesign.Button OnClick="Close">取消</AntDesign.Button>
        <AntDesign.Button Type="primary" OnClick="Save" Disabled="@_validationErrors.Any()">保存</AntDesign.Button>
    </div>
</AntDesign.Drawer>
```

### MonacoEditor.razor

**功能：**

- 封装 Monaco Editor JavaScript 组件
- 支持语言设置（markdown）
- 双向绑定 Value
- 支持主题设置

**实现方式：**

使用 `BlazorMonaco` 库：
```xml
<PackageReference Include="BlazorMonaco" Version="3.2.0" />
```

## 新增文件清单

| 文件路径 | 说明 |
|---------|------|
| `src/Seeing.Agent/Configuration/IAgentConfigLoader.cs` | 修改：添加写入方法、事件、GetAllWithLevelAsync |
| `src/Seeing.Agent/Configuration/AgentConfigLoader.cs` | 修改：实现写入方法、事件触发 |
| `src/Seeing.Agent/Configuration/AgentConfigChangedEventArgs.cs` | 新增：事件参数类 |
| `src/Seeing.Agent/Configuration/AgentMdInfo.cs` | 新增：MD 配置信息模型 |
| `src/Seeing.Agent/Core/AgentRegistry.cs` | 修改：订阅 ConfigChanged 事件 |
| `samples/Seeing.Agent.WebUI/Services/AgentMdConfigService.cs` | 新增：UI 层服务 |
| `samples/Seeing.Agent.WebUI/Components/Agents/AgentMdEditorDrawer.razor` | 新增：编辑器抽屉 |
| `samples/Seeing.Agent.WebUI/Components/MonacoEditor.razor` | 新增：Monaco 封装 |
| `samples/Seeing.Agent.WebUI/Pages/AgentsPage.razor` | 修改：添加 MD 配置管理功能 |
| `samples/Seeing.Agent.WebUI/_Imports.razor` | 修改：添加命名空间引用 |

> **注意**：`ConfigLevel` 枚举已存在于 `UnifiedConfigManager.cs` 中，无需重复定义。

## 默认模板

新建 MD 配置时使用的默认模板：

```markdown
---
name: {{agent-name}}
description: Agent 描述
mode: Primary
category: general
maxSteps: 50
variants: {}
---

# 系统提示词

你是一个 AI 助手，负责...
```

## 验证规则

1. **YAML Front Matter 格式验证**
   - 必须以 `---` 开头和结尾
   - YAML 必须是有效格式
   - `name` 字段必填

2. **文件名验证**
   - 只允许字母、数字、下划线、连字符
   - 不能以数字开头
   - 长度限制 1-64 字符

3. **并发冲突处理**
   - 基于 ETAG 或文件修改时间检测
   - 冲突时提示用户刷新后重试

## 测试计划

### 单元测试

| 测试类 | 测试内容 |
|-------|---------|
| `AgentConfigLoaderTests` | CreateAsync, SaveAsync, DeleteAsync 方法 |
| `AgentMdConfigServiceTests` | 模板生成、内容验证、格式校验 |

### 集成测试

| 测试类 | 测试内容 |
|-------|---------|
| `AgentMdConfigIntegrationTests` | 完整 CRUD 流程、缓存一致性、事件触发 |

### E2E 测试

| 测试场景 | 验证点 |
|---------|-------|
| 新建 MD 配置 | 文件创建、列表刷新、表单验证 |
| 编辑 MD 配置 | Monaco 编辑器、保存成功、运行时生效 |
| 删除 MD 配置 | 确认对话框、文件删除、列表刷新 |
| 层级切换 | 用户级/项目级切换、文件路径正确 |

## 风险与缓解

| 风险 | 缓解措施 |
|------|---------|
| Monaco Editor 加载慢 | 使用 CDN 加速、显示加载状态 |
| YAML 格式错误导致解析失败 | 实时验证、友好错误提示 |
| 并发编辑冲突 | 乐观锁机制、提示用户刷新 |
| 用户级路径不存在 | 自动创建目录 |

## 实现优先级

1. **P0 - 核心功能**
   - AgentConfigLoader 写入方法
   - AgentMdConfigService 基础 CRUD
   - AgentsPage 列表展示和操作按钮

2. **P1 - 编辑器**
   - Monaco Editor 集成
   - AgentMdEditorDrawer 抽屉组件

3. **P2 - 增强功能**
   - YAML 格式实时验证
   - 并发冲突检测
   - 用户级配置支持
