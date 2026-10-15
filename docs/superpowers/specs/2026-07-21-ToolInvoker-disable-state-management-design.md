# ToolInvoker 禁用状态管理设计

**日期:** 2026-07-21  
**任务:** Task 1 - ToolInvoker 添加禁用状态管理  
**状态:** 设计完成

## 概述

在 ToolInvoker 中添加工具禁用状态管理功能，支持用户级和项目级的工具禁用配置。

## 设计目标

1. 支持双层级工具禁用：用户级（~/.seeing/）和项目级（{WorkspaceRoot}/.seeing/）
2. 提供工具启用/禁用状态查询接口
3. 在工具执行和 Schema 生成时过滤禁用工具
4. 支持运行时动态切换工具状态

## 实现步骤

### Step 1: 添加必要的 using 和字段

在 `ToolInvoker.cs` 顶部添加 using：
```csharp
using Seeing.Agent.Configuration;
```

在 `ToolInvoker` 类中添加字段（在现有 `_retryDelay` 字段之后）：
```csharp
private readonly IWorkspaceProvider? _workspace;
private readonly object _toolStateLock = new();
private HashSet<string> _userDisabledTools = new(StringComparer.OrdinalIgnoreCase);
private HashSet<string> _projectDisabledTools = new(StringComparer.OrdinalIgnoreCase);
```

### Step 2: 修改构造函数

修改 `ToolInvoker` 构造函数，添加 `IWorkspaceProvider? workspace = null` 参数：
```csharp
public ToolInvoker(
    ILogger<ToolInvoker> logger,
    Core.Hooks.IHookManager hookManager,
    IServiceProvider? serviceProvider = null,
    IToolDecoratorRegistry? decoratorRegistry = null,
    IRuleEvaluator? ruleEvaluator = null,
    IWorkspaceProvider? workspace = null,
    int maxRetries = 3,
    TimeSpan? retryDelay = null)
{
    _logger = logger;
    _hookManager = hookManager;
    _serviceProvider = serviceProvider;
    _decoratorRegistry = decoratorRegistry;
    _ruleEvaluator = ruleEvaluator;
    _workspace = workspace;
    _maxRetries = maxRetries;
    _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
}
```

### Step 3: 添加禁用状态属性和方法

在 `ToolInvoker` 类中添加以下成员（在 `GetTools()` 方法之后）：

1. `DisabledTools` 属性（合并后的禁用工具集合）
2. `OnToolStateChanged` 事件
3. `IsToolEnabled(string toolId)` 方法
4. `GetToolsIncludingDisabled()` 方法
5. `LoadToolStateAsync()` 方法
6. `SetToolEnabledAsync()` 方法
7. `SaveToolStateAsync()` 私有方法
8. `LoadDisabledSetAsync()` 私有静态方法
9. `SaveDisabledSetAsync()` 私有静态方法
10. `DisabledToolsData` 私有密封类

### Step 4: 修改 GetToolSchemasAsync 添加过滤

在 `GetToolSchemasAsync()` 方法的 foreach 循环中添加过滤：
```csharp
// 过滤禁用工具
if (!IsToolEnabled(tool.Id))
    continue;
```

### Step 5: 修改 ExecuteAsync 添加禁用检查

在 `ExecuteAsync()` 方法的开头添加禁用检查：
```csharp
// 检查工具是否启用
if (!IsToolEnabled(toolCall.ToolId))
{
    _logger.LogWarning("尝试执行已禁用的工具: {ToolId}", toolCall.ToolId);
    return new ToolResult
    {
        Success = false,
        Title = "工具已禁用",
        Output = $"Tool '{toolCall.ToolId}' is disabled. Enable it in the Tools settings."
    };
}
```

## 数据模型

禁用工具状态存储在 `tool-state.json` 文件中：
```json
{
  "disabledTools": ["tool1", "tool2"]
}
```

## 接口依赖

- `IWorkspaceProvider`: 提供用户级和项目级目录路径
- `ConfigLevel`: 配置级别枚举（User/Project）

## 测试策略

1. 单元测试：验证禁用状态加载、保存、查询
2. 集成测试：验证工具执行和 Schema 生成时的过滤
3. 边界测试：验证并发访问和文件操作异常处理

## 风险评估

1. **低风险**: 文件操作异常处理（已有 try-catch）
2. **低风险**: 并发访问（使用 lock 保护）
3. **中风险**: 向后兼容性（构造函数添加可选参数）

## 验证命令

```bash
dotnet build src/Seeing.Agent
```