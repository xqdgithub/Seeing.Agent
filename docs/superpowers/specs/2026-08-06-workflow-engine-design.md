# 工作流引擎系统 — 设计规格

**日期:** 2026-08-06
**状态:** ✅ V15 — 十五审修复完成
**审查:** 十五轮架构专家审查，136 项问题全部修复
**本版修订（V15, #118-#123）:** ①#118(P1) 执行循环 edge==null 二分判定（$route 为空→Completed / $route 有值→Unrouted），对齐路由规则表；②#119(P2) #112 catch 注释交叉引用 StopAsync 步骤2；③#120(P2) 移除 #114 TryRemove 竞态，旧注册表条目由 #100"先注销旧"规则在接管时自然清除；④#121(P2) $ 前缀自定义路由向前兼容声明；⑤#122(P2) 图验证补不可达节点 Warning + 无出边自定义 Route 静态检测；⑥#123(P2) 术语"快照"消歧——状态快照（instances.state_json）/审计快照（snapshots 表）/FromSnapshot 说明。

---

## 概述

一个独立的 .NET 工作流引擎，支持有环状态图拓扑、9 种内置节点类型、多种触发方式和递归子工作流组合。每个节点遵循 `input + state → (output, state, status)` 签名，通过 `$route` 保留键驱动状态转换。

**定位：** 独立 NuGet 包 `Seeing.Workflow`（P2 终审修复），不与 Seeing.Agent 耦合，技术栈一致（.NET 10 + C#）。

---

## 核心概念

| 概念 | 说明 |
|------|------|
| **WorkflowDef** | 工作流定义（模板）— YAML 文件，声明节点、边、参数、触发方式、全局超时 |
| **WorkflowInstance** | 工作流实例 — 一次具体运行，独立 ID、创建时间、当前状态 |
| **Node** | 图中的节点 — 9 种内置类型或自定义 `IWorkflowNode` |
| **Edge** | 节点间有向边 — 包含 `when` 条件表达式和 `input_from` 数据映射 |
| **WorkflowState** | 全局状态字典 — `ConcurrentDictionary` 实现，`$` 前缀为引擎保留键 |
| **Trigger** | 触发源 — Manual / Cron / Webhook / Event，统一 `IWorkflowTrigger` |
| **Snapshot** | 快照 — 实例状态的持久化检查点。**#123 术语消歧**：下文"快照"统一指 instances 表中 `state_json` 的当前状态（唯一权威数据源，#103）；`snapshots` 表条目称为"审计快照"（仅审计/版本历史，不被引擎运行逻辑读取）。节点执行**前**保存（at-least-once 保证） |
| **NodeDecorator** | 节点装饰器链 — 重试/超时/沙箱对所有 IWorkflowNode 统一可用 |

---

## 关键决策汇总

| # | 决策 | 选择 |
|---|------|------|
| 1 | 项目定位 | 独立 NuGet 包，技术栈 .NET 10 |
| 2 | 图拓扑 | 有环状态图，$route 驱动 |
| 3 | 执行模型 | 混合：简单节点数据流 + 复杂节点事件驱动 |
| 4 | 节点类型体系 | 分层：9 种内置 + 自定义 `IWorkflowNode`（装饰器链统一可用） |
| 5 | 状态持久化 | 快照（节点执行前保存），节点幂等性约定 + 回放检测 |
| 6 | 图定义方式 | 代码+配置分层：节点是代码，拓扑是 YAML |
| 7 | 递归组合 | 动态子图执行 + 模板实例化（B+D） |
| 8 | 触发方式 | API + Cron + Webhook + 工作流间触发 |
| 9 | 输出模型 | State 延续 + $route 路由语义 |
| 10 | 错误处理 | 引擎重试 + $error 条件边 |
| 11 | 内置节点 | 9 种：Shell, Agent, Condition, Http, WorkflowRef, Program, Transform, Human, Mcp |
| 12 | 数据格式 | 节点间任意 object，序列化时需可 JSON 化 |

---

## 系统分层架构

```
┌─────────────────────────────────────────────────┐
│  触发层 Trigger Layer                            │
│  Manual | Cron (Quartz+DoNothing misfire)       │
│  Webhook (HMAC签名) | Event (订阅/取消订阅)       │
│  IWorkflowTrigger.SubscribeAsync/UnsubscribeAsync │
├─────────────────────────────────────────────────┤
│  定义层 Definition Layer                         │
│  YAML Parser → WorkflowDef Builder              │
│  Node Registry | Type Resolver | Validator      │
│  文件监听热重载 → ReloadDefinitionAsync          │
├─────────────────────────────────────────────────┤
│  引擎层 Engine Layer                             │
│  Instance Manager | Scheduler | Router          │
│  Snapshot Manager | Error Handler | Loop Det    │
│  启动时 Paused 实例扫描 + 恢复                    │
├─────────────────────────────────────────────────┤
│  节点层 Node Layer                               │
│  IWorkflowNode → 9种内置 + 自定义               │
│  装饰器链: Timeout → Retry → Sandbox → Node.ExecuteAsync │
├─────────────────────────────────────────────────┤
│  存储层 Storage Layer                            │
│  ISnapshotStore | SQLite (默认) | 可替换        │
│  快照支持增量 diff 模式（可选）                   │
└─────────────────────────────────────────────────┘
```

---

## 核心接口

### IWorkflowEngine

```csharp
public interface IWorkflowEngine
{
    Task<WorkflowInstance> StartAsync(string defId, WorkflowState? initState);
    Task<WorkflowInstance> ResumeAsync(string instanceId, string? choice = null);
    Task<WorkflowInstance> StopAsync(string instanceId);                   // P2 终审修复：统一返回 WorkflowInstance
    Task<WorkflowState> GetStateAsync(string instanceId);
    Task<IReadOnlyList<WorkflowInstance>> ListAsync(InstanceStatus? filter = null);
    Task ReloadDefinitionAsync(string defId);
}
```

### IWorkflowNode

```csharp
public interface IWorkflowNode
{
    string NodeType { get; }
    bool RequiresSandbox { get; }        // ← 复审修复：声明是否需要沙箱保护
    Task<NodeResult> ExecuteAsync(
        NodeInput input,
        WorkflowState state,
        CancellationToken ct
    );
}
// #101 修复：NodeResult.Route 是节点唯一的路由上报通道（见 NodeResult 定义）；
//           节点不得直接写 $route（保留键写防护），引擎在 Continue 分支统一消费 Route 并写入 State["$route"]
```

### INodeRegistry — Node 实例化（P0-1 终审修复：DI 注入）

```csharp
public interface INodeRegistry
{
    IWorkflowNode Resolve(string nodeType, NodeConfig config);
    void Register(string nodeType, Func<IServiceProvider, NodeConfig, IWorkflowNode> factory);
}

// NodeConfig 是节点在 YAML 中声明的配置块（P2 终审修复）
public record NodeConfig
{
    IReadOnlyDictionary<string, object?> Values { get; }
    T? Get<T>(string key);
}
```

内置的 9 种节点类型在 DI 中自动注册工厂方法。自定义节点通过 `Register()` 方法注册。工厂接收 `IServiceProvider`（P0-1 修复），节点可获取 `IHttpClientFactory`、`ILogger` 等 DI 服务。

### INodeDecoratorChain — 装饰器链（P0-4 终审修复）

```csharp
public interface INodeDecorator
{
    Task<NodeResult> ExecuteAsync(
        IWorkflowNode inner,
        NodeInput input,
        WorkflowState state,
        CancellationToken ct,
        Func<IWorkflowNode, NodeInput, WorkflowState, CancellationToken, Task<NodeResult>> next);
}

public interface INodeDecoratorChain
{
    Task<NodeResult> ExecuteAsync(
        IWorkflowNode node,
        NodeInput input,
        WorkflowState state,
        CancellationToken ct);
}
```

装饰器链顺序：`TimeoutDecorator → RetryDecorator → SandboxDecorator → node.ExecuteAsync`

### IEventBus — 内部事件总线（P2-1 修复）

```csharp
public interface IEventBus
{
    Task PublishAsync<T>(T @event) where T : class;
    IDisposable Subscribe<T>(Func<T, Task> handler) where T : class;
}
```

引擎内部使用内存实现（`Channel<T>`），不依赖外部 MQ。用于 `WorkflowCompleted`、`InstanceStatusChanged` 等事件。

### IWorkflowApiHandler — REST 端点封装（P1-3 终审修复）

```csharp
public interface IWorkflowApiHandler
{
    Task<IResult> RespondAsync(string instanceId, RespondRequest request);
    Task<IResult> ListPendingAsync(string? assignedTo = null);
}

public record RespondRequest(string Choice, string Signature, long Timestamp);
```

HMAC 签名覆盖：`SHA256(timestamp + ":" + instanceId + ":" + body)`，body 为**紧凑 JSON**（无空格、按 key 字母序排序）— P1-4 终审修复。密钥来自 Human 节点配置的 `hmac_secret`（支持 `${ENV:VAR}`）。时间窗口：±5 分钟。

### IWorkflowTrigger（P2-3 终审修复：最小化暴露）

```csharp
public interface IWorkflowTrigger
{
    string TriggerType { get; }
    Task SubscribeAsync(string defId, TriggerConfig config, CancellationToken ct = default);
    Task UnsubscribeAsync(string defId, CancellationToken ct = default);
}

public record TriggerConfig
{
    string Type { get; }
    IReadOnlyDictionary<string, object?> Values { get; }  // 仅传递触发器相关配置
}
```

Trigger 不再暴露完整 `WorkflowDef`，仅传递 `TriggerConfig`。

### 关键数据类型

```csharp
// ── 图定义（#73 修复）──
public record WorkflowDef
{
    string Name { get; init; }
    string Version { get; init; }
    string? Description { get; init; }
    int? TimeoutSeconds { get; init; }
    IReadOnlyList<ParamDef> Params { get; init; }
    IReadOnlyList<TriggerConfig> Triggers { get; init; }
    IReadOnlyList<NodeDef> Nodes { get; init; }
    IReadOnlyList<EdgeDef> Edges { get; init; }

    NodeDef FindNode(string nodeId);
    EdgeDef? FindEdge(string fromNodeId, string? routeCondition);
    EdgeDef? FindDefaultEdge(string fromNodeId);
    IReadOnlyList<EdgeDef> FindEdgesFrom(string fromNodeId);
}

public record ParamDef(string Name, object? Default);

// #74 修复
public record NodeDef(string Id, string Type, NodeConfig Config, RetryConfig? Retry);

public record RetryConfig(int Max, string Backoff, string[]? On);

public record EdgeDef(string From, string To, string? When, string? InputFrom)
{
    // #77 修复: input_from 为 null → 整个 Data; "$output.xxx" → JMESPath 提取
    object? ResolveInputFrom(NodeOutput upstream) => InputFrom == null
        ? upstream.Data
        : JmesPath(upstream.Data, InputFrom.Replace("$output.", ""));
}

public record NodeOutput(object? Data);

// 节点返回中包含执行状态，引擎据此决定继续/暂停/终止
public record NodeResult(NodeOutput Output, WorkflowState State, ExecutionStatus Status, string? Route = null);
// #101 修复：Route 字段 = 节点的唯一路由上报通道（可选，null = 不改路由）
// 节点返回结果时携带建议的下游路由，由引擎统一写入 State["$route"]；
// 节点自身绝不直接写 $route（见下方写入防护），从而消除"节点写保留键"与"引擎独占保留键"的矛盾。
// 各节点实际值：Shell exit0→"$success"/非0→"$error"；Agent→"$success"；Condition→命中分支 route / "$unrouted"；
//             Http→2xx "$success"/4xx "$warning"/5xx "$error"；WorkflowRef→"$success"/"$error"；
//             Transform→"$success"；Human→Paused（返回 null，路由由 ResumeAsync 设置）；Mcp→"$success"。
// 自定义节点若要终止工作流：Route = null + Status=Completed，或显式 "$route" = ""（经 IEngineStateWriter）。

public enum ExecutionStatus
{
    Continue,   // 正常完成，引擎继续执行循环
    Paused,     // 需要暂停（Human 等待审批），引擎保存快照后 break
    Completed   // 节点主动终止工作流（引擎 break）— 内置节点不使用，保留给自定义节点
                // #101 修复：自定义节点用 Route=""（或 Route=null + Status=Completed）表达终止，不直接写 $route
}

public class WorkflowState
{
    // 底层使用 ConcurrentDictionary<string, object?>
    // ── 引擎保留键（$前缀）— 用户禁止写入 ──  // P0-2 终审修复：集中声明 15 个保留键常量
    public const string ROUTE          = "$route";           // 当前路由值，引擎消费
    public const string ERROR          = "$error";           // 错误信息
    public const string TRACE          = "$trace_id";        // 分布式追踪 ID
    public const string INPUT          = "$input";           // 当前节点收到的输入
    public const string OUTPUT         = "$output";          // 当前节点产出的输出
    public const string OUTPUT_OWNER   = "$output_owner";    // #86修复: $output 所属节点 ID（回放跳过依据）
    public const string NODE_ID        = "$current_node";    // 当前正在执行的节点 ID
    public const string ATTEMPT        = "$attempt";         // 当前节点尝试次数（回放检测）
    public const string RESUME_ROUTE   = "$resume_route";    // 崩溃恢复时暂存的原路由
    public const string RESUME_MODE    = "$resume_mode";     // #85修复: 区分崩溃回放("replay")与 Paused 恢复("resume")
    public const string DEPTH          = "$depth";           // 子工作流递归深度
    public const string CHILD_INSTANCE = "$child_instance";  // WorkflowRef 的子实例 ID
    public const string LAST_MSG       = "$last_user_message"; // Agent 崩溃恢复上下文
    public const string LOOP_COUNTS    = "$loop_counts";     // LoopDetector 状态持久化
    public const string ELAPSED_SECONDS = "$elapsed_seconds";// #85修复: 全局超时已消耗秒数（持久化）
    public const string RESERVED_PREFIX = "$";               // 保留键前缀

    // 用户键
    public object? this[string key] { get; set; }
    public T? Get<T>(string key);
    public void Set<T>(string key, T value);
    // #85 修复：写入防护
    // Set/索引器：若 key 以 "$" 开头 → 调用者非引擎内部（通过 IEngineStateWriter 接口访问时放行）→ 抛出 InvalidOperationException
    // IEngineStateWriter: 引擎内部专用写接口（构造时注入），允许写保留键；节点只能拿到只读 WorkflowState 视图
    // #101 修复：路由不再由节点直接写保留键——节点通过 NodeResult.Route 上报，引擎统一写入 State["$route"]。
    //           节点在 State 上唯一能表达的输出是：用户键（写 State）+ NodeResult.Output（$output 属主）+ NodeResult.Route。
    //            因此保留键写入仅来自：引擎内部逻辑 / IEngineStateWriter 持有者 / 引擎代写（如 $last_user_message）。
    // #123 修复：FromSnapshot 从 instances.state_json 反序列化（不访问 snapshots 审计表）；
    //           所谓"快照恢复"实为从 instances 当前行持久化状态重建 WorkflowState，见 #103 权威性
    //           该路径不经过写防护索引器（直接构造），是唯一的保留键写入例外
    // 该防护是"软约束"：防止误写，不替代"引擎消费保留键"的信任边界（节点仍可读 $route 等）

    // 快照序列化（#80 修复）
    // System.Text.Json，TypeInfoResolver 保留类型信息
    // 基础类型直接序列化，复杂类型: {"$type":"FullTypeName","$value":...}
    public string ToSnapshotJson();
    public static WorkflowState FromSnapshot(string json);

    // 增量快照（可选优化）
    public string ToDiffSnapshot(WorkflowState previous);
}
```

```csharp
public enum InstanceStatus
{
    Running,    // 正在执行
    Paused,     // Human 等待审批 / 外部事件等待
    Completed,  // 正常完成
    Failed,     // 执行失败且无 $error 处理边
    Unrouted,   // 无匹配路由边
    Cancelled,  // 外部取消
    TimedOut    // 全局超时
}

// ★ P1-2 终审修复：完整字段定义
public record WorkflowInstance
{
    string Id { get; init; }
    string DefId { get; init; }
    int DefVersion { get; init; }
    WorkflowDef DefSnapshot { get; init; }
    WorkflowState State { get; set; }
    InstanceStatus Status { get; set; }
    DateTime CreatedAt { get; init; }
    DateTime? UpdatedAt { get; set; }
    DateTime? CompletedAt { get; set; }
    long Version { get; set; }  // 乐观锁版本号，每次 SaveAsync 递增
}

// ★ P0-7 终审修复：CAS 语义
public interface ISnapshotStore
{
    Task SaveAsync(WorkflowInstance instance, long expectedVersion);  // CAS: WHERE Version = @expectedVersion
    Task<WorkflowInstance?> LoadAsync(string instanceId);
    Task<IReadOnlyList<WorkflowInstance>> ListAsync(InstanceStatus? filter = null);
    Task DeleteAsync(string instanceId);
    // SaveAsync 行为:
    //   版本匹配 → 写入 + Version++ → 返回
    //   版本不匹配 → 抛出 ConcurrencyException(expected, actual)  // #81 修复

// #81 修复
public class ConcurrencyException : Exception
{
    long ExpectedVersion { get; }
    long ActualVersion { get; }
}
}
```

---

## 实例状态机

```
                    ┌──────────┐
         Trigger ──→│ Running  │←──────────────┐
                    └────┬─────┘               │
          ┌──────────────┼──────────────────┐  │
          ▼              ▼                  ▼  │
    ┌──────────┐  ┌──────────┐      ┌──────────┐
    │ Paused   │  │Completed │      │  Failed  │
    └────┬─────┘  └──────────┘      └──────────┘
         │ ResumeAsync(+choice)
         ▼
    ┌──────────┐
    │ Running  │──────────────────────────────┘
    └──────────┘

    Running ──(无匹配边)──→ Unrouted
    Running ──(StopAsync)─→ Cancelled
    Running ──(全局超时)──→ TimedOut
    Paused  ──(取消)──────→ Cancelled
    Paused  ──(Human超时)─→ Running (设置 $route="$timeout" 继续)
```

### StopAsync 取消传播（#85 修复）

- 引擎维护**运行中实例 CTS 注册表**（`ConcurrentDictionary<string, CancellationTokenSource>`），实例从 Running → 非 Running 时移除条目
- **#100 修复：注册表生命周期一致性** —— 每一条使实例恢复为 Running 的路径都**必须**同步登记 CTS：
  - `StartAsync`（首次创建全局超时 CTS 时）
  - `ResumeAsync`（重建 CTS 时）
  - `HumanTimeoutScanner`（Human 超时恢复时）
  - `RecoveryScanner`（replay 恢复、重建 CTS 时）
  - 规则：**先注销旧条目（若存在），再写入新 CTS**；`StopAsync` 对注册表访问采用 **null-safe**（缺失条目视为"无活跃节点"，仅落库 Cancelled），避免恢复路径漏登记造成的"取消空洞"
- **#110 修复：移除与 Dispose 定点（防泄漏）** — 条目移除 + `CancellationTokenSource.Dispose()` 的**唯一**发生点是"实例离开 Running"的三处：
  1. **Paused 分支**（见执行循环 #106）：`ctsRegistry.Remove(instanceId)` + `timeoutCts.Cancel()` + `Dispose()`（暂停计时，恢复时重建）
  2. **终态统一落库后**（Completed / Unrouted / TimedOut / Cancelled / Failed，break 出循环后、`WorkflowCompleted` 事件前）：`ctsRegistry.Remove(instanceId)` + `timeoutCts.Dispose()`
  3. **StopAsync 内部**（见下，Cancel 后从注册表移除；若实例落到 Paused/终态由对应路径负责清理）
  - 失败/异常提前 `return` 的路径（#107：非本循环所有权的 CE）**不得移除**条目——运行权已让渡给抢占方
  - 观测性：注册表暴露当前条目数；条目数持续增长触顶阈值时输出告警日志（提示回收缺陷）
- `StopAsync(instanceId)`:
  1. CAS 获取所有权（`SaveAsync(instance, instance.Version)`，将状态改为 `Cancelled`）
  2. 从注册表取出该实例的 CTS → `cts.Cancel()` → 当前节点收到取消 → 节点尽快中止；若注册表无该实例（#100：恢复路径未登记或已退出）→ null-safe 跳过，直接进入步骤 3
  3. 若实例处于 Paused（无活跃 CTS）→ 直接标记 `Cancelled` 并落库
  4. 若实例未运行（终态）→ 返回 404 / 已终态错误
  - 步骤 2 完成后移除该实例的注册表条目（并 Dispose CTS，若不再被引用）
- **与执行循环的协调（#87 + #99 + #107 修复）**: 执行循环将 `OperationCanceledException` / `ConcurrencyException` 捕获后统一落库（终态统一保存点）。因此 StopAsync 步骤 1-2 中：
  - **已 CAS 将状态置为 `Cancelled` 并 `Version++`**：执行循环随后的"终态统一落库 `SaveAsync(instance, instance.Version)`"会抛 `ConcurrencyException`。处理方式：执行循环的终态落库、以及循环内全部 SaveAsync（#99）都**容忍 ConcurrencyException**，但需按抢占者区分行为（#107）：行状态为 `Cancelled` → 确认 StopAsync 已落库目标终态，吞掉异常退出；行状态为 `Running` → 恢复扫描器/并发恢复线程抢占，放弃本循环不覆写（抢占方续跑）。循环每轮迭代开头的 `cancellationCheck` 也会提早检出 `_pendingStop`，从快照中恢复的循环（replay）在首轮即退出
- WorkflowRef 父实例取消时，级联取消 `$child_instance`（见 WorkflowRef 章节）

---

## 引擎执行循环（修复版）

```
StartAsync(defId, initState?):
  1. 加载 Def → 验证（包含全局超时配置）
  2. 创建 Instance（DefSnapshot 不可变副本）
  3. State["$route"] = "start"
  4. State["$current_node"] = "$start"              // ← P0-3 修复：初始化 $current_node
  5. State["$input"] = initState? 的数据
  5b. State["$output"] = initState? 的数据；State["$output_owner"] = "$start"   // #92 修复：$start 的 NodeOutput = initState
  6. State["$trace_id"] = Guid
  7. #88 修复：创建后立即落库（expectedVersion=0）
     await SnapshotStore.SaveAsync(instance, 0)   // 实例必须存在，否则"创建后、首快照前"崩溃会丢实例
  8. 创建全局超时 CancellationTokenSource（如果配置了 timeout_seconds）
  9. 进入 ExecuteLoop(instance)

ExecuteLoop(instance):
  // ★ P0-2 修复：检测崩溃恢复模式
  // 如果 $attempt > 0，说明是从 instances 当前行状态继续（先前已持久化的状态/执行前快照，见 #103 权威性）
  // 跳过路由匹配，直接重执行当前节点
  // ★ #85 修复：用 $resume_mode 区分两种重入
  //   "replay"  = 崩溃回放（上一持久化状态），走通用回放检测
  //   "resume"  = Paused 恢复（ResumeAsync 触发），绕过通用回放跳过逻辑
  if (int)(State["$attempt"] ?? 0) > 0:
    isResume = true
    if State["$resume_mode"] 为空:                   // #85: 无标记 + attempt>0 = 崩溃回放
      State["$resume_mode"] = "replay"
    // 保存当前 $route 作为"恢复前的路由"，用于节点完成后继续路由
    State["$resume_route"] = State["$route"]
    // 不更新 $current_node，保持为快照中的节点
    // 直接跳到节点执行
  else:
    isResume = false

  while true:
    cancellationCheck:                             // #87 修复：每轮迭代开头快速检查
      if _pendingStop 是本实例 → Status = Cancelled; break
      if 全局限时 CancellationToken.IsCancellationRequested → Status = TimedOut; break

    if not isResume:
      route = State["$route"]
      if route 为空 → Status = Completed, break

      edge = Def.FindEdge(State["$current_node"], route)
      // 优先匹配 when 条件，全不匹配时查找 $default 边（P2 五审修复）
      if edge == null:
        edge = Def.FindDefaultEdge(State["$current_node"])
      if edge == null:
        // #118 修复：对齐无匹配路由行为表格的二分判定，不再一律 Completed
        if 当前节点无任何出边:
          if route 为空 → Status = Completed, break     // 没有路由值、也无后续节点 → 自然终止
          else → Status = Unrouted, break                // 节点上报了路由但在图中无对应边 → 配置缺失
        else → Status = Unrouted, break                  // 有出边但都未匹配且无 $default 兜底

      State["$current_node"] = edge.To
      State["$input"] = edge.ResolveInputFrom(上节点.Output)
      State["$attempt"] = 0  // ★ 五审修复：切换节点时重置尝试计数
      // #97 修复：切换节点时清除 $output_owner（该键只代表"本次节点执行完成"）。
      // 有环拓扑中若不清除，节点 A 的旧 owner 标记会在下一次执行前残留，
      // 崩溃回放时 attempt>1 且 owner==current 将误判为"已跳过执行"返回陈旧 $output。
      State.Remove("$output_owner")

    // 无论是否恢复，统一走这里
    State["$attempt"] = (int)(State["$attempt"] ?? 0) + 1   // 先自增再进（exec 前快照）

    // ★ 执行前保存快照（at-least-once 保证），$attempt >= 1
    // #99 修复：执行前 SaveAsync 亦容忍 StopAsync 抢占抛出的 CE（见下方 catch 语义）
    try:
      await SnapshotStore.SaveAsync(instance, instance.Version)
    catch ConcurrencyException → 已被 StopAsync 抢占（Cancelled 已落库）→ Status = Cancelled; break

    // ★ 68 修复：根据节点 ID 从 Def 查找类型和配置，再通过 NodeRegistry 解析
    nodeDef = instance.DefSnapshot.FindNode(isResume ? State["$current_node"] : edge.To)
    node = _nodeRegistry.Resolve(nodeDef.Type, nodeDef.Config)
    input = new NodeInput(State["$input"])

    // #90/#96 修复：引擎代写 Agent 恢复上下文（不违反写防护；每次调用 Agent 前刷新）
    if node.NodeType == "agent":
      State["$last_user_message"] = input.Data

    // ★ P0 四审修复：Human 节点恢复时跳过 ExecuteAsync
    if isResume and node.NodeType == "human":
      isResume = false
      State.Remove("$resume_route")
      State.Remove("$resume_mode")
      continue  // 回到循环顶部 → 正常路由匹配 → 找到 Human 的出边 → 下一节点

    try:
      // #85 修复：把 resume_mode 传给节点（WorkflowRef 依赖它区分重入语义）
      // #86 修复：回放检测由引擎统一执行，节点不再自行判断 $output 属主
      if State["$resume_mode"] == "resume":
        result = await 装饰器链.ExecuteAsync(node, input, state, ct)   // Paused 恢复，强制重入
      else:
        // 崩溃回放（replay）：若上次快照已确认本节点完成（$output_owner == 本节点）
        // 且 $attempt > 1 → 直接用快照结果，不重复执行（#86 修复）
        if (int)(State["$attempt"] ?? 0) > 1 and State["$output_owner"] == State["$current_node"]:
          result = (State["$output"] 包装为 NodeOutput, State, Continue)   // 回放，跳过执行
        else:
          result = await 装饰器链.ExecuteAsync(node, input, state, ct)

if result.Status == ExecutionStatus.Paused:
        Status = Paused
        State = result.State
        // #86/#91 修复：Paused 时不更新 $output_owner（保持指向上游产出节点，Human 透传语义）
        //            清理重入标记，避免 recovery 时误判为 Paused 恢复
        State.Remove("$resume_mode")
        State.Remove("$resume_route")
        // #106 修复：先更新 $elapsed_seconds = 当前已消耗 Running 秒数（含本节点耗时），
        //            再保存快照持久化该值（否则暂停点时间丢失，恢复时剩余时间偏大）
        State["$elapsed_seconds"] = ComputeElapsedSeconds(instance)
        // #110 修复：暂停即离开 Running → 取消并 Dispose 全局超时 CTS（暂停计时契约）
        timeoutCts?.Cancel(); timeoutCts?.Dispose(); timeoutCts = null
        // #99 修复：Paused 点保存亦可能撞上 StopAsync 抢占（CE）→ 直接退出，Cancelled 已由 StopAsync 落库
        await _snapshotStore.SaveAsync(instance, instance.Version)   // Paused 点保存（含执行后快照语义 + #106 elapsed）
        // #110 修复：快照持久化成功后从 CTS 注册表移除条目（实例已非 Running；#100 恢复路径重建时会重新登记）
        ctsRegistry.Remove(instanceId)
        break

      if result.Status == ExecutionStatus.Completed:
        Status = Completed
        State = result.State
        break   // 终态落库统一在循环末尾执行

      // Continue: 更新状态
      State = result.State
      State["$output"] = result.Output.Data
      State["$output_owner"] = State["$current_node"]     // #86 修复：标记输出属主
      # #101 修复：路由上报统一入口——节点通过 result.Route 表达下一跳，引擎写 $route
      #           （节点绝不直接写保留键；Human 返回 Paused 时 Route=null，路由留待 ResumeAsync 设置）
      # #109 修复：引擎对 result.Route 做语义校验后再写入：
      #   - 拒绝引擎关键字：result.Route ∈ { "start", "$start", "$default", "" } → 视为非法上报
      #     → 置 $route = "$unrouted" + $error = "Invalid route reported: {value}"（不抛异常，不终止工作流）
      # #113 修复：放宽校验 —— 除上述四个引擎关键字外，任意字符串均放行（含自定义路由如 "archive"、
      #           "finish" 等）。自定义路由是否有效由之后边缘匹配自然裁定（无匹配边 → 检查 $default 兜底
      #           → 仍无匹配 → Unrouted）。不再额外拒绝"非 $" 开头或"未在图内声明"的字符串。
      #   - 原 V12 校验拒绝"非 $ 开头且未在图定义中声明的路由值"过于严格：Condition 分支 route 值
      #     （如 "archive"）天然落在 $default 兜底边覆盖范围内，不能被 #109 提前拒绝
      if result.Route is not null:
        if 校验非法(result.Route):                    // #109 + #113：仅拦截 engine 关键字
          State["$route"] = "$unrouted"
          State["$error"] = $"Invalid route reported: {result.Route}"
        else:
          State["$route"] = result.Route
      # #86 修复：执行完成即落地（执行后快照），以支撑崩溃回放"完成可跳过"判断
      # #99 修复：其 ConcurrencyException 同样按其捕获语义处理（下方 catch）
      await _snapshotStore.SaveAsync(instance, instance.Version)

      // ★ P0-2 修复：恢复模式下，用原来的 $resume_route 作为返回的 $route
      // #104 修复：仅在"回放跳过"路径还原旧路由——节点被真正重执行并上报了非空
      //           Route 时，必须保留新 Route（$resume_route 只是节点未执行前的旧值，
      //           若强行覆盖会把刚算出的新路由丢弃，导致按旧路由匹配出边错误/Unrouted）
      // #115 修复：若节点重执行后 Route=null（合法语义"不改路由"），Continue 分支未修改
      //            $route（Line 542 对 null skip），故 $resume_route == 当前 $route，
      //            $route 赋值 = 当前值（无操作），保留此防御性赋值以确保未来维护安全
      if isResume:
        if result.Route is null:                       // 回放跳过（未重执行）→ 用旧路由继续
          State["$route"] = State["$resume_route"]
        State.Remove("$resume_route")
        State.Remove("$resume_mode")
        isResume = false
    catch RetryExhaustedException:                      // 重试耗尽 → $error 路由
      State["$route"] = "$error"
      State["$error"] = ex.Message
      State.Remove("$resume_mode")
      if isResume: isResume = false
      continue
    catch OperationCanceledException:                  // #87 修复：取消路径闭环
      依据取消源置终态：State.IsStop → Cancelled；否则（全局超时）→ TimedOut
      apply 到 Status; break   // 结束后统一落库
    catch ConcurrencyException:   // #99/#107 修复：CAS 已被其他持有者（StopAsync 或恢复扫描器）抢占
      // #107 修复：CE 不再一律归因为 StopAsync。CAS 竞争者可能来自 StopAsync、RecoveryScanner、
      //            HumanTimeoutScanner 或并发恢复线程。重读实例行区分抢占者意图：
      // #114 修复：双层 CE 场景——若本循环退出且抢占方自身的执行循环也因 StopAsync 再次 CAS
      //           而 CE，实例成为 Running 孤儿，由 RecoveryScanner 二次扫描兜底恢复（防悬挂）
      reload instance = await _snapshotStore.LoadAsync(instanceId)
      if row.Status == Cancelled:                     // StopAsync 已抢先 CAS 落库 Cancelled
        Status = Cancelled; break                     // 终态已持久化，直接退出（#99/#87 容忍语义）
      else if row.Status == Running:                  // 恢复扫描器/并发恢复线程抢占（写入 Running+resume/start）
        // 仅放弃本执行循环，不覆写终态——抢占方负责继续推进该实例；行已由抢占方 CAS 前进。
        // 不对 Status 赋值，直接退出循环（return），避免与抢占方双写
        // #120 修复：不在此处清理注册表（#114 TryRemove 移除）——若本线程刚在 Paused 分支
        //            Dispose 了 timeoutCts（=null），RecoveryScanner 可能已在中间窗口重建了 CTS 并登记
        //           （#100 先注销旧再写新），TryRemove 会错误删除抢占方新条目。旧条目由 #100 的
        //           "先注销旧"规则在 RecoveryScanner/ResumeAsync 接管时自然清除。
        return
      else:                                           // 其他（Paused 等）——拒绝覆盖，交给占有人/人工
        return

  // #85 修复：终态统一落库（Completed / Unrouted / TimedOut / Cancelled / Failed）
  if Status != Paused:
    try:
      await _snapshotStore.SaveAsync(instance, instance.Version)
      // #110 修复：落库成功后清理运行资源——实例已离开 Running，移除注册表条目并 Dispose 全局超时 CTS
      timeoutCts?.Cancel(); timeoutCts?.Dispose(); timeoutCts = null
      ctsRegistry.Remove(instanceId)
    catch ConcurrencyException:                   // #112 修复：终态落库外包 try-catch
      // #87/#107/#112 修复：其他持有者（StopAsync / 恢复扫描器）可能已抢先 CAS 落库（Version++）
      // → 行状态若已为 Cancelled 则语义正确（StopAsync 已持久化目标终态）；若为 Running 则抢占方负责。
      //   吞掉异常结束，不重试、不覆盖。
      // #119 修复：CTS/注册表清理由抢占方路径负责：
      //   - 抢占方为 StopAsync → 其步骤 2（§StopAsync 取消传播）已 Cancel + Dispose + Remove
      //   - 抢占方为 RecoveryScanner → 接管后走正常执行→终态落库，或 CE 路径下的 #114 TryRemove


  if Status == Completed:
    await _eventBus.PublishAsync(new WorkflowCompleted(instance))
```

### ResumeAsync 恢复逻辑

```
  ResumeAsync(instanceId, choice?):
    1. 加载快照 + CAS Version 检查
    2. 校验 Status == Paused
    3. 当前节点的 $current_node 即为暂停节点 ID（Human 或 WorkflowRef）
    4. 校验 choice 合法性（#85 修复）:
       - Human 节点: choice 必须 ∈ 节点 options；非法 → 拒绝（返回 400），不改变实例
       - choice 为空字符串 "" → 视为无效，与非法 choice 相同处理（拒绝）
    5. 设置 State["$route"] = choice ?? "$timeout"       // 引擎经 IEngineStateWriter 写入（#101：ResumeAsync 是引擎路径）
    6. 设置 State["$resume_mode"] = "resume"     // #85 修复：标记为 Paused 恢复（非崩溃回放）
    7. Status = Running
    8. Instance.Version++
    9. await SnapshotStore.SaveAsync(instance, expectedVersion)
       // CAS 失败 → 返回 409 Conflict（实例已被其他操作修改）
    10. 重建全局超时 CTS（#102 修复，与 RecoveryScanner #89 一致）:
        若 DefSnapshot.TimeoutSeconds 存在:
          剩余时间 = TimeoutSeconds - State["$elapsed_seconds"]（已消耗秒数，随快照持久化）
          剩余 <= 0 → 落库 TimedOut（不重入执行）
          剩余 > 0 → 创建新 CTS（剩余时间）→ 登记 CTS 注册表（#100：先注销旧条目再写入新）
        若未配置全局超时 → 跳过（无 CTS，取消空洞由 #100 注册表规则兜底）
    11. 重新进入 ExecuteLoop(instance)
    // Human: 不重新调用 Human.ExecuteAsync，直接设置 $route 继续路由
  // WorkflowRef: 重新调用 ExecuteAsync → 检查子实例状态 → 继续或再次 Paused
```

### #85 修复：两种重入模式的区分（P0-2 问题的完整解法）+ #86 修订

| 场景 | $resume_mode | 崩溃回放跳过（$attempt>1 且 $output_owner==当前节点） | 说明 |
|------|-------------|-------------------------------------------------------|------|
| 崩溃回放（节点执行中宕机，重启后从 instances 当前行状态继续） | `"replay"` | **生效** — 跳过重复执行，返回已保存 $output | 幂等性保护 |
| Paused 恢复（ResumeAsync 触发） | `"resume"` | **豁免** — WorkflowRef 必须重入 | WorkflowRef 重入才能透传续跑子实例；Human 由引擎特判跳过 ExecuteAsync |

- 引擎执行循环在 `$resume_mode == "resume"` 时**跳过通用回放检测**，直接调用节点 ExecuteAsync（WorkflowRef 依赖此语义透传恢复子实例；Human 仍由引擎特判短路）
- 崩溃回放时引擎在进入 ExecuteLoop 时发现 `$attempt > 0` 且无 `$resume_mode` → 自动标记 `"replay"`
- #86 修订：回放跳过以 **执行后快照的 `$output_owner == $current_node`** 为准（见"幂等性与回放检测"），不再以"存在 $output"判断
- 节点执行完成后 `$resume_mode` 被清除，避免污染后续轮次

### Human 超时检测（P1-1 复审修复：后台扫描器 + 雪崩防护）

引擎内置 `HumanTimeoutScanner`（后台定时任务，默认每 30 秒扫描一次）：
```
并发控制: SemaphoreSlim(10) — 最多同时恢复 10 个超时实例
扫描所有 Status == Paused 且 $current_node 为 Human 节点的实例
  → 检查 Human 节点配置的 timeout_hours
  → 如果超过超时时间:
      获取实例乐观锁（CAS Version 检查）
      if CAS 失败 → 静默跳过（用户已通过 API 手动处理）     // ← P5 四审修复
      设置 State["$route"] = "$timeout"
      Status = Running
      Instance.Version++
      await SnapshotStore.SaveAsync(instance, expectedVersion)  // CAS 保存
      → 入队异步恢复（脱离扫描线程）              // ← P1-1 修复
```

- 扫描器**不直接调用 ExecuteLoop**，而是将超时实例放入恢复队列
- 恢复队列由独立线程池消费，避免阻塞扫描器
- 并发上限 10 可通过配置调整
- Human 超时恢复同样维护 `State["$elapsed_seconds"]` 与全局超时 CTS（#85 修复：与 ResumeAsync 路径一致，全局超时剩余时间不丢失）
- **#100 修复**：HumanTimeoutScanner 恢复队列消费时重建 CTS 后**登记 CTS 注册表**（先注销旧条目再写入新条目），确保 StopAsync 在恢复窗口内可取消到该实例

---

## 崩溃恢复策略（P1-1 修复）

| 场景 | 恢复行为 |
|------|----------|
| **引擎启动** | 扫描所有 Paused 实例 → 通过 Webhook 通知审批人 / 日志记录（不自动恢复，等待显式 API 调用）|
| **节点执行中崩溃** | 执行前状态已持久化 → 重启后 Resume 从 instances 当前行状态继续（"状态快照"=instances.state_json，#123消歧）；回放由引擎依据 `$output_owner` 判断是否跳过（at-least-once，见"幂等性与回放检测"）|
| **Human 暂停期崩溃** | Paused 时状态已持久化到 instances 行 → 重启后实例仍在 Paused 状态，等待 ResumeAsync |
| **Running 实例崩溃**（#85 修复） | 引擎启动时（或以可配置间隔）扫描 `Status == Running` 且 `$attempt > 0` 的实例 → CAS 获取所有权 → 以 `$resume_mode = "replay"` 重新进入 ExecuteLoop 继续执行当前节点 |
| **Agent 长运行中断** | Agent 每次新创建 Session，重启后重新 ExecuteAsync → 从零开始对话。⚠️ Agent 是**固有非幂等**节点，崩溃后丢失对话上下文；但上游节点的数据通过 $output 已持久化，仍可传入新 Session。 |
| **WorkflowRef 父子崩溃** | 父子实例独立快照，恢复时各自独立恢复。子实例 Paused 不阻塞父实例恢复（父实例 WorkflowRef 节点重新 ExecuteAsync → 检查子实例状态 → 如果是 Paused 则父也 Paused）|

### 启动时恢复扫描器（#85 修复）

引擎启动时执行 `RecoveryScanner`（孤儿 + Running 崩溃实例兜底）：

```
RecoveryScanner.Run():
  1. 扫描 Status == Paused 的实例 → Webhook 通知审批人（不自动恢复）
  2. 扫描 Status == Running 且 $attempt > 0 的实例：     // #85 新增：普通节点崩溃恢复入口
     → CAS 获取所有权（SaveAsync(instance, instance.Version)）
     → CAS 成功 → 设置 $resume_mode = "replay" → 入队恢复
     → 重建全局超时 CTS（#89 修复）：
         若 DefSnapshot.TimeoutSeconds 存在：
           剩余时间 = TimeoutSeconds - State["$elapsed_seconds"]（已消耗秒数，随快照持久化）
           剩余 <= 0 → 直接置 TimedOut 落库（不重入执行）
           剩余 > 0 → 创建新 CTS（剩余时间），与 StartAsync 相同的超时语义继续计时
     → 重新进入 ExecuteLoop
     → CAS 失败（ConcurrencyException）→ 已被处理 → 静默跳过
  3. 扫描 Status == Running 且 $current_node == "$start" 且 $attempt == 0 的实例（#98 修复）：
     → StartAsync 创建后已落库（#88 步骤 7），但首节点执行前快照前崩溃 → attempt 仍为 0
     → CAS 获取所有权（SaveAsync(instance, instance.Version)）
     → CAS 成功 → 以 $route="start" 重新进入 ExecuteLoop（等价于重新执行 Start 流程的取首跳部分）
     → #105 修复：补齐全局超时 CTS 重建与登记（第 2 步/ResumeAsync step10 同规格）：
         若 DefSnapshot.TimeoutSeconds 存在：
           $elapsed_seconds 为空/0 → 首次剩余 = TimeoutSeconds（等价 StartAsync 首次创建）
           剩余 <= 0 → 直接置 TimedOut 落库（不重入执行）
           剩余 > 0 → 创建新 CTS（剩余时间）→ 登记 CTS 注册表（先注销旧条目再写入新，#100）
若未配置全局超时 → 跳过
      → #117 已知限制：CTS 已创建但 $elapsed_seconds 初始值 0 未持久化到 instances 行。
          若 CTS 创建后、首节点执行后快照前再次崩溃，下次恢复时 $elapsed_seconds 仍为空
          → 重新从 TimeoutSeconds 起算（全局超时轻微回拨）。该窗口与 #89 的超时重建窗口
          同等对待，文档认可为可接受的保守处理（少计耗时，超时偏晚，不丢进度）。
      → CAS 失败 → 静默跳过
  4. WorkflowRef 孤儿检测（见"孤儿检测所有权"章节）——属于第 2/3 类 Running 实例的子集，统一走 CAS+replay 恢复
```

---

## 9 种内置节点（P0-4 修复：Timer 移除，作为纯触发器）

### Shell — 命令行执行
- **配置**: command, cwd, timeout_sec, env, stdin_from
- **ExecuteAsync 行为**: 启动进程 → 等待退出 → 返回 NodeResult.Route（#101 修复：不再直接写 $route）
- **Route（#101 修复）**: `exit 0 → "$success"`, `非0 → "$error"`（P2-4 修复：统一使用引擎保留键前缀风格）；由引擎写入 State["$route"]
- **引擎特化**: 沙箱装饰器（路径白名单、stdout 截断 50KB、环境变量过滤、超时强制 kill）
- **安全**: 路径注入防护、僵尸进程清理、敏感环境变量过滤

### Agent — LLM 驱动（P1-1 终审修复：抽象层解耦）

Agent 节点不直接依赖 Seeing.Agent，而是通过 `IAgentExecutionService` 抽象接口：

```csharp
public interface IAgentExecutionService
{
    Task<AgentResult> ExecuteAsync(
        string model, string systemPrompt, string userMessage,
        WorkflowState state, CancellationToken ct);
}

public record AgentResult(string Output, string FinishReason);
```

使用方通过 DI 注册具体实现（可对接任何 LLM SDK）。`Seeing.Agent` 集成通过实现此接口完成，引擎包不感知。Agent 每次新创建 Session（或由 `IAgentExecutionService` 实现决定）。

- **崩溃恢复**: 重启后从零开始对话。快照中保留 `State["$last_user_message"]`，恢复时作为新 Session 的初始消息
- **写入时机（#85 + #90 修复）**: `State["$last_user_message"]` **由引擎代写**，而非 Agent 节点自身写入——引擎在执行循环调用 Agent.ExecuteAsync **前**，将 `input.Data` 暂写为保留键 `$last_user_message`（与 `$input` 的机制相同，且不违反 IEngineStateWriter 写防护）。Agent 节点通过 input 读到该值。其余保留键的写入必须经由 `IEngineStateWriter`（引擎内部）完成；业务节点的 Set 不能写 `$` 前缀
- **多轮刷新（#96 修复）**: 引擎**每次**即将调用 Agent.ExecuteAsync 前覆盖 `$last_user_message = 当前迭代的 $input`。由于一期 Agent 每次新建 Session（从零开始），该键始终承载"最近一次 Agent 输入的 userMessage"，崩溃恢复时作为新 Session 初始消息，语义确定、无歧义
- **执行模型**: 事件驱动 + 长时间运行（通过 CancellationToken 检查取消）

### Condition — 条件路由（P1-4 修复：实现 ExecuteAsync）
- **配置**: branches: [{route, when}, ...]
- **ExecuteAsync 行为**: 
  1. 遍历 branches，遇到 `when == "$default"` → 跳过（不交给 DynamicExpresso 求值）
  2. 对普通 when 表达式用 DynamicExpresso 依次求值
  3. 第一个条件为 true → 准备返回 NodeResult.Route = 该 branch.route（#101：节点上报，引擎写入）
  4. 全部不满足且有 $default 分支 → 返回 NodeResult.Route = $default 分支的 route
  5. 全部不满足且无 $default → 返回 NodeResult.Route = "$unrouted"
  （P0-5 终审修复：$default 是引擎关键字，非表达式，需特殊处理）
- **Route（#101 修复）**: 第一个满足条件的 branch.route；全不满足 → `"$unrouted"` → 引擎匹配 `$default` 兜底（P2-8 修复）；节点不直接写 $route，由引擎在 Continue 分支写入
- **引擎特化**: 无（与普通节点一样走 ExecuteAsync，不再绕过接口）
- **表达式引擎**: DynamicExpresso
- **异常处理（P2-5 终审修复）**: 表达式求值异常 → Route = "$unrouted" + `$error = "Expression error: {message}"`。不抛出异常，不终止工作流。 — `state.score >= 0.8 && state.type == "critical"` 风格语法
- **$output 行为（P2-3 修复 + #69 修复）**: Condition 透传 `input.Data` 到 `Output`（`NodeResult(new NodeOutput(input.Data), state, Continue, route)`），避免引擎用 `null` 覆盖 `$output`。

### Http — HTTP 请求
- **配置**: method, url, headers, body, timeout_sec
- **ExecuteAsync 行为**: 发送 HTTP 请求 → 返回 NodeResult.Route（#101 修复）
- **Route（#101 修复）**: `2xx → "$success"`, `4xx → "$warning"`, `5xx/超时 → "$error"`，由引擎写入 State["$route"]
- **引擎特化**: 模板变量替换（`${{ state.key }}`）、连接池复用
- **安全**: 敏感头字段日志脱敏、URL 白名单（可选）

### WorkflowRef — 子工作流
- **配置**: ref, input_from, output_to, timeout_sec, max_depth
- **ExecuteAsync 行为**: 创建子实例（传入 `current_depth + 1`）→ 将子实例 ID 写入 `State["$child_instance"]` → 等待子实例完成 → 合并状态
- **Route（#101 修复）**: `子图正常完成 → "$success"`, `超时 → "$error"`，由引擎写入 State["$route"]
- **状态合并**: 子图 `$output` → 父节点 output.Data；子图所有用户 state 键合并到父图 `State["{output_to}.XXX"]`
- **清理**: 完成后清理 `State["$child_instance"]`，避免残留在 State 中（P2 五审修复）
- **深度限制**: 创建子实例前检查 `current_depth >= max_depth` → 直接 `"$error"`（P1-5 修复：父传入深度，子据此判断）

**嵌套暂停与恢复（复审修复）**:

```
// 暂停传播
WorkflowRef.ExecuteAsync 内部:
  currentDepth = State.Get<int>("$depth")   // ← $depth 是引擎保留键，由父实例传递
  if currentDepth >= config.max_depth: return ($error, state, Continue)
  子实例创建:
    initState = new WorkflowState()
    initState["$depth"] = currentDepth + 1    // ← 引擎保留键，由引擎在子实例 StartAsync 中保持
    // initState 中的用户键与引擎保留键不存在冲突：用户键无 $ 前缀
    childInstance = await engine.StartAsync(ref, initState)
  State["$child_instance"] = 子实例Id
  轮询子实例状态（带 timeout_sec 超时控制）             // ← P1-4 修复
  if 子实例.Status == Paused:
    return (output, state, ExecutionStatus.Paused)    // 父也 Paused

// 恢复链
父实例 ResumeAsync:
  → 设置 $resume_mode = "resume"（#85 修复：Paused 恢复标记）
  → 重新进入 ExecuteLoop → isResume → 当前节点是 WorkflowRef
  → 装饰器链.ExecuteAsync(workflowRefNode, ...)   // 注意：$resume_mode="resume" 豁免通用回放跳过（#85）
  → WorkflowRef.ExecuteAsync:
      加载 State["$child_instance"]
      检查子实例状态（#94 修复：三分支，如下）
      if 子实例.Status == Paused:
        await 子实例.ResumeAsync(choice)
      if 子实例.Status == Running:
        等待子实例完成（timeout_sec 控制，继续轮询；不得当作已完成）
      if 子实例终态（Completed/Failed）:
        合并状态 → return (output, state, Continue)
```

**父恢复时子实例的三种状态（#94 修复）**:

| 子实例状态 | 父 WorkflowRef 行为 | 结果 |
|-----------|--------------------|------|
| Paused | 透传 `ResumeAsync(choice)` 给子实例 → 子继续 | 若子再次 Paused → 父再次返回 Paused（递归）；子完成后 → 父合并 Continue |
| Running | **继续轮询等待**（受 `timeout_sec` 控制），不提前放行 | 等待至子完成/超时。超时 → `$error` |
| Completed / Failed（终态） | 直接合并结果 → Continue / `$error` | 继续父流程 |

- 该三分支与"孤儿检测（子完成但父 Running）"共同形成闭环：子终态而父仍在 Running → 由 RecoveryScanner 以 orphan 处理恢复父实例

**超时与僵死防护（P1-4 修复）**:
- `timeout_sec`: 等待子实例完成的最长时间。超时 → `$error`
- **孤儿检测**: 引擎启动时扫描 `$current_node` 为 WorkflowRef 且 Status=Running 的实例 → 检查 `$child_instance` 是否存在 → 若子实例已 Complete/Failed 但父仍 Running → 通过 CAS 乐观锁（Version 字段）获取所有权后自动恢复父实例（P3 四审修复：避免与执行循环竞态）
- **父终止时子实例处置（#85 修复）**: 父 WorkflowRef 节点因超时返回 `$error`、或父实例因取消/全局超时进入终态时，若 `$child_instance` 存在且子实例仍 Running/Paused → 引擎**级联取消**子实例（调用子实例 `StopAsync`，进入 `Cancelled`）并记录日志，防止子实例成为孤儿悬挂运行

### Program — 外部程序（P1-7 终审修复：边界澄清）

- **配置**: path, args, allowed_paths, timeout_sec
- **ExecuteAsync 行为**: 同 Shell，但路径必须在 `allowed_paths` 白名单中
- **Route（#101 修复）**: 同 Shell（exit 0 → "$success"，非0 → "$error"）
- **与 Shell 关系**: Shell 通过 SandboxDecorator 的路径白名单提供通用沙箱；Program 是**更严格**的子集 — `allowed_paths` 是必须显式声明的安全约束，SandboxDecorator 的路径检查**叠加**生效（P1-7 修复：取交集，两者都通过才允许执行）

### Transform — 数据转换
- **配置**: engine (jmespath/jsonpath), expression, input_from
- **ExecuteAsync 行为**: 纯内存计算，无副作用
- **Route（#101 修复）**: 始终 `"$success"`
- **引擎特化**: 无（纯内存操作，不需要装饰器特化）

### Human — 人工审批（P0-1 修复：完整暂停/恢复机制）
- **配置**: message, options, timeout_hours, notify, assigned_to
- **ExecuteAsync 行为**: 发送通知 → 返回 `ExecutionStatus.Paused` + Route = null（#101 修复：不写 $route；引擎 Paused 分支不消费 Route，路由由 ResumeAsync 重新设置）
- **引擎收到 Paused**: 保存快照 → Status = Paused → break 执行循环
- **恢复 API**: `POST /api/instances/{id}/respond` — HMAC 签名验证（P1-6 复审修复）
  - Header: `X-Signature: t=timestamp,v1=hex_hmac_sha256`（签名覆盖：`timestamp + instanceId + body`）
  - Body: `{ "choice": "approve" }`
  - 签名密钥: Node 配置中的 `hmac_secret`（支持 `${ENV:VAR}` 环境变量引用）
- 时间窗口: 允许 ±5 分钟的 timestamp 偏差（防重放）
   - 引擎: 验证签名 → **校验 `choice ∈ options`（#85 修复：非法或空字符串 → 返回 400，工作流保持 Paused）** → 设置 `State["$route"] = choice` → Status = Running → 进入执行循环
- **超时**: Human 节点通过后台 `HumanTimeoutScanner` 检测 → `$route = "$timeout"` → 自动恢复
- **通知**: Webhook 回调 + API 轮询（`GET /api/instances/pending?assigned_to=user`）

### Mcp — MCP 工具
- **配置**: server, tool, args
- **ExecuteAsync 行为**: 连接 MCP Server → 调用工具 → 返回结果
- **Route（#101 修复）**: `成功 → "$success"`, `连接失败/工具不存在 → "$error"`，由引擎写入 State["$route"]
- **引擎特化**: MCP 连接池（stdio/HTTP/SSE），自动重连

---

## 节点装饰器链（P2-4 修复）

重试/超时/沙箱对所有 `IWorkflowNode` 统一可用：

```
装饰器链: TimeoutDecorator → RetryDecorator → SandboxDecorator → Node.ExecuteAsync
```

- **TimeoutDecorator**: 节点级别超时控制（取自节点 config.timeout_sec 或全局默认）
- **RetryDecorator**: 自动重试（取自节点 retry 配置：max/backoff/on）
- **SandboxDecorator**: 通过 `IWorkflowNode.RequiresSandbox` 判断是否生效，Shell/Program 返回 `true`，其他节点返回 `false` → 透传

自定义节点通过装饰器链自动获得重试和超时能力。

### NodeResult.Route 透气契约（#108 修复）

`NodeResult` 是只读 record，装饰器原则上**透传内部节点原结果**（含 Route）。但 decorator 因业务需要构造新 `NodeResult` 时必须遵守以下契约，否则 Shell/Http/Mcp/WorkflowRef 等节点的路由上报会被链中断：

```
契约:
1. 默认：直接透传 inner 结果 → `return inner;`（含 Output/State/Status/Route 完整保留）
2. 需要改写结果的装饰器（如 Sandbox 截断 $output、Timeout 改 Status、Retry 换 Route）：
   - 必须用 `inner with { ... }`（record 复制，自动保留 Route）方式派生新结果
   - 若确需重建 `new NodeResult(...)`，第 4 参 Route **必须显式透传** `inner.Route`
3. Route 语义（#101）：null = "未上报路由"（引擎不改变 $route）
   - 装饰器不得把非空 Route 静默替换成 null（会导致路由丢失，节点后续按旧路由匹配）
   - 若装饰器因特殊语义（如超时强制路由到 $timeout）需要覆盖 Route，必须显式赋值并有注释
4. 实现约定：所有内置装饰器返回语句统一为 `return inner with { ... }`（改写时）或 `return inner;`（纯透传）
```

**TimeoutDecorator 截断示例（#116 修复）**：

```csharp
// TimeoutDecorator 超时路径 —— 用 inner with 保留 Route
public async Task<NodeResult> ExecuteAsync(
    IWorkflowNode inner, NodeInput input, WorkflowState state, CancellationToken ct)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(_timeoutMs);
    try
    {
        return await inner.ExecuteAsync(input, state, cts.Token);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // 节点级超时：改写 Status 和 Output，with 表达式自动保留 inner.Route（如 Shell 的 "$error"）
        // 这将使 engine 在终态落库时仍能按节点原上报路由继续
        return innerResult with { Status = ExecutionStatus.Continue,
                                  Output = new NodeOutput("timeout") };
    }
}
```

> 若需要新建 `new NodeResult(...)`，则 **必须**显式透传第 4 参：`new NodeResult(output, state, status, inner.Route)`。

**回归测试（#116）**：Shell 结点经 `TimeoutDecorator → RetryDecorator → SandboxDecorator` 全链透传后 `result.Route` 必须仍为 `"$success"`/`"$error"` 原值。

实现时补充一条装饰器链单测：Shell 节点经 `TimeoutDecorator → RetryDecorator → SandboxDecorator` 全链透传后 `result.Route` 必须仍为 `"$success"/"$error"` 原值（#108 回归测试）。

---

## $start 虚拟节点（P0-3 修复）

- `$start` 是引擎保留的虚拟入口节点 ID，不在 nodes 列表中声明
- 触发时引擎自动写入 `State["$route"] = "start"` 和 `State["$current_node"] = "$start"`
- edges 必须至少有一条 `from: $start` 的边作为入口，否则工作流验证失败
- 从 `$start` 出发的边不需要 `when` 条件（不匹配直接用）
- 没有任何节点可以将路由设为 `$start` 或 `$default`（引擎层面禁止）。节点经 `NodeResult.Route` 上报这两个关键字时，引擎在写入前执行 #109 校验并视为非法 → 置 `$unrouted`（不再依赖旧引擎保留键写防护）
- 入口节点可以有多个：多条 `from: $start` 的边 → 但**一期仅支持单入口**（P1 五审修复）。多入口串行执行二期实现。
- **$start 的输出（#92 修复）**: 虚拟节点 `$start` 的 NodeOutput 即 StartAsync 传入的 initState 数据。实现方式：StartAsync 时同步设置 `State["$output"] = initState 的数据`，因此首跳 `edge.ResolveInputFrom(上节点.Output)` 中的"上节点.Output"对 `$start` 而言取 `$output`（= initState 数据）；`input_from` 为空时首节点 `$input` 完整接收 initState

---

## 错误处理

| 层级 | 机制 |
|------|------|
| **装饰器层** | 自动重试（可配置次数/退避/可重试异常类型）、超时控制 |
| **$error 路由** | 重试耗尽后引擎写入 `State["$route"] = "$error"`，匹配错误处理边 |
| **图定义层** | 用户声明 `when: "$route == '$error'"` 条件边，指向降级节点 |
| **默认行为** | 节点不声明错误边 → 工作流以 Failed 状态终止 |

---

## LoopDetector — 无限循环检测（P2-5 + P1 四审修复）

```csharp
public class LoopDetector
{
    // 每个 Instance 独立持有一个 LoopDetector 实例（非 Singleton）
    int WarnThreshold = 3;
    int AbortThreshold = 5;

    // #93 修订：两级检测（路径级 + 参数级）
    Dictionary<string, int> _pathCounts = new();        // 一级：Key = SHA256($current_node + $route)（不含用户参数）
    Dictionary<string, int> _paramHashCounts = new();   // 二级：Key = SHA256($current_node + $route + 用户参数规范化摘要)

    void Check(string currentNodeId, string route, WorkflowState state)
    {
        var pathKey = ComputeHash(currentNodeId, route);                    // #93：主键不含用户参数
        var paramKey = ComputeHash(currentNodeId, route, userParamsHash(state));
        _pathCounts[pathKey]++;                                             // 一级计数恒递增
        _paramHashCounts[paramKey]++;                                       // 二级：同参数才递增

        // Abort 条件：同一路径出现太多次，或同参数组合出现太多次
        if _pathCounts[pathKey] >= AbortThreshold or _paramHashCounts[paramKey] >= AbortThreshold:
          throw new LoopDetectedException(...)
        if _pathCounts[pathKey] >= WarnThreshold or _paramHashCounts[paramKey] >= WarnThreshold:
          _logger.LogWarning(...)
    }
}
```

- **两级检测（#93 修复）**:
  - 一级（路径级）：`$current_node + $route`，**不掺入用户参数值**——即使循环中参数每次都在变（自增计数器、时间戳），一级计数仍然递增，保证真实死循环可被检出
  - 二级（参数级）：`$current_node + $route + 用户参数哈希`（用户键规范化摘要）——参数真正不变时更早触发（可配置阈值更宽松时可精确定位）
  - 用 `_pathCounts + _paramHashCounts` 任一超过 `AbortThreshold` 即 Abort；两级共存避免"参数恒变导致检测失效"（纯一级）与"参数哈希过于敏感"（纯二级）各自的盲区
- **挂点（#85 修复）**: 引擎在 **节点执行前、快照保存之后** 调用 `Check(...)`
- 基于 SHA256，独立于节点执行逻辑
- LoopDetectedException 被引擎捕获 → Status = Failed + State["$error"]
- **持久化（P0-6 终审修复）**: 两个计数字典序列化到 `State["$loop_counts"]`（`{"paths":{...},"params":{...}}`）随快照保存，崩溃恢复后不归零

---

## 边（Edge）完整定义（P1-5 修复）

```yaml
edges:
  - from: $start
    to: fetch

  - from: fetch
    to: analyze
    when: "$route == '$success'"
    input_from: "$output.body"

  - from: fetch
    to: fallback
    when: "$route == '$error'"

  # 一期限制：仅支持扇出（一条边→多个目标，串行）
  # 二期增加并行扇出 (join: all) 语法
```

| 字段 | 必填 | 说明 |
|------|------|------|
| `from` | 是 | 源节点 ID 或 `$start` |
| `to` | 是 | 目标节点 ID |
| `when` | 否 | 条件表达式（DynamicExpresso），省略则无条件匹配 |
| `input_from` | 否 | 数据提取表达式，默认传递整个上游 output.Data |

### 一期执行限制

- **扇入不支持**: 多条边指向同一节点 → 按找到的第一条执行（后续边不触发）
- **扇出串行**: 同一 `from` 节点多条出边，引擎按声明顺序逐个匹配 when 条件（P0-3 终审修复：`to` 为单个 string）
- **并行扇出**: 二期通过 `to: [a, b, c]` + `join: all` 语法支持

### 图验证规则（P4 四审修复）

- 同一 `from` 节点最多一条无 `when` 条件的出边（多条 → **Error**）         // P1-5 终审修复：Warning → Error
- 一期必须恰好一条 `from: $start` 的边（多条 → **Error**）                // P1 五审修复
- 所有 `to` 引用的节点 ID 必须在 `nodes` 列表中存在
- **#122 修复：可达性验证（两阶段）**：
  - **不可达节点（Warning，不阻断）**：加载时从 `$start` 出发 BFS 遍历所有出边，输出在 `nodes` 中但从未被访问的节点 ID 列表 + Warning 日志（提示可能是配置遗漏或废弃节点）
  - **孤立子图（P2 延后至 2.0）**：完整连通分量分析与可达性报告。一期仅依赖 Warning + 人工巡检
  - **无出边 + 自定义 Route（#118 联动）**：验证阶段即拒绝"节点在 YAML 中无任何出边但通过节点类型推断其将上报非空自定义 Route"的配置（如 Shell 默认 Route="$success"/"$error" 的场景，若无出边则应在加载时报 Warning；自定义节点如果无法静态推断则跳过此检查）

---

## 路由规则

### $route 值约定

| 值 | 来源（#111 修复：统一为"经 NodeResult.Route 上报 + 引擎写入"，除引擎直接写入外） | 含义 |
|----|------|------|
| `"start"` | 引擎（触发时直接写入） | 入口，匹配 `from: $start` 的边 |
| `"$success"` | 节点经 NodeResult.Route 上报（引擎写入） | 正常完成 |
| `"$error"` | 引擎（重试耗尽 / 节点上报） | 执行失败，需降级处理 |
| `"$warning"` | 节点经 NodeResult.Route 上报（引擎写入） | 部分成功/降级，非致命 |
| `"$timeout"` | 引擎 / Human 节点（Route 或引擎写入） | 超时 |
| `"$unrouted"` | Condition 节点上报 / 引擎校验兜底 | 无匹配分支（配合 $default 兜底） |
| `"$default"` | 边声明 / Condition 分支声明 | **引擎关键字**，当所有其他 when 均不匹配时匹配此边/分支。在 Condition 的 `when` 中直接识别为兜底分支，不经 DynamicExpresso 求值；在 Edge 的 `when` 中同理（P2-4 终审修复：区分了两种上下文中 $default 的语义）。**节点上报 `$default`/`start` 会被 #109 校验拒绝 → `$unrouted`** |
| 自定义字符串 | 节点经 NodeResult.Route 上报 / 图内边 when 值 | 任意路由值，图定义中自行声明对应边；引擎校验（#109）通过后写入 |

> **#111 修复**：旧表格中的"节点自身"措辞与实际实现（节点返回 `NodeResult.Route`，引擎在 Continue 分支统一写入 `State["$route"]`）不一致，已统一。"引擎直接写入"仅出现在：触发时 `start`、重试耗尽 `$error`、HumanTimeoutScanner `$timeout`、Condition 无匹配兜底 `$unrouted`、以及 ResumeAsync 的 `choice`。

> **#121 修复：向前兼容声明** — 除已文档化的引擎约定值（`$success`、`$error`、`$warning`、`$timeout`、`$unrouted`）外，**不建议**在生产图定义中使用其他 `$` 前缀的字符串作为自定义 Route 值（如 `$fork`、`$parallel`）。这些值保留供引擎未来扩展使用；当前不拦截但可能在未来版本中产生语义冲突。

### 无匹配路由行为（P2-7 修复）

1. 当前节点的 `$route` 值在图中找不到任何匹配边
2. 检查当前节点是否**没有任何出边**：
   - 无出边 + `$route` 为空或不存在 → 正常终止（`Completed`）
   - 无出边 + `$route` 有值 → `Unrouted`
3. 有出边但都不匹配 → 检查是否有 `when: "$default"` 的边 → 有则匹配，无则 `Unrouted`

### 表达式求值异常处理（P2-5 终审修复）

边 `when` 或 Condition 的 DynamicExpresso 表达式求值异常时：
- **不抛异常终止** — 将该条件视为 `false`（不匹配），继续检查下一条边/分支
- `$error` 记录: `"Expression error at {when}: {message}"`
- 所有 `when` 都求值异常且无 `$default` → Unrouted

---

## 子工作流状态合并（P1-6 修复）

WorkflowRef 完成时：
- 子图 `State["$output"]` → 父节点的 `output.Data`
- 如果配置了 `output_to: prefix`，子图的所有用户 state 键合并到父图 `State["{prefix}.XXX"]`
- 子图的 `$` 前缀保留键不合并
- `output_to` 为空 → 只回传 `$output`

**嵌套暂停传播**:
- 子实例 Paused → 父 WorkflowRef 节点返回 `ExecutionStatus.Paused`
- 父引擎收到 → 父实例 Status = Paused → 保存快照 → break
- 恢复时调用父实例 `ResumeAsync` → 父引擎检查 `$current_node` 是 WorkflowRef → 透传恢复子实例 → 等待子实例完成 → 继续父实例

---

## 并发策略

- **多实例**: 同一 Def 允许同时运行多个 Instance，每个独立 state + 独立 CancellationToken
- **实例内**: 一期串行执行（单节点串行），二期支持并行扇出
- **WorkflowState 线程安全**: 底层 `ConcurrentDictionary<string, object?>`（P1-2 修复）

---

## 版本管理

- 实例创建时将 WorkflowDef 的不可变副本写入 `DefSnapshot`
- 修改 YAML → 新实例用新定义，运行中实例不受影响
- 实例记录 `DefVersion` 用于追踪
- 提供 `ReloadDefinitionAsync(defId)` + 文件变更监听自动热重载（P2-3 修复）

---

## 全局超时（P1-7 + P1-3 复审修复）

```yaml
name: my-workflow
timeout_seconds: 3600   # 1小时全局超时（仅计 Running 累计时间）
```

- 引擎在 `StartAsync` 时创建 `CancellationTokenSource` 关联超时
- **Paused 期间暂停计时**：Status = Paused → 记录已消耗时间 → 取消 CTS → 恢复时创建新的 CTS，剩余时间 = timeout_seconds - 已消耗
- **已消耗时间持久化（#85 修复）**：累计已消耗秒数写入保留键 `State["$elapsed_seconds"]`（整数，随快照保存；`ResumeAsync`/`HumanTimeoutScanner` 恢复时读取该值继续计时）。进程重启后 Paused 实例恢复时仍能还原剩余时间
- **运行期持续刷新（#95 修复，#111 补充）**：`$elapsed_seconds` 在每个节点执行**后**的 SaveAsync（执行后快照）时由引擎刷新（记录从实例 `CreatedAt` 起算的 Running 累计秒数），保证任意崩溃点之后的快照都带最新已耗时段；崩溃回放恢复（RecoveryScanner）据此重建超时（见 #89）。**长时节点精化（#111）**：若存在单个节点执行时间很长（如长时间工作的 Agent），执行前快照中的 `$elapsed_seconds` 会滞后——可选用"节点执行中定时刷新"（经 IEngineStateWriter 对 `$elapsed_seconds` 周期更新并落库，成本可控时）或"节点耗时单独计入"两种策略其一；一期若不做此精化，则长时节点崩溃回放时重建的剩余时间以略偏保守（少计部分耗时）处理，可接受
- **恢复一致性（#89 修复）**：所有恢复路径（ResumeAsync、HumanTimeoutScanner、RecoveryScanner replay 第 2 步/第 3 步（#105））都要从 `$elapsed_seconds` 重建全局超时 CTS，语义一致
- **恢复路径 CTS 登记（#100 修复）**：上述恢复路径重建 CTS 后须同步登记实例 CTS 注册表（先注销旧条目再写入新条目），保证 StopAsync 在"恢复入队 → 实际重入执行循环"的窗口内仍能取消该实例；StopAsync 对注册表缺失条目 null-safe 兜底
- 超时触发 → Ct 被取消 → 当前节点收到取消 → 执行循环 `catch OperationCanceledException` → `Status = TimedOut` → 统一落库
- 默认无超时（0 = 不限制）
- 与节点级别的 timeout_sec 独立（节点超时是装饰器管，全局超时是引擎管）

---

## 幂等性与回放检测（P1-3 修复 + #86 修订）

| 策略 | 说明 |
|------|------|
| **快照时机** | 节点执行**前**保存快照（at-least-once 保证）+ 节点执行**后**保存快照（#86 新增：记录完成结果与输出属主） |
| **回放检测** | 引擎统一执行：`$attempt > 1 且 $output_owner == 当前节点` → 判定本节点已完成，直接用快照结果，不重复执行 |
| **输出属主** | `State["$output_owner"]` 记录最近一次 `$output` 的产出节点 ID（#86 新增），作为"节点是否真正完成"的依据 |
| **节点要求** | 节点无需自行检查 `$attempt`（回放由引擎统一处理）；节点应保持幂等（副作用安全），以覆盖"执行后快照丢失"窗口 |
| **#85 豁免** | `$resume_mode == "resume"`（Paused 恢复）时，**强制重入执行**（尤其是 WorkflowRef），回放跳过逻辑不生效 |
| **不可幂等节点** | Http POST 等副作用型节点采用幂等键/请求指纹去重等业务级语义保证，引擎不特殊处理 |

### 回放检测流程（#86 修订）

```
引擎在崩溃回放（$resume_mode == "replay" 或空）时统一判断，不再依赖节点自身:
  if State["$attempt"] > 1 且 State["$output_owner"] == State["$current_node"]:
    // 执行后快照已确认本节点完成 → 直接返回快照结果，跳过重复执行
    result = (State["$output"] 包装为 NodeOutput, State, Continue)
  else:
    // 本节点未完成：执行前快照中的 $output 属于上游节点，不能当作本节点结果
    result = await 装饰器链.ExecuteAsync(node, input, state, ct)   // 真正的 at-least-once
```

- **关键修正（#86）**：执行前快照时刻 `$output` 仍是**上游节点**的输出（本节点 `$output` 只在完成后才写入），因此"前次快照有 $output 就跳过"是缺陷——它可能让下游拿到上游数据，或对 null 输出重复执行。改为以 **执行后快照中的 `$output_owner`** 为准：只有确认本节点已完成才跳过。
- **窗口说明**：节点执行完成但执行后快照尚未写入前崩溃 → 回放时 `$output_owner` 仍指上游节点 → 节点被重新执行（at-least-once 语义正常生效）。该窗口依赖节点幂等性兜底。
- **有环拓扑（#97 修复）**: `$output_owner` 在**切换节点时被清除**（执行循环"切换节点"分支），因此：
  - A 完成后 owner=A → 切换到 B 时清空 → B 完成前 owner 为空，回放判定唯一
  - 自环/回访 A：第 N 次执行完成后 owner=A → **切换（回到 A）时清空** → 第 N+1 次执行前 owner 为空 → 崩溃回放 `owner != A` → **不会误跳过**，正确重跑下一轮
  - 该清空由引擎独占执行，与用户键无冲突；`$output_owner` 由引擎维护

---

## 存储

- 接口: `ISnapshotStore`
  - `SaveAsync(WorkflowInstance instance, long expectedVersion)` — 保存完整实例（State + Status + DefSnapshot），CAS 版本控制
  - `LoadAsync(string instanceId)` — 加载实例
  - `ListAsync(InstanceStatus? filter)` — 列出实例
  - `DeleteAsync(string instanceId)` — 删除实例
- 默认实现: SQLite
- 可替换为 PostgreSQL / Redis
- **快照大小控制（P2-6 + P2-7 终审修复）**: State 单个 value 最大 1MB，超出截断 + 日志警告；State 总大小限制 10MB；可选 diff 增量模式
- 数据库表（#82 修复：SQLite schema，+ #85 + #103 修复）:
  - `defs`: id TEXT PK, name TEXT, version TEXT, yaml_content TEXT, updated_at TEXT
  - `instances`: id TEXT PK, def_id TEXT, def_version INT, def_snapshot_json TEXT, status TEXT, state_json TEXT, version INT, created_at TEXT, updated_at TEXT, completed_at TEXT
    - `def_snapshot_json` 为序列化后的 `WorkflowDef DefSnapshot`（不可变副本），实例加载后据此还原 `DefSnapshot`，与 defs 表现行 YAML 解耦（YAML 编辑不影响运行中实例）（#85 修复）
  - `snapshots`: instance_id TEXT, version INT, state_json TEXT, created_at TEXT, PK(instance_id, version)
  - ISnapshotStore 内部管理 defs 表（SaveDef/GetDef），对外仅暴露实例/快照操作
- **#103 修复：表职责与权威性界定**
  - `instances` 表是**唯一权威数据源**（source of truth）：引擎运行期的创建、路由、状态迁移、终态判定、恢复扫描、并发控制（CAS on `Version`）全部以它为准。`ISnapshotStore.SaveAsync` 语义即"**更新 instances 表该行**"（CAS + Version++），**不依赖 snapshots 表**做任何运行逻辑判断。
  - `snapshots` 表是**审计 / 版本历史**：每次 SaveAsync 顺带追加一行（instance_id, version, state_json）记录演进足迹，用于排障、时间旅行、回放审计。其写入为**尽力而为（best-effort）**——审计写入失败不影响主数据（instances）的正确性，仅记录警告。
  - 因此引擎崩溃、恢复扫描、回放判定、终态判定**均不得读取 snapshots 表**；任何"以旧快照恢复"的判定只能来自 instances 当前行。snapshots 如需恢复特性（未来可做），需显式注明为可选特性，不进入本期共识。

---

## 触发方式（P2-5 修复）

```yaml
triggers:
  - type: cron
    schedule: "0 */6 * * *"
    misfire: do_nothing    # P2-1 修复：错过即忽略
    concurrency: allow      # allow | skip | queue（P2-5 五审修复）

# concurrency 语义:
#   allow: 允许并发实例，不限制
#   skip:  上一实例未完成时跳过本次触发（不创建新实例）
#   queue: 排入单元素队列，上一实例完成后自动启动排队实例
    with:
      input: { key: "value" }

  - type: webhook
    path: "/hooks/my-workflow"
    hmac_secret: "${ENV:WEBHOOK_SECRET}"   # 签名验证
```

### Webhook Trigger 防重放与签名（#85 修复）

与 Human Respond 端点共用 HMAC 标准，但实例 ID 尚未存在，故签名覆盖采用 `path` 替代 `instanceId`：

- HTTP Header: `X-Webhook-Signature: t=<unix秒>,v1=<hex_hmac_sha256>`（与 Human 端点的 `X-Signature` 命名区分）
- 签名覆盖内容: `t + ":" + path + ":" + rawBody`
  - `path`: 前导规范化的请求路径（如 `/hooks/my-workflow`）
  - `rawBody`: **原始请求体字节串**（不重新序列化、不做字段排序），保证与客户端签名一致
- **时间窗口**: 允许 ±5 分钟的 timestamp 偏差，超出直接 401（防重放）
- 密钥来源: Trigger 配置 `hmac_secret`（支持 `${ENV:VAR}`）
- 校验失败 → 401，不创建实例；成功 → 创建 Instance → `$route="start"`
- 校验通过后创建实例时须校验存放 `original body` 供 `with.input` 使用

| 触发类型 | 生命周期 | 实现 |
|----------|----------|------|
| Manual | - | C# API / CLI 直接调用 `Engine.StartAsync()` |
| Cron | SubscribeAsync/UnsubscribeAsync | Quartz Job → 创建 Instance → $route="start" |
| Webhook | SubscribeAsync/UnsubscribeAsync | HTTP Endpoint → HMAC 签名验证 → 创建 Instance |
| Event | SubscribeAsync/UnsubscribeAsync | 内部事件总线 → WorkflowCompleted → 订阅者启动 |

### IWorkflowTrigger 生命周期

```
定义加载时:   trigger.SubscribeAsync(defId, config)    // 注册 Cron Job / HTTP Route / Event 监听（config 为对应 TriggerConfig，而非整个 WorkflowDef）（#85 修复）
定义卸载时:   trigger.UnsubscribeAsync(defId)        // 取消注册
定义重载时:   UnsubscribeAsync → 重新 SubscribeAsync
```

---

## 技术选型

| 组件 | 选型 | 理由 |
|------|------|------|
| 表达式引擎 | DynamicExpresso | 轻量 C# 表达式解析，无外部依赖 |
| Shell 沙箱 | 装饰器链（路径白名单 + 超时 + kill） | 不引入 Docker |
| 存储 | SQLite (ISnapshotStore) | 零配置，生产可换 PG/Redis |
| 调度 | Quartz (InMemory → AdoJobStore 可选) | 已在技术栈中 |
| Human 通知 | Webhook 回调 + API 轮询 | 一期简单方案 |
| Human 认证 | HMAC 签名验证（P2-2 修复） | 防未授权审批 |
| Agent Session | 每次新创建 | 避免泄漏，上下文通过 state 传递 |
| 可观测性 | ILogger 结构化日志 + Engine API | 二期引入 OTEL |
| 并发安全 | ConcurrentDictionary + 实例隔离 | 无锁竞争 |

---

## YAML 模板语法（#79 修复）

`${{ }}` 为模板插值语法，在定义加载时解析：

- `${{ params.xxx }}` → 替换为 WorkflowDef.Params 中对应参数的 default 值
- `${{ state.xxx }}` → **不在此阶段解析**（运行时表达式，交给 DynamicExpresso）
- `${{ ENV:VAR }}` → 替换为环境变量值
- 插值范围：仅 `config` 字段和 `with.input` 字段；不在 `when` 表达式中使用

## 表达式变量声明（#78 修复）

DynamicExpress 表达式中可用的变量：

| 上下文 | 可用变量 | 示例 |
|--------|---------|------|
| Edge `when` | `$route` (string，见下方标识符预处理) | `"$route == '$success'"` |
| Condition `when` | `state.xxx` (任意类型) | `"state.score >= 0.8"` |

`state.xxx` 中的 `.xxx` 通过 JMESPath 从 `WorkflowState["xxx"]` 取值。

### $ 前缀标识符处理（#85 修复）

DynamicExpresso 遵循 C# 标识符规则，**`$` 不能出现在标识符中**，因此 `$route` 无法直接作为表达式变量。引擎在求值前对 Expression 做**标识符预处理扫描**：

- 将表达式字符串中的合法引用 `$route`（词法边界：前后非字母/数字/下划线）**替换为内部合法标识符 `__route`**，然后注入参数 `__route = State["$route"]` 求值。YAML 中仍写 `when: "$route == '$success'"`，实现层不可见地完成映射。
- Condition 的 `state.xxx` 同样通过 JMESPath 取值注入为对应合法参数名（如 `state` 对象参数）。
- 预处理只影响表达式字符串内的裸标识符；`"$default"` 关键字判断发生在预处理**之前**（`when == "$default"` 的字符串判定不经过表达式引擎）。
- `input_from` / `${{ }}` 模板字段不经过此预处理（它们是数据提取/占位符，非表达式）。

## Agent $route 映射（#75 修复）

```csharp
// AgentResult.FinishReason → $route 映射
"stop"      → "$success"      // 正常完成
"length"    → "$warning"      // Token 耗尽
"tool_calls"→ "$success"      // 工具调用后完成
"error"     → "$error"        // API 错误
```

## Human $output 来源（#76 修复 + #86 澄清）

Human 节点不产出自己的 Output（返回 Paused 时 `Output.Data = null`），其 `$output` 保留上游节点的值。恢复后引擎用 `edge.ResolveInputFrom(上节点.Output)` 获取数据时，"上节点"是 Human，取到的是上游数据——这是预期行为：Human 仅做路由决策，数据透传。

**与 `$output_owner` 的约定（#86 澄清）**: Human 返回 Paused 时的执行后快照中，`$output_owner` 保持为**上游节点**（不更新为 Human）。因为：
1. Human 的 `$output` 数据属于上游节点，若把 owner 标为 Human，恢复时 `$output_owner==Human` 会误标"Human 已完成"；
2. 但 Human 的恢复走专门路径（`isResume && nodeType=="human"` 直接短路），根本不进入回放检测分支——引擎在 Paused 时仅保留 `$output_owner` 为原值，保证路径归一。

## API Handler 与 Engine 关系（#84 修复）

- `IWorkflowApiHandler.RespondAsync` 是**对外 HTTP 端点**，负责 HMAC 验证 → 调用 `IWorkflowEngine.ResumeAsync`
- 使用方**不直接**调用 `IWorkflowEngine.ResumeAsync`（除非内部集成）
- HMAC 密钥在 Human 节点配置中（`hmac_secret`），非 handler 中硬编码

## 孤儿检测所有权（#83 修复）

引擎启动时的孤儿扫描（现为 `RecoveryScanner` 的一部分，见"崩溃恢复策略"）：

1. 查询 Status=Running 且 $attempt > 0 的实例（含 $current_node 为 WorkflowRef 的孤儿）
2. 通过 `ISnapshotStore.SaveAsync(instance, instance.Version)` 尝试 CAS 写入（标记 `$resume_mode = "replay"`）
3. CAS 成功 → 获得所有权 → 执行恢复逻辑（重新进入 ExecuteLoop）
4. CAS 失败（ConcurrencyException）→ 实例已被其他线程/扫描器处理 → 静默跳过

```yaml
name: data-pipeline
version: "1.0"
description: "数据采集 → 清理 → 分析 → 告警"
timeout_seconds: 3600   # 全局超时

params:
  - name: source_url
    default: "https://api.example.com"
  - name: alert_threshold
    default: 0.8

triggers:
  - type: cron
    schedule: "0 */6 * * *"
    misfire: do_nothing
    concurrency: allow
  - type: webhook
    path: "/hooks/data-pipeline"
    hmac_secret: "${ENV:WEBHOOK_SECRET}"

nodes:
  - id: fetch
    type: http
    config:
      method: GET
      url: "${{ params.source_url }}"
    retry: { max: 3, backoff: exponential }

  - id: clean
    type: workflow-ref
    config:
      ref: data-cleaner
      max_depth: 5
      output_to: cleaned

  - id: analyze
    type: agent
    config:
      model: gpt-4
      max_turns: 5
      system_prompt: "分析清洗后的数据"

  - id: check
    type: condition
    config:
      branches:
        - route: alert
          when: "state.score >= 0.8"
        - route: archive
          when: "$default"           # ← P2-8 修复：引擎关键字兜底

  - id: send_alert
    type: http
    config:
      method: POST
      url: "https://alert.internal/webhook"

  - id: need_approval
    type: human
    config:
      message: "请审核告警内容"
      options: [approve, reject]
      timeout_hours: 24
      assigned_to: admin
      notify:
        webhook: "https://notify.internal/hook"

  - id: log_result
    type: shell
    config:
      command: 'echo "pipeline complete"'

edges:
  - from: $start
    to: fetch

  - from: fetch
    to: clean
    when: "$route == '$success'"
    input_from: "$output.body"

  - from: fetch
    to: log_result
    when: "$route == '$error'"

  - from: clean
    to: analyze

  - from: analyze
    to: check

  - from: check
    to: need_approval
    when: "$route == 'alert'"        # Condition 设置的 route

  - from: check
    to: log_result
    when: "$default"                 # ← 引擎关键字

  - from: need_approval
    to: send_alert
    when: "$route == 'approve'"      # 用户审批通过

  - from: need_approval
    to: log_result
    when: "$route == 'reject'"       # 用户驳回
```

---

## 设计约束

1. 节点产出的 object 必须可 JSON 序列化（快照持久化要求）
2. 节点应实现幂等性，利用 `$attempt` 进行回放检测
3. 用户不应写 `$` 前缀的 state key（引擎保留）；`WorkflowState` 索引器/`Set` 对 `$` 前缀做防护（#85 修复：节点写 `$` 抛异常，`IEngineStateWriter` 例外）
4. 从 `$start` 的边必须存在，否则工作流验证失败
5. 所有 `IWorkflowNode` 自动享有装饰器链（超时+重试+沙箱）
6. State 单个 value 最大 1MB，State 总大小限制 10MB（P2-7 终审修复）
7. `$default` 是引擎路由关键字，用户不应在节点中设置 `$route = "$default"`
8. 终态（Completed/Failed/Unrouted/TimedOut/Cancelled）必须落库（#85 修复）
9. 表达式中的 `$route` 由引擎预处理为 `__route` 后才交给 DynamicExpresso（#85 修复）

---

## 空图与单节点行为（P2-7 修复）

| 场景 | 行为 |
|------|------|
| **空图**（无节点） | 验证失败 ← 至少需要一个节点 |
| **无 $start 边** | 验证失败 ← edges 必须有一条 from: $start |
| **单节点 + 无出边** | $start → node → 执行完毕 → 无出边 → $route 为空 → Completed |
| **单节点 + 有出边但不匹配** | $start → node → $route = "$success" → 有出边但 when 不匹配 → Unrouted |
| **所有 when 不匹配但有 $default** | $start → node → $route = "xxx" → 所有 when 不匹配 → 匹配 $default → 继续 |
