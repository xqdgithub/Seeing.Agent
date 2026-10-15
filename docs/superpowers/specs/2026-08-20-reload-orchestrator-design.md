# 统一重载编排器（ReloadOrchestrator）设计

**日期:** 2026-08-20
**状态:** 待评审
**关联:** 配置/工作区变更后组件不重载问题（会话/Skill/Tool/MCP/插件/Agent MD 等必须重启才生效）

---

## 1. 背景与问题

当前配置/工作区变更后的重载机制存在系统性缺陷：

1. **无统一编排入口**：`ComponentManager.LoadAllAsync`（Skill/MCP/Plugin 三合一）只在启动时调用一次（`ServiceCollectionExtensions.cs:794`），工作区切换后无重载入口。
2. **接口不强制**：`IComponentLoader` 只有 `LoadAsync` 无重载语义，实现者无需考虑"重载"，导致 SkillLoader/McpLoader/PluginLoader 全部为启动一次性。
3. **事件订阅靠自觉**：
   - `WorkspaceRootChanged` 事件**零订阅者**（死事件，设计意图"依赖服务自行订阅响应"未落实）。
   - `IAcpConfigurationReloader` 注册后**无调用者**（孤儿接口）。
   - `AgentRuntimeManager.OnConfigChanged` 在全量重载事件（`ChangedSections.Length == 0`）时直接 return，不处理。
4. **一次性初始化标志**：`McpClientManager.InitializeAsync` 用 `Interlocked.CompareExchange(ref _initialized, ...)` 保证只初始化一次（`McpClientManager.cs:712`），即使重跑 McpLoader 也直接 return，新工作区 mcp.json 永不加载。
5. **缓存残留**：`SessionManager._sessionDataCache` 只增不清（`SessionManager.cs:760`）；`FileSessionStore._baseDirectory` 在 DI 构造时固定为旧工作区路径。

**目标**：设计一个"接口级强制"的统一重载框架，使所有加载过内容的组件必须实现重载逻辑，并统一编排。

---

## 2. 设计原则

| 原则 | 说明 |
|------|------|
| 接口级强制 | 重载语义编入组件接口，实现者必须实现（编译期约束） |
| 变更数据接口化 | 每种触发源一个强类型变更数据实现，新增触发源无需改框架 |
| 统一注册 | 所有 Handler 通过 DI 显式注册，编排器 `IEnumerable<IReloadHandler>` 收集 |
| 保持依赖规范 | Abstractions 零实现纪律；Seeing.Session 原语层定义轻量接口；扩展包只引用 Abstractions |
| 全部迁移 | 现有 7 个自订阅 ConfigChanged 的组件全部迁移到编排器，不留双轨 |

---

## 3. 架构总览

### 3.1 新增/改动文件

```
src/Seeing.Agent.Abstractions/
├── Configuration/
│   ├── IReloadSignal.cs           # 新增：重载信号基接口 + ConfigChange/WorkspaceChange
│   ├── IReloadHandler.cs          # 新增：重载处理器接口 + ReloadHandlerBase<TChange>
│   ├── IReloadSignalBus.cs        # 新增：重载信号总线（推送方接口，插件可推送自定义信号）
│   ├── ReloadResult.cs            # 新增：重载执行结果（从主库移到 Abstractions，推送方需接收）
│   └── Components/IComponentLoader.cs  # 改：增加 ReloadAsync（默认实现转调 LoadAsync）

src/Seeing.Agent/
├── Configuration/
│   └── ReloadOrchestrator.cs      # 新增：统一重载编排器
├── Configuration/ReloadHandlers.cs # 新增：主库各 Handler（Provider/Model/AgentRuntime/AgentManager/Session）
├── Extensions/ServiceCollectionExtensions.cs  # 改：注册编排器 + 各 Handler
├── Core/ComponentManager.cs       # 改：实现 IReloadHandler，分发到三个 Loader
├── Core/ComponentManager 内三个 Loader   # 改：实现 ReloadAsync
├── MCP/McpClientManager.cs        # 改：修复 _initialized 一次性标志，增加幂等 Reload
└── Skills/SkillManager.cs         # 改：无（已有 ResetSearchDirectoriesToDefault/DiscoverSkillsAsync）

src/Seeing.Session/
├── Storage/IRelocatableSessionStore.cs  # 新增：SetBaseDirectory
├── Storage/FileSessionStore.cs    # 改：实现 IRelocatableSessionStore
└── Management/SessionManager.cs   # 改：增加 ClearCache()

src/Seeing.Agent.Memory/Configuration/MemoryOptionsProvider.cs   # 改：删自订阅，改 Handler
src/Seeing.Agent.Scheduler/Configuration/SchedulerOptionsProvider.cs # 改：删自订阅，改 Handler
src/Seeing.Agent.Acp/Hosting/       # 改：AcpHandler 挂接 IAcpConfigurationReloader
src/Seeing.Agent.Gateway/Channels/ChannelHostHostedService.cs  # 改：删自订阅，改 Handler
samples/Seeing.Agent.WebUI/Services/WorkspaceSwitchService.cs   # 改：移除手动状态加载
```

### 3.2 接口定义（Abstractions，零实现纪律）

```csharp
// ===== IReloadSignal.cs =====

/// <summary>重载变更数据基接口：每种触发源一个强类型实现</summary>
public interface IReloadSignal { }

/// <summary>配置节变更数据</summary>
public sealed class ConfigChange : IReloadSignal
{
    public IReadOnlyList<string> ChangedSections { get; init; } = Array.Empty<string>();
}

/// <summary>工作区变更数据</summary>
public sealed class WorkspaceChange : IReloadSignal
{
    public string? OldWorkspace { get; init; }
    public string? NewWorkspace { get; init; }
}

// 未来新增触发源（如文件监视）= 新增一个 IReloadSignal 实现类，框架零改动

// ===== IReloadSignalBus.cs（插件等推送方入口）=====

/// <summary>重载执行结果</summary>
public sealed class ReloadResult
{
    public string ComponentId { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// 重载信号总线：变更推送方（含插件/扩展包）通过此接口发布信号
/// <para>主库 ReloadOrchestrator 实现并注册为该接口；扩展包只引用 Abstractions，依赖规范合规</para>
/// </summary>
public interface IReloadSignalBus
{
    Task<IReadOnlyList<ReloadResult>> PublishAsync(IReloadSignal signal, CancellationToken ct = default);
}

/// <summary>
/// 重载处理器注册表：供运行时动态加载的插件（ExtensionManager 加载的 DLL）
/// 在 InitializeAsync 时注册/注销自己的 Handler；静态 DI 注册仍是推荐路径
/// </summary>
public interface IReloadHandlerRegistry
{
    void RegisterHandler(IReloadHandler handler);
    void UnregisterHandler(IReloadHandler handler);
}

// 插件推送示例：
//   public sealed class MyPlugin : IExtension
//   {
//       private readonly IReloadSignalBus _bus;   // 注入 Abstractions 接口
//       public void NotifyPluginStateChanged() =>
//           _bus.PublishAsync(new PluginStateChange { ... });   // 自定义信号，路由自动生效
//   }

// ===== IReloadHandler.cs =====

/// <summary>组件重载处理器：实现者必须声明订阅的变更类型（编译期可见、可自检）</summary>
public interface IReloadHandler
{
    /// <summary>组件标识（日志与诊断用）</summary>
    string ComponentId { get; }

    /// <summary>声明订阅的变更类型（支持订阅多个源）</summary>
    IReadOnlyList<Type> ChangeTypes { get; }

    /// <summary>执行重载（由编排器统一调度）</summary>
    Task ReloadAsync(IReloadSignal change, CancellationToken ct = default);
}

/// <summary>泛型便捷基类：Handler 直接拿到强类型变更数据，无需 pattern match</summary>
public abstract class ReloadHandlerBase<TChange> : IReloadHandler where TChange : IReloadSignal
{
    public abstract string ComponentId { get; }
    public IReadOnlyList<Type> ChangeTypes { get; } = new[] { typeof(TChange) };
    public Task ReloadAsync(IReloadSignal change, CancellationToken ct)
        => change is TChange typed ? ReloadAsync(typed, ct) : Task.CompletedTask;
    protected abstract Task ReloadAsync(TChange change, CancellationToken ct);
}
```

### 3.3 IComponentLoader 升级（Abstractions）

```csharp
public interface IComponentLoader
{
    string Type { get; }
    Task<ComponentLoadResult> LoadAsync(IServiceProvider services, string workspaceRoot, CancellationToken ct = default);

    /// <summary>重载组件（默认转调 LoadAsync，扩展包无需修改也能编译；需要重载语义者可覆盖）</summary>
    Task<ComponentLoadResult> ReloadAsync(IServiceProvider services, string workspaceRoot, CancellationToken ct = default)
        => LoadAsync(services, workspaceRoot, ct);
}
```

三个内置 Loader（Skill/MCP/Plugin）必须实现真正的重载逻辑（覆盖默认实现）。

---

## 4. ReloadOrchestrator 调度设计（主库）

### 4.1 生命周期与收集

- **Singleton**，构造时订阅 `IConfigSectionStore.ConfigChanged` + `IWorkspaceProvider.WorkspaceRootChanged`。
- 构造函数注入 `IEnumerable<IReloadHandler>`，DI 自动收集所有已注册 Handler，构造时建路由表 `Dictionary<Type, List<IReloadHandler>>`（按 `ChangeTypes` 反射建表，一次性）。
- **实现 `IReloadSignalBus`**（`PublishAsync` = `ReloadAsync`）：插件等推送方经 Abstractions 接口发布自定义信号，依赖规范合规。
- **动态注册 API**：`RegisterHandler(IReloadHandler)` / `UnregisterHandler(IReloadHandler)`——供运行时动态加载的插件（ExtensionManager 加载的 DLL）在 `InitializeAsync` 时注册自己的 Handler；静态 DI 注册仍是推荐路径。

### 4.2 调度规则

| 关注点 | 设计 |
|--------|------|
| 并发 | `SemaphoreSlim` 串行执行；执行期间新变更不排队，仅标记 dirty 合并为一次待处理（执行完后若 dirty 再跑一轮）——避免重入无限排队 |
| 双触发去抖 | 工作区切换会连发两个事件（`WorkspaceRootChanged` → 全量；随后 `ReloadAsync` → `ConfigChanged` 空节=全量）。对"全量"变更在 150ms 窗口内合并为一次 |
| 错误隔离 | 每 Handler 独立 try/catch，失败结构化日志 `{ComponentId}` 记录，不阻断其他 Handler；结果汇入 `IReadOnlyList<ReloadResult>`（`ComponentId/Success/Error/Duration`） |
| 启动自检 | 每个已注册 Change 类型必须有 ≥1 Handler（警告"无组件响应"）；Handler 声明的 ChangeTypes 无对应触发源时告警；启动日志输出注册清单 |
| 显式入口 | `ReloadAsync(IReloadSignal change, CancellationToken)` 公开，供手动触发（如未来文件监视、CLI 命令） |

`ReloadResult` 定义见 3.2（Abstractions 层，推送方需接收结果类型）。

### 4.3 注册与收集机制（回答"实现接口就能接到变更吗"）

**实现接口本身不会自动收到变更，必须通过 DI 注册**：

```
主库 AddSeeingAgent():
  services.AddSingleton<IReloadHandler, ProviderReloadHandler>();
  services.AddSingleton<IReloadHandler, ModelReloadHandler>();
  services.AddSingleton<IReloadHandler, AgentManagerReloadHandler>();
  services.AddSingleton<IReloadHandler, SessionReloadHandler>();
  ...（约 12 个）

扩展包（各自 AddXxx 扩展方法）：
  Seeing.Agent.Memory       → AddSingleton<IReloadHandler, MemoryReloadHandler>()
  Seeing.Agent.Scheduler    → AddSingleton<IReloadHandler, SchedulerReloadHandler>()
  Seeing.Agent.Acp          → AddSingleton<IReloadHandler, AcpReloadHandler>()
  Seeing.Agent.Gateway      → AddSingleton<IReloadHandler, ChannelHostReloadHandler>()

编排器收集：
  public ReloadOrchestrator(IEnumerable<IReloadHandler> handlers, ...)
  // DI 自动注入所有已注册 Handler，构造时建路由表
```

- 漏注册 → 组件收不到变更，但启动自检日志输出注册清单，遗漏可排查。
- 不使用反射扫描自动注册（隐式、有副作用，不符合项目显式注册风格）。

**插件作为推送方**（本节补丁）：

```
Abstractions:  IReloadSignalBus.PublishAsync(IReloadSignal) —— 推送入口
主库:          ReloadOrchestrator 实现 IReloadSignalBus
              services.AddSingleton<IReloadSignalBus>(sp => sp.GetRequiredService<ReloadOrchestrator>());
插件:          注入 IReloadSignalBus，发布自定义 IReloadSignal（如 PluginStateChange）
              路由自动生效：插件 Handler 声明 ChangeTypes 含该类型即可收到
```

**插件 Handler 注册（动态加载场景）**：

```
静态加载（推荐）: 扩展包 AddXxx() 里 AddSingleton<IReloadHandler, XxxReloadHandler>()
动态加载:        插件 InitializeAsync 里调用
                orchestrator.RegisterHandler(new PluginReloadHandler(...))
                → 路由表重建；Dispose 时 UnregisterHandler
                （编排器以 IReloadSignalBus/IReloadHandlerRegistry 接口形式注入扩展包）
```

> 依赖规范要点：扩展包只引用 Abstractions 的 `IReloadSignalBus`/`IReloadHandler`/`IReloadSignal`，具体编排器类型不暴露给扩展包。

---

## 5. 组件改造清单

| # | 组件 | 订阅变更 | 重载行为 | 额外修复 |
|---|------|---------|---------|---------|
| 1 | **IComponentLoader**（Abstractions） | — | 增加 `ReloadAsync`（默认转调 LoadAsync） | — |
| 2 | **SkillLoader** | Workspace + Config(Skills) | 重挂载目录（`ResetSearchDirectoriesToDefault`）+ `DiscoverSkillsAsync` | — |
| 3 | **McpLoader** | Workspace + Config(Mcp) | 断开旧服务器 + 加载新 mcp.json | **修复 `_initialized` 一次性标志**，改为幂等初始化；新增 DisconnectAll + Reload |
| 4 | **PluginLoader** | Workspace + Config(Plugins/PluginEnabled) | ExtensionManager 全量重载（DeactivateAll → Dispose → 重新 InitializeAsync） | — |
| 5 | **ComponentManager** | 实现 IReloadHandler | 按类型分发给各 Loader | — |
| 6 | ProviderManager | Config(Providers) | 删自订阅，改 Handler | — |
| 7 | ModelConfigManager | Config(Providers) | 删自订阅，改 Handler（EnqueueRefresh） | — |
| 8 | SchedulerOptionsProvider | Config(Scheduler) | 删自订阅，改 Handler | — |
| 9 | MemoryOptionsProvider | Config(Memory) | 删自订阅，改 Handler | — |
| 10 | ChannelHostHostedService | Config(Gateway/GatewayClients) | 删自订阅，改 Handler | — |
| 11 | AgentRuntimeManager | Config(DefaultAgent/AgentModels) + Workspace | 删自订阅，改 Handler；**修复全量事件被忽略**（`ChangedSections.Length == 0` 时也要处理） | — |
| 12 | AgentManager | Workspace | `ApplyMdOverridesAsync` 重跑（MD 重新发现） | — |
| 13 | **SessionReloadHandler**（新，主库） | Workspace | `IRelocatableSessionStore.SetBaseDirectory(新工作区/.seeing/sessions)` + `SessionManager.ClearCache()` | Seeing.Session 新增接口 + 缓存清理 |
| 14 | **AcpReloadHandler**（新，Seeing.Agent.Acp 包） | Config(Acp) | 调 `IAcpConfigurationReloader.ReloadAsync`（挂接孤儿接口） | — |
| 15 | **SeeingAgentOptionsMonitor** | 保留自订阅 | **例外**：IOptionsMonitor 基础设施（非加载组件），OnChange 监听者机制保留 | — |

---

## 6. Seeing.Session 原语层改动

```csharp
// Storage/IRelocatableSessionStore.cs（新增）
public interface IRelocatableSessionStore : ISessionStore
{
    void SetBaseDirectory(string baseDirectory);
}

// Storage/FileSessionStore.cs（改）
// _baseDirectory 改为可变字段（加锁），实现 SetBaseDirectory

// Management/SessionManager.cs（改）
public void ClearCache();   // 清空 _sessionDataCache（修复只增不清）
```

---

## 7. 触发流示例（工作区切换）

```
SetWorkspaceRoot("新路径")
  → WorkspaceRootChanged → 编排器构造 WorkspaceChange
    → [150ms 去抖合并]
    → 串行执行：
        ComponentManager（Skill/MCP/Plugin 三个 Loader 全量重载）
        → SessionReloadHandler（换目录 + 清缓存）
        → AgentManagerReloadHandler（MD 重新发现）
        → AgentRuntimeManagerReloadHandler（全量重应用）
  → WebUI 后续 ReloadAsync → ConfigChanged(空) → 被去抖窗口合并，不重复执行
```

## 8. 错误处理

| 场景 | 策略 |
|------|------|
| 单个 Handler 失败 | try/catch 隔离 + 结构化日志，不阻断；结果汇入 `ReloadResult` 集合 |
| 工作区切换失败 | 沿用 `WorkspaceSwitchService` 回滚（`SetWorkspaceRoot(old)` 天然触发反向全量重载）；重载本身失败不回滚（配置已落盘，仅记录，MCP 靠 reconnector 自愈） |
| 重入防护 | 执行期间重入不排队，dirty 合并为一次 |
| 半完成状态 | MCP 断开成功、新连接失败 → 记录失败，后续变更/重连自愈 |

## 9. 测试策略

**ReloadOrchestrator 单测：**
- 路由：ChangeTypes 匹配分发正确（多源订阅 Handler 只收到对应 Change）
- 串行：并发触发排队执行、顺序稳定
- 失败隔离：一个 Handler 抛异常，其余正常，结果含 Error
- 去抖：双事件 150ms 窗口内仅一次全量
- 重入合并：Handler 内保存触发的新变更合并为一次待处理
- 自检：无 Handler 的 Change 类型产生警告日志

**组件级：**
- Provider/Model/Scheduler/Memory 现有测试迁移为验证 Handler 行为
- AgentRuntimeManager：新增"全量重载事件不忽略"回归测试
- McpClientManager：InitializeAsync 幂等测试（重复调用不重复注册工具、可断开后重新初始化）
- FileSessionStore：SetBaseDirectory 后读写新目录
- SessionManager：ClearCache 清空只增缓存
- ComponentManager：ReloadAsync 分发到三个 Loader

## 10. 兼容性 / 风险

| 风险 | 缓解 |
|------|------|
| `IComponentLoader` 加成员破坏扩展包编译 | 默认实现转调 LoadAsync，扩展包无需修改 |
| 同步 → 异步时序变化 | `SaveAsync`/`SaveSectionAsync` 后组件显式 `Reload()` 的路径保留（即时一致）；事件驱动面向"外部变更" |
| 重复 Reload 幂等性 | 所有 Handler 重载必须幂等（MCP 修一次性标志、插件先卸载后加载、Skill 全量重扫） |
| WebUI 改动 | `WorkspaceSwitchService` 移除手动状态加载；UI 刷新靠 `OnSkillStateChanged`/`OnToolStateChanged` 事件 |
| 循环触发 | 重入合并策略 |

## 11. 不在范围内（YAGNI）

- 文件系统监视（FileSystemWatcher）自动重载：未来可作为新的 `IReloadSignal` 实现加入，框架已支持
- 重载失败自动回滚：仅记录，不自动回滚
- 反射扫描自动注册 Handler