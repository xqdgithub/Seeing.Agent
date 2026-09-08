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
| `Scenario` | 当前进程场景名 |
| `Scenarios` | 自定义/覆盖场景字典（modules/defaultAgent/seams/toolsDisabled） |
| `Modules` | `enabled`/`disabled`/`tools.disabled` 覆盖 |
| `Seams` | 覆盖 scenario seams（如 executionWorld → 模块 id） |
| `DefaultAgent` / `DefaultModel` | 全局默认 |
| `Permission` / `Workspace` / … | 脊柱其它节 |

UI：**设置 → 场景** 管理 `Scenarios` CRUD；**常规** 可快切 `Scenario`。

脊柱节登记实现：`src/spine/Seeing.Agent.Core`（`ConfigSectionRegistry.CreateWithSpine` 含 `Scenario`/`Scenarios`/`Modules`/`Seams`/…）。

## 3. 进程级结算公式

```
available = 宿主登记的 ISeeingModule 描述符
scenario  = seeing.Scenario ?? HostDefaultScenario
base      = scenario.modules ∩ available
enabled   = (modules.enabled ?? base) ∩ available − modules.disabled
```

再校验：

- 未知 id → 告警忽略  
- `DependsOn` 未在 enabled → **SettlementException 拒启**  
- 独占 seam（executionWorld / permissionChannel）：多提供方或消费方未绑定 → 拒启  

实现：`SettlementEngine` + `ModuleCatalog`；入口 `InitializeSeeingAsync` / `ModuleSettlementReloadHandler`。

## 4. 会话级结算

```
sessionScenario = session.Scenario ?? 进程 scenario
sessionEnabled  = sessionScenario.modules ∩ 进程 enabled  − 会话/用户 tools.disabled
```

- **只能收窄，不能新增**进程未启用模块。  
- 切换 `session.Scenario` **只对下一次 `SubmitAsync` 生效**。  
- schema 在 `ExecutionJobService`（`src/hosting/`）按会话结算结果计算一次。

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
        └─ ModuleSettlementReloadHandler（Scenario / Modules / Seams / …）
                │
                ▼
           SettlementEngine 再算 enabled'
                │
                ├─ 新增 id → ActivateAsync
                │     · RegisterTool / UI Register / Open 连接
                │     · IModuleHostedService 若 !IsRunning → StartAsync
                │
                └─ 移除 id →（默认）等在途结束 → DeactivateAsync
                      · UnregisterTool / UI Unregister / Close 连接
                      · HostedService StopAsync
```

| 变更 | 行为 |
|------|------|
| `Scenario` / `Scenarios` / `Modules` / `Seams` / 工作区 | 再结算 → Activate 新增；Deactivate 移除 |
| 有在途执行 | **默认**推迟 Deactivate；`ModuleReloadOptions.ForceCancelInFlight`（经 `ReloadOrchestrator.ForceCancelInFlightOnModuleReload`）可取消在途 |
| 从未引用的包 | **不能**启用（不在 available） |
| 能力包 Options 热字段 | 须挂在已 Register 的节上，经 OptionsMonitor / ReloadHandler 消费 |

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
4. 允许清空的字段：Save 必须支持 **null 删除键**（`RemoveSeeingAgentKeys` / SaveLevel），否则旧值残留  
5. 验证：改文件 → 日志出现结算/Reload → `IModuleCatalog` / UI 与期望一致  

## 8. 配置逻辑禁止清单

| 禁止 | 后果 |
|------|------|
| 在 Core `RegisterCoreServices` 硬编码能力模块 | 脊柱再次变胖；零工具不变量失效 |
| 配置节只写在 Options 类却未 `IConfigSectionRegistry.Register` | 保存/热重载静默丢节 |
| 双算 schema（executor + JobService） | 可见≠可请求，权限洞 |
| 会话场景期望立刻改侧栏 | 侧栏只跟进程场景 |
| 用 `modules.enabled` 塞进未引用包的 id | 无效；应改宿主引用 |
| 清空 Scenario 却 Save 时跳过 null | 旧值残留 |
| `ConfigureServices` 挂 `ITool` 且 Activate 不对称卸载 | enabled 与 schema 分叉；热重载卸不干净 |
| 第二套「偷偷读 json」开关 | 与结算双源；热重载不同步 |

## 9. 排障：现象 → 根因

| 现象 | 先查 |
|------|------|
| 改了 Scenario，侧栏不变 | 是否只改了会话 Scenario？侧栏跟进程；或 Reload 未触发 |
| 关了模块，模型仍调该工具 | `ConfigureServices` 是否挂了 `ITool`？Deactivate 是否 Unregister？ |
| 开了模块，工具仍没有 | 宿主是否 ProjectReference + `AddSeeingModule`？id 是否在 available？ |
| 热重载后后台还在跑 | HostedService 是否实现门闩 / `IModuleHostedService`？Deactivate 是否 Stop？ |
| 热重载后连接挂了 | Owner 是否在再 Activate 时 Open/重建？是否把打开连接当永久 Singleton？ |
| 保存设置后重启才生效 | 节未 Register，或无 ReloadHandler，或写到了 appsettings |
| 结算拒启 | `DependsOn` / seam 冲突；看 SettlementException 消息 |
| schema 与权限不一致 | 是否有第二处 `GetToolSchemas*` 写入 ChatRequest？ |

## 10. 相关实现锚点

| 概念 | 位置 |
|------|------|
| 结算 | `src/spine/.../SettlementEngine`、`ModuleCatalog` |
| 生命周期 | `ModuleLifecycleManager` |
| 模块 Reload | `ModuleSettlementReloadHandler`、`ModuleReloadOptions` |
| 总路由 | `ReloadOrchestrator` |
| schema 单点 | `src/hosting/.../ExecutionJobService` |
