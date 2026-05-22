# 修复实施计划

**创建时间:** 2025-01-22
**状态:** 待执行
**执行者:** Sisyphus (执行代理)

---

## 修复清单

### FIX-001: GitCommitTool 语法错误 [P0]
**文件:** `src/Seeing.Agent/Git/Tools/GitCommitTool.cs`
**行号:** 17
**修改:**
```csharp
// 修改前
public string Description = "Commit changes to the repository";

// 修改后
public string Description => "Commit changes to the repository";
```

---

### FIX-002: 添加 System.Reactive 包引用 [P0]
**文件:** `src/Seeing.Agent/Seeing.Agent.csproj`
**位置:** 在 `</ItemGroup>` 前（约第53行后）
**添加:**
```xml
<!-- Reactive Extensions for IObservable support -->
<PackageReference Include="System.Reactive" Version="6.0.0" />
```

---

### FIX-003: SnapshotDiff ModifiedLines 语义修正 [P1]
**文件1:** `src/Seeing.Agent/Core/Snapshot/SnapshotDiff.cs`
**修改:**
```csharp
// 修改前
public int ModifiedLines { get; init; }

// 修改后
public int UnchangedLines { get; init; }
```

**文件2:** `src/Seeing.Agent/Core/Snapshot/SnapshotManager.cs`
**位置:** 第150行和第176行
**修改:**
```csharp
// 修改前
ModifiedLines = diffs.Count(d => d.Operation == DiffOperation.Equal),

// 修改后
UnchangedLines = diffs.Count(d => d.Operation == DiffOperation.Equal),
```

---

### FIX-004: AgentGenerator 注册到 IAgentRegistry [P1]
**文件:** `src/Seeing.Agent/Core/Generation/AgentGenerator.cs`

**步骤1:** 添加构造函数参数
```csharp
// 修改前
public AgentGenerator(
    ILogger<AgentGenerator> logger,
    AgentTemplateEngine templateEngine,
    AgentValidator validator)

// 修改后
private readonly IAgentRegistry? _agentRegistry;

public AgentGenerator(
    ILogger<AgentGenerator> logger,
    AgentTemplateEngine templateEngine,
    AgentValidator validator,
    IAgentRegistry? agentRegistry = null)
{
    _logger = logger;
    _templateEngine = templateEngine;
    _validator = validator;
    _agentRegistry = agentRegistry;
    LoadBuiltinTemplates();
}
```

**步骤2:** 在 GenerateAsync 方法末尾添加注册逻辑（第92行后）
```csharp
_definitions[definition.Id] = definition;

// 注册到 IAgentRegistry（如果可用）
if (_agentRegistry != null)
{
    var agentInfo = ToAgentInfo(definition);
    await _agentRegistry.RegisterAgentAsync(agentInfo, cancellationToken);
}

_logger.LogInformation("Generated agent {AgentId} ({Name}) from template {TemplateId}", 
    definition.Id, definition.Name, template?.Id ?? "none");
```

**步骤3:** 添加转换方法（在类末尾）
```csharp
private static AgentInfo ToAgentInfo(AgentDefinition definition)
{
    return new AgentInfo
    {
        Name = definition.Name,
        Description = definition.Description,
        SystemPrompt = definition.SystemPrompt,
        AllowedTools = definition.AllowedTools,
        DeniedTools = definition.DeniedTools,
        Model = definition.ModelConfig != null 
            ? new ModelReference 
            { 
                Provider = definition.ModelConfig.Provider ?? "",
                ModelId = definition.ModelConfig.ModelId ?? ""
            } 
            : null,
        MaxSteps = definition.MaxIterations,
        Tags = definition.Tags,
        Mode = AgentMode.SubAgent,
        IsNative = false
    };
}
```

**步骤4:** 添加 using 语句
```csharp
using Seeing.Agent.Core.Interfaces;
```

---

### FIX-005: MCP OAuth 端点配置支持 [P2]
**文件:** `src/Seeing.Agent/MCP/OAuth/McpOAuthConfig.cs`
**添加:**
```csharp
/// <summary>授权端点URL</summary>
public string? AuthorizationEndpoint { get; set; }

/// <summary>令牌端点URL</summary>
public string? TokenEndpoint { get; set; }
```

**文件:** `src/Seeing.Agent/MCP/OAuth/McpOAuthProvider.cs`
**修改 StartAuthAsync 方法:**
```csharp
public async Task<OAuthStartResult> StartAuthAsync(
    string mcpName,
    McpOAuthConfig? config = null,  // 添加配置参数
    CancellationToken cancellationToken = default)
{
    // ... 
    var authorizationEndpoint = config?.AuthorizationEndpoint 
        ?? throw new InvalidOperationException("AuthorizationEndpoint is required");
    
    var authorizationUrl = $"{authorizationEndpoint}?" +
        $"response_type=code&" +
        $"client_id={config?.ClientId}&" +
        $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
        $"state={state}&" +
        $"code_challenge={codeChallenge}&" +
        $"code_challenge_method=S256";
    // ...
}
```

---

## 执行顺序

1. **FIX-001** - 简单语法修复
2. **FIX-002** - 添加包引用
3. **FIX-003** - 重命名属性
4. **FIX-004** - 添加依赖注入和注册逻辑
5. **FIX-005** - OAuth 端点配置（可选，较大改动）

---

## 验证步骤

执行修复后，运行以下命令验证：
```bash
cd E:\Projects\CSharp\Seeing.Agent
dotnet build
dotnet test tests/Seeing.Agent.Tests/Seeing.Agent.Tests.csproj
```

---

## 备注

- 所有修复保持向后兼容
- FIX-004 使用可选参数，不破坏现有调用
- FIX-005 是增强功能，可延后实施
