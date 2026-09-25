# 04 开发规范与反模式

## 1. 命名与分层后缀

沿用 `AGENTS.md`：`Store` / `Registry` / `Manager` / `Service` / `Provider` / `Executor` / `Channel` / `Loader` / `Sink` / `Aggregator` / `Factory` / `Authorizer` / `Mapper`。  
- `Aggregator`：UI 投影聚合（Scoped/circuit 维度，不持权威执行态，允许落盘 UI 缓存），如 `TaskCardAggregator`。  
- `Factory`：构造器端口（Provider 语义），如 `IPermissionAuthorizerFactory`/`LlmClientFactoryResolver`。  
- `Authorizer`：授权判定窄端口（请求-响应），如 `IPermissionAuthorizer`/`ExecutionContextPermissionAuthorizer`。  
- `Mapper`：纯函数映射，如 `PermissionKindMapper`。  
Hook 点用 `HookPoints.*`，禁止魔法字符串。  
私有字段 `_camelCase`；静态 `s_camelCase`。  
Core 程序集内命名空间须为 `Seeing.Agent.Core.*`（契约仍可在 Abstractions 的 `Seeing.Agent.Configuration` / `Seeing.Agent.Llm` 等）。

## 2. 磁盘放置

| 新项目类型 | 目录 |
|------------|------|
| 原语 | `src/primitives/` |
| 契约 | `src/abstractions/`（通常只扩展 Abstractions） |
| 脊柱 | `src/spine/` |
| Hosting / Shape | `src/hosting/` |
| 能力模块 | `src/capabilities/` |
| Gateway 协议/集成 | `src/gateway/` |
| 对应测试 | `tests/` 同名分类 |

**禁止**在 `src/` 根下新建平铺项目目录。程序集名保持 `Seeing.Agent.*`（或既有命名），只改路径。

## 3. DI 生命周期（脊柱）

| 类型 | 生命周期 |
|------|----------|
| ToolManager、HookManager、ModuleCatalog、SettlementEngine、ScenarioCatalog | Singleton |
| 模块工具具体类型 | Singleton 工厂；**经 Activate 才 Register 到 ToolManager** |
| `IModuleHostedService` | 与 `IHostedService` 同实例；可停可复活 |
| Middleware | Transient |

**工具装饰器链（实际语义，2026-09-25 校正）：** 注册顺序为「重试→超时→缓存→输出限长」，`ToolDecoratorRegistry.Apply` 按注册顺序依次包裹（**后注册者居外层**），最终包装为
`ToolOutputLimiter(Cached(Timeout(Retry(tool))))`：
- `RetryToolDecorator` 在最内层（3 次、1s 指数退避；可重试集合 `TimeoutException`/`HttpRequestException`/`TaskCanceledException`/`IOException`）；
- `ToolTimeoutDecorator` 包裹重试，**全局超时覆盖重试总时长**（能力声明 `timeout.skip`/`timeout.budget`，兜底 `SeeingAgentOptions.ToolExecutionTimeout`）；
- `ToolOutputLimiterDecorator` 在最外层处理最终返回结果；缓存默认关闭（仅 `cache.enabled=true` 的外部工具生效）。
注册点：`ServiceCollectionExtensions`（`AddSeeingCore` 内 `IToolDecoratorRegistry` 工厂）；`ToolManager.RegisterToolAsync` 时 `Apply()`。

## 4. 强制检查清单（每个 PR）

### 依赖

- [ ] 新能力包 **无** `ProjectReference` → `Seeing.Agent.Core`  
- [ ] Core **无** 新增能力包引用  
- [ ] Host Shape **无** Tools/Memory/Scheduler/Gateway 集成包引用  
- [ ] Hosting **无** ACP / Skills 引用（允许 Tools.Support）  
- [ ] Gateway 集成包 **无** Core / Hosting 引用  

### 模块与热重载对称

- [ ] 实现 `ISeeingModule`；稳定 `Id`；声明 `ProvidedTools` / `ProvidedSeams` / `DependsOn`  
- [ ] `ConfigureServices`：只登记 DI 与配置节；**不**无条件 Start 连接；**不** `AddSingleton<ITool>`  
- [ ] `ActivateAsync(sp, ct)`：`RegisterTool*` + UI Register + 开连接  
- [ ] `DeactivateAsync`：**对称** `UnregisterTool*` + UI Unregister + 关连接 / Stop Hosted  
- [ ] 长驻循环：`IModuleHostedService` + 启用门闩；可再 Activate 复活  
- [ ] 连接：Owner 空壳；Activate Open / Deactivate Close  
- [ ] 需要 UI：`IUiContribution`；路由用 `RouteContribution`（勿 `@page`）  

### 配置

- [ ] 新 Options 经 `IConfigSectionRegistry.Register`  
- [ ] 变更能进 `ConfigChanged` / ReloadHandler  
- [ ] 不引入第二套「偷偷读 json」旁路  
- [ ] 可清空字段支持 null 删除键  

### 执行与 schema

- [ ] 不在 Native executor 自算工具 schema 写入请求  
- [ ] 工具观察 `CancellationToken`；取消后不写父会话  

### 测试与扫描

- [ ] `dotnet build Seeing.Agent.slnx`  
- [ ] `dotnet vstest …/Seeing.Agent.Invariants.Tests.dll`  
- [ ] 粘贴 [README 防回归扫描](README.md) 无意外命中  

## 5. 反模式（看到即拒）

| 反模式 | 正确做法 |
|--------|----------|
| 在 Core 里 `new XxxModule().ConfigureServices` | 宿主 `AddSeeingModule` |
| 页面里 `if (GetService<MemoryService>()!=null)` | `IModuleCatalog.IsEnabled("memory")` |
| 为修 sample 在 Core 开特例开关 | 改 sample 组合或 Scenario |
| 复制结算/schema 逻辑 | 复用 SettlementEngine / JobService |
| 能力包引用 `UnifiedConfigManager` 具体类 | `IConfigSectionStore` / Abstractions 端口 |
| `ConfigureServices` 挂死 `ITool` | Activate/Deactivate 挂载 |
| Hosting.Gateway 直接 `AddSeeingGatewayServer` | sample 显式组合 |
| 混用 `\\` 与 `/` | 抽象文件系统 API |
| 静默吞掉模块注册失败 | 记录并失败可见 |
| 同 id 模块二次登记不 Replace | `AddSeeingModule(..., replace: true)` |
| 新项目平铺在 `src/Xxx` | 放入分类子目录 |
| Activate 开连接、Deactivate 不关 | 对称生命周期 |
| 用 appsettings 覆盖 SeeingAgent 真相 | 只写 `seeing.json` |

## 6. 常见错误 PR 对照

| 意图 | 错误改法 | 规范改法 |
|------|----------|----------|
| WebUI 要加 Memory 页 | 页面 `@page` + 直接注入服务 | 模块 `RouteContribution` + Activate Register |
| Gateway.Server 要起通道 | Hosting.Gateway 引用 Agent.Gateway | sample 组合两个 Add* |
| 工具默认始终可用 | `AddSingleton<ITool, T>` | Activate `RegisterTool` |
| 关模块立刻断在途 | 强杀且无选项 | 默认推迟；显式 `ForceCancelInFlight` |
| 能力包要用 Core 某个 Helper | ProjectReference Core | 上提接口到 Abstractions 或复制纯函数到 Support |
| 配置加字段 | 只加 Options 属性 | Register 节 + Reload 路径 + 测试 Save/Load |

## 7. 代码评审最低标准

1. 依赖方向（csproj ProjectReference）与磁盘分类  
2. 是否破坏 `InitializeSeeingAsync` 前模块登记顺序  
3. 是否破坏进程/会话场景语义与热重载对称性  
4. 是否引入配置双源或双算 schema  
5. Activate/Deactivate 是否成对（工具 / UI / 连接 / Hosted）

## 8. 测试命令

```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/spine/Seeing.Agent.Invariants.Tests/bin/Debug/net10.0/Seeing.Agent.Invariants.Tests.dll
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```

注意：测试项目若启用 MTP，本环境常出现 “Zero tests ran”；优先对已构建 dll 使用 `dotnet vstest`。
