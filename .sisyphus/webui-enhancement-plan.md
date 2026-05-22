# Seeing.Agent WebUI 功能增强计划

## 一、目标概述

参考 QwenPaw Console (http://192.168.31.244:8088/) 的设计，完善 Seeing.Agent WebUI 的功能，重点实现：
- Skills 管理
- Tools 管理  
- MCP 管理
- Sessions 管理
- 侧边栏分组菜单设计

## 二、参考页面功能结构

```
侧边栏:
├── Header (Logo + 版本)
├── Agent Selector
├── Chat 快捷按钮
├── Control 分组
│   ├── Inbox
│   ├── Channels
│   ├── Sessions (表格管理)
│   ├── Cron Jobs
│   └── Heartbeat
├── Workspace 分组
│   ├── Files
│   ├── Skills (卡片/列表视图)
│   ├── Tools (开关控制)
│   ├── MCP (客户端管理)
│   ├── Configuration
│   └── Agent Statistics
└── Settings 分组
    ├── Agent Management
    ├── Models
    ├── Security
    └── Debug
```

## 三、已有服务接口

| 功能 | 服务 | 关键方法 |
|------|------|---------|
| Skills | `SkillManager` | `GetAllSkillInfos()`, `GetSkillInfo()`, `DiscoverSkillsAsync()` |
| Tools | `ToolInvoker` | `GetTools()`, `GetToolSchemasAsync()`, `RegisterTool()`, `UnregisterTool()` |
| MCP | `IMcpManager` | `GetAllStatus()`, `ConnectServerAsync()`, `AddServerAsync()`, `RemoveServerAsync()` |
| Sessions | `SessionProvider` | `GetSessionList()`, `CreateSessionAsync()`, `DeleteSessionAsync()` |
| Agents | `IAgentRegistry` | `GetAgentsAsync()`, `SetDefaultAgentAsync()` |
| Rules | `IRuleEngine` | `AddRule()`, `GetRules()` |
| Hooks | `IHookManager` | `Register()`, `Count()` |

## 四、实现任务

### 任务1: 创建侧边栏样式
**文件**: `wwwroot/css/sidebar.css`
**内容**: 分组菜单样式、折叠动画、响应式设计

### 任务2: 创建工作区页面通用样式
**文件**: `wwwroot/css/workspace-page.css`
**内容**: 页面头部、筛选区、卡片网格、表格样式

### 任务3: 创建新侧边栏组件
**文件**: `Components/AppSidebar.razor`
**功能**: 
- Logo区域
- Agent选择器
- 分组菜单（Control/Workspace/Settings）
- 折叠功能
- 导航高亮

### 任务4: 创建技能状态服务
**文件**: `Services/SkillStateService.cs`
**功能**:
- 维护技能启用/禁用状态
- 持久化到配置文件
- 提供状态变更事件

### 任务5: 创建Skills管理页面
**文件**: `Pages/SkillsPage.razor`
**路由**: `/skills`
**功能**:
- 技能列表（卡片/列表视图切换）
- 搜索筛选
- 启用/禁用开关
- 技能详情查看
- 创建新技能

### 任务6: 创建工具状态服务
**文件**: `Services/ToolStateService.cs`
**功能**:
- 维护工具启用/禁用状态
- 持久化到配置文件

### 任务7: 创建Tools管理页面
**文件**: `Pages/ToolsPage.razor`
**路由**: `/tools`
**功能**:
- 工具列表展示
- 全局开关
- 单个工具启用/禁用
- 查看参数Schema

### 任务8: 创建MCP管理页面
**文件**: `Pages/McpPage.razor`
**路由**: `/mcp`
**功能**:
- MCP客户端列表
- 连接状态显示
- 连接/断开/重连操作
- 添加/删除客户端
- 查看工具列表

### 任务9: 创建Sessions管理页面
**文件**: `Pages/SessionsPage.razor`
**路由**: `/sessions`
**功能**:
- 会话表格
- 筛选（用户ID、Channel）
- 分页
- 查看/编辑/删除操作

### 任务10: 创建Agent管理页面
**文件**: `Pages/AgentsPage.razor`
**路由**: `/agents`
**功能**:
- Agent列表
- 设置默认Agent
- 配置模型

### 任务11: 创建权限规则页面
**文件**: `Pages/RulesPage.razor`
**路由**: `/rules`
**功能**:
- 规则列表
- 添加/删除规则

### 任务12: 更新MainLayout
**文件**: `Shared/MainLayout.razor`
**修改**: 使用新的AppSidebar组件

### 任务13: 更新CSS引用
**文件**: `Pages/_Host.cshtml`
**修改**: 添加新的CSS文件引用

## 五、数据绑定

### Skills页面
```csharp
@Inject SkillManager SkillManager
@Inject SkillStateService SkillStateService

var skills = SkillManager.GetAllSkillInfos();
var isEnabled = SkillStateService.IsEnabled(skillName);
```

### Tools页面
```csharp
@Inject ToolInvoker ToolInvoker
@Inject ToolStateService ToolStateService

var tools = ToolInvoker.GetTools();
var isEnabled = ToolStateService.IsEnabled(toolId);
```

### MCP页面
```csharp
@Inject IMcpManager McpManager
@Inject McpStateService McpStateService

var status = McpManager.GetAllStatus();
await McpManager.ConnectServerAsync(name);
```

### Sessions页面
```csharp
@Inject SessionProvider SessionProvider

var sessions = SessionProvider.GetSessionList();
await SessionProvider.DeleteSessionAsync(id);
```

## 六、验收标准

1. 侧边栏分组菜单正常展开/折叠
2. 各页面路由导航正确
3. Skills页面：列表显示、启用/禁用功能正常
4. Tools页面：列表显示、开关功能正常
5. MCP页面：状态显示、连接操作正常
6. Sessions页面：表格显示、筛选分页正常
7. 响应式设计正常
8. 暗色主题支持正常
