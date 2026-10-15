# SystemOne 能力模块设计（Seeing.Agent.SystemOne）

**日期:** 2026-09-23
**状态:** 待评审
**范围:** 新增单包 `Seeing.Agent.SystemOne`，提供 System One（TypeSafe/Jev）typed-question 客户端抽象、TypeSafe 实现、配置与 DI。仅客户端 + 抽象 + DI，不含 Agent 工具、不含现有流程集成。

---

## 1. 背景与目标

TypeSafe 的 System One API 是**结构化判别**接口，与聊天补全本质不同：

- `POST {baseUrl}/v1/systemone` — 一次请求发送 `state` + 多个 typed `questions`，并行返回结构化 `answers`（含概率与 confidence）
- `GET {baseUrl}/v1/models` — 列出可用模型/别名
- 三种 question 类型：`choice`（选一个选项）、`score`（有序级别打分）、`noul`（是/否概率）
- **无** messages、无流式、无 tools；应答是 typed 值而非文本
- 鉴权：`Authorization: Bearer <API_KEY>`；错误码 `401/422/429/529`，`429/529` 需按 `Retry-After` 退避重试

**目标：** 在 Seeing.Agent 中新增一个能力模块，提供该接口的契约抽象、TypeSafe 实现、配置解析与 DI 注册，使其他能力包/宿主可以消费 System One 判别能力，并支持**后续切换不同服务商**（按 provider 类型路由）。

**非目标（明确排除）：**
- 不暴露 `[Tool]` 给原生 Agent
- 不改造现有标题/路由/摘要等流程
- 不做流式、不做多模态、不做 Ask 之外的高级编排（question 组合由消费方在代码中完成）
- 不修改 Core（瘦脊柱零工具原则）

**成功标准：**
1. 通过 `AddSystemOne(registry)` 注册后，可从 DI 解析 `ISystemOneService` 并成功调用 `EvaluateAsync`
2. 内置 `typesafe` provider 自动读取 `SYSTEMONE_API_KEY`/`SYSTEMONE_BASE_URL`/`SYSTEMONE_MODEL`，默认 BaseUrl `https://api.typesafe.ai`、默认模型 `jev-latest`
3. 支持 `systemone.json` 登记额外 provider，并按 `Type` 经工厂路由到不同实现
4. 无 ApiKey 时模块可正常启动（仅告警不注册 provider），不阻断宿主
5. 停用（Deactivate）对称注销本模块注册的所有 provider
6. 单测覆盖模块元数据、工厂路由、DTO 序列化、环境变量优先级、provider 注册/注销、默认 provider 选择

---

## 2. 架构决策（含理由）

| 决策 | 选择 | 理由 |
|------|------|------|
| 包结构 | 单包 `Seeing.Agent.SystemOne`（协议 + TypeSafe provider + 模块） | 对齐 `Seeing.Agent.Llm.OpenAI`；单一内置实现无需拆包 |
| 抽象层级 | 完整镜像 LLM：`ISystemOneClient` + Factory + Provider + Registry + Service | 用户明确预期后续切换服务商；工厂按 `Type` 路由是扩展缝 |
| 契约建模 | DTO 映射契约（方案 X），与 `LlmModels.cs` 一致 | 与线上 JSON 一一对应，实现最简，新增字段可透传；避免 C# 手写 discriminated union |
| 配置来源 | 独立顶层文件 `systemone.json`（`Dictionary<string, SystemOneProviderConfig>`） | 镜像 `providers.json`；用户选择 |
| 环境变量 | `SYSTEMONE_API_KEY` / `SYSTEMONE_BASE_URL` / `SYSTEMONE_MODEL` / `SYSTEMONE_PROVIDER` | 用户选择项目前缀 |
| Provider type | `typesafe` | 与协议/实现对应 |
| 模块 id | `systemone` | 稳定 id，参照 `llm.openai` |
| 重试 | 内建在 `SystemOneHttpHelper`（429/529 + 网络异常，指数退避，遵循 `Retry-After`） | 简化；不引入装饰器链 |
| HTTP 客户端 | 自建 `HttpClient`（`SocketsHttpHandler`），镜像 LLM | 避免引入 `Microsoft.Extensions.Http`；便于按 provider 隔离代理/超时 |

---

## 3. 契约层（Abstractions）

位置：`src/abstractions/Seeing.Agent.Abstractions/SystemOne/`
命名空间：`Seeing.Agent.Abstractions.SystemOne`
硬约束：**零实现**，仅接口/DTO/常量/异常。

### 3.1 `SystemOneProviderConfig.cs`

```csharp
using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne provider 类型标识常量（小写，用作路由键）</summary>
public static class SystemOneProviderTypes
{
    public const string TypeSafe = "typesafe";

    /// <summary>归一为规范小写；未知类型原样 Trim</summary>
    public static string Normalize(string? type) { /* 同 ProviderTypes.Normalize 范式 */ }
}

/// <summary>SystemOne provider 连接配置（systemone.json 的值）</summary>
public class SystemOneProviderConfig
{
    [JsonPropertyName("id")]           public string Id { get; set; } = "typesafe";
    [JsonPropertyName("type")]         public string Type { get; set; } = SystemOneProviderTypes.TypeSafe;
    [JsonPropertyName("name")]         public string? Name { get; set; }
    [JsonPropertyName("baseURL")]      public string? BaseUrl { get; set; }   // 默认 https://api.typesafe.ai
    [JsonPropertyName("apiKey")]       public string? ApiKey { get; set; }    // 环境变量优先覆盖
    [JsonPropertyName("model")]        public string Model { get; set; } = "jev-latest";
    [JsonPropertyName("timeout")]      public int Timeout { get; set; } = 10000;      // 毫秒，默认 10s
    [JsonPropertyName("max_retries")]  public int MaxRetries { get; set; } = 2;
    [JsonPropertyName("retry_base_delay")] public int RetryBaseDelayMs { get; set; } = 500;
    [JsonPropertyName("retry_max_delay")]  public int RetryMaxDelayMs { get; set; } = 5000;
    [JsonPropertyName("headers")]      public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>SystemOne 常量</summary>
public static class SystemOneDefaults
{
    public const string DefaultBaseUrl = "https://api.typesafe.ai";
    public const string DefaultModel = "jev-latest";
    public const string EvaluatePath = "/v1/systemone";
    public const string ModelsPath = "/v1/models";
    public const int DefaultTimeoutMs = 10000;
}
```

### 3.2 `SystemOneModels.cs`（DTO）

```csharp
using System.Text.Json.Serialization;

namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>question 类型常量</summary>
public static class SystemOneQuestionTypes
{
    public const string Noul = "noul";
    public const string Choice = "choice";
    public const string Score = "score";
}

/// <summary>评估请求：state + model + questions</summary>
public sealed record SystemOneRequest
{
    /// <summary>string | object | array（运行期类型序列化）</summary>
    [JsonPropertyName("state")]     public object? State { get; init; }
    /// <summary>留空时由客户端用 provider 配置的 Model 补全（见 §4.8）</summary>
    [JsonPropertyName("model")]     public string Model { get; init; } = string.Empty;
    [JsonPropertyName("questions")] public Dictionary<string, SystemOneQuestion> Questions { get; init; } = new();
}

/// <summary>单个 typed question</summary>
public sealed record SystemOneQuestion
{
    [JsonPropertyName("type")]         public string Type { get; init; } = string.Empty;   // noul|choice|score
    [JsonPropertyName("instructions")] public object? Instructions { get; init; }           // string|object|array
    [JsonPropertyName("criteria")]     public object? Criteria { get; init; }               // choice:map / score:array / noul:可选 object
}

/// <summary>评估响应</summary>
public sealed record SystemOneResponse
{
    [JsonPropertyName("model")]   public string? Model { get; init; }
    [JsonPropertyName("answers")] public Dictionary<string, SystemOneAnswer> Answers { get; init; } = new();
    [JsonPropertyName("usage")]   public SystemOneUsage? Usage { get; init; }
}

/// <summary>单个应答（按 type 取对应字段）</summary>
public sealed record SystemOneAnswer
{
    [JsonPropertyName("type")]          public string? Type { get; init; }
    [JsonPropertyName("noul")]          public double? Noul { get; init; }
    [JsonPropertyName("choice")]        public string? Choice { get; init; }
    [JsonPropertyName("score")]         public double? Score { get; init; }
    [JsonPropertyName("legend")]        public Dictionary<string, string>? Legend { get; init; }
    [JsonPropertyName("probabilities")] public Dictionary<string, double>? Probabilities { get; init; }
    [JsonPropertyName("confidence")]    public double? Confidence { get; init; }
}

/// <summary>token 用量</summary>
public sealed record SystemOneUsage
{
    [JsonPropertyName("input_tokens")]  public int InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
}

/// <summary>模型条目</summary>
public sealed record SystemOneModel
{
    [JsonPropertyName("name")]         public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")]  public string? Description { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
}

/// <summary>GET /v1/models 响应包装</summary>
public sealed record SystemOneModelsResponse
{
    [JsonPropertyName("models")] public List<SystemOneModel> Models { get; init; } = new();
}
```

### 3.3 `SystemOneException.cs`

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne API 调用失败（最终重试耗尽后抛出）</summary>
public sealed class SystemOneException : Exception
{
    /// <summary>HTTP 状态码；纯网络/超时失败（无响应）时为 0</summary>
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public SystemOneException(int statusCode, string? responseBody, string message, Exception? inner = null)
        : base(message, inner) { StatusCode = statusCode; ResponseBody = responseBody; }
}
```

### 3.4 `ISystemOneClient.cs`

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>SystemOne 协议客户端</summary>
public interface ISystemOneClient
{
    string ProviderId { get; }
    string ProviderType { get; }

    Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>协议工厂：按 provider 类型创建客户端</summary>
public interface ISystemOneClientFactory
{
    IReadOnlySet<string> SupportedTypes { get; }
    bool SupportsType(string type);
    ISystemOneClient Create(SystemOneProviderConfig config);
}
```

### 3.5 `ISystemOneProvider.cs`

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>配置驱动的 provider（连接实例）</summary>
public interface ISystemOneProvider
{
    string Id { get; }
    string? Name { get; }
    string Type { get; }
    int MaxRetries { get; }

    ISystemOneClient GetClient();
    Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>provider 集合管理（注册/注销/变更通知）</summary>
public interface ISystemOneProviderRegistry
{
    IReadOnlyList<ISystemOneProvider> Providers { get; }
    ISystemOneProvider? DefaultProvider { get; }

    void Register(ISystemOneProvider provider, string? ownerExtensionId = null);
    bool Unregister(string providerId);
    int UnregisterByOwner(string ownerExtensionId);

    event EventHandler? ProvidersChanged;
}
```

### 3.6 `ISystemOneService.cs`

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>业务门面：按默认 provider 转发 SystemOne 调用</summary>
public interface ISystemOneService
{
    Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(string? providerId = null, CancellationToken cancellationToken = default);
}
```

### 3.7 `SystemOneClientFactoryResolver.cs`

```csharp
namespace Seeing.Agent.Abstractions.SystemOne;

/// <summary>按 type 解析唯一工厂（同 type 多工厂 first wins）</summary>
public static class SystemOneClientFactoryResolver
{
    public static ISystemOneClientFactory? Find(IEnumerable<ISystemOneClientFactory> factories, string type);
    public static ISystemOneClientFactory Require(IEnumerable<ISystemOneClientFactory> factories, string type);
    // Require: 未找到时抛 NotSupportedException（同 LlmClientFactoryResolver 的异常类型），消息列出已支持类型（在 LLM 版基础上增强）
}
```

> 说明：与 `LlmClientFactoryResolver` 的差异——LLM 版本仅有 `Require`（抛 `NotSupportedException`，消息仅 `不支持的 Provider 类型: {type}`）；此处额外提供 `Find` 以便 provider 加载时"跳过未知类型"而非中断，并增强错误消息列出已支持类型。属有意扩展。

---

## 4. 能力包（Seeing.Agent.SystemOne）

位置：`src/capabilities/Seeing.Agent.SystemOne/`
命名空间：`Seeing.Agent.SystemOne`

### 4.1 文件清单

```
SystemOneModule.cs
Extensions/SystemOneServiceExtensions.cs        # AddSystemOne(registry)
Clients/TypeSafeSystemOneClient.cs
Clients/SystemOneHttpHelper.cs
Clients/SystemOneHttpClientFactory.cs
Clients/TypeSafeSystemOneClientFactory.cs
Configuration/SystemOneConfigStore.cs
Configuration/SystemOneEnvironmentConfig.cs
Configuration/SystemOneProviderManager.cs      # + IReloadHandler 触发
Configuration/SystemOneReloadHandler.cs
Runtime/ConfiguredSystemOneProvider.cs
Runtime/SystemOneService.cs
PredefinedSystemOneProviders.cs                 # 内置 typesafe 默认配置工厂
```

### 4.2 `SystemOneEnvironmentConfig.cs`

仿 `BootOverrideSource`：常量 + `Resolve` 静态读取。

```csharp
public static class SystemOneEnvironmentConfig
{
    public const string ApiKeyVar    = "SYSTEMONE_API_KEY";
    public const string BaseUrlVar   = "SYSTEMONE_BASE_URL";
    public const string ModelVar     = "SYSTEMONE_MODEL";
    public const string ProviderVar  = "SYSTEMONE_PROVIDER";

    public static string? ResolveApiKey()  => Read(ApiKeyVar);
    public static string? ResolveBaseUrl() => Read(BaseUrlVar);
    public static string? ResolveModel()   => Read(ModelVar);
    public static string? ResolveProvider()=> Read(ProviderVar);
    private static string? Read(string name) { /* Trim；空白→null */ }
}
```

### 4.3 `SystemOneConfigStore.cs`

由 `SystemOneProviderManager` 注入使用。

- `const string SectionName = "SystemOne";`
- `const string FileName = "systemone.json";`
- 基于注入的 `IConfigSectionStore`：`Read()` → `GetSection<Dictionary<string, SystemOneProviderConfig>>(SectionName)`（返回空字典兜底）
- 提供 `SectionMeta` 静态构造：`new ConfigSectionMeta(SectionName, FileName, ConfigScope.UserOnly, typeof(Dictionary<string, SystemOneProviderConfig>))`

### 4.4 `SystemOneProviderManager.cs`

Singleton，实现 `ISystemOneProviderRegistry`，`IDisposable`。

```csharp
public sealed class SystemOneProviderManager : ISystemOneProviderRegistry, IDisposable
{
    private readonly SystemOneConfigStore _configStore;
    private readonly IEnumerable<ISystemOneClientFactory> _factories;
    private readonly ILogger<SystemOneProviderManager> _logger;
    private readonly List<(ISystemOneProvider Provider, string Owner)> _entries = new();
    private readonly object _gate = new();
    private const string OwnerId = "systemone";   // 本模块自身 owner

    public IReadOnlyList<ISystemOneProvider> Providers { get; }   // 快照
    public ISystemOneProvider? DefaultProvider { get; }           // 解析结果

    /// <summary>从配置 + 环境变量重建"本模块自有的"provider 集合（幂等）</summary>
    public void Reload()
    {
        // 1) 读出文件条目 Dictionary<string, SystemOneProviderConfig>（可能为空）
        // 2) 构造 work = new Dictionary(file entries)
        // 3) 内置 typesafe：work 若无 "typesafe" 则填 PredefinedSystemOneProviders.CreateDefaultTypeSafe()；
        //    随后用 env 非空值覆盖其 BaseUrl/ApiKey/Model
        //    type 归一化：Type = SystemOneProviderTypes.Normalize(Type)
        // 4) 逐条：
        //    - 工厂 = SystemOneClientFactoryResolver.Find(factories, cfg.Type)；无 → Warning 跳过
        //    - ApiKey 为空 → Warning 跳过（不阻断启动）
        //    - provider = new ConfiguredSystemOneProvider(cfg, factory, logger)
        // 5) 仅替换 owner == OwnerId 的条目：先移除并 Dispose 旧的 OwnerId 条目，
        //    再加入新条目；**保留**其他 owner（经 Register 注册的外部 provider）原样不动
        // 6) 解析 DefaultProvider = env SYSTEMONE_PROVIDER ?? "typesafe" ?? 首个已注册 ?? null
        // 7) 触发 ProvidersChanged
    }

    public void Register(ISystemOneProvider provider, string? ownerExtensionId = null) { /* 去重 by Id，记录 owner */ }
    public bool Unregister(string providerId) { /* 移除并 Dispose */ }
    public int UnregisterByOwner(string ownerExtensionId) { /* 移除该 owner 全部并 Dispose */ }

    public event EventHandler? ProvidersChanged;
    public void Dispose() { /* Dispose 全部 provider */ }
}
```

**默认 provider 选择规则（高→低）：** `SYSTEMONE_PROVIDER` env > id `"typesafe"` > 首个已注册；都无 → `null`。

> `Reload()` 与 `Register()` 的边界：`Reload()` 只管理 `owner == "systemone"` 的配置驱动条目，不会丢弃外部 `Register` 进来的 provider。`DeactivateAsync` 调 `UnregisterByOwner("systemone")` 只清本模块条目。

### 4.5 `ConfiguredSystemOneProvider.cs`

```csharp
public sealed class ConfiguredSystemOneProvider : ISystemOneProvider, IDisposable
{
    private readonly SystemOneProviderConfig _config;
    private readonly ISystemOneClientFactory _factory;
    private readonly Lazy<ISystemOneClient> _client;   // 延迟创建

    public string Id => _config.Id;
    public string? Name => _config.Name;
    public string Type => _config.Type;
    public int MaxRetries => _config.MaxRetries;

    public ISystemOneClient GetClient() => _client.Value;
    public Task<IReadOnlyList<SystemOneModel>> GetModelsAsync(CancellationToken ct = default) => GetClient().ListModelsAsync(ct);
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => GetClient().TestConnectionAsync(ct);

    public void Dispose() { if (_client.IsValueCreated && _client.Value is IDisposable d) d.Dispose(); }
}
```

### 4.6 `SystemOneHttpClientFactory.cs`

镜像 `LlmHttpClientFactory`：

```csharp
public static class SystemOneHttpClientFactory
{
    public static HttpClient Create(SystemOneProviderConfig config)
    {
        var client = new HttpClient(CreateHandler(config), disposeHandler: true)
        {
            // Timeout 必须在首次请求前设置一次（HttpClient 发出请求后不可再改）
            Timeout = TimeSpan.FromMilliseconds(config.Timeout > 0 ? config.Timeout : SystemOneDefaults.DefaultTimeoutMs)
        };
        return client;
    }

    internal static SocketsHttpHandler CreateHandler(SystemOneProviderConfig config)
    {
        // PooledConnectionIdleTimeout=2min, PooledConnectionLifetime=5min
        // UseProxy=false（当前无代理配置；沿用 ProviderConfig 代理留待后续）
    }
}
```

> 说明：本期 `SystemOneProviderConfig` 不含代理字段（YAGNI）；如需代理后续再加 `proxy`/`useProxy`。

### 4.7 `SystemOneHttpHelper.cs`

```csharp
internal static class SystemOneHttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<TResp> PostAsync<TReq, TResp>(
        HttpClient http, SystemOneProviderConfig config, string path, TReq body, CancellationToken ct);
    public static async Task<TResp> GetAsync<TResp>(
        HttpClient http, SystemOneProviderConfig config, string path, CancellationToken ct);

    // 共同点：
    // - RequestUri = config.BaseUrl(默认 DefaultBaseUrl).TrimEnd('/') + path
    // - Header: Authorization: Bearer {ApiKey}；Accept: application/json；额外 config.Headers
    // - 超时：由 SystemOneHttpClientFactory 在创建 HttpClient 时一次性设置（helper 不改 Timeout）
    // - 重试：状态码 429/529（及 HttpRequestException / TaskCanceledException 超时，但排除调用方取消
    //   `when (!ct.IsCancellationRequested)`）→ 退避重试
    //   delay = min(RetryBaseDelayMs * 2^attempt, RetryMaxDelayMs)；若响应带 Retry-After（秒或 HTTP 日期）则取 max
    //   重试上限 config.MaxRetries
    // - 最终失败：抛 SystemOneException(statusCode(网络失败为 0), bodySnippet, message)
}
```

> 说明：重试逻辑在 helper 内，`MaxRetries` 从 `config` 读；`ConfiguredSystemOneProvider.MaxRetries` 暴露同一值供上层参考。

### 4.8 `TypeSafeSystemOneClient.cs`

```csharp
public sealed class TypeSafeSystemOneClient : ISystemOneClient, IDisposable
{
    private readonly SystemOneProviderConfig _config;
    private readonly HttpClient _http;   // 由 factory 创建，本类负责 Dispose
    private readonly ILogger<TypeSafeSystemOneClient> _logger;

    public TypeSafeSystemOneClient(
        SystemOneProviderConfig config,
        HttpClient http,
        ILogger<TypeSafeSystemOneClient> logger)
    { _config = config; _http = http; _logger = logger; }

    public string ProviderId => _config.Id;
    public string ProviderType => SystemOneProviderTypes.TypeSafe;

    public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken ct)
    {
        // 模型补全：request.Model 为空时用 provider 配置的 Model（使 SYSTEMONE_MODEL 生效）
        var effective = string.IsNullOrWhiteSpace(request.Model)
            ? request with { Model = _config.Model }
            : request;
        return SystemOneHttpHelper.PostAsync<SystemOneRequest, SystemOneResponse>(
            _http, _config, SystemOneDefaults.EvaluatePath, effective, ct);
    }

    public async Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken ct)
    {
        var resp = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            _http, _config, SystemOneDefaults.ModelsPath, ct);
        return resp.Models;
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
                _http, _config, SystemOneDefaults.ModelsPath, ct).ConfigureAwait(false);
            return true;
        }
        catch (SystemOneException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public void Dispose() => _http.Dispose();
}
```

> `TestConnectionAsync` 语义：能列出模型即视为连通；异常吞掉返回 `false`（与 `ILlmClient.TestConnectionAsync` 一致）。
> `_logger` 用于记录重试/失败诊断（helper 通过回调或客户端在调用前后记录；实现时保持最小侵入）。

### 4.9 `TypeSafeSystemOneClientFactory.cs`

```csharp
public sealed class TypeSafeSystemOneClientFactory : ISystemOneClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public IReadOnlySet<string> SupportedTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SystemOneProviderTypes.TypeSafe };

    public bool SupportsType(string type) => SupportedTypes.Contains(type);

    public ISystemOneClient Create(SystemOneProviderConfig config)
    {
        var http = SystemOneHttpClientFactory.Create(config);
        return new TypeSafeSystemOneClient(config, http, _loggerFactory.CreateLogger<TypeSafeSystemOneClient>());
    }
}
```

### 4.10 `SystemOneService.cs`（门面）

```csharp
public sealed class SystemOneService : ISystemOneService
{
    private readonly ISystemOneProviderRegistry _registry;

    public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken ct)
        => RequireProvider(_registry.DefaultProvider).GetClient().EvaluateAsync(request, ct);

    public Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(string? providerId = null, CancellationToken ct)
        => (providerId is null ? RequireProvider(_registry.DefaultProvider)
                               : RequireProvider(_registry.Providers.FirstOrDefault(p => p.Id == providerId)))
           .GetModelsAsync(ct);

    private static ISystemOneProvider RequireProvider(ISystemOneProvider? p)
        => p ?? throw new InvalidOperationException("未配置可用的 SystemOne provider（检查 SYSTEMONE_API_KEY 与 systemone.json）");
}
```

### 4.11 `SystemOneModule.cs`

```csharp
public sealed class SystemOneModule : ISeeingModule
{
    public string Id => "systemone";
    public IReadOnlyList<string> ProvidedTools => Array.Empty<string>();
    public IReadOnlyList<string> ProvidedSeams => Array.Empty<string>();
    public IReadOnlyList<string> DependsOn => Array.Empty<string>();

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SystemOneConfigStore>();
        services.AddSingleton<ISystemOneClientFactory, TypeSafeSystemOneClientFactory>();
        services.AddSingleton<SystemOneProviderManager>();
        services.AddSingleton<ISystemOneProviderRegistry>(sp => sp.GetRequiredService<SystemOneProviderManager>());
        services.AddSingleton<ISystemOneService, SystemOneService>();
        services.AddSingleton<IReloadHandler, SystemOneReloadHandler>();
    }

    public Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        services.GetRequiredService<SystemOneProviderManager>().Reload();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        services.GetRequiredService<ISystemOneProviderRegistry>().UnregisterByOwner("systemone");
        return Task.CompletedTask;
    }
}
```

**`SystemOneReloadHandler`：**

```csharp
internal sealed class SystemOneReloadHandler : ReloadHandlerBase<ConfigChange>
{
    private readonly SystemOneProviderManager _manager;
    public override string ComponentId => "systemone";

    protected override Task ReloadAsync(ConfigChange change, CancellationToken ct)
    {
        if (change.ChangedSections.Count == 0 || change.ChangedSections.Contains(SystemOneConfigStore.SectionName))
            _manager.Reload();
        return Task.CompletedTask;
    }
}
```

### 4.12 `SystemOneServiceExtensions.cs`（入口）

镜像 `AddSeeingAcp`：

```csharp
public static class SystemOneServiceExtensions
{
    public static IServiceCollection AddSystemOne(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.EnsureConfigSectionRegistry(registry);
        registry.Register(SystemOneConfigStore.SectionMeta);

        var module = new SystemOneModule();
        module.ConfigureServices(services);
        services.AddSingleton<ISeeingModule>(module);
        return services;
    }
}
```

> 采用 `AddSeeingAcp`/`AddMemoryServices` 范式：由扩展方法持有 `registry`，先 `registry.Register(SectionMeta)` 完成自定义配置节登记，再登记模块。`AddSeeingModule<T>` 虽也能在 `registry.Register` 后调用，但其要求 `new()` 且不把 registry 交给模块；为与既有能力包（Acp/Memory）保持一致，采用扩展方法 + `AddSingleton<ISeeingModule>(module)`。模块 Id 仍为 `systemone`，`SystemOneModule` 仍实现 `ISeeingModule`。
>
> **调用时序**：须在 `AddSeeingCore` 之前调用（与 `AddSeeingAcp` 约定一致），以保证 `SystemOneModule.ConfigureServices` 的注册先于 Core 结算。

### 4.13 `PredefinedSystemOneProviders.cs`

提供内置 typesafe provider 的默认配置工厂（纯数据，无副作用）：

```csharp
public static class PredefinedSystemOneProviders
{
    /// <summary>内置 typesafe provider 的默认配置（未含 env 覆盖）</summary>
    public static SystemOneProviderConfig CreateDefaultTypeSafe() => new()
    {
        Id = "typesafe",
        Type = SystemOneProviderTypes.TypeSafe,
        Name = "TypeSafe Jev",
        BaseUrl = SystemOneDefaults.DefaultBaseUrl,
        Model = SystemOneDefaults.DefaultModel,
        Timeout = SystemOneDefaults.DefaultTimeoutMs,
        MaxRetries = 2
    };
}
```

> `SystemOneProviderManager.Reload()` 在文件中无 `typesafe` 条目时调用此工厂；随后统一套用 `SYSTEMONE_*` 环境变量覆盖。

---

## 5. 配置与优先级

### 5.1 `systemone.json` 形状

用户级 `~/.seeing/systemone.json`（顶层，非 seeing.json 嵌套）：

```json
{
  "typesafe": {
    "id": "typesafe",
    "type": "typesafe",
    "name": "TypeSafe Jev",
    "baseURL": "https://api.typesafe.ai",
    "apiKey": "ts-...",
    "model": "jev-latest",
    "timeout": 10000,
    "max_retries": 2
  }
}
```

### 5.2 优先级（高 → 低）

1. 环境变量 `SYSTEMONE_API_KEY` / `SYSTEMONE_BASE_URL` / `SYSTEMONE_MODEL` —— **仅覆盖内置/同 id `typesafe` provider**
2. `systemone.json` 中显式 provider
3. 硬编码默认（`DefaultBaseUrl`、`DefaultModel`、`DefaultTimeoutMs`）

### 5.3 规则

- 若文件中无 `typesafe` 条目，仍创建内置 typesafe 默认条目
- 环境变量仅当非空时覆盖对应字段
- `ApiKey` 为空的 provider **跳过注册并 `LogWarning`**（不抛异常）
- `Type` 无工厂支持 → 跳过并 `LogWarning`
- 额外 provider 仅来自 `systemone.json`，env 不作用于非 `typesafe` 条目

---

## 6. 生命周期与 DI

| 服务 | 生命周期 |
|------|----------|
| `SystemOneConfigStore` | Singleton |
| `ISystemOneClientFactory` (TypeSafe) | Singleton |
| `SystemOneProviderManager` | Singleton |
| `ISystemOneProviderRegistry` | Singleton（同实例） |
| `ISystemOneService` | Singleton |
| `IReloadHandler` (SystemOneReloadHandler) | Singleton |

- **Activate**：`SystemOneProviderManager.Reload()` 构建并注册 provider（owner = `"systemone"`）
- **Deactivate**：`UnregisterByOwner("systemone")` 对称注销
- **热重载**：配置节 `SystemOne` 变更 → 编排器调用 `SystemOneReloadHandler` → `manager.Reload()`（幂等重建）

---

## 7. HTTP、错误与重试

- **BaseUrl**：`config.BaseUrl?.TrimEnd('/') ?? DefaultBaseUrl`；请求 URI = `BaseUrl + path`
- **超时**：`HttpClient.Timeout` 在 `SystemOneHttpClientFactory.Create` 时一次性设为 `config.Timeout` 毫秒（默认 10000，见 §4.6）
- **鉴权头**：`Authorization: Bearer {ApiKey}`、`Accept: application/json`，附加 `config.Headers`
- **重试触发**：响应 `429`/`529`，或 `HttpRequestException`/`TaskCanceledException`（超时）；**调用方主动取消（`ct.IsCancellationRequested`）不重试**，直接向上抛
- **退避**：`delay = min(RetryBaseDelayMs * 2^attempt, RetryMaxDelayMs)`；优先 `Retry-After`（秒数或 HTTP 日期），取 `max(delay, retryAfter)`
- **上限**：`config.MaxRetries`（默认 2）
- **最终失败**：抛 `SystemOneException(statusCode, bodySnippet, message)`；网络异常作为 `inner`
- **JSON**：`PropertyNameCaseInsensitive=true`、`DefaultIgnoreCondition=WhenWritingNull`

---

## 8. 测试计划

位置：`tests/capabilities/Seeing.Agent.SystemOne.Tests/`（命名空间 `Seeing.Agent.SystemOne.Tests`）

| 测试类 | 用例 |
|--------|------|
| `SystemOneModuleTests` | `Id == "systemone"`；`DependsOn`/`ProvidedTools`/`ProvidedSeams` 为空 |
| `TypeSafeSystemOneClientFactoryTests` | `SupportsType("typesafe")` 为 true 且大小写不敏感；`SupportsType("other")` 为 false；`Create` 返回 `TypeSafeSystemOneClient` |
| `TypeSafeSystemOneClientTests` | `request.Model` 为空时用 `config.Model` 补全（stub handler 捕获请求体断言）；显式 Model 不被覆盖 |
| `SystemOneHttpClientFactoryTests` | `Create` 返回的 `HttpClient.Timeout == config.Timeout`（默认 10000） |
| `SystemOneSerializationTests` | 序列化 `state` 为 string / object / array；三种 question 序列化；反序列化 `noul`/`choice`/`score` 应答（含 `probabilities`/`confidence`/`legend`）与 `input_tokens`/`output_tokens` |
| `SystemOneEnvironmentConfigTests` | 非空环境变量被解析、空白→null；用可注入读取或在测试中设置/清理 `Environment` |
| `SystemOneProviderManagerTests` | 注册/注销/`UnregisterByOwner` 计数；`ProvidersChanged` 触发；无 ApiKey 不注册；未知 type 跳过；env 覆盖内置 typesafe；`Reload()` 保留外部 `Register` 的 provider（只重建 owner==systemone） |
| `SystemOneServiceTests` | 默认 provider 选择顺序（env > typesafe > first）；无 provider 抛 `InvalidOperationException`；`ListModelsAsync(providerId)` 指定 provider |
| `SystemOneHttpHelperTests` | stub `HttpMessageHandler`：429 重试并遵循 `Retry-After`；超 `MaxRetries` 抛 `SystemOneException`；401 直接抛（不重试）；调用方取消不重试 |

运行方式（AGENTS.md 约定）：
```bash
dotnet build tests/capabilities/Seeing.Agent.SystemOne.Tests
dotnet vstest tests/capabilities/Seeing.Agent.SystemOne.Tests/bin/Debug/net10.0/Seeing.Agent.SystemOne.Tests.dll
```

---

## 9. 依赖与登记

### 9.1 csproj（`src/capabilities/Seeing.Agent.SystemOne/Seeing.Agent.SystemOne.csproj`）

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <RootNamespace>Seeing.Agent.SystemOne</RootNamespace>
  <Description>System One（TypeSafe/Jev）typed-question 客户端与工厂模块</Description>
</PropertyGroup>
<ItemGroup>
  <InternalsVisibleTo Include="Seeing.Agent.SystemOne.Tests" />
</ItemGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  <ProjectReference Include="..\..\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />
</ItemGroup>
```

- **不引** `Microsoft.Extensions.Http`（自建 HttpClient）
- 版本由中央包管理控制，**不写版本号**
- `<InternalsVisibleTo Include="Seeing.Agent.SystemOne.Tests" />`（供 `SystemOneHttpHelper`、`SystemOneReloadHandler` 等 internal 测试）

### 9.1b 测试 csproj（`tests/capabilities/Seeing.Agent.SystemOne.Tests/Seeing.Agent.SystemOne.Tests.csproj`）

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <RootNamespace>Seeing.Agent.SystemOne.Tests</RootNamespace>
  <IsPackable>false</IsPackable>
  <IsTestProject>true</IsTestProject>
  <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.NET.Test.Sdk" />
  <PackageReference Include="xunit.v3" />
  <PackageReference Include="xunit.runner.visualstudio">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
  <PackageReference Include="coverlet.collector">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
  <PackageReference Include="FluentAssertions" />
  <PackageReference Include="Moq" />
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
</ItemGroup>
<ItemGroup>
  <ProjectReference Include="..\..\..\src\capabilities\Seeing.Agent.SystemOne\Seeing.Agent.SystemOne.csproj" />
</ItemGroup>
```

### 9.2 slnx 登记

- 能力包加入 `Seeing.Agent.slnx` 的 `<Folder Name="/src/capabilities/">`
- 测试加入 `<Folder Name="/tests/capabilities/">`

---

## 10. 验收标准

1. `dotnet build Seeing.Agent.slnx` 通过（含新包与测试包）
2. 单测全部通过（VSTest 运行）
3. 依赖方向合规：`Seeing.Agent.SystemOne` 仅依赖 Abstractions（+ BCL/DI/Logging 抽象）
4. 无 ApiKey 环境下宿主可启动，日志有明确 Warning
5. `AddSystemOne(registry)` + `Activate` 后 `ISystemOneService.EvaluateAsync` 可路由到内置 typesafe provider
6. Deactivate 后 registry 中本模块 provider 数归零
