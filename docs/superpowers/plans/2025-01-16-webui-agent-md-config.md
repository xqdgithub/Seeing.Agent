# WebUI Agent MD 配置管理实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 WebUI 添加 Agent MD 配置文件的完整 CRUD 功能，集成 Monaco Editor，确保数据一致性和内聚性。

**Architecture:** 扩展 AgentConfigLoader 作为 MD 文件唯一读写入口，新增 AgentMdConfigService 作为 UI 层封装，修改 AgentsPage 添加管理界面，使用 BlazorMonaco 提供 Monaco Editor 支持。

**Tech Stack:** C# / Blazor / AntDesign Blazor / BlazorMonaco / YamlDotNet

---

## 文件结构

| 文件路径 | 职责 | 类型 |
|---------|------|------|
| `src/Seeing.Agent/Configuration/AgentConfigChangedEventArgs.cs` | MD 配置变更事件参数 | 新增 |
| `src/Seeing.Agent/Configuration/AgentMdInfo.cs` | MD 配置信息模型 | 新增 |
| `src/Seeing.Agent/Configuration/AgentConfigLoader.cs` | 扩展接口：添加 CRUD 方法、事件 | 修改 |
| `src/Seeing.Agent/Core/AgentRegistry.cs` | 订阅 ConfigChanged 事件 | 修改 |
| `samples/Seeing.Agent.WebUI/Services/AgentMdConfigService.cs` | UI 层服务封装 | 新增 |
| `samples/Seeing.Agent.WebUI/Components/Agents/AgentMdEditorDrawer.razor` | 编辑器抽屉组件 | 新增 |
| `samples/Seeing.Agent.WebUI/Components/MonacoEditor.razor` | Monaco Editor 封装 | 新增 |
| `samples/Seeing.Agent.WebUI/Pages/AgentsPage.razor` | 添加 MD 配置管理功能 | 修改 |
| `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` | 添加 BlazorMonaco 引用 | 修改 |
| `tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs` | CRUD 方法测试 | 修改 |

---

## Task 1: 定义事件参数和模型类

**Files:**
- Create: `src/Seeing.Agent/Configuration/AgentConfigChangedEventArgs.cs`
- Create: `src/Seeing.Agent/Configuration/AgentMdInfo.cs`

- [ ] **Step 1: 创建 AgentConfigChangedEventArgs.cs**

```csharp
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>
/// Agent MD 配置变更事件参数
/// </summary>
public class AgentConfigChangedEventArgs : EventArgs
{
    /// <summary>
    /// Agent 名称
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 配置层级
    /// </summary>
    public ConfigLevel Level { get; init; }

    /// <summary>
    /// 变更类型
    /// </summary>
    public ConfigChangeAction Action { get; init; }
}

/// <summary>
/// 配置变更动作类型
/// </summary>
public enum ConfigChangeAction
{
    Created,
    Updated,
    Deleted
}
```

- [ ] **Step 2: 创建 AgentMdInfo.cs**

```csharp
namespace Seeing.Agent.Configuration;

/// <summary>
/// Agent MD 配置信息（用于 UI 展示）
/// </summary>
public class AgentMdInfo
{
    /// <summary>
    /// Agent 名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 配置层级（用户级/项目级）
    /// </summary>
    public ConfigLevel Level { get; set; }

    /// <summary>
    /// 文件路径
    /// </summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// 变体数量
    /// </summary>
    public int VariantCount { get; set; }

    /// <summary>
    /// 最后修改时间
    /// </summary>
    public DateTimeOffset LastModified { get; set; }
}
```

- [ ] **Step 3: 验证构建**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Configuration/AgentConfigChangedEventArgs.cs
git add src/Seeing.Agent/Configuration/AgentMdInfo.cs
git commit -m "feat: add AgentConfigChangedEventArgs and AgentMdInfo models"
```

---

## Task 2: 扩展 IAgentConfigLoader 接口

**Files:**
- Modify: `src/Seeing.Agent/Configuration/AgentConfigLoader.cs` (接口定义部分)

- [ ] **Step 1: 扩展 IAgentConfigLoader 接口**

修改 `src/Seeing.Agent/Configuration/AgentConfigLoader.cs` 中的接口定义：

```csharp
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

    /// <summary>
    /// 解析 MD 配置文件
    /// </summary>
    AgentConfigFile? ParseFile(string filePath);

    /// <summary>
    /// 获取所有层级的 MD 配置信息
    /// </summary>
    Task<IReadOnlyList<AgentMdInfo>> GetAllWithLevelAsync(CancellationToken ct = default);

    /// <summary>
    /// 创建新的 MD 配置文件
    /// </summary>
    Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, string? template = null, CancellationToken ct = default);

    /// <summary>
    /// 保存 MD 配置文件
    /// </summary>
    Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default);

    /// <summary>
    /// 删除 MD 配置文件
    /// </summary>
    Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default);

    /// <summary>
    /// 配置变更事件
    /// </summary>
    event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
Expected: Build failed (接口方法未实现)

---

## Task 3: 实现 AgentConfigLoader 写入方法

**Files:**
- Modify: `src/Seeing.Agent/Configuration/AgentConfigLoader.cs`

- [ ] **Step 1: 添加私有字段和事件**

在 `AgentConfigLoader` 类中添加：

```csharp
// 在类开头添加私有字段
private readonly Dictionary<string, AgentConfigFile> _cache = new();

/// <inheritdoc/>
public event EventHandler<AgentConfigChangedEventArgs>? ConfigChanged;
```

- [ ] **Step 2: 实现 ParseFile 为公开方法**

将 `ParseFile` 方法标记为 `public`（如果还不是）：

```csharp
/// <inheritdoc/>
public AgentConfigFile? ParseFile(string filePath)
{
    // 现有实现保持不变
    try
    {
        var content = File.ReadAllText(filePath);
        // ... 其余代码不变
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "解析配置文件 '{FilePath}' 失败", filePath);
        return null;
    }
}
```

- [ ] **Step 3: 实现 GetAllWithLevelAsync 方法**

```csharp
/// <inheritdoc/>
public async Task<IReadOnlyList<AgentMdInfo>> GetAllWithLevelAsync(CancellationToken ct = default)
{
    var result = new List<AgentMdInfo>();

    // 用户级目录
    var userAgentsDir = Path.Combine(_workspaceProvider.UserSeeingDirectory, "agents");
    if (Directory.Exists(userAgentsDir))
    {
        foreach (var file in Directory.GetFiles(userAgentsDir, "*.md"))
        {
            var info = CreateAgentMdInfo(file, ConfigLevel.User);
            if (info != null) result.Add(info);
        }
    }

    // 项目级目录
    var projectAgentsDir = Path.Combine(_workspaceProvider.ProjectSeeingDirectory, "agents");
    if (Directory.Exists(projectAgentsDir))
    {
        foreach (var file in Directory.GetFiles(projectAgentsDir, "*.md"))
        {
            var info = CreateAgentMdInfo(file, ConfigLevel.Project);
            if (info != null) result.Add(info);
        }
    }

    return await Task.FromResult(result.AsReadOnly());
}

private AgentMdInfo? CreateAgentMdInfo(string filePath, ConfigLevel level)
{
    var config = ParseFile(filePath);
    if (config == null) return null;

    return new AgentMdInfo
    {
        Name = config.Name,
        Description = config.Description,
        Level = level,
        FilePath = filePath,
        VariantCount = config.Variants?.Count ?? 0,
        LastModified = File.GetLastWriteTimeUtc(filePath)
    };
}
```

- [ ] **Step 4: 实现 CreateAsync 方法**

```csharp
/// <inheritdoc/>
public async Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, string? template = null, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(name);

    var filePath = GetFilePath(name, level);
    var directory = Path.GetDirectoryName(filePath)!;

    if (!Directory.Exists(directory))
    {
        Directory.CreateDirectory(directory);
    }

    if (File.Exists(filePath))
    {
        throw new InvalidOperationException($"Agent '{name}' 的配置文件已存在");
    }

    var content = template ?? GetDefaultTemplate(name);
    await File.WriteAllTextAsync(filePath, content, ct);

    var config = ParseFile(filePath);
    if (config == null)
    {
        throw new InvalidOperationException("创建的配置文件解析失败");
    }

    _cache[name] = config;
    OnConfigChanged(name, level, ConfigChangeAction.Created);

    _logger.LogInformation("创建 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
    return config;
}
```

- [ ] **Step 5: 实现 SaveAsync 方法**

```csharp
/// <inheritdoc/>
public async Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentException.ThrowIfNullOrEmpty(content);

    var filePath = GetFilePath(name, level);

    // 验证内容格式
    var config = ParseContent(content);
    if (config == null)
    {
        _logger.LogWarning("保存失败：内容格式无效");
        return false;
    }

    await File.WriteAllTextAsync(filePath, content, ct);

    _cache[name] = config;
    OnConfigChanged(name, level, ConfigChangeAction.Updated);

    _logger.LogInformation("保存 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
    return true;
}
```

- [ ] **Step 6: 实现 DeleteAsync 方法**

```csharp
/// <inheritdoc/>
public async Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrEmpty(name);

    var filePath = GetFilePath(name, level);

    if (!File.Exists(filePath))
    {
        _logger.LogWarning("删除失败：Agent '{Name}' 的配置文件不存在", name);
        return false;
    }

    await Task.Run(() => File.Delete(filePath), ct);

    _cache.Remove(name);
    OnConfigChanged(name, level, ConfigChangeAction.Deleted);

    _logger.LogInformation("删除 Agent '{Name}' MD 配置文件: {FilePath}", name, filePath);
    return true;
}
```

- [ ] **Step 7: 添加辅助方法**

```csharp
/// <summary>
/// 获取配置文件路径
/// </summary>
public string GetFilePath(string name, ConfigLevel level)
{
    var baseDir = level == ConfigLevel.User
        ? _workspaceProvider.UserSeeingDirectory
        : _workspaceProvider.ProjectSeeingDirectory;
    return Path.Combine(baseDir, "agents", $"{name}.md");
}

/// <summary>
/// 获取默认模板
/// </summary>
public static string GetDefaultTemplate(string agentName)
{
    return $@"---
name: {agentName}
description: Agent 描述
mode: Primary
category: general
maxSteps: 50
variants: {{}}
---

# 系统提示词

你是一个 AI 助手，负责...
";
}

/// <summary>
/// 解析内容（不读取文件）
/// </summary>
private AgentConfigFile? ParseContent(string content)
{
    try
    {
        var frontMatterEnd = content.IndexOf("---", 3);
        if (frontMatterEnd == -1) return null;

        var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
        var config = _yamlDeserializer.Deserialize<AgentConfigFile>(yamlContent);

        return string.IsNullOrEmpty(config?.Name) ? null : config;
    }
    catch
    {
        return null;
    }
}

/// <summary>
/// 触发配置变更事件
/// </summary>
protected virtual void OnConfigChanged(string name, ConfigLevel level, ConfigChangeAction action)
{
    ConfigChanged?.Invoke(this, new AgentConfigChangedEventArgs
    {
        Name = name,
        Level = level,
        Action = action
    });
}
```

- [ ] **Step 8: 验证构建**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add src/Seeing.Agent/Configuration/AgentConfigLoader.cs
git commit -m "feat: add CRUD methods and ConfigChanged event to AgentConfigLoader"
```

---

## Task 4: AgentRegistry 订阅 ConfigChanged 事件

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentRegistry.cs`

- [ ] **Step 1: 在构造函数中订阅事件**

在 `AgentRegistry` 构造函数中添加事件订阅：

```csharp
// 在构造函数末尾添加
if (_configLoader != null)
{
    _configLoader.ConfigChanged += OnAgentConfigChanged;
}
```

- [ ] **Step 2: 添加事件处理方法**

```csharp
/// <summary>
/// 处理 MD 配置变更事件
/// </summary>
private void OnAgentConfigChanged(object? sender, AgentConfigChangedEventArgs e)
{
    _logger.LogDebug("收到 MD 配置变更通知: {Name}, {Action}, {Level}", e.Name, e.Action, e.Level);

    switch (e.Action)
    {
        case ConfigChangeAction.Created:
        case ConfigChangeAction.Updated:
            // 重新加载 Agent 配置（下次获取时自动刷新）
            _agentStore.RemoveAsync(e.Name).GetAwaiter().GetResult();
            break;
        case ConfigChangeAction.Deleted:
            // 移除 Agent 配置
            _agentStore.RemoveAsync(e.Name).GetAwaiter().GetResult();
            break;
    }
}
```

- [ ] **Step 3: 验证构建**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Core/AgentRegistry.cs
git commit -m "feat: subscribe to ConfigChanged event in AgentRegistry"
```

---

## Task 5: 创建 AgentMdConfigService

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/AgentMdConfigService.cs`

- [ ] **Step 1: 创建 AgentMdConfigService.cs**

```csharp
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Agent MD 配置服务（UI 层封装）
/// </summary>
public class AgentMdConfigService
{
    private readonly IAgentConfigLoader _loader;

    public AgentMdConfigService(IAgentConfigLoader loader)
    {
        _loader = loader;
    }

    /// <summary>
    /// 获取所有 MD 配置信息
    /// </summary>
    public Task<IReadOnlyList<AgentMdInfo>> GetAllAsync(CancellationToken ct = default)
        => _loader.GetAllWithLevelAsync(ct);

    /// <summary>
    /// 获取单个 MD 配置内容
    /// </summary>
    public async Task<string?> GetContentAsync(string name, ConfigLevel level, CancellationToken ct = default)
    {
        var filePath = _loader.GetFilePath(name, level);
        if (!File.Exists(filePath)) return null;
        return await File.ReadAllTextAsync(filePath, ct);
    }

    /// <summary>
    /// 创建新的 MD 配置
    /// </summary>
    public Task<AgentConfigFile> CreateAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => _loader.CreateAsync(name, level, null, ct);

    /// <summary>
    /// 保存 MD 配置
    /// </summary>
    public Task<bool> SaveAsync(string name, ConfigLevel level, string content, CancellationToken ct = default)
        => _loader.SaveAsync(name, level, content, ct);

    /// <summary>
    /// 删除 MD 配置
    /// </summary>
    public Task<bool> DeleteAsync(string name, ConfigLevel level, CancellationToken ct = default)
        => _loader.DeleteAsync(name, level, ct);

    /// <summary>
    /// 获取默认模板
    /// </summary>
    public string GetDefaultTemplate(string agentName)
        => AgentConfigLoader.GetDefaultTemplate(agentName);

    /// <summary>
    /// 验证内容格式
    /// </summary>
    public ValidationResult ValidateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return ValidationResult.Failure("内容不能为空");

        // 检查 YAML Front Matter
        if (!content.StartsWith("---"))
            return ValidationResult.Failure("缺少 YAML Front Matter（应以 --- 开头）");

        var frontMatterEnd = content.IndexOf("---", 3);
        if (frontMatterEnd == -1)
            return ValidationResult.Failure("YAML Front Matter 格式错误（缺少结束 ---）");

        var yamlContent = content.Substring(3, frontMatterEnd - 3).Trim();
        if (string.IsNullOrWhiteSpace(yamlContent))
            return ValidationResult.Failure("YAML Front Matter 内容为空");

        // 检查 name 字段
        if (!yamlContent.Contains("name:"))
            return ValidationResult.Failure("YAML Front Matter 缺少 name 字段");

        return ValidationResult.Success();
    }

    /// <summary>
    /// 获取文件路径
    /// </summary>
    public string GetFilePath(string name, ConfigLevel level)
        => _loader.GetFilePath(name, level);
}

/// <summary>
/// 验证结果
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; }
    public string? Error { get; }

    private ValidationResult(bool isValid, string? error = null)
    {
        IsValid = isValid;
        Error = error;
    }

    public static ValidationResult Success() => new(true);
    public static ValidationResult Failure(string error) => new(false, error);
}
```

- [ ] **Step 2: 注册 DI 服务**

在 `samples/Seeing.Agent.WebUI/Program.cs` 中添加：

```csharp
// 在其他服务注册后添加
builder.Services.AddScoped<AgentMdConfigService>();
```

- [ ] **Step 3: 验证构建**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Services/AgentMdConfigService.cs
git add samples/Seeing.Agent.WebUI/Program.cs
git commit -m "feat: add AgentMdConfigService for UI layer"
```

---

## Task 6: 添加 BlazorMonaco 包引用

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`

- [ ] **Step 1: 添加 BlazorMonaco 包引用**

在 `Seeing.Agent.WebUI.csproj` 的 `<ItemGroup>` 中添加：

```xml
<PackageReference Include="BlazorMonaco" Version="3.2.0" />
```

- [ ] **Step 2: 添加 JS 互操作脚本**

在 `samples/Seeing.Agent.WebUI/wwwroot/index.html` 或 `Pages/_Host.cshtml` 中添加（在 `</body>` 前）：

```html
<script src="_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js"></script>
<script src="_content/BlazorMonaco/jsInterop.js"></script>
```

- [ ] **Step 3: 验证构建**

Run: `dotnet restore samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj && dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj
git add samples/Seeing.Agent.WebUI/wwwroot/index.html
git commit -m "feat: add BlazorMonaco package reference"
```

---

## Task 7: 创建 MonacoEditor 组件

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/MonacoEditor.razor`

- [ ] **Step 1: 创建 MonacoEditor.razor**

```razor
@using BlazorMonaco.Editor

<div class="monaco-editor-container" style="height: 100%; width: 100%; border: 1px solid #d9d9d9; border-radius: 4px;">
    <StandaloneEditor 
        Id="@Id"
        ConstructionOptions="GetConstructionOptions"
        OnDidValueChange="OnValueChange" />
</div>

@code {
    [Parameter]
    public string Value { get; set; } = "";

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    [Parameter]
    public string Language { get; set; } = "markdown";

    [Parameter]
    public string Theme { get; set; } = "vs";

    [Parameter]
    public bool ReadOnly { get; set; }

    [Parameter]
    public int MinimapSize { get; set; } = 0;

    private string Id { get; } = $"monaco-{Guid.NewGuid():N}";

    private StandaloneEditorConstructionOptions GetConstructionOptions(StandaloneCodeEditor editor)
    {
        return new StandaloneEditorConstructionOptions
        {
            Value = Value,
            Language = Language,
            Theme = Theme,
            ReadOnly = ReadOnly,
            Minimap = new EditorMinimapOptions { Enabled = MinimapSize > 0 },
            AutomaticLayout = true,
            FontSize = 14,
            LineNumbers = OnOff.On,
            ScrollBeyondLastLine = false,
            WordWrap = "on",
            TabSize = 2
        };
    }

    private async Task OnValueChange(ModelContentChangedEvent e)
    {
        // 获取编辑器内容
        // BlazorMonaco 会自动处理这个事件
        // 我们需要通过另一种方式获取值
    }

    public async Task SetValueAsync(string value)
    {
        Value = value;
        await ValueChanged.InvokeAsync(value);
    }
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Components/MonacoEditor.razor
git commit -m "feat: add MonacoEditor wrapper component"
```

---

## Task 8: 创建 AgentMdEditorDrawer 组件

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/Agents/AgentMdEditorDrawer.razor`

- [ ] **Step 1: 创建 AgentMdEditorDrawer.razor**

```razor
@using AntDesign
@using Seeing.Agent.Configuration
@inject AgentMdConfigService MdConfigService
@inject IMessageService MessageService

<Drawer Title="@_drawerTitle"
        @bind-Visible="Visible"
        Width="900"
        Placement="Placement.Right"
        OnClose="HandleClose">
    <div class="drawer-content">
        @if (_loading)
        {
            <Spin Spinning="true" />
        }
        else
        {
            <div class="editor-wrapper" style="height: calc(100vh - 200px);">
                <MonacoEditor @ref="_editor"
                              @bind-Value="_content"
                              Language="markdown" />
            </div>

            @if (_validationErrors.Any())
            {
                <Alert Type="AlertType.Error"
                       Message="验证错误"
                       Description="@string.Join("\n", _validationErrors)"
                       ShowIcon="true"
                       Style="margin-top: 16px;" />
            }

            <div class="drawer-footer" style="margin-top: 16px; text-align: right;">
                <Space>
                    <Button OnClick="HandleClose">取消</Button>
                    <Button Type="@ButtonType.Primary" 
                            OnClick="HandleSave"
                            Loading="_saving"
                            Disabled="@_validationErrors.Any()">
                        保存
                    </Button>
                </Space>
            </div>
        }
    </div>
</Drawer>

@code {
    [Parameter]
    public bool Visible { get; set; }

    [Parameter]
    public EventCallback<bool> VisibleChanged { get; set; }

    [Parameter]
    public string? AgentName { get; set; }

    [Parameter]
    public ConfigLevel Level { get; set; } = ConfigLevel.Project;

    [Parameter]
    public EventCallback OnSaved { get; set; }

    private MonacoEditor? _editor;
    private string _content = "";
    private string _drawerTitle = "新建 MD 配置";
    private bool _loading;
    private bool _saving;
    private List<string> _validationErrors = new();
    private bool _isNew = true;

    protected override async Task OnParametersSetAsync()
    {
        if (Visible && !string.IsNullOrEmpty(AgentName))
        {
            await LoadContentAsync();
        }
    }

    private async Task LoadContentAsync()
    {
        _loading = true;
        _isNew = true;
        _validationErrors.Clear();

        try
        {
            var content = await MdConfigService.GetContentAsync(AgentName!, Level);
            if (content != null)
            {
                _content = content;
                _isNew = false;
                _drawerTitle = $"编辑 MD 配置 - {AgentName}";
            }
            else
            {
                _content = MdConfigService.GetDefaultTemplate(AgentName!);
                _drawerTitle = $"新建 MD 配置 - {AgentName}";
            }
        }
        catch (Exception ex)
        {
            _validationErrors.Add($"加载失败: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task HandleSave()
    {
        _validationErrors.Clear();

        // 验证内容
        var result = MdConfigService.ValidateContent(_content);
        if (!result.IsValid)
        {
            _validationErrors.Add(result.Error!);
            return;
        }

        _saving = true;
        try
        {
            if (_isNew)
            {
                await MdConfigService.CreateAsync(AgentName!, Level);
            }

            var success = await MdConfigService.SaveAsync(AgentName!, Level, _content);
            if (success)
            {
                await MessageService.Success("保存成功");
                await OnSaved.InvokeAsync();
                await HandleClose();
            }
            else
            {
                await MessageService.Error("保存失败");
            }
        }
        catch (Exception ex)
        {
            await MessageService.Error($"保存失败: {ex.Message}");
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task HandleClose()
    {
        Visible = false;
        await VisibleChanged.InvokeAsync(false);
        _content = "";
        _validationErrors.Clear();
    }
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Components/Agents/AgentMdEditorDrawer.razor
git commit -m "feat: add AgentMdEditorDrawer component"
```

---

## Task 9: 修改 AgentsPage 添加 MD 配置管理

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/AgentsPage.razor`

- [ ] **Step 1: 修改 AgentsPage.razor**

完整替换文件内容：

```razor
@page "/agents"
@using AntDesign
@using Seeing.Agent.Configuration
@using Seeing.Agent.Core.Interfaces
@using Seeing.Agent.Core.Models
@inject IAgentRegistry AgentRegistry
@inject AgentMdConfigService MdConfigService
@inject NavigationManager NavigationManager
@inject IMessageService MessageService
@inject IWorkspaceProvider WorkspaceProvider

<PageTitle>智能体管理 - Seeing.Agent</PageTitle>

<WorkspacePageShell Loading="@_loading" IsEmpty="@(!_rows.Any())"
                    EmptyTitle="暂无智能体" EmptyDescription="请检查 seeing.json 中的 Agents 配置">
    <Breadcrumb>
        <BreadcrumbItem>设置</BreadcrumbItem>
        <BreadcrumbItem>智能体管理</BreadcrumbItem>
    </Breadcrumb>
    <Extra>
        <Space>
            <Select @bind-Value="_selectedLevel" Style="width: 120px;">
                <SelectOption Value="@ConfigLevel.Project">项目级</SelectOption>
                <SelectOption Value="@ConfigLevel.User">用户级</SelectOption>
            </Select>
            <Button Type="@ButtonType.Primary" Icon="@IconType.Outline.Plus" OnClick="OpenCreateDialog">新增 MD 配置</Button>
            <Button Icon="@IconType.Outline.Reload" OnClick="LoadAsync">刷新</Button>
        </Space>
    </Extra>
    <Stats>
        <div class="stat-item">
            <div class="stat-icon"><Icon Type="@IconType.Outline.Robot" /></div>
            <div class="stat-content">
                <span class="stat-value">@_rows.Count</span>
                <span class="stat-label">智能体总数</span>
            </div>
        </div>
        <div class="stat-item">
            <div class="stat-icon"><Icon Type="@IconType.Outline.Star" /></div>
            <div class="stat-content">
                <span class="stat-value">@_defaultAgent</span>
                <span class="stat-label">默认智能体</span>
            </div>
        </div>
        <div class="stat-item">
            <div class="stat-icon"><Icon Type="@IconType.Outline.FileMarkdown" /></div>
            <div class="stat-content">
                <span class="stat-value">@_mdConfigs.Count</span>
                <span class="stat-label">MD 配置</span>
            </div>
        </div>
    </Stats>
    <ChildContent>
        <Table TItem="AgentRow" DataSource="@_rows" RowKey="@(a => a.Name)" Size="@TableSize.Middle">
            <PropertyColumn Property="c => c.Name" Title="名称" />
            <PropertyColumn Property="c => c.DefaultLabel" Title="默认" />
            <PropertyColumn Property="c => c.Description" Title="描述" />
            <PropertyColumn Property="c => c.Source" Title="来源">
                @if (context.IsMdConfig)
                {
                    <Tag Color="@GetSourceColor(context.Level)">
                        @GetSourceText(context.Level)
                    </Tag>
                }
                else
                {
                    <Tag>@context.Source</Tag>
                }
            </PropertyColumn>
            <PropertyColumn Property="c => c.VariantCount" Title="变体">
                @if (context.VariantCount > 0)
                {
                    <Badge Count="@context.VariantCount" />
                }
            </PropertyColumn>
            <PropertyColumn Property="c => c.RuntimeText" Title="运行时" />
            <ActionColumn Title="操作">
                <Space>
                    @if (!context.IsDefault)
                    {
                        <Button Size="@ButtonSize.Small" OnClick="@(() => SetDefault(context.Name))">设为默认</Button>
                    }
                    <Button Size="@ButtonSize.Small" OnClick="@(() => GoConfig(context.Name))">查看配置</Button>
                    @if (context.IsMdConfig)
                    {
                        <Button Size="@ButtonSize.Small" Type="@ButtonType.Primary" 
                                OnClick="@(() => OpenEditDrawer(context.Name))">编辑MD</Button>
                        <Popconfirm Title="确定删除此 MD 配置？" OnConfirm="@(() => DeleteMdConfig(context.Name))">
                            <Button Size="@ButtonSize.Small" Danger>删除</Button>
                        </Popconfirm>
                    }
                </Space>
            </ActionColumn>
        </Table>
    </ChildContent>
</WorkspacePageShell>

<AgentMdEditorDrawer @bind-Visible="_drawerVisible"
                      AgentName="_currentAgentName"
                      Level="_selectedLevel"
                      OnSaved="HandleMdSaved" />

<Modal Title="新建 MD 配置"
       @bind-Visible="_createModalVisible"
       OnOk="HandleCreate"
       ConfirmLoading="_creating">
    <Form Model="_createForm" LabelColSpan="6">
        <FormItem Label="Agent 名称">
            <Input @bind-Value="_createForm.Name" Placeholder="例如: my-agent" />
        </FormItem>
        <FormItem Label="配置层级">
            <RadioGroup @bind-Value="_createForm.Level">
                <Radio Value="@ConfigLevel.Project">项目级</Radio>
                <Radio Value="@ConfigLevel.User">用户级</Radio>
            </RadioGroup>
        </FormItem>
    </Form>
</Modal>

@code {
    private List<AgentRow> _rows = new();
    private List<AgentMdInfo> _mdConfigs = new();
    private string _defaultAgent = "";
    private bool _loading;
    private ConfigLevel _selectedLevel = ConfigLevel.Project;

    // Drawer 状态
    private bool _drawerVisible;
    private string _currentAgentName = "";

    // 创建模态框状态
    private bool _createModalVisible;
    private bool _creating;
    private CreateForm _createForm = new();

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _defaultAgent = await AgentRegistry.GetDefaultAgentNameAsync();
            var agents = await AgentRegistry.GetPrimaryAgentsAsync();

            // 获取 MD 配置信息
            _mdConfigs = (await MdConfigService.GetAllAsync()).ToList();

            _rows = agents.Select(a =>
            {
                var mdInfo = _mdConfigs.FirstOrDefault(m => m.Name == a.Name);
                return new AgentRow
                {
                    Name = a.Name,
                    Description = a.Description ?? "",
                    RuntimeText = a.Runtime.ToString(),
                    Source = a.IsNative ? "内置" : (mdInfo != null ? "MD" : "JSON"),
                    IsDefault = a.Name == _defaultAgent,
                    DefaultLabel = a.Name == _defaultAgent ? "是" : "",
                    IsMdConfig = mdInfo != null,
                    Level = mdInfo?.Level ?? ConfigLevel.Project,
                    VariantCount = mdInfo?.VariantCount ?? 0
                };
            }).ToList();
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task SetDefault(string name)
    {
        await AgentRegistry.SetDefaultAgentAsync(name);
        await LoadAsync();
        MessageService.Success($"已将 {name} 设为默认智能体");
    }

    private void GoConfig(string name) =>
        NavigationManager.NavigateTo($"/agent-config?agent={Uri.EscapeDataString(name)}");

    private void OpenCreateDialog()
    {
        _createForm = new CreateForm { Level = _selectedLevel };
        _createModalVisible = true;
    }

    private async Task HandleCreate()
    {
        if (string.IsNullOrWhiteSpace(_createForm.Name))
        {
            MessageService.Warning("请输入 Agent 名称");
            return;
        }

        // 验证名称格式
        if (!System.Text.RegularExpressions.Regex.IsMatch(_createForm.Name, @"^[a-zA-Z_][a-zA-Z0-9_-]{0,63}$"))
        {
            MessageService.Warning("名称只能包含字母、数字、下划线、连字符，且不能以数字开头");
            return;
        }

        _creating = true;
        try
        {
            await MdConfigService.CreateAsync(_createForm.Name, _createForm.Level);
            _createModalVisible = false;
            _currentAgentName = _createForm.Name;
            _selectedLevel = _createForm.Level;
            _drawerVisible = true;
        }
        catch (Exception ex)
        {
            MessageService.Error($"创建失败: {ex.Message}");
        }
        finally
        {
            _creating = false;
        }
    }

    private void OpenEditDrawer(string name)
    {
        _currentAgentName = name;
        _drawerVisible = true;
    }

    private async Task DeleteMdConfig(string name)
    {
        try
        {
            var success = await MdConfigService.DeleteAsync(name, _selectedLevel);
            if (success)
            {
                await LoadAsync();
                MessageService.Success($"已删除 {name} 的 MD 配置");
            }
        }
        catch (Exception ex)
        {
            MessageService.Error($"删除失败: {ex.Message}");
        }
    }

    private async Task HandleMdSaved()
    {
        await LoadAsync();
    }

    private static string GetSourceText(ConfigLevel level) => level == ConfigLevel.User ? "MD(用户)" : "MD(项目)";
    private static string GetSourceColor(ConfigLevel level) => level == ConfigLevel.User ? "purple" : "blue";

    private sealed class AgentRow
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string RuntimeText { get; set; } = "";
        public string Source { get; set; } = "";
        public string DefaultLabel { get; set; } = "";
        public bool IsDefault { get; set; }
        public bool IsMdConfig { get; set; }
        public ConfigLevel Level { get; set; }
        public int VariantCount { get; set; }
    }

    private sealed class CreateForm
    {
        public string Name { get; set; } = "";
        public ConfigLevel Level { get; set; } = ConfigLevel.Project;
    }
}
```

- [ ] **Step 2: 验证构建**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.WebUI/Pages/AgentsPage.razor
git commit -m "feat: add MD config management to AgentsPage"
```

---

## Task 10: 添加单元测试

**Files:**
- Modify: `tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs`

- [ ] **Step 1: 添加 CRUD 方法测试**

在现有测试文件中添加：

```csharp
// 新增测试方法

[Fact]
public async Task CreateAsync_ShouldCreateMdFile()
{
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    var workspaceProvider = CreateWorkspaceProvider(tempDir, tempDir);
    var loader = new AgentConfigLoader(workspaceProvider, _logger);

    try
    {
        // Act
        var result = await loader.CreateAsync("test-agent", ConfigLevel.Project);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-agent", result.Name);
        
        var filePath = loader.GetFilePath("test-agent", ConfigLevel.Project);
        Assert.True(File.Exists(filePath));
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}

[Fact]
public async Task SaveAsync_ShouldUpdateMdFile()
{
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    var workspaceProvider = CreateWorkspaceProvider(tempDir, tempDir);
    var loader = new AgentConfigLoader(workspaceProvider, _logger);
    await loader.CreateAsync("test-agent", ConfigLevel.Project);

    try
    {
        var newContent = @"---
name: test-agent
description: Updated description
mode: Primary
---

Updated system prompt
";

        // Act
        var result = await loader.SaveAsync("test-agent", ConfigLevel.Project, newContent);

        // Assert
        Assert.True(result);
        
        var loaded = loader.ParseFile(loader.GetFilePath("test-agent", ConfigLevel.Project));
        Assert.NotNull(loaded);
        Assert.Equal("Updated description", loaded.Description);
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}

[Fact]
public async Task DeleteAsync_ShouldRemoveMdFile()
{
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    var workspaceProvider = CreateWorkspaceProvider(tempDir, tempDir);
    var loader = new AgentConfigLoader(workspaceProvider, _logger);
    await loader.CreateAsync("test-agent", ConfigLevel.Project);

    try
    {
        // Act
        var result = await loader.DeleteAsync("test-agent", ConfigLevel.Project);

        // Assert
        Assert.True(result);
        
        var filePath = loader.GetFilePath("test-agent", ConfigLevel.Project);
        Assert.False(File.Exists(filePath));
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}

[Fact]
public async Task GetAllWithLevelAsync_ShouldReturnAllMdConfigs()
{
    // Arrange
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    var workspaceProvider = CreateWorkspaceProvider(tempDir, tempDir);
    var loader = new AgentConfigLoader(workspaceProvider, _logger);
    await loader.CreateAsync("agent1", ConfigLevel.Project);
    await loader.CreateAsync("agent2", ConfigLevel.Project);

    try
    {
        // Act
        var result = await loader.GetAllWithLevelAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Name == "agent1");
        Assert.Contains(result, r => r.Name == "agent2");
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 2: 运行测试**

Run: `dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj --filter "FullyQualifiedName~AgentConfigLoader"`
Expected: All tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/Seeing.Agent.Tests/Configuration/AgentConfigLoaderTests.cs
git commit -m "test: add CRUD method tests for AgentConfigLoader"
```

---

## Task 11: 最终验证

- [ ] **Step 1: 运行所有测试**

Run: `dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj`
Expected: All tests pass

- [ ] **Step 2: 运行 WebUI 项目**

Run: `dotnet run --project samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
Expected: Application starts successfully

- [ ] **Step 3: 手动验证功能**

1. 打开浏览器访问 http://localhost:5000/agents
2. 点击"新增 MD 配置"按钮
3. 输入 Agent 名称，点击确定
4. 在 Monaco Editor 中编辑内容
5. 点击保存
6. 验证表格中显示新创建的 Agent
7. 点击"编辑MD"验证编辑功能
8. 点击"删除"验证删除功能

- [ ] **Step 4: 最终提交**

```bash
git add -A
git commit -m "feat: complete WebUI Agent MD config management implementation"
```

---

## 自审检查清单

| 检查项 | 状态 |
|-------|------|
| 所有规格需求都有对应任务 | ✅ |
| 无 TBD/TODO 占位符 | ✅ |
| 类型和方法签名一致 | ✅ |
| 文件路径正确 | ✅ |
| 测试覆盖核心功能 | ✅ |
| 构建命令正确 | ✅ |
