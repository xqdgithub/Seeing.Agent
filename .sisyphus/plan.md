# Seeing.Agent 功能补齐 - Agent 执行计划

**版本:** 1.0
**创建日期:** 2025-01-18
**目标框架:** .NET 10.0
**预计总工时:** 71 小时
**执行模式:** 8 个独立子任务，可并行执行

---

## 执行概览

| 任务 ID | 模块名称 | 优先级 | 预计工时 | 依赖 | 状态 |
|---------|----------|--------|----------|------|------|
| TASK-01 | MCP OAuth 集成 | P0 | 13.5h | 无 | done |
| TASK-02 | Snapshot 系统 | P0 | 10.5h | 无 | done |
| TASK-03 | Agent 动态生成 | P1 | 6h | 无 | done |
| TASK-04 | Skill 系统增强 | P1 | 8.5h | 无 | done |
| TASK-05 | Git 集成模块 | P1 | 8.5h | 无 | done |
| TASK-06 | Background Task 增强 | P1 | 7.5h | 无 | done |
| TASK-07 | Plan 工具实现 | P2 | 6h | TASK-05 | done |
| TASK-08 | Session 管理增强 | P2 | 10.5h | TASK-02 | done |

**并行执行建议:**
- 第一批 (无依赖): TASK-01, TASK-02, TASK-03, TASK-04, TASK-05, TASK-06
- 第二批 (有依赖): TASK-07 (依赖 TASK-05), TASK-08 (依赖 TASK-02)

---

## 项目上下文

### 项目结构
```
Seeing.Agent/
├── src/
│   ├── Seeing.Agent/           # 主项目
│   │   ├── Core/               # 核心接口与抽象
│   │   │   ├── Interfaces/     # IAgent, ITool, ISkill, IHook, IRuleEngine
│   │   │   ├── Abstractions/   # AgentBase, SkillBase, ToolBase
│   │   │   ├── Models/         # ChatMessage, ConfigurationModels
│   │   │   ├── Background/     # 后台任务 (已有)
│   │   │   └── Snapshot/       # 快照 (待实现)
│   │   ├── Tools/              # 工具系统
│   │   │   ├── Attributes/     # [Tool], [ToolParam] 注解
│   │   │   ├── Discovery/      # 工具发现
│   │   │   ├── ToolInvoker.cs  # 统一调用器
│   │   │   └── ToolRegistry.cs # 工具注册表
│   │   ├── MCP/                # MCP 协议 (已有 McpClientManager)
│   │   ├── Skills/             # 技能系统 (已有 SkillManager)
│   │   ├── Hooks/              # 钩子系统
│   │   ├── Rules/              # 权限规则
│   │   ├── Llm/                # LLM 集成
│   │   ├── Decorators/         # 装饰器链
│   │   └── Extensions/         # DI 扩展
│   ├── Seeing.Session/         # 独立会话包
│   │   ├── Core/               # ISession, ISessionManager
│   │   ├── Management/         # SessionManager (已有)
│   │   ├── Storage/            # ISessionStore, FileSessionStore
│   │   ├── Compression/        # SlidingWindowCompression
│   │   ├── Hooks/              # Session 钩子
│   │   └── Execution/          # 执行状态
│   ├── Seeing.Agent.Memory/    # 记忆系统
│   └── Seeing.Agent.Plugins/   # 插件系统
```

### 关键依赖
```xml
<!-- 已有 -->
<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
<PackageReference Include="Microsoft.Extensions.Http" />
<PackageReference Include="Microsoft.Extensions.Http.Polly" />
<PackageReference Include="ModelContextProtocol.Core" />
<PackageReference Include="OpenAI" />
<PackageReference Include="YamlDotNet" />
```

### 编码约定
- 接口命名: `I{领域}Manager`, `I{领域}Provider`
- 上下文类: `{领域}Context`
- 结果类: `{领域}Result`
- Hook 点: `{领域}.{事件}` 格式，使用 `HookPoints.*` 常量
- DI 注册: 使用 `ServiceCollection` 扩展方法
- 日志: 使用 `ILogger<T>` + 结构化日志
- 线程安全: 使用 `ConcurrentDictionary`
- 异步: 所有 IO 操作返回 `Task`/`Task<T>`

---

# TASK-01: MCP OAuth 集成

## 目标

为远程 MCP 服务器添加 OAuth 2.0 授权支持，使用 PKCE 流程（无需 client_secret）。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/MCP/OAuth/IMcpOAuthProvider.cs` | 新建 | OAuth 提供者接口 |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthProvider.cs` | 新建 | PKCE OAuth 实现 |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthStorage.cs` | 新建 | 令牌存储 (DPAPI 加密) |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthCallbackServer.cs` | 新建 | localhost 回调服务器 |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthConfig.cs` | 新建 | OAuth 配置模型 |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthToken.cs` | 新建 | 令牌模型 |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthException.cs` | 新建 | OAuth 异常 |
| `src/Seeing.Agent/MCP/OAuth/McpOAuthModels.cs` | 新建 | 结果模型 (OAuthStartResult, OAuthResult, McpAuthStatus) |
| `src/Seeing.Agent/MCP/McpClientManager.cs` | 修改 | 集成 OAuth 到 HttpMcpClientWrapper |

## 接口定义

```csharp
// MCP/OAuth/IMcpOAuthProvider.cs
namespace Seeing.Agent.MCP.OAuth;

public interface IMcpOAuthProvider
{
    Task<OAuthStartResult> StartAuthAsync(string mcpName, CancellationToken ct = default);
    Task<OAuthResult> FinishAuthAsync(string mcpName, string code, string state, CancellationToken ct = default);
    Task<OAuthResult> AuthenticateAsync(string mcpName, CancellationToken ct = default);
    Task<OAuthResult> RefreshTokenAsync(string mcpName, CancellationToken ct = default);
    Task RemoveAuthAsync(string mcpName);
    Task<bool> HasStoredTokensAsync(string mcpName);
    Task<McpAuthStatus> GetAuthStatusAsync(string mcpName);
}
```

## 数据模型

```csharp
// MCP/OAuth/McpOAuthModels.cs
namespace Seeing.Agent.MCP.OAuth;

public record OAuthStartResult(
    string AuthorizationUrl,
    string State,
    int CallbackPort,
    string CodeVerifier);

public record OAuthResult(
    bool Success,
    McpAuthStatus Status = McpAuthStatus.Authenticated,
    string? Error = null,
    McpOAuthToken? Token = null);

public enum McpAuthStatus
{
    Authenticated,
    Expired,
    NotAuthenticated,
    NeedsClientRegistration,
    NeedsAuthorization
}

// MCP/OAuth/McpOAuthToken.cs
public class McpOAuthToken
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public int ExpiresIn { get; set; }
    public string? Scope { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt => CreatedAt.AddSeconds(ExpiresIn);
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsExpiringSoon => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-5);
}

// MCP/OAuth/McpOAuthConfig.cs
public class McpOAuthConfig
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Scope { get; set; }
    public string? RedirectUri { get; set; }
    public bool Disabled { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public bool UsePkce { get; set; } = true;
}
```

## 实现关键逻辑

### McpOAuthProvider.cs - PKCE 流程

```
1. StartAuthAsync:
   a. 生成 code_verifier = Base64Url(RandomBytes(32))
   b. 计算 code_challenge = Base64Url(SHA256(code_verifier))
   c. 生成 state = Guid.NewGuid().ToString("N")
   d. 启动 CallbackServer (Kestrel, localhost:0 随机端口)
   e. 构建授权 URL: {authorization_endpoint}?response_type=code&client_id={}&redirect_uri={}&state={}&code_challenge={}&code_challenge_method=S256
   f. 存储 state + code_verifier 到 _pendingStates
   g. 返回 OAuthStartResult

2. FinishAuthAsync:
   a. 验证 state 匹配 _pendingStates
   b. POST token_endpoint: grant_type=authorization_code&code={}&code_verifier={}&redirect_uri={}&client_id={}
   c. 解析响应为 McpOAuthToken
   d. 调用 McpOAuthStorage.SaveTokenAsync 加密存储
   e. 返回 OAuthResult

3. AuthenticateAsync:
   a. 从 Storage 加载 Token
   b. 如果 Token.IsExpired 且有 RefreshToken -> RefreshTokenAsync
   c. 如果 Token 有效 -> 返回 Success
   d. 否则 -> 返回 NeedsAuthorization

4. RefreshTokenAsync:
   a. POST token_endpoint: grant_type=refresh_token&refresh_token={}&client_id={}
   b. 更新存储的 Token
   c. 返回新 OAuthResult
```

### McpOAuthStorage.cs - DPAPI 加密存储

```
存储路径: ~/.seeing/oauth-tokens/{mcpName}.token
1. SaveTokenAsync: JsonSerializer.Serialize -> ProtectedData.Protect(CurrentUser) -> File.WriteAllBytes
2. LoadTokenAsync: File.ReadAllBytes -> ProtectedData.Unprotect -> JsonSerializer.Deserialize
3. DeleteTokenAsync: File.Delete
```

### McpOAuthCallbackServer.cs - Kestrel 回调

```
1. EnsureRunningAsync:
   a. 如果已运行则返回端口
   b. 创建 WebApplication (Kestrel, ListenLocalhost(0))
   c. 映射 GET /callback (code, state) -> 设置 TaskCompletionSource
   d. StartAsync
   e. 返回端口

2. WaitForCallbackAsync:
   a. await _tcs.Task (带超时 CancellationTokenSource)
   b. 返回 (Code, State)

3. Dispose: StopAsync
```

### McpClientManager.cs 修改

在 `HttpMcpClientWrapper.ConnectAsync` 中:
```
1. 检查 _config.OAuth?.Disabled
2. 如果需要 OAuth:
   a. GetAuthStatusAsync
   b. 如果 NeedsAuthorization -> StartAuthAsync (打开浏览器) -> WaitForCallbackAsync -> FinishAuthAsync
   c. 如果 Expired -> RefreshTokenAsync
3. 获取 Token.AccessToken 设置到 transportOptions.AdditionalHeaders["Authorization"] = "Bearer {token}"
4. 继续原有连接逻辑
```

在 `McpServerConfig` 中添加 `OAuth` 属性:
```csharp
public McpOAuthConfig? OAuth { get; set; }
```

## DI 注册

```csharp
// Extensions/McpOAuthServiceExtensions.cs
public static IServiceCollection AddMcpOAuth(this IServiceCollection services)
{
    services.AddSingleton<McpOAuthStorage>();
    services.AddSingleton<McpOAuthCallbackServer>();
    services.AddSingleton<IMcpOAuthProvider, McpOAuthProvider>();
    return services;
}
```

## 验收标准

1. PKCE 流程: code_verifier/code_challenge 正确生成，授权 URL 格式正确
2. 令牌存储: DPAPI 加密/解密正确，文件权限安全
3. 令牌刷新: 过期令牌自动刷新，刷新失败返回 NeedsAuthorization
4. 回调服务器: 正确接收授权码，超时处理正确
5. 集成: HttpMcpClientWrapper 连接时自动处理 OAuth

---

# TASK-02: Snapshot 系统

## 目标

实现文件快照系统，支持创建快照、Diff 计算、持久化存储、恢复快照。使用 Diff 模式节省存储空间。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/Core/Snapshot/ISnapshotManager.cs` | 新建 | 快照管理器接口 |
| `src/Seeing.Agent/Core/Snapshot/SnapshotManager.cs` | 新建 | 快照管理器实现 |
| `src/Seeing.Agent/Core/Snapshot/Snapshot.cs` | 新建 | 快照实体 |
| `src/Seeing.Agent/Core/Snapshot/SnapshotDiff.cs` | 新建 | Diff 实体 |
| `src/Seeing.Agent/Core/Snapshot/DiffCalculator.cs` | 新建 | Diff 计算器 |
| `src/Seeing.Agent/Core/Snapshot/SnapshotStorage.cs` | 新建 | 快照存储 |
| `src/Seeing.Agent/Core/Snapshot/SnapshotOptions.cs` | 新建 | 配置选项 |

## 接口定义

```csharp
// Core/Snapshot/ISnapshotManager.cs
namespace Seeing.Agent.Core.Snapshot;

public interface ISnapshotManager
{
    Task<Snapshot> CreateSnapshotAsync(
        string filePath, string sessionId, string? label = null, CancellationToken ct = default);

    Task<IReadOnlyList<Snapshot>> GetSnapshotsAsync(
        string filePath, string? sessionId = null, CancellationToken ct = default);

    Task<SnapshotDiff> ComputeDiffAsync(
        string snapshotId1, string snapshotId2, CancellationToken ct = default);

    Task<SnapshotDiff> ComputeDiffWithCurrentAsync(
        string snapshotId, CancellationToken ct = default);

    Task<bool> RestoreAsync(string snapshotId, CancellationToken ct = default);

    Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct = default);

    Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>获取快照的完整内容（自动应用 Diff 链）</summary>
    Task<string> GetSnapshotContentAsync(string snapshotId, CancellationToken ct = default);
}
```

## 数据模型

```csharp
// Core/Snapshot/Snapshot.cs
public class Snapshot
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? Label { get; init; }
    public string ContentHash { get; init; } = "";  // SHA256
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public long FileSize { get; init; }

    // Diff 模式存储
    public string? BaseSnapshotId { get; init; }    // 基于哪个快照的 Diff
    public string? DiffPatches { get; init; }       // 序列化的 Diff 补丁
}

// Core/Snapshot/SnapshotDiff.cs
public class SnapshotDiff
{
    public string SnapshotId1 { get; init; } = "";
    public string SnapshotId2 { get; init; } = "";
    public int AddedLines { get; init; }
    public int DeletedLines { get; init; }
    public int ModifiedLines { get; init; }
    public string UnifiedDiff { get; init; } = "";  // Unified diff 格式
}

// Core/Snapshot/SnapshotOptions.cs
public class SnapshotOptions
{
    public string StoragePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".seeing", "snapshots");
    public int MaxSnapshotsPerFile { get; set; } = 50;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);
}
```

## 实现关键逻辑

### DiffCalculator.cs - Diff 算法

```
使用 Google diff-match-patch 算法 (自行实现，无需外部依赖):

1. ComputePatches(text1, text2):
   a. diff_match_patch.diff_main(text1, text2) -> diffs
   b. diff_match_patch.diff_cleanupSemantic(diffs)
   c. diff_match_patch.patch_make(text1, diffs) -> patches
   d. return patches

2. ApplyPatches(text, patches):
   a. diff_match_patch.patch_apply(patches, text)
   b. return result text

3. SerializePatches(patches):
   a. diff_match_patch.patch_toText(patches) -> string

4. DeserializePatches(serialized):
   a. diff_match_patch.patch_fromText(serialized) -> patches

5. ToUnifiedDiff(text1, text2):
   a. 按行分割，生成 Unified Diff 格式输出
```

### SnapshotManager.cs - 创建快照

```
CreateSnapshotAsync:
1. 读取文件内容
2. 计算 SHA256 哈希
3. 查找同一文件的最后一个快照 (同 sessionId)
4. 如果内容未变 (哈希相同) -> 返回已有快照
5. 如果有上一个快照:
   a. 获取上一个快照的完整内容
   b. 计算 Diff 补丁
   c. 序列化补丁
   d. 创建 Snapshot (BaseSnapshotId = 上一个, DiffPatches = 补丁)
6. 如果没有上一个快照:
   a. 创建 Snapshot (无 BaseSnapshotId)
   b. 保存完整内容到文件
7. 保存快照元数据
```

### SnapshotManager.cs - 获取快照内容

```
GetSnapshotContentAsync:
1. 加载快照元数据
2. 如果无 BaseSnapshotId:
   a. 从 content/{snapshotId}.txt 读取完整内容
   b. 返回
3. 如果有 BaseSnapshotId:
   a. 递归获取 BaseSnapshot 的内容
   b. 反序列化 DiffPatches
   c. 应用补丁: DiffCalculator.ApplyPatches(baseContent, patches)
   d. 返回结果
4. 缓存结果避免重复计算
```

### SnapshotManager.cs - 恢复快照

```
RestoreAsync:
1. 获取快照完整内容 (GetSnapshotContentAsync)
2. 写入文件: File.WriteAllText(snapshot.FilePath, content)
3. 触发 Hook: "snapshot.restored"
4. 返回 true
```

### SnapshotStorage.cs - 存储结构

```
{StoragePath}/
├── {session-id}/
│   ├── meta/
│   │   └── {snapshot-id}.json     # 快照元数据
│   └── content/
│       └── {snapshot-id}.txt      # 完整内容 (仅首个快照)
```

## 验收标准

1. 创建快照: 文件内容正确记录，哈希正确
2. Diff 模式: 后续快照仅存储 Diff，节省空间
3. 内容恢复: 递归应用 Diff 链还原完整内容
4. Diff 计算: 两个快照间的 Diff 正确
5. 清理: 过期快照正确删除

---

# TASK-03: Agent 动态生成

## 目标

实现 Agent 动态生成系统，支持从模板生成 Agent 配置并运行时实例化。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/Core/Generation/IAgentGenerator.cs` | 新建 | Agent 生成器接口 |
| `src/Seeing.Agent/Core/Generation/AgentGenerator.cs` | 新建 | Agent 生成器实现 |
| `src/Seeing.Agent/Core/Generation/AgentTemplate.cs` | 新建 | Agent 模板模型 |
| `src/Seeing.Agent/Core/Generation/AgentTemplateEngine.cs` | 新建 | 模板变量渲染 |
| `src/Seeing.Agent/Core/Generation/AgentValidation.cs` | 新建 | 模板验证 |
| `src/Seeing.Agent/Core/Interfaces/IAgentRegistry.cs` | 修改 | 添加 RegisterAgentAsync/UnregisterAgentAsync |

## 接口定义

```csharp
// Core/Generation/IAgentGenerator.cs
namespace Seeing.Agent.Core.Generation;

public interface IAgentGenerator
{
    Task<IAgent> GenerateAsync(
        AgentTemplate template,
        Dictionary<string, object>? variables = null,
        CancellationToken ct = default);

    Task<IAgent> GenerateFromConfigAsync(
        string configPath,
        CancellationToken ct = default);

    (bool Valid, IReadOnlyList<string> Errors) Validate(AgentTemplate template);

    Task<IReadOnlyList<AgentTemplate>> GetTemplatesAsync(CancellationToken ct = default);
}
```

## 数据模型

```csharp
// Core/Generation/AgentTemplate.cs
public class AgentTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }
    public int? MaxSteps { get; set; }
    public AgentMode Mode { get; set; } = AgentMode.SubAgent;
    public Dictionary<string, string> Variables { get; set; } = new();
    public List<string>? AllowedTools { get; set; }
    public List<string>? DeniedTools { get; set; }
    public List<PermissionRule>? Permissions { get; set; }
}
```

## 实现关键逻辑

### AgentGenerator.cs

```
GenerateAsync:
1. Validate(template) -> 如果无效则抛出 AgentGenerationException
2. AgentTemplateEngine.Render(template.SystemPrompt, variables) -> 渲染变量
3. 创建 GeneratedAgent 实例 (实现 IAgent):
   - Name = 渲染后的名称
   - SystemPrompt = 渲染后的提示词
   - Mode, Model, MaxSteps, AllowedTools, DeniedTools, Permissions 从模板复制
4. RegisterAgentAsync(agent) -> 注册到 IAgentRegistry
5. 返回 agent

GenerateFromConfigAsync:
1. 读取 JSON/YAML 配置文件
2. 反序列化为 AgentTemplate
3. 调用 GenerateAsync(template)

GetTemplatesAsync:
1. 扫描模板目录: .seeing/agents/templates/
2. 解析所有 .json/.yaml 模板文件
3. 返回 AgentTemplate 列表
```

### AgentTemplateEngine.cs

```
Render(template, variables):
1. 遍历 variables
2. 替换 {{variable_name}} 为对应值
3. 支持嵌套变量: {{parent.child}}
4. 未匹配的变量保留原样 (不报错)
```

### AgentValidation.cs

```
Validate(template):
1. Name 不能为空
2. Name 长度 <= 64
3. Name 格式: 字母数字+连字符
4. Description 不能为空
5. Description 长度 <= 1024
6. MaxSteps 范围: 1-200
7. 如果有 Variables，验证名称格式
8. 返回 (Valid, Errors)
```

### IAgentRegistry 修改

```csharp
// 在现有 IAgentRegistry 接口中添加:
Task RegisterAgentAsync(IAgent agent, CancellationToken ct = default);
Task UnregisterAgentAsync(string name, CancellationToken ct = default);
```

## 验收标准

1. 从模板生成 Agent，变量正确渲染
2. 生成的 Agent 可通过 IAgentRegistry 查询
3. 配置文件验证正确
4. 模板变量替换正确处理缺失变量

---

# TASK-04: Skill 系统增强

## 目标

增强 Skill 系统，支持 URL 拉取技能 (Git/HTTP)、内置技能加载、权限过滤。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/Skills/ISkillPuller.cs` | 新建 | 技能拉取器接口 |
| `src/Seeing.Agent/Skills/SkillPuller.cs` | 新建 | Git/HTTP 拉取实现 |
| `src/Seeing.Agent/Skills/BuiltinSkillLoader.cs` | 新建 | 内置技能加载 |
| `src/Seeing.Agent/Skills/SkillPermissionFilter.cs` | 新建 | 权限过滤器 |
| `src/Seeing.Agent/Skills/SkillManager.cs` | 修改 | 添加 PullSkillAsync 方法 |

## 接口定义

```csharp
// Skills/ISkillPuller.cs
namespace Seeing.Agent.Skills;

public interface ISkillPuller
{
    Task<SkillPullResult> PullAsync(
        string url, string? targetDirectory = null, CancellationToken ct = default);
    bool SupportsUrl(string url);
}

public record SkillPullResult(
    bool Success, string? SkillName = null, string? LocalPath = null, string? Error = null);
```

## 实现关键逻辑

### SkillPuller.cs

```
PullAsync:
1. 判断 URL 类型:
   a. git@ / .git 后缀 -> PullFromGitAsync
   b. http:// / https:// -> PullFromHttpAsync
   c. 其他 -> 返回错误

PullFromGitAsync:
1. 解析技能名称 (URL 最后一段路径)
2. 确定目标目录 (默认: ./.seeing/skills/{name})
3. 执行: git clone --depth 1 {url} {target}
4. 验证 SKILL.md 存在
5. 返回 SkillPullResult

PullFromHttpAsync:
1. 使用 HttpClient 下载内容
2. 如果是 .md 文件 -> 直接保存为 SKILL.md
3. 如果是 .zip -> 解压到目标目录
4. 验证 SKILL.md 存在
5. 返回 SkillPullResult
```

### BuiltinSkillLoader.cs

```
LoadBuiltinSkillsAsync:
1. 扫描程序集内嵌资源: Seeing.Agent.Skills.Builtin.*.SKILL.md
2. 或扫描目录: {AppDomain.BaseDirectory}/Skills/Builtin/
3. 解压/复制到 .seeing/skills/builtin/
4. 调用 SkillManager.AddSearchDirectory
5. 调用 SkillManager.DiscoverSkillsAsync
```

### SkillPermissionFilter.cs

```
FilterSkills(skills, permissions):
1. 遍历每个 SkillInfo
2. 检查 Skill 的 requires 字段
3. 根据 IRuleEngine 评估权限
4. 过滤掉无权限的技能
5. 返回过滤后的列表
```

### SkillManager.cs 修改

```csharp
// 新增方法:
public async Task<SkillPullResult> PullSkillAsync(string url, CancellationToken ct = default)
{
    var puller = new SkillPuller(_logger);
    var result = await puller.PullAsync(url, ct: ct);
    if (result.Success && result.LocalPath != null)
    {
        AddSearchDirectory(result.LocalPath);
        await DiscoverSkillsAsync(ct);
    }
    return result;
}
```

## 验收标准

1. Git URL 拉取技能成功，SKILL.md 正确解析
2. HTTP URL 拉取技能成功
3. 内置技能自动加载
4. 权限过滤正确过滤无权限技能
5. SkillManager.PullSkillAsync 正确更新搜索目录

---

# TASK-05: Git 集成模块

## 目标

实现 Git 集成模块，提供 Git 操作封装和 LLM 可调用的 Git 工具。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/Git/IGitService.cs` | 新建 | Git 服务接口 |
| `src/Seeing.Agent/Git/GitService.cs` | 新建 | Git 服务实现 (git CLI) |
| `src/Seeing.Agent/Git/GitModels.cs` | 新建 | Git 状态/提交模型 |
| `src/Seeing.Agent/Git/GitException.cs` | 新建 | Git 异常 |
| `src/Seeing.Agent/Git/Tools/GitStatusTool.cs` | 新建 | git status 工具 |
| `src/Seeing.Agent/Git/Tools/GitDiffTool.cs` | 新建 | git diff 工具 |
| `src/Seeing.Agent/Git/Tools/GitLogTool.cs` | 新建 | git log 工具 |
| `src/Seeing.Agent/Git/Tools/GitCommitTool.cs` | 新建 | git commit 工具 |

## 接口定义

```csharp
// Git/IGitService.cs
namespace Seeing.Agent.Git;

public interface IGitService
{
    Task<GitStatus> GetStatusAsync(string? path = null, CancellationToken ct = default);
    Task<string> GetDiffAsync(string? path = null, bool staged = false, CancellationToken ct = default);
    Task<IReadOnlyList<GitCommit>> GetLogAsync(string? path = null, int limit = 10, CancellationToken ct = default);
    Task<bool> CommitAsync(string message, string[]? paths = null, CancellationToken ct = default);
    Task<bool> StageAsync(string[] paths, CancellationToken ct = default);
    Task<bool> IsGitRepositoryAsync(string? path = null);
    Task<string> GetCurrentBranchAsync(string? path = null, CancellationToken ct = default);
}
```

## 数据模型

```csharp
// Git/GitModels.cs
public class GitStatus
{
    public string Branch { get; set; } = "";
    public string RepositoryPath { get; set; } = "";
    public List<GitFileStatus> Files { get; set; } = new();
    public bool HasUncommittedChanges => Files.Count > 0;
}

public class GitFileStatus
{
    public string Path { get; set; } = "";
    public GitFileState State { get; set; }
}

public enum GitFileState
{
    Untracked, Modified, Added, Deleted, Renamed, Copied, Staged, StagedModified
}

public class GitCommit
{
    public string Hash { get; set; } = "";
    public string ShortHash { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset Date { get; set; }
}
```

## 实现关键逻辑

### GitService.cs - 使用 git CLI

```
核心方法: RunGitCommandAsync(workingDir, arguments, ct)
1. ProcessStartInfo: git {arguments}, WorkingDirectory = workingDir
2. RedirectStandardOutput/StandardError
3. 启动进程，异步读取输出
4. 等待退出，检查 ExitCode
5. 返回 (success, output, error)

GetStatusAsync:
1. git status --porcelain=v1
2. 解析每行: XY PATH (X=staged, Y=worktree)
3. 映射状态码到 GitFileState
4. git branch --show-current -> Branch

GetDiffAsync:
1. git diff [--staged] [-- {path}]
2. 返回原始 diff 输出

GetLogAsync:
1. git log --format="%H%n%h%n%s%n%an%n%aI" -n {limit}
2. 解析每条提交记录

CommitAsync:
1. 如果 paths != null: git add {paths}
2. git commit -m {message}
3. 检查 ExitCode == 0

StageAsync:
1. git add {paths}
```

### Git 工具实现模式

所有 Git 工具使用 `[Tool]` 注解注册:

```csharp
[Tool("git_status", "获取 Git 仓库状态")]
public class GitStatusTool : ToolBase
{
    protected override ToolResult ExecuteCore(JsonElement args, ToolContext ctx)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var status = await _gitService.GetStatusAsync(path, ctx.CancellationToken);
        return Success("Git Status", FormatStatus(status));
    }
}
```

## DI 注册

```csharp
public static IServiceCollection AddGitIntegration(this IServiceCollection services)
{
    services.AddSingleton<IGitService, GitService>();
    // 工具通过 [Tool] 注解自动发现
    return services;
}
```

## 验收标准

1. GetStatusAsync 正确解析 git status 输出
2. GetDiffAsync 返回正确的 diff 内容
3. GetLogAsync 正确解析提交历史
4. CommitAsync 成功提交
5. 所有 Git 工具可通过 ToolInvoker 调用
6. 非 Git 仓库返回友好错误

---

# TASK-06: Background Task 系统增强

## 目标

增强后台任务系统，添加进度报告、流式输出、结果注入功能。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/Core/Background/IBackgroundTaskManager.cs` | 修改 | 添加 SubscribeProgress, SubscribeOutput, InjectResultAsync |
| `src/Seeing.Agent/Core/Background/BackgroundTaskManager.cs` | 修改 | 实现新方法 |
| `src/Seeing.Agent/Core/Background/BackgroundTaskInfo.cs` | 修改 | 添加 Progress, OutputLines 字段 |
| `src/Seeing.Agent/Core/Background/IBackgroundTaskProgress.cs` | 新建 | 进度报告接口 |
| `src/Seeing.Agent/Core/Background/BackgroundTaskProgress.cs` | 新建 | 进度模型 |

## 接口修改

```csharp
// IBackgroundTaskManager.cs - 新增方法
public interface IBackgroundTaskManager
{
    // === 现有方法保持不变 ===
    Task<string> StartAsync(BackgroundTaskLaunchArgs args);
    Task<BackgroundTaskInfo?> GetAsync(string taskId);
    Task<string?> GetOutputAsync(string taskId);
    Task<bool> CancelAsync(string taskId);
    Task<int> CancelAllAsync();
    Task<IReadOnlyList<BackgroundTaskInfo>> ListAsync(BackgroundTaskStatus? status = null);
    Task<BackgroundTaskInfo?> WaitAsync(string taskId, int timeoutMs = 60000);

    // === 新增方法 ===

    /// <summary>订阅任务进度 (IObservable)</summary>
    IObservable<BackgroundTaskProgress> SubscribeProgress(string taskId);

    /// <summary>订阅任务输出流 (IObservable)</summary>
    IObservable<string> SubscribeOutput(string taskId);

    /// <summary>注入任务结果到当前会话</summary>
    Task<bool> InjectResultAsync(string taskId, string sessionId, CancellationToken ct = default);
}
```

## 数据模型

```csharp
// BackgroundTaskProgress.cs
public class BackgroundTaskProgress
{
    public string TaskId { get; init; } = "";
    public int Percent { get; init; }  // 0-100
    public string? Message { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

// IBackgroundTaskProgress.cs
public interface IBackgroundTaskProgress
{
    void Report(int percent, string? message = null);
    void ReportOutput(string line);
}

// BackgroundTaskInfo.cs - 新增字段
public class BackgroundTaskInfo
{
    // === 现有字段保持不变 ===
    // ... (Id, Status, AgentName, Prompt, StartedAt, etc.)

    // === 新增字段 ===
    public int Progress { get; set; }  // 0-100
    public string? ProgressMessage { get; set; }
    public DateTimeOffset? ProgressUpdatedAt { get; set; }
    public List<string> OutputLines { get; set; } = new();
}
```

## 实现关键逻辑

### BackgroundTaskManager.cs 修改

```
新增字段:
- _progressSubjects: ConcurrentDictionary<string, Subject<BackgroundTaskProgress>>
- _outputSubjects: ConcurrentDictionary<string, Subject<string>>

SubscribeProgress(taskId):
1. _progressSubjects.GetOrAdd(taskId, _ => new Subject<>())
2. 返回 .AsObservable()

SubscribeOutput(taskId):
1. _outputSubjects.GetOrAdd(taskId, _ => new Subject<>())
2. 返回 .AsObservable()

InjectResultAsync(taskId, sessionId):
1. GetAsync(taskId) -> info
2. 如果 info == null -> 返回 false
3. 如果 info.Status != Completed -> 返回 false
4. 获取 info.Output
5. 创建 ChatMessage (role=tool, content=output)
6. 注入到 sessionId 对应的 Session
7. 返回 true

内部方法 ReportProgress(taskId, percent, message):
1. 更新 BackgroundTaskInfo 的 Progress/ProgressMessage
2. 如果 _progressSubjects 存在 -> subject.OnNext(progress)

内部方法 ReportOutput(taskId, line):
1. 添加到 BackgroundTaskInfo.OutputLines
2. 如果 _outputSubjects 存在 -> subject.OnNext(line)

任务完成时:
1. _progressSubjects.TryGetValue -> subject.OnCompleted()
2. _outputSubjects.TryGetValue -> subject.OnCompleted()

任务取消时:
1. 同上 OnCompleted()
```

## NuGet 依赖

```xml
<PackageReference Include="System.Reactive" Version="6.0.0" />
```

## 验收标准

1. SubscribeProgress 收到进度更新
2. SubscribeOutput 收到流式输出
3. InjectResultAsync 正确注入到 Session
4. 任务完成/取消时 Observable 正确完成
5. 与现有 API 完全兼容

---

# TASK-07: Plan 工具实现

**依赖:** TASK-05 (Git 集成) - Plan 需要持久化到 Git 仓库

## 目标

实现 Plan 工具，支持 Agent 进入/退出计划模式，管理执行计划。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Agent/Tools/BuiltIn/Plan/PlanManager.cs` | 新建 | 计划管理器 |
| `src/Seeing.Agent/Tools/BuiltIn/Plan/PlanModel.cs` | 新建 | 计划数据模型 |
| `src/Seeing.Agent/Tools/BuiltIn/Plan/PlanEnterTool.cs` | 新建 | 进入计划工具 |
| `src/Seeing.Agent/Tools/BuiltIn/Plan/PlanExitTool.cs` | 新建 | 退出计划工具 |
| `src/Seeing.Agent/Tools/BuiltIn/Plan/PlanListTool.cs` | 新建 | 列出计划工具 |

## 数据模型

```csharp
// PlanModel.cs
public class Plan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string SessionId { get; set; } = "";
    public List<PlanStep> Steps { get; set; } = new();
    public PlanStatus Status { get; set; } = PlanStatus.Draft;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public class PlanStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Content { get; set; } = "";
    public PlanStepStatus Status { get; set; } = PlanStepStatus.Pending;
    public int Order { get; set; }
    public string? Result { get; set; }
}

public enum PlanStatus { Draft, InProgress, Completed, Cancelled }
public enum PlanStepStatus { Pending, InProgress, Completed, Skipped, Failed }
```

## 实现关键逻辑

### PlanManager.cs

```
CreatePlanAsync(title, description, sessionId):
1. 创建 Plan 实例
2. 存储到 _plans + _sessionPlans
3. 持久化到 .seeing/plans/{planId}.json
4. 返回 Plan

GetActivePlanAsync(sessionId):
1. _sessionPlans[sessionId] -> planId
2. _plans[planId] -> Plan
3. 返回 Plan 或 null

AddStepAsync(planId, content, order):
1. 创建 PlanStep
2. 添加到 Plan.Steps
3. 持久化

UpdateStepStatusAsync(stepId, status, result):
1. 更新 PlanStep.Status 和 Result
2. 如果所有 Step 完成 -> Plan.Status = Completed
3. 持久化

CompletePlanAsync(planId, sessionId):
1. Plan.Status = Completed
2. CompletedAt = now
3. _sessionPlans.TryRemove(sessionId)
4. 持久化
```

### PlanEnterTool.cs

```
Parameters:
- title: string (required) - 计划标题
- description: string (optional) - 计划描述
- steps: string[] (optional) - 初始步骤列表

ExecuteAsync:
1. 调用 PlanManager.CreatePlanAsync
2. 如果有 steps，逐个 AddStepAsync
3. 返回格式化的计划摘要
```

### PlanExitTool.cs

```
Parameters:
- plan_id: string (optional) - 计划 ID (默认当前活跃计划)
- summary: string (optional) - 完成摘要

ExecuteAsync:
1. 调用 PlanManager.CompletePlanAsync
2. 返回计划完成摘要 (含步骤完成率)
```

### PlanListTool.cs

```
Parameters:
- session_id: string (optional) - 过滤 Session
- status: PlanStatus (optional) - 过滤状态

ExecuteAsync:
1. 列出所有/过滤的计划
2. 格式化为表格输出
```

## 验收标准

1. PlanEnter 创建计划并添加步骤
2. PlanExit 完成计划
3. PlanList 列出计划
4. 计划持久化到文件
5. 每个 Session 只有一个活跃计划

---

# TASK-08: Session 管理增强

**依赖:** TASK-02 (Snapshot 系统) - Fork/Revert 需要快照支持

## 目标

增强 Session 管理系统，添加 Fork、Archive、Share、Revert、全局列表功能。

## 文件清单

| 文件路径 | 操作 | 说明 |
|----------|------|------|
| `src/Seeing.Session/Management/ISessionManager.cs` | 修改 | 添加 Fork/Archive/Share/Revert/ListAll |
| `src/Seeing.Session/Management/SessionManager.cs` | 修改 | 实现新方法 |
| `src/Seeing.Session/Management/SessionForker.cs` | 新建 | Session 分支 |
| `src/Seeing.Session/Management/SessionArchiver.cs` | 新建 | Session 归档 |
| `src/Seeing.Session/Management/SessionSharer.cs` | 新建 | Session 分享 |
| `src/Seeing.Session/Management/SessionReverter.cs` | 新建 | Session 回滚 |
| `src/Seeing.Session/Storage/GlobalSessionStore.cs` | 新建 | 全局 Session 存储 |
| `src/Seeing.Session/Core/SessionData.cs` | 修改 | 添加 ParentSessionId, ForkLabel, IsArchived |

## 接口修改

```csharp
// ISessionManager.cs - 新增方法
public interface ISessionManager
{
    // === 现有方法保持不变 ===
    SessionData Create(string? partitionId = null, string? selectedAgent = null);
    SessionData? Get(string id);
    bool Delete(string id);
    void Register(SessionData session);
    IReadOnlyList<SessionData> List();
    Task SaveAsync(string id);
    Task<SessionData?> LoadAsync(string id);
    IReadOnlyList<SessionMessage> Compress(string id);

    // === 新增方法 ===

    /// <summary>Fork Session - 创建分支</summary>
    Task<SessionData> ForkAsync(
        string sessionId,
        string? atMessageId = null,
        string? label = null,
        CancellationToken ct = default);

    /// <summary>Archive Session - 归档</summary>
    Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Share Session - 分享</summary>
    Task<string> ShareAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Revert Session - 回滚到指定消息</summary>
    Task<bool> RevertAsync(
        string sessionId, string messageId, CancellationToken ct = default);

    /// <summary>列出所有 Session (全局)</summary>
    Task<IReadOnlyList<SessionMetadata>> ListAllAsync(
        string? partitionId = null, CancellationToken ct = default);
}
```

## 数据模型

```csharp
// SessionData.cs - 新增字段
public class SessionData
{
    // === 现有字段保持不变 ===
    // ...

    // === 新增字段 ===
    public string? ParentSessionId { get; set; }
    public string? ForkLabel { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

// SessionMetadata.cs - 新建
public class SessionMetadata
{
    public string Id { get; init; } = "";
    public string? PartitionId { get; init; }
    public string? SelectedAgent { get; init; }
    public string? ParentSessionId { get; init; }
    public string? ForkLabel { get; init; }
    public bool IsArchived { get; init; }
    public int MessageCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastActiveAt { get; init; }
}
```

## 实现关键逻辑

### SessionForker.cs

```
ForkAsync(sessionId, atMessageId, label):
1. 获取源 Session
2. 创建新 Session (同一 PartitionId, SelectedAgent)
3. 设置 ParentSessionId = sessionId, ForkLabel = label
4. 复制消息:
   a. 如果 atMessageId != null: 复制到该消息之前 (不含)
   b. 如果 atMessageId == null: 复制所有消息
   c. 每条消息 Clone() 确保独立
5. 为跟踪的文件创建快照 (依赖 ISnapshotManager)
6. 触发 Hook: "session.forked"
7. 返回新 Session
```

### SessionArchiver.cs

```
ArchiveAsync(sessionId):
1. 获取 Session
2. 标记 IsArchived = true, ArchivedAt = now
3. 序列化为 JSON
4. GZip 压缩
5. 保存到 {archivePath}/{sessionId}_{timestamp}.json.gz
6. 从活跃缓存中移除
7. 触发 Hook: "session.archived"
8. 返回 true
```

### SessionSharer.cs

```
ShareAsync(sessionId):
1. 获取 Session
2. 生成 shareId = Guid.NewGuid().ToString("N")
3. 创建分享记录 (shareId, sessionId, createdAt, expiresAt = +7days)
4. 保存分享记录
5. 返回分享标识: "session://share/{shareId}"

ResolveShareAsync(shareId):
1. 查找分享记录
2. 检查是否过期
3. 返回 SessionData
```

### SessionReverter.cs

```
RevertAsync(sessionId, messageId):
1. 获取 Session
2. 找到消息索引
3. 截断消息列表到该消息 (含)
4. 恢复文件快照 (依赖 ISnapshotManager):
   a. 获取该消息时间点之前最近的快照
   b. 调用 SnapshotManager.RestoreAsync
5. 触发 Hook: "session.reverted"
6. 返回 true
```

### GlobalSessionStore.cs

```
ListAllAsync(partitionId):
1. 扫描存储目录下所有分区
2. 如果 partitionId != null -> 仅扫描该分区
3. 读取每个 .session.json 的元数据 (不加载完整内容)
4. 返回 SessionMetadata 列表，按 LastActiveAt 降序

存储结构:
{basePath}/
├── {partition-id}/
│   ├── {session-id}.session.json
│   └── ...
├── _archive/
│   ├── {session-id}_{timestamp}.json.gz
│   └── ...
└── _shares/
    └── {share-id}.share.json
```

## 验收标准

1. Fork: 创建独立副本，消息正确截断
2. Archive: 压缩归档，从活跃列表移除
3. Share: 生成分享标识，可解析
4. Revert: 消息截断 + 文件恢复
5. ListAll: 全局列出，支持分区过滤
6. 与现有 SessionManager API 完全兼容

---

# 依赖关系图

```
TASK-01 (MCP OAuth) ──────────────────────────────┐
                                                   │
TASK-02 (Snapshot) ───────────────────────────────┼──► TASK-08 (Session)
                                                   │
TASK-03 (Agent Gen) ──────────────────────────────┤
                                                   │
TASK-04 (Skill) ──────────────────────────────────┤
                                                   │
TASK-05 (Git) ────────────────────────────────────┼──► TASK-07 (Plan)
                                                   │
TASK-06 (Background) ─────────────────────────────┘
```

---

# NuGet 依赖汇总

```xml
<!-- MCP OAuth (需要 Kestrel) -->
<PackageReference Include="Microsoft.AspNetCore.App" />

<!-- Background Task (Rx) -->
<PackageReference Include="System.Reactive" Version="6.0.0" />

<!-- Snapshot (diff-match-patch 自行实现，无需外部依赖) -->

<!-- Git (使用 git CLI，无需外部依赖) -->
<!-- 或者使用 LibGit2Sharp -->
<PackageReference Include="LibGit2Sharp" Version="0.29.0" />
```

---

# 各 TASK 执行检查清单

## TASK-01 MCP OAuth
- [x] 创建 `MCP/OAuth/` 目录及 8 个文件
- [x] 实现 IMcpOAuthProvider + McpOAuthProvider (PKCE)
- [x] 实现 McpOAuthStorage (DPAPI 加密)
- [x] 实现 McpOAuthCallbackServer (Kestrel)
- [x] 修改 McpClientManager 集成 OAuth
- [x] 添加 DI 注册扩展
- [x] 编写单元测试

## TASK-02 Snapshot
- [x] 创建 `Core/Snapshot/` 目录及 7 个文件
- [x] 实现 ISnapshotManager + SnapshotManager
- [x] 实现 DiffCalculator (diff-match-patch)
- [x] 实现 SnapshotStorage (文件存储)
- [x] 编写单元测试

## TASK-03 Agent Gen
- [x] 创建 `Core/Generation/` 目录及 5 个文件
- [x] 实现 IAgentGenerator + AgentGenerator
- [x] 实现 AgentTemplateEngine (变量渲染)
- [x] 修改 IAgentRegistry 添加动态注册 (已存在 RegisterAgentAsync)
- [x] 编写单元测试

## TASK-04 Skill
- [x] 实现 ISkillPuller + SkillPuller (Git/HTTP)
- [x] 实现 BuiltinSkillLoader
- [x] 实现 SkillPermissionFilter
- [x] 修改 SkillManager 添加 PullSkillAsync
- [x] 编写单元测试

## TASK-05 Git
- [x] 创建 `Git/` 目录及 8 个文件
- [x] 实现 IGitService + GitService (git CLI)
- [x] 实现 4 个 Git 工具 (Status/Diff/Log/Commit)
- [x] 添加 DI 注册扩展
- [x] 编写单元测试

## TASK-06 Background
- [x] 修改 IBackgroundTaskManager 添加 3 个新方法
- [x] 修改 BackgroundTaskManager 实现
- [x] 添加 BackgroundTaskProgress 模型
- [x] 添加 IBackgroundTaskProgress 接口
- [x] 编写单元测试

## TASK-07 Plan
- [x] 创建 Plan/ 目录及 3 个文件
- [x] 实现 PlanModel + PlanTask
- [x] 实现 PlanManager
- [x] 实现 3 个 Plan 工具 (Enter/Exit/AddTask)
- [x] 编写单元测试

## TASK-08 Session
- [x] 修改 ISessionManager 添加 5 个新方法
- [x] 修改 SessionData 添加 Fork/Archive 字段
- [x] 实现 SessionForker
- [x] 实现 SessionArchiver
- [x] 实现 SessionSharer
- [x] 实现 SessionReverter
- [x] 实现 GlobalSessionStore
- [x] 修改 SessionManager 实现新方法
- [x] 编写单元测试
