# DeepSeek Provider 插件设计

**日期:** 2026-08-05  
**状态:** 待用户确认书面 spec  
**范围:** 在 `plugs/providers` 下新增 DeepSeek LLM Provider；WebUI 默认 DI 启用；配置与模型目录自管；不改动 `Seeing.Agent` 核心库增加 DeepSeek 固定内容

---

## 1. 背景与目标

框架已具备 `ILlmProvider` / `IProviderRegistry` / `IConfigurableLlmProvider` 与 Models 页 schema 双轨 UI。DeepSeek 作为 OpenAI 兼容服务，适合以扩展 Provider 形态接入，而不是改核心 `ProviderType` 枚举或写死配置节。

### 目标

- 新建独立项目实现 DeepSeek Provider（身份、Client、动态模型列表、连接配置）。
- WebUI 通过依赖注入默认启用（类似 Memory 的 `AddMemoryServices` 模式）。
- 核心配置为用户级 **ApiKey**；固定官方 BaseUrl。
- 配置与模型拉取均在扩展包内自实现；**禁止**在 `Seeing.Agent` 中增加 DeepSeek 专用硬编码或固定配置节。

### 非目标

- 不修改 `Seeing.Agent` 源码以注册 DeepSeek 节名 / 预置 Providers。
- 不把插件或配置写入项目级 `.seeing` 或项目 `Plugins` 列表。
- 不默认在 Gateway.Server / Cli 中启用。
- 不继承 `OpenAiClient`；通过 `ILlmClientFactory` 包装即可。
- 不依赖 `OpenAiModelsClient`（`internal`，外部不可用；不为此改可见性）。

---

## 2. 决策摘要

| 决策点 | 选择 |
|--------|------|
| 启用方式 | DI 直注（`AddDeepSeekProvider`），非用户级 `Plugins` DLL 加载 |
| 配置存储 | 扩展自管；`IConfigurableLlmProvider` + schema；不写 `Providers[deepseek]` |
| 模型目录 | 动态 `GET /models` + TTL 缓存 |
| BaseUrl | 固定 `https://api.deepseek.com/v1`（适配内置 OpenAI Client 相对路径约定）；UI 仅暴露 ApiKey |
| Client | `ILlmClientFactory.Create(ProviderConfig { Type=OpenAI, ... })` |
| 配置节 | **不**使用 `UnifiedConfigManager`（无动态节注册 API；不向核心加固定节） |
| 核心改动 | **零** DeepSeek 专用改动；仅消费公开 API |

---

## 3. 架构与项目边界

### 3.1 布局

```
plugs/providers/Seeing.Provider.DeepSeek/
  Seeing.Provider.DeepSeek.csproj
  DeepSeekOptions.cs
  DeepSeekConfigStore.cs
  DeepSeekModelsClient.cs
  DeepSeekProvider.cs
  DeepSeekProviderHostedService.cs
  DeepSeekExtension.cs                 # 可选：IExtension，非 WebUI 主路径
  ServiceCollectionExtensions.cs       # AddDeepSeekProvider()
```

解决方案：`Seeing.Agent.slnx` 增加文件夹 `/plugs/providers/`，纳入上述项目；测试项目固定为  
`tests/Seeing.Provider.DeepSeek.Tests`（与仓库其他扩展测试并列）。

### 3.2 依赖

| 包 | 用途 |
|----|------|
| `Seeing.Agent` | `LlmProviderBase`、`ILlmClientFactory`、`IProviderRegistry`、`IConfigurableLlmProvider`、`ProviderConfig`、`ModelConfig`、`IWorkspaceProvider`（可选） |
| `Seeing.ConfigSchema` | `ConfigFieldSchema` / `ConfigFieldType` |
| Microsoft.Extensions.* | DI / Hosting / Logging |

TFM：`net10.0`。包版本遵循中央包管理（CPM）。

### 3.3 运行时关系

```
WebUI Program
  └─ AddDeepSeekProvider()
       ├─ DeepSeekConfigStore     → ~/.seeing/deepseek.json
       ├─ DeepSeekProvider        → ILlmProvider + IConfigurableLlmProvider
       └─ DeepSeekProviderHostedService
            └─ IProviderRegistry.Register(..., "seeing.provider.deepseek")

Models 页
  └─ IProviderManager.TryGetConfigurable("deepseek")
       └─ SchemaDrivenConfigForm (ApiKey)

对话 / 目录
  └─ GetClient() → OpenAI 兼容 Client
  └─ GetModelsAsync() → DeepSeekModelsClient + TTL
```

### 3.4 明确不做

- 改 `Seeing.Agent` 增加 DeepSeek 硬编码或 Memory 式固定配置节。
- 项目级插件路径或项目级 DeepSeek 配置文件。
- 将配置写入 `seeing.json` 的 `Providers` 节。

---

## 4. 配置（自管，仅用户级）

### 4.1 为什么不用 UnifiedConfigManager

`UnifiedConfigManager` 的 `_sectionRegistry` 在构造时静态构建，**没有**公开的动态 `RegisterSection` API。现有 `Memory` 节是核心内硬编码。若走 `IConfigSectionStore`，要么改核心加 `DeepSeek` 固定节（禁止），要么先做通用动态节 API（超出本期范围且用户要求配置自实现）。

因此 DeepSeek **自行读写文件**，不调用 `IConfigSectionStore` / `SaveSectionAsync`。

### 4.2 文件与形状

| 项 | 约定 |
|----|------|
| 路径 | `{UserSeeingDirectory}/deepseek.json` |
| 目录解析 | 优先 `IWorkspaceProvider.UserSeeingDirectory`；若不可用则 `Path.Combine(Environment.GetFolderPath(UserProfile), ".seeing")` |
| JSON | `{ "apiKey": "..." }`（属性名 camelCase，与仓库配置习惯一致） |
| 作用域 | **仅用户级**；`SaveConfigAsync` **忽略**传入的 `ConfigLevel`，永不写项目 `.seeing` |

### 4.3 Schema

`GetConfigSchema()` 返回非空列表（进入 Models 页扩展轨），仅一字段：

- `Name`: `ApiKey`
- `Label`: `API Key`
- `Type`: `ConfigFieldType.Secret`
- `Required`: `true`

可用手写 `ConfigFieldSchema`，或 `OptionsSchemaBuilder.FromType(typeof(DeepSeekOptions))`（若 `ApiKey` 属性能被启发式识别为 Secret）。

### 4.4 Load / Save 行为

- 文件缺失或损坏：视为空 Key，记录 warning，不抛穿。
- `SaveConfigAsync`：原子写（写临时文件再替换）；保存后：
  1. 清空模型 TTL 缓存；
  2. 重置 Client 的 `Lazy`；
  3. 再次 `IProviderRegistry.Register(this, ownerExtensionId)` 同 Id 覆盖，以触发既有 `ProvidersChanged` → `ModelConfigManager` 刷新管线（不改核心）。

---

## 5. Provider 与 Client

### 5.1 身份

| 字段 | 值 |
|------|-----|
| `Id` | `deepseek` |
| `Name` | `DeepSeek` |
| `MaxRetries` | 默认 3（基类） |
| `ownerExtensionId` | `seeing.provider.deepseek` |

### 5.2 GetClient

```text
ProviderConfig {
  Id = "deepseek",
  Type = ProviderType.OpenAI,
  Name = "DeepSeek",
  BaseUrl = "https://api.deepseek.com/v1",
  ApiKey = <from store>
}
→ ILlmClientFactory.Create(config)
```

说明：内置 `OpenAiHttpHelper` 以 `BaseUrl` 为 `BaseAddress`，请求路径为相对路径（如 `chat/completions`、`models`），故必须带 `/v1`。用户不可配置；UI 不展示 BaseUrl。

使用 `Lazy<ILlmClient>`（`ExecutionAndPublication`）。ApiKey 变更后丢弃 Lazy，下次 `GetClient` 重建。

无 ApiKey 时：仍可构造 Client（由内置行为决定是否在请求时失败）；`TestConnectionAsync` 在无 Key 时直接返回 `false`。

`DeepSeekProvider` 构造注入 `IProviderRegistry`，以便 Save 后同 Id 再 `Register` 触发目录刷新。

### 5.3 GetModelsAsync

- 无 ApiKey：返回空列表。
- 有 ApiKey：`DeepSeekModelsClient.ListAsync` → `GET https://api.deepseek.com/v1/models`（与 Client 同一 BaseUrl），`Authorization: Bearer {apiKey}`。
- 响应按 OpenAI List Models 形状解析：`{ "data": [ { "id": "..." } ] }`。
- 映射 `ModelConfig`：`Id`/`Name` = 模型 id，`Provider` = `deepseek`；上下文窗口等未知字段使用框架/`ModelConfig` 默认值。
- TTL：默认 **5 分钟** 内存缓存；保存配置后强制失效。
- 401 / 网络 / 解析失败：记 warning，返回空列表（符合目录聚合「单 Provider 失败不影响其他」）。

不引用核心 `OpenAiModelsClient`（internal）；在扩展包内自实现等价逻辑。

### 5.4 TestConnectionAsync

有 Key：委托 `GetClient().TestConnectionAsync(modelId, ct)`。  
无 Key：`false`。

### 5.5 可选 IExtension

`DeepSeekExtension` 实现 `IExtension`，`GetProviders()` 返回同一 `DeepSeekProvider` 实例（需从 DI/`ExtensionContext.Services` 解析）。WebUI **默认不**走 `Plugins` 加载；保留扩展入口供将来文件/NuGet 插件路径使用。

---

## 6. DI 与 WebUI 接线

### 6.1 `AddDeepSeekProvider`

```csharp
services.TryAddSingleton<DeepSeekConfigStore>();
services.TryAddSingleton<DeepSeekProvider>();
services.AddHostedService<DeepSeekProviderHostedService>();
```

`DeepSeekProviderHostedService`：在宿主启动时  
`registry.Register(provider, "seeing.provider.deepseek")`。

依赖前提：`AddSeeingAgent` 已注册 `IProviderRegistry`、`ILlmClientFactory`。

### 6.2 WebUI

- `Seeing.Agent.WebUI.csproj`：`ProjectReference` → DeepSeek 项目。
- `Program.cs`：`builder.Services.AddDeepSeekProvider();`（与 `AddMemoryServices` 等并列）。
- Models 页已有 `TryGetConfigurable` + `SchemaDrivenConfigForm`；预期无需改 UI（若联调有缺口再做最小修补）。
- **不**写入用户/项目 `Plugins`；**不**在项目 `.seeing` 创建 DeepSeek 文件。

### 6.3 其他宿主

Gateway.Server / Cli：**不**默认调用 `AddDeepSeekProvider()`。

---

## 7. 错误处理

| 场景 | 行为 |
|------|------|
| 配置文件缺失/损坏 | 空 ApiKey + warning |
| List Models 失败 | 空目录 + warning |
| 未配置 Key | Provider 仍注册（可在 UI 填 Key）；不可用对话 / Test 失败 |
| Client 创建失败 | 按工厂现有行为抛出并记日志 |

---

## 8. 测试

项目：`Seeing.Provider.DeepSeek.Tests`（xUnit + FluentAssertions，与仓库惯例一致）。

| 用例 | 预期 |
|------|------|
| ConfigStore 读写 | 写入用户路径下的 `deepseek.json`；不创建项目级文件 |
| 无 ApiKey → GetModelsAsync | 空列表 |
| ModelsClient 解析假 JSON | 产出 `Provider=deepseek` 的 ModelConfig |
| GetConfigSchema | 含 ApiKey / Secret |
| SaveConfig 后 Load | 值一致；再次 GetClient 使用新 Key（假 `ILlmClientFactory` 断言） |

集成级「真实 HTTP」测试非必须；可用 `HttpMessageHandler` stub。

---

## 9. 验收标准

1. WebUI 启动后 Models 页出现 **deepseek** Provider。  
2. 填写并保存 ApiKey → 仅 `{UserSeeingDirectory}/deepseek.json` 变化。  
3. 保存后模型列表可刷新（动态拉取）。  
4. 选择 deepseek 模型可正常对话（OpenAI 兼容 Client）。  
5. `Seeing.Agent` 源码无 DeepSeek 专用变更。

---

## 10. 实现顺序（供后续 plan 参考）

1. 建项目与 slnx 文件夹 / 引用。  
2. Options + ConfigStore。  
3. ModelsClient + Provider（Client/模型/schema）。  
4. `AddDeepSeekProvider` + HostedService。  
5. WebUI 引用与默认注册。  
6. 单元测试与验收联调。

---

## 11. 开放问题

无。配置节动态注入本期不做；若未来要统一进 `UnifiedConfigManager`，应先在核心增加**通用** `RegisterSection(ConfigSectionMeta)`，再迁移本 Provider——不在本次范围。
