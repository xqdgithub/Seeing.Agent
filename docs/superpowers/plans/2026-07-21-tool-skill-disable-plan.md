# Tool & Skill 禁用配置实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Tool/Skill 启用/禁用功能从 WebUI 装饰性 UI 升级为核心框架功能，确保禁用状态在所有使用场景中生效。

**Architecture:** 在 ToolInvoker 和 SkillManager 中集成双层级状态管理（用户级 + 项目级），修改关键方法过滤禁用项，删除 WebUI 中的冗余服务。

**Tech Stack:** C#, .NET 10.0, System.Text.Json, System.Collections.Concurrent

---

## 文件结构

| 文件 | 变更 | 职责 |
|------|------|------|
| `src/Seeing.Agent/Tools/ToolInvoker.cs` | 修改 | 添加工具禁用状态管理、过滤逻辑 |
| `src/Seeing.Agent/Skills/SkillManager.cs` | 修改 | 添加技能禁用状态管理、过滤逻辑 |
| `src/Seeing.Agent/Tools/SkillTool.cs` | 修改 | ExecuteAsync 添加禁用检查 |
| `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | 修改 | 初始化时加载状态 |
| `samples/Seeing.Agent.WebUI/Pages/ToolsPage.razor` | 修改 | 使用核心库 API |
| `samples/Seeing.Agent.WebUI/Pages/SkillsPage.razor` | 修改 | 使用核心库 API |
| `samples/Seeing.Agent.WebUI/Pages/SkillDetailPage.razor` | 修改 | 使用核心库 API |
| `samples/Seeing.Agent.WebUI/Services/ToolStateService.cs` | 删除 | 被核心库替代 |
| `samples/Seeing.Agent.WebUI/Services/SkillStateService.cs` | 删除 | 被核心库替代 |
| `samples/Seeing.Agent.WebUI/Program.cs` | 修改 | 移除旧服务注册 |

---

### Task 1: ToolInvoker 添加禁用状态管理

**Files:**
- Modify: `src/Seeing.Agent/Tools/ToolInvoker.cs`

- [ ] **Step 1: 添加必要的 using 和字段**

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

- [ ] **Step 2: 修改构造函数**

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

- [ ] **Step 3: 添加禁用状态属性和方法**

在 `ToolInvoker` 类中添加以下成员（在 `GetTools()` 方法之后）：
```csharp
/// <summary>合并后的禁用工具集合（用户级 + 项目级）</summary>
public IReadOnlySet<string> DisabledTools
{
    get
    {
        lock (_toolStateLock)
        {
            return _userDisabledTools
                .Union(_projectDisabledTools)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>状态变更事件</summary>
public event Action? OnToolStateChanged;

/// <summary>检查工具是否启用</summary>
public bool IsToolEnabled(string toolId)
{
    lock (_toolStateLock)
    {
        return !_userDisabledTools.Contains(toolId) && !_projectDisabledTools.Contains(toolId);
    }
}

/// <summary>获取所有工具（包括禁用的，供 UI 使用）</summary>
public IReadOnlyCollection<ITool> GetToolsIncludingDisabled() => _tools.Values.ToList().AsReadOnly();

/// <summary>加载工具禁用状态（双层级）</summary>
public async Task LoadToolStateAsync(CancellationToken ct = default)
{
    if (_workspace == null) return;

    var userPath = Path.Combine(_workspace.UserSeeingDirectory, "tool-state.json");
    var projectPath = Path.Combine(_workspace.ProjectSeeingDirectory, "tool-state.json");

    var userDisabled = await LoadDisabledSetAsync(userPath, ct);
    var projectDisabled = await LoadDisabledSetAsync(projectPath, ct);

    lock (_toolStateLock)
    {
        _userDisabledTools = userDisabled;
        _projectDisabledTools = projectDisabled;
    }

    _logger.LogInformation("工具禁用状态已加载，用户级: {UserCount} 个，项目级: {ProjectCount} 个",
        _userDisabledTools.Count, _projectDisabledTools.Count);
}

/// <summary>设置工具启用/禁用状态</summary>
public async Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default)
{
    if (_workspace == null) return;

    // 判断保存级别：项目级已存在的禁用条目保存到项目级，其他保存到用户级
    ConfigLevel level;
    lock (_toolStateLock)
    {
        level = _projectDisabledTools.Contains(toolId) ? ConfigLevel.Project : ConfigLevel.User;
    }

    await SaveToolStateAsync(toolId, enabled, level, ct);
    OnToolStateChanged?.Invoke();
}

/// <summary>保存工具禁用状态到指定级别</summary>
private async Task SaveToolStateAsync(string toolId, bool enabled, ConfigLevel level, CancellationToken ct)
{
    if (_workspace == null) return;

    var filePath = level == ConfigLevel.User
        ? Path.Combine(_workspace.UserSeeingDirectory, "tool-state.json")
        : Path.Combine(_workspace.ProjectSeeingDirectory, "tool-state.json");

    HashSet<string> targetSet;
    lock (_toolStateLock)
    {
        targetSet = level == ConfigLevel.User ? _userDisabledTools : _projectDisabledTools;

        if (enabled)
            targetSet.Remove(toolId);
        else
            targetSet.Add(toolId);
    }

    await SaveDisabledSetAsync(filePath, targetSet, ct);
}

/// <summary>从文件加载禁用 ID 集合</summary>
private static async Task<HashSet<string>> LoadDisabledSetAsync(string filePath, CancellationToken ct)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (!File.Exists(filePath))
        return result;

    try
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        var data = JsonSerializer.Deserialize<DisabledToolsData>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (data?.DisabledTools != null)
        {
            foreach (var id in data.DisabledTools)
            {
                result.Add(id);
            }
        }
    }
    catch (Exception)
    {
        // 加载失败时返回空集合
    }

    return result;
}

/// <summary>保存禁用 ID 集合到文件</summary>
private static async Task SaveDisabledSetAsync(string filePath, HashSet<string> disabledTools, CancellationToken ct)
{
    var dir = Path.GetDirectoryName(filePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }

    var data = new DisabledToolsData { DisabledTools = disabledTools.ToList() };
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    await File.WriteAllTextAsync(filePath, json, ct);
}

/// <summary>禁用工具数据模型</summary>
private sealed class DisabledToolsData
{
    public List<string>? DisabledTools { get; set; }
}
```

- [ ] **Step 4: 修改 GetToolSchemasAsync 添加过滤**

修改 `GetToolSchemasAsync()` 方法，在遍历 `_tools.Values` 时添加过滤：
```csharp
public async Task<List<FunctionToolSchema>> GetToolSchemasAsync()
{
    var schemas = new List<FunctionToolSchema>();

    foreach (var tool in _tools.Values)
    {
        // 过滤禁用工具
        if (!IsToolEnabled(tool.Id))
            continue;

        // ========== Hook: tool.definition ==========
        var mutable = new Dictionary<string, object?>
        {
            ["description"] = tool.Description,
            ["parameters"] = tool.ParametersSchema
        };

        await _hookManager.TriggerBlockingAsync(
            HookRegistry.ToolDefinition,
            string.Empty,
            new Dictionary<string, object?> { ["toolId"] = tool.Id },
            mutable);

        schemas.Add(new FunctionToolSchema
        {
            Function = new FunctionSchema
            {
                Name = tool.Id,
                Description = mutable["description"]?.ToString() ?? tool.Description,
                Parameters = mutable["parameters"] is JsonElement je ? je : tool.ParametersSchema
            }
        });
    }

    return schemas;
}
```

- [ ] **Step 5: 修改 ExecuteAsync 添加禁用检查**

在 `ExecuteAsync()` 方法的开头（在现有权限检查之前）添加禁用检查：
```csharp
public async Task<ToolResult> ExecuteAsync(ToolCall toolCall, AgentContext? context = null, CancellationToken ct = default)
{
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

    // 现有代码继续...
}
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build src/Seeing.Agent`
Expected: BUILD SUCCEEDED

- [ ] **Step 7: 提交**

```bash
git add src/Seeing.Agent/Tools/ToolInvoker.cs
git commit -m "feat: add tool disable state management to ToolInvoker"
```

---

### Task 2: SkillManager 添加禁用状态管理

**Files:**
- Modify: `src/Seeing.Agent/Skills/SkillManager.cs`

- [ ] **Step 1: 添加 using 和字段**

在 `SkillManager.cs` 顶部添加 using：
```csharp
using Seeing.Agent.Configuration;
```

在 `SkillManager` 类中添加字段（在现有 `_skillPuller` 字段之后）：
```csharp
private readonly IWorkspaceProvider? _workspace;
private readonly object _skillStateLock = new();
private HashSet<string> _userDisabledSkills = new(StringComparer.OrdinalIgnoreCase);
private HashSet<string> _projectDisabledSkills = new(StringComparer.OrdinalIgnoreCase);
```

- [ ] **Step 2: 修改构造函数**

修改 `SkillManager` 构造函数，添加 `IWorkspaceProvider? workspace = null` 参数：
```csharp
public SkillManager(ILogger<SkillManager> logger, ISkillPuller? skillPuller = null, IWorkspaceProvider? workspace = null)
{
    _logger = logger;
    _skillPuller = skillPuller;
    _workspace = workspace;

    // 现有目录初始化代码...
}
```

- [ ] **Step 3: 添加禁用状态属性和方法**

在 `SkillManager` 类中添加以下成员（在 `GetAllSkillInfos()` 方法之后）：
```csharp
/// <summary>合并后的禁用技能集合（用户级 + 项目级）</summary>
public IReadOnlySet<string> DisabledSkills
{
    get
    {
        lock (_skillStateLock)
        {
            return _userDisabledSkills
                .Union(_projectDisabledSkills)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>状态变更事件</summary>
public event Action? OnSkillStateChanged;

/// <summary>检查技能是否启用</summary>
public bool IsSkillEnabled(string skillName)
{
    if (string.IsNullOrWhiteSpace(skillName))
        return true;

    lock (_skillStateLock)
    {
        return !_userDisabledSkills.Contains(skillName) && !_projectDisabledSkills.Contains(skillName);
    }
}

/// <summary>获取所有技能信息（包括禁用的，供 UI 使用）</summary>
public IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfosIncludingDisabled() => _skillInfos;

/// <summary>加载技能禁用状态（双层级）</summary>
public async Task LoadSkillStateAsync(CancellationToken ct = default)
{
    if (_workspace == null) return;

    var userPath = Path.Combine(_workspace.UserSeeingDirectory, "skill-state.json");
    var projectPath = Path.Combine(_workspace.ProjectSeeingDirectory, "skill-state.json");

    var userDisabled = await LoadDisabledSetAsync(userPath, ct);
    var projectDisabled = await LoadDisabledSetAsync(projectPath, ct);

    lock (_skillStateLock)
    {
        _userDisabledSkills = userDisabled;
        _projectDisabledSkills = projectDisabled;
    }

    _logger.LogInformation("技能禁用状态已加载，用户级: {UserCount} 个，项目级: {ProjectCount} 个",
        _userDisabledSkills.Count, _projectDisabledSkills.Count);
}

/// <summary>设置技能启用/禁用状态</summary>
public async Task SetSkillEnabledAsync(string skillName, bool enabled, CancellationToken ct = default)
{
    if (_workspace == null) return;

    // 判断保存级别：项目级已存在的禁用条目保存到项目级，其他保存到用户级
    ConfigLevel level;
    lock (_skillStateLock)
    {
        level = _projectDisabledSkills.Contains(skillName) ? ConfigLevel.Project : ConfigLevel.User;
    }

    await SaveSkillStateAsync(skillName, enabled, level, ct);
    OnSkillStateChanged?.Invoke();
}

/// <summary>保存技能禁用状态到指定级别</summary>
private async Task SaveSkillStateAsync(string skillName, bool enabled, ConfigLevel level, CancellationToken ct)
{
    if (_workspace == null) return;

    var filePath = level == ConfigLevel.User
        ? Path.Combine(_workspace.UserSeeingDirectory, "skill-state.json")
        : Path.Combine(_workspace.ProjectSeeingDirectory, "skill-state.json");

    HashSet<string> targetSet;
    lock (_skillStateLock)
    {
        targetSet = level == ConfigLevel.User ? _userDisabledSkills : _projectDisabledSkills;

        if (enabled)
            targetSet.Remove(skillName);
        else
            targetSet.Add(skillName);
    }

    await SaveDisabledSetAsync(filePath, targetSet, ct);
}

/// <summary>从文件加载禁用名称集合</summary>
private static async Task<HashSet<string>> LoadDisabledSetAsync(string filePath, CancellationToken ct)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (!File.Exists(filePath))
        return result;

    try
    {
        var json = await File.ReadAllTextAsync(filePath, ct);
        var data = JsonSerializer.Deserialize<DisabledSkillsData>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (data?.DisabledSkills != null)
        {
            foreach (var name in data.DisabledSkills)
            {
                result.Add(name);
            }
        }
    }
    catch (Exception)
    {
        // 加载失败时返回空集合
    }

    return result;
}

/// <summary>保存禁用名称集合到文件</summary>
private static async Task SaveDisabledSetAsync(string filePath, HashSet<string> disabledSkills, CancellationToken ct)
{
    var dir = Path.GetDirectoryName(filePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }

    var data = new DisabledSkillsData { DisabledSkills = disabledSkills.ToList() };
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    await File.WriteAllTextAsync(filePath, json, ct);
}

/// <summary>禁用技能数据模型</summary>
private sealed class DisabledSkillsData
{
    public List<string>? DisabledSkills { get; set; }
}
```

- [ ] **Step 4: 修改 GetAllSkillInfos 添加过滤**

修改 `GetAllSkillInfos()` 方法，只返回启用的技能：
```csharp
/// <summary>
/// 获取所有技能信息（只返回启用的技能）
/// </summary>
public IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfos()
{
    return _skillInfos
        .Where(kvp => IsSkillEnabled(kvp.Key))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: 修改 GetSkillInfo 添加过滤**

修改 `GetSkillInfo()` 方法，禁用的技能返回 null：
```csharp
/// <summary>
/// 获取技能信息（禁用的技能返回 null）
/// </summary>
public SkillInfo? GetSkillInfo(string name)
{
    if (string.IsNullOrEmpty(name)) return null;
    if (!IsSkillEnabled(name)) return null;
    return _skillInfos.TryGetValue(name, out var info) ? info : null;
}
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build src/Seeing.Agent`
Expected: BUILD SUCCEEDED

- [ ] **Step 7: 提交**

```bash
git add src/Seeing.Agent/Skills/SkillManager.cs
git commit -m "feat: add skill disable state management to SkillManager"
```

---

### Task 3: SkillTool 添加禁用检查

**Files:**
- Modify: `src/Seeing.Agent/Tools/SkillTool.cs`

- [ ] **Step 1: 修改 ExecuteAsync 添加禁用检查**

在 `SkillTool.ExecuteAsync()` 方法中，在获取技能信息之后、权限检查之前添加禁用检查：
```csharp
public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
{
    // 获取技能名称
    if (!arguments.TryGetProperty("name", out var nameProp))
    {
        return Failure("Missing required parameter: name");
    }

    var skillName = nameProp.GetString();
    if (string.IsNullOrEmpty(skillName))
    {
        return Failure("Parameter 'name' must be a non-empty string");
    }

    // 检查技能是否启用
    if (!_skillManager.IsSkillEnabled(skillName))
    {
        _logger.LogWarning("尝试加载已禁用的技能: {SkillName}", skillName);
        return Failure($"Skill '{skillName}' is disabled. Enable it in the Skills settings.");
    }

    // 获取技能信息
    var skill = _skillManager.GetSkillInfo(skillName);
    if (skill == null)
    {
        var available = string.Join(", ", _skillManager.GetAllSkillInfos().Keys);
        return Failure($"Skill \"{skillName}\" not found. Available skills: {(string.IsNullOrEmpty(available) ? "none" : available)}");
    }

    // 现有代码继续...
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/Seeing.Agent`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Tools/SkillTool.cs
git commit -m "feat: add skill disable check to SkillTool.ExecuteAsync"
```

---

### Task 4: 初始化流程集成

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 在 InitializeSeeingAgentAsync 中添加状态加载**

在 `InitializeSeeingAgentAsync()` 方法中，在 `ComponentManager.LoadAllAsync()` 之后添加：
```csharp
// 加载工具/技能禁用状态
var toolInvoker = sp.GetService<ToolInvoker>();
if (toolInvoker != null)
{
    await toolInvoker.LoadToolStateAsync(ct);
}

var skillManager = sp.GetService<SkillManager>();
if (skillManager != null)
{
    await skillManager.LoadSkillStateAsync(ct);
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/Seeing.Agent`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat: load tool/skill disable state during initialization"
```

---

### Task 5: WebUI 迁移 - 删除旧服务

**Files:**
- Delete: `samples/Seeing.Agent.WebUI/Services/ToolStateService.cs`
- Delete: `samples/Seeing.Agent.WebUI/Services/SkillStateService.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`

- [ ] **Step 1: 删除 ToolStateService.cs**

```bash
rm samples/Seeing.Agent.WebUI/Services/ToolStateService.cs
```

- [ ] **Step 2: 删除 SkillStateService.cs**

```bash
rm samples/Seeing.Agent.WebUI/Services/SkillStateService.cs
```

- [ ] **Step 3: 修改 Program.cs 移除旧服务注册**

在 `Program.cs` 中移除以下行：
```csharp
// 移除这两行
builder.Services.AddSingleton<SkillStateService>();
builder.Services.AddSingleton<ToolStateService>();
```

移除启动时加载状态的代码：
```csharp
// 移除这些行
var skillState = sp.GetRequiredService<SkillStateService>();
var toolState = sp.GetRequiredService<ToolStateService>();
await skillState.LoadAsync();
await toolState.LoadAsync();
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: BUILD SUCCEEDED（可能有编译错误，需要在后续 Task 中修复引用）

- [ ] **Step 5: 提交**

```bash
git add -A samples/Seeing.Agent.WebUI/
git commit -m "refactor: remove redundant ToolStateService and SkillStateService from WebUI"
```

---

### Task 6: WebUI 迁移 - 修改 ToolsPage

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/ToolsPage.razor`

- [ ] **Step 1: 添加 ToolInvoker 引用**

在 `@code` 块中添加 `ToolInvoker` 注入：
```csharp
@inject ToolInvoker ToolInvoker
```

- [ ] **Step 2: 替换 ToolStateService 引用**

将所有 `ToolStateService.IsEnabled(tool.Id)` 替换为 `ToolInvoker.IsToolEnabled(tool.Id)`

将所有 `ToolStateService.SetEnabled(tool.Id, v)` 替换为 `ToolInvoker.SetToolEnabledAsync(tool.Id, v)`

将 `_tools` 的获取方式从 `ToolStateService` 改为 `ToolInvoker.GetToolsIncludingDisabled()`：
```csharp
private List<ITool> _tools = new();

protected override void OnInitialized()
{
    _tools = ToolInvoker.GetToolsIncludingDisabled().ToList();
    // 其余初始化代码...
}
```

- [ ] **Step 3: 修改 ToggleTool 方法**

```csharp
private async Task ToggleTool(string id, bool enabled)
{
    await ToolInvoker.SetToolEnabledAsync(id, enabled);
    _enabledCount = _tools.Count(t => ToolInvoker.IsToolEnabled(t.Id));
    _allEnabled = _enabledCount == _tools.Count;
    StateHasChanged();
}
```

- [ ] **Step 4: 修改 ToggleAllTools 方法**

```csharp
private async Task ToggleAllTools(bool enabled)
{
    foreach (var tool in _tools)
    {
        await ToolInvoker.SetToolEnabledAsync(tool.Id, enabled);
    }
    _allEnabled = enabled;
    _enabledCount = enabled ? _tools.Count : 0;
    StateHasChanged();
}
```

- [ ] **Step 5: 编译验证**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: BUILD SUCCEEDED

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/ToolsPage.razor
git commit -m "refactor: migrate ToolsPage to use core ToolInvoker API"
```

---

### Task 7: WebUI 迁移 - 修改 SkillsPage

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/SkillsPage.razor`

- [ ] **Step 1: 添加 SkillManager 引用**

在 `@code` 块中添加 `SkillManager` 注入：
```csharp
@inject SkillManager SkillManager
```

- [ ] **Step 2: 替换 SkillStateService 引用**

将所有 `SkillStateService.IsEnabled(skill.Key)` 替换为 `SkillManager.IsSkillEnabled(skill.Key)`

将 `_allSkills` 的获取方式改为 `SkillManager.GetAllSkillInfosIncludingDisabled()`：
```csharp
private Dictionary<string, SkillInfo> _allSkills = new();

protected override async Task OnInitializedAsync()
{
    _allSkills = SkillManager.GetAllSkillInfosIncludingDisabled()
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    // 其余初始化代码...
}
```

- [ ] **Step 3: 修改 ToggleSkill 方法**

```csharp
private async Task ToggleSkill(string name, bool enabled)
{
    await SkillManager.SetSkillEnabledAsync(name, enabled);
    StateHasChanged();
}
```

- [ ] **Step 4: 修改统计计算**

```csharp
private int _enabledCount => _allSkills.Count(s => SkillManager.IsSkillEnabled(s.Key));
private int _disabledCount => _totalCount - _enabledCount;
```

- [ ] **Step 5: 编译验证**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: BUILD SUCCEEDED

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/SkillsPage.razor
git commit -m "refactor: migrate SkillsPage to use core SkillManager API"
```

---

### Task 8: WebUI 迁移 - 修改 SkillDetailPage

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/SkillDetailPage.razor`

- [ ] **Step 1: 添加 SkillManager 引用**

在 `@code` 块中添加 `SkillManager` 注入：
```csharp
@inject SkillManager SkillManager
```

- [ ] **Step 2: 替换 SkillStateService 引用**

将 `SkillStateService.IsEnabled(SkillName)` 替换为 `SkillManager.IsSkillEnabled(SkillName)`

将 `SkillStateService.SetEnabled(SkillName, enabled)` 替换为 `SkillManager.SetSkillEnabledAsync(SkillName, enabled)`

- [ ] **Step 3: 修改 ToggleEnabled 方法**

```csharp
private async Task ToggleEnabled(bool enabled)
{
    await SkillManager.SetSkillEnabledAsync(SkillName, enabled);
    _enabled = enabled;
    MessageService.Success(enabled ? "已启用" : "已禁用");
}
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: BUILD SUCCEEDED

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/SkillDetailPage.razor
git commit -m "refactor: migrate SkillDetailPage to use core SkillManager API"
```

---

### Task 9: 最终验证

**Files:**
- All modified files

- [ ] **Step 1: 完整编译验证**

Run: `dotnet build Seeing.Agent.slnx`
Expected: BUILD SUCCEEDED

- [ ] **Step 2: 运行测试**

Run: `dotnet test tests/Seeing.Agent.Tests`
Expected: All tests pass

- [ ] **Step 3: 功能验证**

1. 启动 WebUI：`dotnet run --project samples/Seeing.Agent.WebUI`
2. 访问 `/tools` 页面，禁用一个工具
3. 验证 `~/.seeing/tool-state.json` 或 `.seeing/tool-state.json` 已更新
4. 验证 LLM 不再收到该工具的 schema
5. 访问 `/skills` 页面，禁用一个技能
6. 验证 `~/.seeing/skill-state.json` 或 `.seeing/skill-state.json` 已更新
7. 验证 LLM 不再看到该技能

- [ ] **Step 4: 最终提交**

```bash
git add -A
git commit -m "feat: complete tool/skill disable configuration integration"
```
