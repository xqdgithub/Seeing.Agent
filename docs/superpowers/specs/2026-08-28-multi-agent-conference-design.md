# 多 Agent 会议大屏页面设计

**创建时间:** 2026-08-28
**状态:** 已批准
**目标:** 基于 WebUI 事件改造（`SessionEventStreamRouter` 多流订阅 + `EventStreamHandler` 每会话实例 + `TaskCardAggregator` 子会话聚合），构建可交互的多 Agent 会议式页面，支持主窗口 + 若干小窗口同时实时渲染、点击切换（主从交换）

---

## 1. 背景与目标

### 1.1 背景

- 2026-08-27 重构已建立统一多流订阅基础设施（`SessionEventStreamRouter`）：任意会话流只订阅一次、广播给多消费者；`EventStreamHandler` 已每会话实例化；`TaskCardAggregator` 经 `origin_tool_call_id` 关联子会话并聚合 TaskSteps。
- 既有 spec（2026-08-27-task-card-ui-aggregation-design.md）明确将 **Agent 大屏**（同时操作与展示若干会话）列为未来方向，本页即该方向的首个落地。
- 现有 `Session.razor`（2134 行）将"会话头部 + 消息渲染 + 事件分派 + 输入提交 + 窗口状态"全部耦合在一页，无法支撑多会话并存。

### 1.2 目标

1. 新增会议式多 Agent 页面：主窗口 + 小窗口网格，点击小窗口主从交换，每个窗口实时渲染。
2. 主窗口完整可操作（发送消息、取消执行、切换 Agent/模型、权限弹窗），小窗口只读摘要。
3. 抽取可复用 `SessionWindow` 组件，`Session.razor` 与大屏页共用，消除重复逻辑。
4. 每窗口数据完整独立（各自的 `EventStreamHandler` + `MessageTimelineStore`），小窗口为摘要视图、交换后自然获得完整视图。
5. 子会话窗口随主 agent 的 `task` 工具调用动态出现，完成后保留。

### 1.3 非目标

- 不做手动派发子任务（本期由主 agent 经 `task` 工具自然产生）。
- 不恢复服务端 Task 事件体系（展示聚合仍在 UI 层）。
- 不做摘要窗口内的孙级 TaskSteps 详情渲染（仅 root 主会话经 `TaskCardAggregator` 聚合）。
- 不做 razor 组件级 UI 自动化测试（无 bUnit 基础设施，布局/交互手工验证）。
- 不引入手动"会议参与者"（窗口列表 = 主会话 + 其全部 SubAgent 子会话）。
- **已知扩展路径（本期不实现，仅预留结构与语义）**：递归枚举子会话（深层级）、每父会话独立聚合（孙级 TaskSteps 展示）、多 root agent 团队与团队间协调、窗口分组/团队布局、agent 间互发消息。

---

## 2. 用户确认的需求约束

| 项 | 决策 |
|----|------|
| 页面定位 | 可交互操作台（主窗口完整可操作） |
| 窗口内容 | 主窗口 = 主会话（URL 指定）；小窗口 = 主会话全部 SubAgent 子会话（动态出现、完成保留） |
| 切换行为 | 主从交换（点击小窗口 → 升为 Full，原 Full 降为 Summary） |
| 渲染粒度 | 数据完整 / 小窗摘要（每窗口独立 timeline，小窗渲染摘要视图） |
| 页面形态 | 独立路由 `/conference/{sessionId}`，Session 页头部加"大屏"入口 |
| 子任务派发 | 本期不做手动派发 |

---

## 3. 架构总览

```
Pages/
├── Session.razor            # 瘦身：会话头部 + 重命名/出站对话框 + <SessionWindow ViewMode="Full">
├── Conference.razor         # 新增：/conference/{sessionId} 大屏页
Components/
├── SessionWindow.razor      # 新增：通用会话窗口（复用自 Session.razor 抽取）
Services/
├── SessionWindowContext.cs  # 新增：窗口对外暴露的状态与操作
├── ConferenceRegistry.cs    # 新增：子会话窗口注册表（circuit 维度 IStreamConsumer）
├── SessionWindowTimelineSync.cs # 新增：纯 C# 事件→时间线分派（可单测）
├── TaskSessionResolver.cs   # 新增：共享 taskId 解析 helper（提取自 TaskCardAggregator）
```

### 3.1 复用路径

- `Session.razor`：头部 UI（标题/badge/选择器/操作按钮/移动端面板）+ 对话框 + `<SessionWindow>`。
- `Conference.razor`：1 个 Full 窗口（active）+ N 个 Summary 窗口（其余）。
- 两者共用同一个 `SessionWindow` 组件与同一套事件/时间线管道。

### 3.2 依赖方向

- `SessionWindow`/`Conference` 仅依赖既有 WebUI 服务与渲染组件（`SessionEventStreamRouter`、`EventStreamHandler`、`MessageTimelineStore`、`MessageList`、`PromptInput`、`TimelineItemView`、`TaskMessageComponent`、`TodoPanel`）。
- 不改动主库/Abstractions/原语层，改动全部位于 `samples/Seeing.Agent.WebUI`。

### 3.3 Router circuit consumer 泛化（前置改造）

现有 `SessionEventStreamRouter` 的 circuit 维度 consumer 仅特判 `TaskCardAggregator`（`GetOrCreateConsumer<T>` 中硬编码类型判断），`ConferenceRegistry` 无法以此接入且无法被 `DetachAllForCircuit` 发现/释放。**前置改造**：将 circuit 维度机制泛化为通用注册维度

```csharp
// 泛化：任意类型经此获取 circuit 维度消费者（同 circuit 跨会话复用、circuit 关闭统一释放）
T GetOrCreateCircuitConsumer<T>(string circuitId) where T : IStreamConsumer;
```

- `GetOrCreateConsumer<T>` 的类型特判收敛到该泛化方法；`TaskCardAggregator` 与 `ConferenceRegistry` 均改走它。
- **实现形态即支持多实例**：内部按 `(circuit, type, key)` 键注册（`key` 默认空串 = 单实例语义）。`key` 参数以**可选参数/重载**形式引入（`GetOrCreateCircuitConsumer<T>(circuitId, string? key = null)`），避免与无 key 签名冲突。`TaskCardAggregator` 用默认 key（保持现有单实例行为）；未来"每父会话一个聚合器"仅需以父会话 Id 为 key 调用，无需再改 Router。
- **清理适配（S5）**：`ReleaseConsumer`/`DetachAllForCircuit` 的键清理遍历从 `(circuit, type)` 升级为 `(circuit, type, key)` 三元键；补"同 circuit 多 key 多实例共存 + circuit 关闭统一释放"回归测试。
- 该改造为多 Agent 大屏的硬前提，亦为后续 agent 团队扩展的前置。

---

## 4. `SessionWindow` 组件抽取（核心）

### 4.1 组件参数

| 参数 | 类型 | 说明 |
|------|------|------|
| `SessionId` | `string` | 窗口绑定的会话 ID |
| `ViewMode` | `enum SessionWindowMode` | `Full`（完整消息 + 输入 + Todo）/ `Summary`（摘要卡片） |
| `Header` | `RenderFragment<SessionWindowContext>?` | 可选，外层自绘头部（Session.razor 使用） |
| `OnClick` | `EventCallback<string>` | Summary 模式点击回调（大屏主从交换） |
| `AggregatorSessionId` | `string?` | **aggregator 聚合绑定目标**（B1）：默认 null = 绑定自身 `SessionId`（Session 页语义）；大屏页始终传 root 主会话 Id（不随 active 切换）。决定"本窗口聚合渲染哪个父会话的 TaskSteps" |

### 4.2 封装范围（从 Session.razor 迁移）

`ViewMode=Full` 时全部激活：

- **会话加载**：`LoadSessionByIdAsync`、`EnsureDefaultAgentSelectionAsync`、`ReconcileIncompleteTasksAsync`、`UpdateBudgetStatus`、`RestoreExecutionFromOverview`
- **事件流绑定**：`EnsureStreamBinding`（handler + `OnHandlerChanged` + `OnAggregatorAssistantChanged`）
- **时间线**：`Reset/Reconcile/SyncTimeline`、`CompleteTurn`、`HandleStreamDeltaThrottled`（节流）
- **提交/取消**：`OnSubmitMessage`（模型校验、权限通道、防重复提交）、`OnCancelExecution`
- **窗口级状态**：自建 `SessionState`（执行态、Todo、budget、压缩进度）+ 自建 `MessageTimelineStore` + 只读横幅 + `PromptInput` + `TodoPanel` + 错误 Alert + 标题订阅
- **aggregator**：按 `AggregatorSessionId` 绑定 `TaskCardAggregator`（`Rebind` 该目标），**接收聚合结果渲染 Task 卡片**——聚合绑定目标由参数决定，与窗口自身会话解耦

`ViewMode=Summary` 时仅保留：会话加载 + 事件流绑定 + 时间线同步，渲染摘要卡片（标题/badge/执行状态/最新消息预览/运行中工具），不激活输入、budget、压缩、Todo、aggregator。

### 4.3 窗口状态实例化（关键改造）

- `SessionState`：`SessionWindow` 内部 `new`（构造函数无参），不再注入 Scoped 单例。
  - **全局 DI 保留** `AddScoped<SessionState>()`（`SecurityPage` 读当前会话 agent、兼容性）；**Session.razor 不再注入**（头部经 `SessionWindowContext` 读写窗口状态）。
  - **写入来源（避免回归）**：`SecurityPage` 依赖 Scoped 实例的"当前会话智能体"展示。由 **Full 窗口在 active 会话变化时同步写 Scoped 实例**（语义 = 当前活跃窗口）。**Session 页与大屏页均遵循该桥接**：大屏页的 Full 窗口随 active 切换同样写入，`SecurityPage` 在任一场景下均显示当前活跃会话。
  - **桥接方式（消除"注入 vs 自建"歧义）**：`SessionWindow` **仅注入全局 Scoped `SessionState` 作为桥接写入目标**（私有字段 `_globalSessionState`），窗口内渲染状态一律用自建实例；active 变化时仅调用 `_globalSessionState.SetSession(currentSession)` 一行，不读写该实例其他字段。Session 页场景下 Full 窗口即唯一窗口，行为与现状一致。
- `MessageTimelineStore`：`SessionWindow` 内部 `new`（构造函数无参）。
  - DI 注册保留，`MessageList` 改为**参数化 store**：`[Parameter] public MessageTimelineStore? Timeline`，参数优先、回退 DI。
- **`SessionState` 实现 `IDisposable`**（S4）：释放 `_executionLock`（`SemaphoreSlim`）；窗口 `Dispose` 时调用（原 Scoped 随 scope 回收无需，窗口 new 后需显式释放）。
- `SessionWindow.Dispose` 释放：退订事件、`Router.DetachConsumer`、`SessionState.CancelExecution()`、`SessionState.Dispose()`。
- **不再由窗口写 `AppState.CurrentSessionId`**（当前 `Session.razor:1281` 行为）：多窗口并存会互相覆盖、侧边栏"回到会话"链接漂移。`AppState.CurrentSessionId` 改由页面级显式管理：
  - **写入机制**：`SessionWindowContext` 新增 `SessionLoaded` 事件（窗口加载完成时触发）；`Session.razor` 订阅该事件，在窗口加载完成后写 `AppState.CurrentSessionId`（Session 页为单窗口，无覆盖问题）。
  - Conference 页不写（顶部条无侧边栏联动需求）。

### 4.4 `SessionWindowContext` 暴露

- 只读状态：`CurrentSession`、`Title`、`IsSubAgentView`、`IsQueued`、`QueuePosition`、`HasActiveExecution`、`IsExecuting`、`SelectedAgent/Model/AcpMode`、`ExecutionStatus`、`CurrentTodoList`
- 操作：`OnAgentChanged`、`OnModelChanged`、`OnAcpModeChanged`、`Rename`、`Branch`、`Clear`、`ReturnToParent`、`GetWorkspace`
- 事件：`StateChanged`（状态变更重渲染）、`SessionLoaded`（窗口加载完成，供页面写 `AppState.CurrentSessionId`）

### 4.5 `Session.razor` 瘦身后职责

- 会话头部（标题、badge、workspace、agent/model 选择器、操作按钮：编辑出站/返回主会话/重命名/分支/新建/清空）
- 移动端溢出面板、重命名对话框、出站绑定对话框
- 导航（首页/创建会话/分支/返回父会话）
- 头部状态经 `<SessionWindow Header="HeaderTemplate">` 的 `SessionWindowContext` 读写

### 4.6 Agent/Model 选项加载归属

- **选项列表加载（`LoadOptionsAsync`：agents/models）归外层页**，`SessionWindow` 不做选项加载。
- `Session.razor` 保留 `LoadOptionsAsync`（头部选择器需要）；选项数据经 `SessionWindowContext` 只读态传入窗口（窗口内仅消费已选值 `SelectedAgent/Model/AcpMode`）。
- `Conference.razor` 顶部条不内置选择器，不做选项加载（YAGNI；若未来需要在顶部切换主会话的 agent，再按"外层页加载 + Context 写入"模式扩展）。
- 避免 `SessionWindow` 与 `Session.razor` 双份加载选项。

---

## 5. 大屏页 `Conference.razor`

### 5.1 布局

- 顶部条：主会话标题、当前 active 窗口标识、返回会话页按钮
- 主体：1 个 Full 窗口（大区）+ N 个 Summary 窗口（小窗网格，AntDesign `Row`/`Col` 或 `Grid`）
- **每个窗口用 `@key=sessionId`**：Full/Summary 切换只变 `ViewMode` 参数、不重建组件，handler/timeline store 实例保留（避免切换丢失状态/重复订阅）
- **Conference 页不渲染窗口的 `Header` 参数**（顶部条独立实现）；`SessionWindowContext` 的头部操作（`Rename`/`Branch`/`Clear`/`ReturnToParent` 等）仅供 Session.razor 头部使用

### 5.2 主从交换

```
点击 Summary 窗口 → Conference._activeSessionId = 该 sessionId → 渲染重排
```

- 原 Full 窗口降为 Summary（保留 handler/timeline，仅视图变化）
- 新 Full 窗口激活输入区、budget、压缩、Todo 订阅与**聚合结果渲染**（aggregator 绑定目标由 `AggregatorSessionId` 决定——大屏页恒为 root 主会话，见 5.3）
- active 为子会话时 Full 窗口自动只读（`Kind==SubAgent`），摘要区可见主会话窗口，点击切回

### 5.3 aggregator 归属

- `TaskCardAggregator` 为 circuit 维度单实例，**绑定目标由 `AggregatorSessionId` 参数决定**：`Session.razor` 不传（默认自身会话）；`Conference.razor` 所有窗口始终传 **root 主会话 Id**（聚合绑定不随 active 切换，`Rebind(rootId)` 仅随主会话路由变化）。
- 理由：Summary 窗口不需要 TaskSteps 聚合；子会话窗口只读（其内部再派发的孙级 TaskSteps 本期不在摘要/只读窗口展示）。避免 active 频繁切换触发 `Rebind` 反复清态重扫（`TaskCardAggregator.Rebind` 会 Detach 旧父 + 清空 `_tasks`）。
- 扩展路径：未来需在 Summary 窗口展示孙级进度时，改为"每父会话一个聚合实例"（第 3.3 节泛化机制支持），本期不启用。

---

## 6. 数据流

### 6.1 每窗口统一管道

```
Router(每会话单 loop) → EventStreamHandler(该会话) → SessionWindowTimelineSync → MessageTimelineStore
                                                    └→ SessionWindowContext.StateChanged → 重渲染
```

- 窗口经 `Router.GetOrCreateConsumer<EventStreamHandler>(sessionId, circuitId)` 获取 handler，`AttachConsumer` 订阅
- `ViewMode=Full` 渲染完整 `MessageList`；`ViewMode=Summary` 从同一 timeline/状态渲染摘要卡片
- **`SessionWindowTimelineSync`（纯 C# 类）**：承接事件 → **时间线/状态**分派，保持"纯 C# 可单测"，覆盖：`StreamDelta` 节流、`ToolCallEvent` 同步、`ExecutionComplete`/`LoopComplete`/`LoopCancelled`/`Error` 完成 turn + reconcile、`CompactionStarted/Delta/Completed/Failed` 进度与全量重建、`SessionUpdatedEvent` 全量重建、`SessionTitleChangedEvent` 标题更新、会话归属过滤（父会话事件不污染子窗口）。
- **标题事件落点（S6）**：`SessionTitleChangedEvent` 在纯类中**只更新标题状态属性**（`SessionWindowContext.Title`），渲染副作用（`_renderedTitle` 去重、`StateHasChanged`）归窗口组件，纯类不碰渲染。
- **分派边界**：**导航类副作用事件留在 razor 层桥接**（`NavigateEvent`、`CommandResultEvent.NavigationTarget` 导航；`CommandResultEvent.NeedsRefresh` 触发时间线刷新），由窗口组件持有 `NavigationManager`/`IMessageService` 处理，不进入纯类——纯类依赖仅限 timeline/状态对象。

### 6.2 子会话窗口注册表 `ConferenceRegistry`

新增（Scoped，`IStreamConsumer`，**circuit 维度**，经第 3.3 节泛化的 `GetOrCreateCircuitConsumer<ConferenceRegistry>(circuitId)` 获取与登记）：

- **初始枚举**：`SessionManager.ListChildrenAsync(mainId, SubAgent)` + `LoadChildrenFromStorageAsync(mainId)`（冷缓存兜底）
- **动态新增**：订阅父会话流，识别 `ToolCallEvent`（`toolName=="task"`）→ `TaskSessionResolver` 解析 taskId → 触发 `WindowsChanged` → Conference 页新增 Summary 窗口
- **完成保留**：子会话终态事件不影响窗口集合
- **`Rebind(mainId)`**：同 circuit 内主会话路由变化（`/conference/{id}` 参数切换）时重新枚举并重建窗口集合（参照 `TaskCardAggregator.Rebind` 模式，摘除旧父订阅、清空集合、挂新父）
- **异步防护**：`OnEvent` 内的 fire-and-forget 任务自身 try/catch（Router 的 `SafeInvoke` 只吞 OnEvent 同步异常），参照 `TaskCardAggregator.AttachFromToolCallAsync` 防护模式
- **生命周期**：经 circuit 维度登记后，随 circuit 关闭由 `Router.DetachAllForCircuit` 统一释放

**窗口集合数据模型（树化，成本 ~0）**：`ConferenceRegistry.Windows` 暴露轻量树节点 `{ SessionId, ParentId, Kind, Title }`。本期窗口集合 = 单 root 主会话 + 一级子会话（根数=1、深度=1 的树）；数据结构与语义为后续多 root 团队、递归子层留路，不改变本期实现（仍一级 `ListChildrenAsync` 查询）。
**`ExecutionStatus` 来源（B2）**：树节点**不含** `ExecutionStatus`——registry 只订阅父流、无子流状态刷新机制。各 Summary 窗口的执行状态由**窗口自身的 handler**（每窗口一个实例，经 `RestoreExecutionFromOverview` 初始化 + 事件流实时更新）提供，经 `SessionWindowContext.ExecutionStatus` 暴露供摘要卡片渲染。

### 6.3 共享 helper `TaskSessionResolver`

从 `TaskCardAggregator.ResolveTaskIdAsync` 提取（低风险小重构），registry 与 aggregator 共用：

- `origin_tool_call_id` 精确匹配（内存缓存枚举 → 磁盘冷缓存兜底）
- 续跑分支不覆盖已有关联
- **注册方式（S2）**：`TaskSessionResolver` 为 Scoped 服务，注入 `ISessionManager`（与 `TaskCardAggregator` 一致，`Program.cs` `AddScoped<TaskSessionResolver>()`）；`TaskCardAggregator` 与 `ConferenceRegistry` 经 DI 注入使用

---

## 7. 错误处理与生命周期

| 场景 | 处理 |
|------|------|
| 窗口 Dispose | 退订事件 + `Router.DetachConsumer` + `SessionState.CancelExecution()` + `SessionState.Dispose()`；**沿用 2026-08-27 原则：不取消后端执行、不标记未完成任务** |
| Circuit 关闭 | 无新代码：`SeeingCircuitHandler` → `Router.DetachAllForCircuit` 统一释放 |
| 刷新/切换恢复 | 每窗口 `RestoreExecutionFromOverview` + handler 重建绑定；Conference 刷新后 `ConferenceRegistry` 重新枚举重建窗口集合 |
| 渲染异常 | **每窗口外包 `ErrorBoundary`**，单个窗口降级占位不拖垮大屏 |
| 会话缺失/被删除 | 窗口显示"找不到会话"占位；主会话缺失时整体错误态 + 返回会话列表；主从交换目标无效则跳过 |
| 流结束 `OnStreamEnd` | `handler.ClearCache()`；窗口保留已渲染内容，再次提交经 I1 逻辑自动重启订阅 |
| 权限 | Full 窗口提交走既有 `IPermissionChannel`（`BlazorPermissionChannel` + 全局 `PermissionHost`） |
| `MessageList` 多实例边界（S1） | **本期 Conference 页至多 1 个 Full 窗口 → `MessageList` 恒为单实例**，无实际冲突。`MessageList` 容器/锚点 Id 参数化（`[Parameter] public string? IdPrefix`）仅为未来多 Full 留位；Summary 模式用轻量摘要渲染、不渲染 `MessageList`。**已知硬约束**：`app.js` 的 `messageListScrollManager` 为模块级全局单例，`ScrollToBottomButton` 硬编码 `id="message-list-scroll-to-bottom"`——**真正的多 Full 并存需同步改造 JS 滚动管理器与硬编码 Id，本期不实现**，文档如实标注 |

---

## 8. 测试

新增（沿用 xUnit + Moq，Services 层测试，无 razor 测试）：

1. **`ConferenceRegistryTests`**：初始枚举、动态识别 + 去重、冷缓存兜底、完成保留、`Rebind` 换主会话重枚举
2. **`SessionWindowTimelineSyncTests`**：delta 节流、工具状态同步、终态完成 turn + reconcile、压缩进度/重建、`SessionTitleChanged`/`SessionUpdated` 处理、会话归属过滤（导航类事件不在纯类职责）
3. **`TaskSessionResolverTests`**：`origin_tool_call_id` 精确匹配、磁盘兜底、续跑不覆盖

补充/回归：

- `MessageTimelineStoreTests`：多实例互不干扰（验证每窗口实例化改造）
- `SessionEventStreamRouterTests`：同 circuit 多消费者并发订阅不同会话互不干扰；**泛化 `GetOrCreateCircuitConsumer<T>` 后 TaskCardAggregator/ConferenceRegistry 接入与 circuit 关闭统一释放；同 circuit 多 key 多实例共存 + `ReleaseConsumer`/`DetachAllForCircuit` 三元键清理（S5）**
- `TaskCardAggregatorTests`：提取共享 helper 后行为不变（回归保护）；`SessionState` 实现 `IDisposable` 后释放路径（S4）
- `SessionWindow` 聚合绑定：`AggregatorSessionId` 参数决定 `Rebind` 目标（Session 页=自身 / Conference=root，B1）——经组件参数语义覆盖，razor 层手工验证

不覆盖（明确边界）：

- razor 布局/点击交换交互（手工验证）
- `MessageList` 参数化 store 的组件行为（编译 + 大屏手工联调验证）
- `Session.razor` 约 60 处 `SessionState` 读写迁移至 `SessionWindowContext` 的行为等价性——以逐项对照表约束 + 手工回归验证

---

## 9. 影响面与顺序建议

改动全部位于 `samples/Seeing.Agent.WebUI`（+ `tests/Seeing.Agent.WebUI.Tests`）：

1. 提取 `TaskSessionResolver`（含 `TaskCardAggregator` 改造 + 回归测试）
2. Router circuit consumer 泛化（第 3.3 节，含 `SessionEventStreamRouter` 改造 + 测试）
   - **现有调用点同步改造**：`Session.razor:1560` 的 `GetOrCreateConsumer<TaskCardAggregator>(sessionId, circuitId)` 改为 `GetOrCreateCircuitConsumer<TaskCardAggregator>(circuitId)`
3. 新增 `SessionWindowTimelineSync`（纯类 + 测试）
4. `MessageList` 参数化 store
5. 抽取 `SessionWindow` + `SessionWindowContext`，重构 `Session.razor`（保持行为不变，编译 + 手工验证）
6. 新增 `ConferenceRegistry`（+ 测试）
7. 新增 `Conference.razor` 大屏页 + Session 页"大屏"入口

---

## 10. 可扩展性（面向 agent 团队）

后续可能从"主会话 + 其子会话"单棵树扩展为多 root agent 团队、更深层级与团队协调。本设计的支撑与边界：

### 10.1 本设计已为演进铺垫的决策

| 决策 | 演进价值 |
|------|----------|
| 每窗口独立 `SessionState`/`MessageTimelineStore` | 任意数量会话窗口并存的根基；多 root 团队 = 多棵树的窗口集合，窗口单元不变 |
| `SessionWindow` + `ViewMode`（Full/Summary） | 窗口角色通用化，"摘要/完整"切换可复用于团队分组视图 |
| Router 多消费者广播 + 引用快照去重 | 一会话流被多消费者（渲染/聚合/发现）监听，天然支持跨会话订阅的团队协调 |
| `TaskSessionResolver` 解耦 | 子会话发现逻辑可复用，未来递归/批量发现只需扩展该 helper |
| circuit consumer 泛化（第 3.3 节） | 任意类型、可多实例的 circuit 维度注册，为"每父会话一个聚合器"与多树场景留位 |
| 窗口集合数据模型树化（第 6.2 节） | 单 root 是根数=1 的树特例，DTO 层面为递归子层留路；**多 root 的真正成本在 registry 订阅生命周期**（见 10.3） |

### 10.2 已标注的扩展路径（本期不实现）

递归枚举子会话（深层级）、每父会话独立聚合（孙级 TaskSteps 展示）、多 root agent 团队与团队间协调、窗口分组/团队布局、agent 间互发消息。均在第 1.3 节列为非目标，防止实现者误以为当前结构已覆盖。

### 10.3 明确的远期边界

- **Aggregator 单槽 Rebind**：本期大屏页 aggregator 经 `AggregatorSessionId` 固定绑定 root 主会话；未来多树并行聚合需升级为"每父会话一个聚合实例"（泛化 `(circuit, type, key)` 机制已支持存储，消费端装配——窗口→父聚合器引用映射、聚合器 Rebind 与窗口生命周期关联——仍需另行设计）。
- **多 root agent 团队**：`ConferenceRegistry.Rebind(mainId)` 为**单父单槽**模式；到多 root 需重构为"多父订阅集合 + 每父一个 resolver/枚举实例"，属订阅生命周期管理的重构，非简单枚举扩展。本期树化仅解决 DTO 层。
- **团队间协调**（agent 互发消息/共享上下文）完全超出本期范围。事件流为服务端→UI **单向广播**；反向"UI/agent→另一 agent"提交虽经 `ChatOrchestrator.SubmitAsync`（任意 sessionId）技术可行，但涉及身份/权限/会话内往返语义，不应视为"不会成为障碍"。

---

## 11. 子 agent 架构审查落实清单

子 agent 审查结论：**有条件通过**。以下逐条落实，全部已修订入本文档（无遗留未决项）：

### 必改项（4 项，全部落实）

| # | 审查意见 | 落实位置 |
|---|----------|----------|
| M1 | `ConferenceRegistry` 的 Router 接入/释放机制缺失（`DetachAllForCircuit` 无法发现） | 第 3.3 节：Router circuit consumer 泛化 `GetOrCreateCircuitConsumer<T>`；第 6.2 节：registry 经此登记、随 circuit 关闭统一释放 |
| M2 | Scoped `SessionState` 保留后无写入来源 → `SecurityPage` 读空 agent 回归 | 第 4.3 节：Full 窗口在 active 变化时经桥接字段 `_globalSessionState.SetSession(currentSession)` 写入全局 Scoped 实例 |
| M3 | `SessionWindowTimelineSync` 分派边界未定死（导航副作用） | 第 6.1 节：导航类事件留 razor 层，纯类仅时间线/状态分派；完整事件覆盖清单列出 |
| M4 | Aggregator 归属语义歧义（spec 5.3 vs 1.3） | 第 5.3 节：大屏页 aggregator 始终绑定 root 主会话，不随 active 切换 |

### 建议项（9 项，全部落实或显式标注）

| # | 审查意见 | 落实位置 |
|---|----------|----------|
| S1 | Router circuit consumer 泛化顺带支持任意类型多实例 | 第 3.3 节：实现形态即 `(circuit, type, key)` 多实例键 |
| S2 | 窗口集合数据模型树化（低成本） | 第 6.2 节：`Windows` 暴露 `{ SessionId, ParentId, Kind, ... }` 树节点 |
| S3 | `AppState.CurrentSessionId` 多窗口写冲突 | 第 4.3 节：窗口不再写，改由页面级显式管理 |
| S4 | 同 circuit 换主会话（路由参数变化）未覆盖 | 第 6.2 节：`ConferenceRegistry.Rebind(mainId)` |
| S5 | `MessageList` 多实例 DOM Id 冲突 | 第 7 节：Id 参数化（`IdPrefix`）；Summary 不渲染 `MessageList` |
| S6 | `SessionWindowContext` 补 `QueuePosition` | 第 4.4 节：已加入只读状态 |
| S7 | `LoadOptionsAsync` 归属未明确（双份加载风险） | 第 4.6 节：选项加载归外层页，`SessionWindow` 不做 |
| S8 | `ConferenceRegistry.OnEvent` 异步任务异常逃逸 | 第 6.2 节：fire-and-forget 任务自身 try/catch |
| S9 | 三处已知扩展路径显式标注 | 第 1.3 节非目标 + 第 10 节可扩展性章节 |

### 代码一致性核对

子 agent 对 11 项现有组件/API 引用逐一核对，**全部真实存在、无引用错误**（`SessionEventStreamRouter`/`EventStreamHandler`/`TaskCardAggregator`/`MessageTimelineStore`/`SessionState`/`CircuitContext`/`SeeingCircuitHandler`/`MessageList`/`PromptInput`/`ISessionManager.ListChildrenAsync`/`LoadChildrenFromStorageAsync`/`SessionMetadataKeys.OriginToolCallId` 等）。两处非错误说明：`ErrorBoundary` 为 .NET 10 内置组件（项目无使用先例，仅捕获渲染期异常，已注明）；`EnsureSessionRef` 无此方法（对应 `EventStreamHandler.EnsureCurrentSessionRef`，文档未引用）。

---

### 独立终审（第二轮，2026-08-28，全新 agent 复审）

第二轮由全新 agent（无前序上下文）独立终审，结论：**有条件通过**。必改项与建议项已全部落实：

#### 必改项（2 项，全部落实）

| # | 审查意见 | 落实位置 |
|---|----------|----------|
| B1 | aggregator 归属矛盾（4.2/5.2"Full 激活聚合" vs 5.3"始终绑定 root"） | 第 4.1 节新增 `AggregatorSessionId` 参数（默认自身、大屏传 root）；第 4.2/5.2/5.3 改写为一致语义（"Full 窗口接收聚合结果渲染，绑定目标由参数决定"） |
| B2 | `ConferenceRegistry.Windows.ExecutionStatus` 无刷新来源 | 第 6.2 节：树节点**移除** `ExecutionStatus`；状态由各 Summary 窗口自身 handler 经 `SessionWindowContext.ExecutionStatus` 提供 |

#### 建议项（7 项，全部落实）

| # | 审查意见 | 落实位置 |
|---|----------|----------|
| S1 | `MessageList` 多实例边界澄清（JS 滚动管理器为全局单例） | 第 7 节：明示本期 MessageList 恒单实例；Id 参数化仅为留位；多 Full 需同步改造 `app.js` 全局单例与 `ScrollToBottomButton` 硬编码 Id（本期不实现） |
| S2 | `TaskSessionResolver` 注册方式未定 | 第 6.3 节：Scoped 服务、注入 `ISessionManager`，`AddScoped<TaskSessionResolver>()` |
| S3 | Conference 页 `SecurityPage` 读空 agent 回归 | 第 4.3 节：桥接覆盖 Session 页与大屏页（Full 窗口随 active 变化写入全局 Scoped 实例） |
| S4 | 窗口自建 `SessionState` 后 `_executionLock` 泄漏 | 第 4.3 节：`SessionState` 实现 `IDisposable`（释放 `_executionLock`），窗口 Dispose 调用 |
| S5 | Router 泛化需同步适配三元键清理 | 第 3.3 节：`ReleaseConsumer`/`DetachAllForCircuit` 升级 `(circuit, type, key)` 遍历 + 回归测试 |
| S6 | `SessionTitleChangedEvent` 落点未指明 | 第 6.1 节：纯类只更新标题状态属性，渲染副作用归窗口组件 |
| S7 | 第 10 节措辞过于乐观 | 第 10.3 节：多 root 需重构 registry 为多父订阅集合；agent 互发消息涉及反向提交语义，如实标注 |

#### 独立终审确认的隐藏障碍（已如实标注）

- `app.js` 的 `messageListScrollManager` 为模块级全局单例 + `ScrollToBottomButton` 硬编码 Id → **真正的多 Full 并存本期不可行**（第 7 节已澄清本期恒单实例，不触发）。
- `Session.razor` 约 60 处直接读写 `SessionState` 全部迁往 `SessionWindowContext`，为本次最大重构面——实施时以逐项对照表约束"保持行为不变"。
