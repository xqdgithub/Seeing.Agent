# 29 TypeSafe Provider 下沉为插件 Release Notes

**日期:** 2026-11-05
**设计:** [2026-11-05-typesafe-provider-extraction-design.md](../superpowers/specs/2026-11-05-typesafe-provider-extraction-design.md)

## 1. 一句话

`llm.systemone` 拆成两件事：能力包 `llm.typesafe` **只管 typesafe 协议**（只登记工厂，不造 Provider），
具体端点下沉为插件 `provider.typesafe-api`。设置页不再出现一个冒充「配置型」的 SystemOne 条目。

## 2. 问题

能力包原先在 `ActivateAsync` 里往 `IProviderRegistry` 注册一个 `ProviderOrigin.Builtin` 的
`SystemOneLlmProvider`，自带 `~/.seeing/providers/systemone.json` 与 `jev-latest` 种子模型。

而 `ModelsPage.razor` 只对 `ProviderOrigin.Extension` 显示「扩展」标签，其余一律走「配置」分支。
`Builtin` 没有分支，于是被当成用户手写的配置型 Provider 渲染，并给出了「保存配置」按钮
（实际写它自己的私有 JSON，不是 `Providers` 节）。

同时包名/类名（`SystemOneLlmProvider` / `SystemOneConfigStore` / `systemone.json`）都指向一个
早已删除的「SystemOne 平行栈」，而真正的 wire 协议叫 TypeSafe。

## 3. 改动

### 3.1 能力包

| 旧 | 新 |
|---|---|
| `Seeing.Agent.Llm.SystemOne` | `Seeing.Agent.Llm.TypeSafe` |
| 模块 id `llm.systemone` | `llm.typesafe` |
| `SystemOneModule` | `TypeSafeModule` |
| `TypeSafeSystemOneClientFactory` | `TypeSafeClientFactory` |
| `TypeSafeSystemOneClient` | `TypeSafeClient` |
| `SystemOneHttpHelper` | `TypeSafeHttpHelper` |
| `AddSystemOne(registry)` | `AddTypeSafe(registry)` |

**删除**：`SystemOneLlmProvider`、`SystemOneConfigStore`、`SystemOneOptions`、
`SystemOneProviderActivationTests`。

`TypeSafeModule.ConfigureServices` 现在与 `OpenAiLlmModule` 完全同构：只登记协议工厂。
`Activate`/`Deactivate` 为空操作，`DependsOn` 为空，也不再读写任何用户目录文件。

### 3.2 新插件 `Seeing.Provider.TypeSafeApi`

- 扩展 id：`seeing.provider.typesafe-api`；模块 id：`provider.typesafe-api`（`DependsOn: llm.typesafe`）
- Provider id：`typesafe-api`（模型引用前缀 `typesafe-api/`）
- 配置文件：`~/.seeing/providers/typesafe-api.json`
- 来源：`ProviderOrigin.Extension` → 设置页标「扩展」、schema 驱动表单、来源说明写「由扩展自身管理」
- 生命周期：`DisposeByRegistry: false`（DI 单例归容器；否则热重载后再 Activate 会撞 `ObjectDisposedException`）
- 默认进 `Full` 及各档能力集（`s_providerModules`）

### 3.3 能力集

`llm.typesafe` 同时加进 `Minimal` / `Code` / `Work` / `Research`（原先这四档只有 `llm.openai`）。
原因：`provider.typesafe-api` 的 `DependsOn: llm.typesafe`，而 `SettlementEngine.ValidateHardDependencies`
对「启用集里依赖未启用」是**抛异常拒启**，`Hosting.Embed` 默认 boot 正是 `minimal`。
`llm.typesafe` 是零成本协议工厂包（无网络、无后台任务、无文件读写），加进去不改启动成本。

### 3.4 工具包（最小连带改动）
- `SystemOneToolsModule.DependsOn`：`llm.systemone` → `llm.typesafe`
- `SystemOneTool` 失败文案：`请启用 llm.typesafe 模块`
- 工具 id（`systemone_*`）、Skill 名、包名、`llm.systemone.tools` 模块 id **均未改**

## 4. 顺带修复：能力多选渲染成空下拉

**范围**：`Seeing.ConfigSchema` + `Seeing.Agent.Llm.Support` + WebUI `SchemaFieldEditor`。
与 TypeSafe 改动独立，但同批交付。

### 症状

所有 Provider 插件的 `Capabilities` 字段在设置页渲染成一个空下拉框，只有「暂无数据」，
既没有选项也无法输入。DeepSeek / MiMo / OpenCodeZen / SiliconFlow 全都如此。

### 根因

`OptionsSchemaBuilder` 把 `List<string>` 判为 `ConfigFieldType.StringList`，
但该类型没有任何候选值来源；`SchemaFieldEditor` 一律用 `SelectMode.Tags` 渲染，
于是得到一个空的标签下拉。

### 为何必须修而不是隐藏

`Capabilities` 对**配置型** Provider 是真正需要用户决定的：自建或代理端点可能额外提供
embedding，用户必须能勾选。把它藏掉等于取消这条能力。它与 TypeSafe 那种「能力是协议事实」
的情况不同——那里才应该不可配。

### 修法

- `ConfigFieldSchema` 新增可选成员 `Choices`（与由枚举推导的 `EnumValues` 分开：
  后者不可定制，前者的候选来自领域知识）。
- `ConfigurableProviderBase.GetConfigSchema` 统一为名为 `Capabilities` 且无候选的字段
  填入 `LlmCapabilityMap.ConfigKeys`。放在基类是因为候选是 LLM 领域知识，
  `Seeing.ConfigSchema` 原语层不该依赖它；且所有插件都继承本基类，一处生效。
- `SchemaFieldEditor` 按有无候选分派：
  - 有候选 → `SelectMode.Multiple` 下拉，只能从合法值里挑；
  - 无候选 → 逗号分隔文本框（**绝不再渲染成空下拉**，那是死框）。

`TypeSafeApiOptions` 不含 `Capabilities`（能力是协议事实），因此不受此项影响。

## 5. 顺带修复：decision 调用丢失出站拦截器

**范围**：`Seeing.Agent.Llm.TypeSafe`（工厂 + 客户端 + HTTP 辅助）。

### 症状

`TypeSafeClientFactory` 与 `OpenAiLlmClientFactory` 的不对称：前者完全不注入
`ILlmCallInterceptorRegistry`，`TypeSafeHttpHelper.ApplyHeaders` 硬编码传
`Array.Empty<ILlmCallInterceptor>()`。结果是 `llm.params` / `llm.headers` 这类横切机制
以及 `OpenCodeZenCallInterceptor` 等品牌网关塑形，**对 decision 调用完全失效**，且无任何提示。

这是随原 SystemOne 平行栈合流时带过来的既有缺陷，不是本次下沉引入的。

### 修法（对齐 OpenAI）

- `TypeSafeClientFactory` 构造签名加 `ILlmCallInterceptorRegistry? interceptors`（置于
  `decorators` 之前，与 `OpenAiLlmClientFactory` 的参数顺序一致），并传给客户端。
- `TypeSafeClient` 持有注册表，`EvaluateAsync` 与 `ListModelsAsync` 前
  `ResolveInterceptors()` → 交给 `TypeSafeHttpHelper`。
- `TypeSafeHttpHelper.PostAsync` / `GetAsync` / `SendAsync` / `ApplyHeaders` 增加可选
  `interceptors` 参数；`ApplyHeaders` 把它交给既有的 `OutboundPipeline.Apply`
  （该方法本来就支持拦截器，只是此前一直收到空列表）。

### 守门测试

`TypeSafeHttpHelperTests`：
- `PostAsync_ShouldApplyInterceptors` —— 拦截器被调用、且塑形后的头真的发出去了；
- `PostAsync_Interceptor_ShouldOverrideStaticHeader` —— 同名时拦截器覆盖静态头。

## 6. 修复：TypeSafe 未跟随系统代理（导致 451）

### 症状

连接测试报 `HTTP 451 Unavailable For Legal Reasons`。

### 根因

`TypeSafeApiProvider.BuildProviderConfig` 把 `UseProxy` 写死为 `false`。
这一行是从旧 `SystemOneOptions.UseProxy = false`（注释理由「决策服务通常在内网/直连」）照搬来的，
但语义变了：原先它还是一个**可被 JSON 覆盖的默认值**，下沉后变成了**不可覆盖的硬编码**。

后果是 TypeSafe 与其它所有 Provider 行为不一致：

| 组件 | UseProxy |
|---|---|
| `ProviderConfig.UseProxy` 属性默认 | `true`（跟随系统代理） |
| OpenAI / Anthropic 工厂 | 透传 `config.UseProxy` → true |
| 配置驱动 Provider | 读 `Providers` 节，未写则 true |
| TypeSafe（修复前） | **硬编码 false → 直连** |

451 的典型成因正是来源 IP 被按地域拒绝；直连绕过了系统代理，出口 IP 不符合服务方要求。
（实测佐证：该端点无凭据直连返回 403，带假 key 返回 401——都说明域名与端点可用，
451 是服务端对特定来源的策略性拒绝。）

### 修法

删掉 `UseProxy = false`，回到 `ProviderConfig` 默认值（`true` = 跟随系统代理），
与 openai / anthropic / 配置驱动 Provider 完全一致。守门断言从 `BeFalse()` 改为 `BeTrue()`。

### 同时补的诊断

`TypeSafeClient.TestConnectionAsync` 原先把服务端拒绝整个塞进 `LogError(exception)`，
451 的**响应体**（服务端会写明原因）被淹没。现在按状态码分流：

- 捕获 `DecisionProtocolException` 且 `StatusCode > 0` → 输出
  `HTTP {状态码}（{可操作提示}）。服务器响应: {ResponseBody}`；
- 401 / 403 / 404 / 429 / 451 / 5xx 各有提示语（451 明说「通常是来源地区受限，请更换出口网络或代理」）；
- 纯网络故障（`StatusCode == 0`）仍走原异常日志。

守门：`TestConnectionAsync_WhenServerRefuses_ShouldSurfaceStatusAndBody`（451/401/403 三组）。


## 7. 破坏性变更

1. **Provider id**：`systemone` → `typesafe-api`；模型引用 `systemone/jev-latest` → `typesafe-api/jev-latest`。
2. **模块 id**：`llm.systemone` → `llm.typesafe`。写在 `workMode` / Boot / 能力集里的旧 id 会失配。
3. **程序集**：`Seeing.Agent.Llm.SystemOne` → `Seeing.Agent.Llm.TypeSafe`。
4. **配置文件**：不再写 `~/.seeing/providers/systemone.json`。
5. **`DefaultModels.decision`**：若原指向 `systemone/jev-latest` 会失效。

### 迁移

- 旧 `systemone.json` **只读回退**：新文件不存在时会读它（不删除、不搬移），保存一律写
  `typesafe-api.json`，用户下次保存即完成迁移。已填的 apiKey 不会静默丢失。
- `DefaultModels.decision` **不自动迁移**：模型页顶部已用 `ValidateDefaultModels()` 明示
  「以下默认模型已失效（配置已保留，请手动修正）」，重选一次即可。
  Core 里硬编码插件 id 会破坏分层，故不做。

## 8. 优先级（未改变）

扩展优先于用户配置：扩展占了 id=X，用户在 `Providers` 写同 id 会被 `ProviderManager` 跳过并记 Warning。

因此 **`typesafe` 这个自然 id 刻意留空**：用户想用自己的 typesafe 端点，就新建一个
id 任取（如 `typesafe`）的配置型 Provider、协议选 `typesafe`——工厂由 `llm.typesafe` 提供，
这条路不依赖任何插件。

## 9. 守门测试

- `Seeing.Agent.Llm.TypeSafe.Tests`：`Activate_ShouldNotRegisterAnyProvider`
  —— 反向钉住「能力包不造 Provider」。`PostAsync_ShouldApplyInterceptors` /
  `PostAsync_Interceptor_ShouldOverrideStaticHeader` —— decision 调用必须走拦截器。
- `Seeing.Provider.TypeSafeApi.Tests`：来源为 `Extension`、`DisposeByRegistry == false`、
  二次 Activate 不抛、种子模型可路由、schema 只暴露 `ApiKey`、端点/超时/代理固定不读配置、
  旧文件回退读。
- `Seeing.Provider.DeepSeek.Tests`：`GetConfigSchema_Capabilities_ShouldCarryChoices`
  —— 配置型插件的能力多选必须带候选值（防空下拉回归）。
- `LlmRoutingEndToEndTests`：`typesafe-api/jev-latest` 能走完 Router 五层校验。

## 10. 已知债

`DeepSeek` / `MiMo` / `OpenCodeZen` / `SiliconFlow` 四个 provider 插件的 `Activate` 都用
`ProviderRegistration` 默认的 `DisposeByRegistry: true` 注册 DI 单例，存在同类热重载
`ObjectDisposedException` 风险。本次未改，留待统一整改。
