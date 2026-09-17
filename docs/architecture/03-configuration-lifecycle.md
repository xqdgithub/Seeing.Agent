# 03 配置 · 结算 · 热重载

## 1. 配置文件位置

| 级别 | 路径 |
|------|------|
| 用户 | `~/.seeing/seeing.json`、`providers.json` 等 |
| 项目 | `./.seeing/seeing.json` |

- **项目覆盖用户**；数组按 `MergeDeep` 整段替换。  
- **禁止**把 Agent/Gateway/ACP 主配置写进 `appsettings.json` 的 SeeingAgent 节。  
- 配置节由 `IConfigSectionRegistry` 登记：Core 只登记脊柱节；能力包在 `ConfigureServices` 自登记。

## 2. 关键脊柱字段（SeeingAgent）

| 字段 | 含义 |
|------|------|
| `Boot` | 进程启动指针：能力集名或 `"*"` → 决定 `bootEnabled` / Activate。可用 `BootOverride`（CLI `--boot` / 环境变量 `SEEING_BOOT`）覆盖文件值 |
| `CapabilitySets` | 能力集字典（`Modules` + 可选 `Disabled`，**仅模块层**）。内置：`minimal` / `code` / `work` / `research` / `full` / `secure` / `dev` |
| `Scenario` | 进程**默认工作模式名**（新会话默认 / 诊断）；**不**驱动 Activate |
| `Scenarios` | 工作模式字典（`Modules` / `DefaultAgent` / `Seams` / `Tools.Disabled`） |
| `Modules` | `Disabled`（boot 全局模块黑名单）+ `Tools.Disabled`（工具层全局裁剪）；**不再**消费 `Enabled` |
| `Seams` | 进程级 seam 绑定覆盖（如 `executionWorld` → 模块 id）；与 `HostDefaultSeams` 合并 |
| `DefaultAgent` / `DefaultModel` | 全局默认 |
| `Permission` / `Workspace` / … | 脊柱其它节 |

JSON 键统一 **PascalCase**。模块层减法统一 `Disabled`；工具层统一 `Tools.Disabled`（勿与模块层混用）。

UI：**设置 → 启动能力** 管理 `Boot` + `CapabilitySets`；**设置 → 场景** 管理工作模式 `Scenarios`；**常规** 可快切默认 `Scenario` 名。

脊柱节登记实现：`src/spine/Seeing.Agent.Core`（`ConfigSectionRegistry.RegisterSpineSections` 含 `Boot` / `CapabilitySets` / `Scenario` / `Scenarios` / `Modules` / `Seams` / …）。

### 配置迁移（旧 → 新）

| 旧写法 | 新写法 |
|--------|--------|
| 用 `Scenario=minimal` 当「沙箱 / 启动天花板」 | 改为 `Boot=minimal`（或自定义 CapabilitySet + `Boot`）；`Scenario` 只表示工作模式 |
| `Modules.Enabled=[...]` 当启动白名单 | 新建 `CapabilitySets.<name>`（`Modules` + 可选 `Disabled`）并设 `Boot=<name>`；`Modules.Enabled` **警告忽略** |
| 扁平 `ToolsDisabled` | 统一为嵌套 `Tools.Disabled`（工具层）；旧扁平键 deprecate / 合并 |

## 3. 进程级结算公式（bootEnabled）

```
available     = 宿主登记的 ISeeingModule 描述符
bootPointer   = BootOverride > 文件 Boot > HostDefaultBoot（缺省 "*"）
bootEnabled   = resolve(bootPointer) ∩ Available − Disabled(set) − Modules.Disabled
```

`resolve`：

| `Boot` | `bootEnabled` |
|--------|----------------|
| `"*"` | `Available − Modules.Disabled` |
| 能力集名 | `(CapabilitySets[name].Modules ∩ Available) − CapabilitySets[name].Disabled − Modules.Disabled` |

补充规则：

- `CapabilitySets[name].Modules` 可为 `["*"]` → **解析层展开**为全 `Available`，**不**进字面 `IntersectWithAvailable`。  
- 未知 Boot 名 → **`SettlementException` 拒启**。  
- 检测到 `Modules.Enabled` → **警告 + 忽略**（不再作启动 base）。  
- `IModuleCatalog.Enabled` ≡ `bootEnabled`。

**内置能力集（`BuiltInCapabilitySets`）：**

| 名 | 含义 |
|----|------|
| `minimal` / `code` / `work` / `research` / `full` | 与内置 Scenario 模块列表同源（Scenario 编译期引用常量） |
| `secure` | `Modules=["*"]`，`Disabled=[shell,mcp,acp,gateway]`（全开再禁高风险） |
| `dev` | 与 `full` 同一模块列表引用 |

**BootOverride 接线：**

| 入口 | 行为 |
|------|------|
| 环境变量 `SEEING_BOOT` | Host Shape 登记时读入 `ProcessSettlementOptions.BootOverride` |
| CLI `seeing start\|web\|gateway --boot <name>` | 写入子进程 `SEEING_BOOT` |
| 直接 `dotnet run … -- --boot <name>` | `BootOverrideSource.ApplyToServices(services, args)`（WebUI / Gateway / Cli bootstrap） |

优先级：`--boot` 参数 &gt; `SEEING_BOOT` &gt; 文件 `Boot` &gt; `HostDefaultBoot` &gt; `*`。

再校验：

- 未知模块 id → 告警忽略  
- `DependsOn` 未在 `bootEnabled` → **SettlementException 拒启**  
- 独占 seam（`executionWorld`）：提供方须 ∈ `bootEnabled`；多提供方或消费方未绑定 → 拒启  

**进程 BoundSeams** 只来自 `SeeingAgent.Seams`（用户覆盖）+ `HostDefaultSeams`（宿主默认）。Scenario.`Seams` 为会话/文档语义，**不**驱动进程 Activate、**不**驱动进程 seam 绑定。

实现：`SettlementEngine` + `ModuleCatalog` + `ICapabilitySetCatalog`；入口 `InitializeSeeingAsync` / `ModuleSettlementReloadHandler`。

## 4. 会话级结算（严格两层）

会话只能在 `bootEnabled` 上**收窄**，不能越过启动天花板。结算分**模块层**与**工具层**，禁止把工具 id 写进模块公式。

### 4.1 模块层（操作 module id）

```
sessionScenario = session.Scenario ?? 进程默认 Scenario 名
sessionModules  = scenario.Modules ∩ bootEnabled
                  （再 ∩ session.ScenarioOverride.Modules.Enabled，若有，仍 ∩ bootEnabled）
```

- **只能收窄，不能新增**进程未 Activate 的模块。  
- `Tools.Disabled` **禁止**进入本层公式（旧文档曾把 `tools.disabled` 写进模块求交，已废止）。

### 4.2 工具层（操作 tool id；模块层之后）

```
settledToolIds = ⋃ sessionModules.ProvidedTools
                 − scenario.Tools.Disabled
                 − session.ScenarioOverride.Tools.Disabled
                 − Modules.Tools.Disabled
schema         = ToolManager 注册表 ∩ settledToolIds ∩ Agent AllowedTools − DeniedTools
```

- 切换 `session.Scenario` **只对下一次 `SubmitAsync` 生效**。  
- schema 在 `ExecutionJobService`（`src/hosting/`）按会话结算结果计算一次。  
- 场景解析与 `IScenarioCatalog` 同源（内置 ∪ `seeing.json Scenarios`）。

## 5. 热重载时序

```
seeing.json 变更 / SaveLevel
        │
        ▼
UnifiedConfigManager / 文件监视
        │
        ▼
ReloadOrchestrator 路由变更节
        │
        ├─ 脊柱 Options 刷新（IOptionsMonitor）
        ├─ Boot 路径：ModuleSettlementReloadHandler
        │         （Boot / CapabilitySets / Modules.Disabled / Seams / Workspace …）
        │                │
        │                ▼
        │           SettlementEngine 再算 bootEnabled'
        │                │
        │                ├─ 新增 id → ActivateAsync
        │                │     · RegisterTool / UI Register / Open 连接
        │                │     · IModuleHostedService 若 !IsRunning → StartAsync
        │                │
        │                └─ 移除 id →（默认）等在途结束 → DeactivateAsync
        │                      · UnregisterTool / UI Unregister / Close 连接
        │                      · HostedService StopAsync
        │
        └─ Scenario 路径：只刷 IScenarioCatalog / OptionsMonitor
                  （Scenarios / 默认 Scenario 名）
                  → 不跑 boot Activate diff；会话滤镜 / UI 下次 Submit 生效
```

| 变更节 | 行为 |
|--------|------|
| `Boot` / `CapabilitySets` / `Modules.Disabled` / `Seams` / `Workspace`（及 available 相关） | 再结算 → Activate/Deactivate **diff**（模块层） |
| `Scenarios` / 默认 `Scenario` 名 | **不**跑 boot Activate diff；只刷新 `IScenarioCatalog` / OptionsMonitor（会话滤镜 / UI 下次生效） |
| 有在途执行 | **默认**推迟 Deactivate；`ModuleReloadOptions.ForceCancelInFlight`（经 `ReloadOrchestrator.ForceCancelInFlightOnModuleReload`）可取消在途 |
| 从未引用的包 | **不能**启用（不在 available） |
| 能力包 Options 热字段 | 须挂在已 Register 的节上，经 OptionsMonitor / ReloadHandler 消费 |

改 `Scenarios.*.Modules` / `Tools.Disabled` → **不**触发 Activate diff。改 `Seams` → 可能改 BoundSeams → 再结算（可能拒启）。

改配置后若 UI/工具集不变，先查本节 §9 排障表。

## 6. HostedService 与连接

长驻服务须实现 `IModuleHostedService`（或经 `AddModuleHostedService<T>`）：

1. `StartAsync` 首行检查 `IModuleCatalog.IsEnabled(moduleId)`（或 `ModuleHostedRunGate`）  
2. 长循环：`!ct && moduleEnabled`（或等价门闩）  
3. `ModuleLifecycleManager`：Activate 后对 `!IsRunning` 调 `StartAsync`；Deactivate 先 `StopAsync` 再卸模块  

连接（Acp / Memory Sqlite 等）：

- DI 只登记 **Owner 空壳**（或工厂），**不**把已打开连接登记为永久 Singleton  
- Activate：建连 / Open；Deactivate：释放 / Close  
- 消费者持有 Owner（Memory：`SqliteConnectionSource` → `RequireOpen()`），**禁止**在 DI 工厂里 `EnsureInstance` 后把连接实例注入并长期捕获  
- 再 Activate 须能恢复可用连接（Acp 可重建 Manager；Memory 对同实例再 Open，消费者下次 `RequireOpen` 取得）
- **禁止**子组件 `Dispose` 掉 Owner 拥有的共享连接  
- **禁止**在未启用时仍占用连接/后台循环

## 7. 配置节登记自检

新 Options / 新 seeing.json 字段上线前：

1. 在模块 `ConfigureServices`（或 Core 脊柱）调用 `IConfigSectionRegistry.Register(...)`  
2. Save / Load 路径走统一 store，不旁路 `File.ReadAllText`  
3. 需要热反应：实现 `IReloadHandler` 或订阅已有 Orchestrator 路由  
4. 允许清空的字段：Save 必须支持 **null 删除键**（`RemoveSeeingAgentKeys` / SaveLevel，含 `Boot` / `CapabilitySets`），否则旧值残留  
5. 验证：改文件 → 日志出现结算/Reload → `IModuleCatalog` / UI 与期望一致  

## 8. 配置逻辑禁止清单

| 禁止 | 后果 |
|------|------|
| 在 Core `RegisterCoreServices` 硬编码能力模块 | 脊柱再次变胖；零工具不变量失效 |
| 配置节只写在 Options 类却未 `IConfigSectionRegistry.Register` | 保存/热重载静默丢节 |
| 双算 schema（executor + JobService） | 可见≠可请求，权限洞 |
| 用 `Modules.Enabled` 当启动白名单 / 结算 base | **已废除**；警告忽略；应改 `Boot` + `CapabilitySets` |
| 把 `Tools.Disabled` 写进模块层求交 / Activate 公式 | 两层混淆；工具裁剪不得卸模块 |
| 用 `Scenario` 当启动天花板（期望换场景 = Activate diff） | Scenario 只是工作模式；天花板是 `Boot` |
| 会话场景期望立刻改侧栏 | 侧栏跟 **bootEnabled**（Requires ⊆ bootEnabled） |
| 用配置塞进未引用包的 id | 无效；应改宿主引用 |
| 清空 `Boot` / `Scenario` 却 Save 时跳过 null | 旧值残留 |
| `ConfigureServices` 挂 `ITool` 且 Activate 不对称卸载 | enabled 与 schema 分叉；热重载卸不干净 |
| 第二套「偷偷读 json」开关 | 与结算双源；热重载不同步 |

## 9. 排障：现象 → 根因

| 现象 | 先查 |
|------|------|
| 改了默认 `Scenario`，侧栏 / 已挂载模块不变 | **预期**：Scenario 不驱动 Activate；侧栏跟 `bootEnabled`。要缩启动集改 `Boot` / `CapabilitySets` |
| 改了 `Boot` / 能力集，模块未装卸 | Reload 是否走到 boot 路径？节是否已 Register？看 Settlement 日志 |
| 未知 Boot 名拒启 | `CapabilitySets` / 内置名是否拼写正确（PascalCase 键、能力集 `Name`） |
| `Modules.Enabled` 写了却无效 | **已忽略**；建 CapabilitySet + 设 `Boot` |
| 会话切到 `full` 仍无 filesystem 等工具 | 进程 `Boot` 是否过窄（如 `minimal`）？会话不能越过 `bootEnabled` |
| 工具没了但模块仍启用 | 查 `Tools.Disabled`（场景 / 会话 / `Modules.Tools.Disabled`），属工具层正常裁剪 |
| 改了 `Scenarios` 却触发 / 未触发 Activate | 仅改 Scenarios **不应**跑 boot diff；会话下次 Submit 才用新滤镜 |
| 关了模块，模型仍调该工具 | `ConfigureServices` 是否挂了 `ITool`？Deactivate 是否 Unregister？ |
| 开了模块，工具仍没有 | 宿主是否 ProjectReference + `AddSeeingModule`？id 是否在 available / bootEnabled？ |
| 热重载后后台还在跑 | HostedService 是否实现门闩 / `IModuleHostedService`？Deactivate 是否 Stop？ |
| 热重载后连接挂了 | Owner 是否在再 Activate 时 Open/重建？是否把打开连接当永久 Singleton？ |
| 保存设置后重启才生效 | 节未 Register，或无 ReloadHandler，或写到了 appsettings |
| 结算拒启 | `DependsOn` / seam 冲突 / 未知 Boot；看 SettlementException 消息 |
| BoundSeams 与预期不符 | 是否误指望 Scenario.`Seams`？进程绑定只读 `Seams` + `HostDefaultSeams` |
| schema 与权限不一致 | 是否有第二处 `GetToolSchemas*` 写入 ChatRequest？ |

## 10. 相关实现锚点

| 概念 | 位置 |
|------|------|
| 结算 | `src/spine/.../SettlementEngine`、`ModuleCatalog` |
| 能力集目录 | `ICapabilitySetCatalog`（内置 ∪ `seeing.json CapabilitySets`；同名配置覆盖内置） |
| 场景目录 | `IScenarioCatalog`（内置 ∪ `Scenarios`；自带 Modules，不外键绑 CapabilitySet） |
| 生命周期 | `ModuleLifecycleManager` |
| 模块 Reload | `ModuleSettlementReloadHandler`（boot 节 vs Scenarios 分流）、`ModuleReloadOptions` |
| 总路由 | `ReloadOrchestrator` |
| schema 单点 | `src/hosting/.../ExecutionJobService` |
