# WebUI 聊天渲染性能 Release Notes

**日期：** 2026-11-04
**设计规格：** `docs/superpowers/plans/2026-11-03-webui-chat-render-performance.md`
**实施计划：** 同上
**实测记录：** `docs/superpowers/plans/2026-11-03-webui-chat-render-performance-results.md`

## 背景

长会话流式输出与多 Agent 会话切换存在明显卡顿。代码审查定位到 8 处瓶颈（D-1..D-8，均附文件行号证据），其中两点推翻了初始假设：

1. **「一个 delta 触发 3 次全树重渲染」不成立** —— 三处 `StateHasChanged` 都在同一 Dispatcher 同步栈上，Blazor 的 `_hasPendingQueuedRender` 会合并为一次渲染通道。真问题是**这一次通道渲染的子树过大**。
2. **切换卡顿的主因不是数据加载，而是整窗销毁重建 + 侧栏 Summary 承担 Full 职责** —— 每次切换会新建 `EventStreamHandler`，订阅即重放 ≤100 条环形缓冲事件；且因侧栏也用 `@key`，一次切换等于两个会话各跑一遍完整 `OnInitializedAsync`。

## 变更摘要

### 加载边界（Summary 窗口轻量化）

- `SessionWindow` 的 `OnInitializedAsync` 拆为 Full / Summary 两条路径，抽出 `WireContextCallbacks()` 共用。
- 新增 `LoadSummaryOnlyAsync()`：只取会话快照 + 计算摘要投影，不建时间线、不跑预算估算、不做 `ReconcileIncompleteTasks`、不加载命令列表。
- 新增 `SessionSummaryProjector`：把「全量消息扫描」从渲染期移到状态变更时，一次算好 `Preview`/`ActionLine`/`SourceLine`/`StatusBarClass`。
- `OnHandlerChanged` 加 ViewMode 白名单：Summary 只放行 `ExecutionStarted`/`ExecutionComplete`/`LoopComplete`/`SessionTitleChanged` 四类事件，其余在 `InvokeAsync` **之前**丢弃（回放期事件不进 Dispatcher 队列）。时间线管道（`_sync.ProcessEvent`）对 Summary 不执行。

### 窗口生命周期（切换不再整窗重建）

- `Pages/Session.razor` 主窗移除 `@key`，切换 active 时复用窗口实例，走 `OnParametersSetAsync` 切换分支。
- 抽出 `ApplySessionAsync()`；`ReconcileIncompleteTasksAsync` 移到 `ResetTimelineFromSession` **之前**，并去掉其内部的二次 Reset。
- 切换分支补齐 4 项（原先只在本窗首次挂载时执行）：`ResetLocalExecutionState()`、`Context.SessionId`、`SubscribeToBudgetUpdates()`、`LoadCommandsFromBackend()`。
- 新增 `SessionState.ResetExecutionState()`：清执行态 + 取消并释放旧 CTS + 释放执行锁。避免「空闲会话显示执行中」且「点取消停错会话」。

### 渲染粒度（Loop 分段缓存）

- `TimelineItem` 新增 `StructureRevision` + `TouchStructure()`；`MessageViewModel` 新增 `Revision` + `Touch()`。
- `ContentBlockBuilder.BuildLoopSegments()`：按 Step 分段，块 Id 以消息 Id 为前缀（重复 Id 追加 `#{n}` 消歧），保证 Loop 内唯一且跨次构建稳定。Id 前缀分配逻辑抽为 `AllocateIdPrefix()`，**生产渲染路径（`LoopGroupItem`）与 `BuildLoopSegments` 共用同一实现**，两条路径的块 Id 口径一致。
- `LoopGroupItem` 改为按消息分段缓存：结构版本不变时只重建内容版本变化的那条消息。Id 前缀在结构变化时按完整消息集合统一分配（跨消息共享计数表），单条重建时沿用上次分配的前缀以保持块 Id 稳定。
- `IMessageRenderPipeline` 新增 `RenderBlock`/`GetLoopContainerClass`/`GetLoopStatus`/`BuildLoopTags`/`BuildLoopMetaItems`；`RenderLoop` 改用 `BuildLoopSegments`，每段用自己消息的 `RenderContext`（修复了原先整个 Loop 共用首条消息导致 Markdown 缓存键撞车的既有缺陷）。
- 删除 `BuildFromLoopMessages` / `GetAdditionalKey`。

### 流式渲染策略

- 新增 `RenderMarkdownPolicy.ShouldUseMarkdown()`：流式中的文本/思考/子代理块跳过 Markdown，按纯文本渲染。
- `message-item.css` 新增 `.message-content--streaming { white-space: pre-wrap; ... }` —— 不做这步流式文本会塌成一整段。
- 完成后 `block.IsStreaming` 转 false，自动走回 Markdown 分支。

### 热路径数据结构

- `SessionData.Messages` 改为**写时失效的锁内快照**（同版本返回同一实例）；新增 `TryGetMessage()` 按 Id 查（锁内惰性建索引）。9 处写点全部挂失效。
- `MessageTimelineStore` 加 3 个索引：`_turnByKey`（轮次 key → 条目）、`_itemByMessageId`（消息 Id → 条目，null 表示排队）、`_pendingOrphanKeys`（待认领的 `single-*`）。去掉每 delta 的全表扫描；`RenumberLoopIndexes` 移出吸收循环。
- 新增 `RenderCoalescer`：33ms 合并窗口，封顶高频 delta 的渲染速率。**注意**：`MessageList` 保持自渲染（未启用 `SelfRender=false`，因为父级传给它的参数在流式期全等，Blazor 可能跳过 `SetParametersAsync` 导致不推进），因此合并器主要封顶 `SessionWindow` 整树；最贵的 `MessageList → TimelineItemView` 子树仍按 delta 频率自渲染，其成本由 `TimelineItemView.ShouldRender` + Loop 分段缓存压低。
- 新增 `ScrollInteropThrottle` + `ScrollInteropBudget`：40ms 限流滚动 JS 往返。**贴底判定与懒加载判定共享一次窗口分配**（`ScrollInteropBudget.Allocate`），避免先执行的一方消费窗口导致另一方永久饿死；配 `_scrollDirty` 补偿避免贴底落后。
- 新增 `SerialGate`：`TaskCardAggregator` 的 `SemaphoreSlim._writeLock` 改为 Monitor 实现（可重入）。
- 聚合器回调经 `RenderCoalescer` 统一驱动渲染。

### 关系模型预计算

- `RelationTreeModel` 新增 `BarVisible`/`BarOverflow`/`Rows`（构建期算好）；`SidebarSection` 新增 `PendingPermissionTotal`。去掉 render 期的 O(M²) LINQ。

## 已知边界

1. **`RenderCoalescer` 是每窗口独立**，多 agent 场景下 N 个 Full 窗口各自 33ms 一次；circuit 级统一渲染调度未做。
2. **`TryLoadMoreAsync` 与贴底判定共享同一个限流窗口分配**（`ScrollInteropBudget.Allocate`）：两者都在同一次渲染通道内执行且只按窗口放行一次，因此「本次渲染贴底 + 下次渲染懒加载」的交替节奏下，极端快速滚动时懒加载触发可能延后一个窗口（≤40ms）。不会永久饿死（有专门的守门测试）。
3. **`SerialGate` 用 `Monitor`，可重入**（`SemaphoreSlim` 不可重入）。这是有意的语义变更。另外 `Monitor` 同样阻塞线程 —— 真实收益是去掉异步等待状态机与 `Task` 分配，不是「不阻塞」。
4. **`SessionData.Messages` 是只读视图但运行时类型仍是 `List`**。调用方 Cast 后就地修改会污染会话真相且不失效缓存。契约靠 XML 注释 + `GetActiveMessages` 的独立副本语义共同维护。
5. **`TryGetMessage` 不感知元素 Id 的后续改写**。契约是「Id 由写者在入列前确定，索引建立后不得再改」；缺 Id 的消息不入索引、也不进时间线。UI 层已无任何 Id 写入点（原先两处归一化已删除）。
6. **Summary 窗口仍保留事件流订阅**（执行态事件是卡片状态唯一可靠来源）。收益来自掐掉时间线管道，而非取消订阅 —— `SessionEventStreamRouter` 已按 `sessionId` 共享 loop，不同子会话的 loop 任何方案都省不掉。
7. **`SyncContextState()` 保持即时渲染**，不走合并器。它承载 AntDesign Select 受控值同步，延迟会放大回滚窗口。
8. **`GiveSummaryMode` 的 `ExecutionStarted` 分支会调 `SyncContextState()`，其中会 `RebuildSummarySnapshot()`**，加载 Summary 时有一次重复计算（O(M)，可忽略）。

## 契约变更清单

| 类型 | 变更 | 影响 |
|---|---|---|
| `SessionData.Messages` | 每次返回新 `List` → 同版本返回同一实例 | **行为变更**：调用方不得 Cast 后修改 |
| `SessionData.TryGetMessage` | 新增 | 无破坏性影响 |
| `TimelineItem.StructureRevision` / `TouchStructure()` | 新增 | 无破坏性影响 |
| `MessageViewModel.Revision` / `Touch()` | 新增 | 无破坏性影响 |
| `SidebarSection` 第 4 个位置参数 | 新增 `PendingPermissionTotal`（带默认值） | 位置参数构造仍兼容 |
| `RelationTreeModel.BuildRows()` | 方法保留但改为返回预计算结果 | 语义不变 |
| `ContentBlockBuilder.BuildFromLoopMessages` | **删除** | 已被 `BuildLoopSegments` 取代 |
| `IMessageRenderPipeline` | 新增 5 个成员 | 自定义实现需补齐 |

## 测试

新增 10 个测试类，共 40 个用例：

| 测试类 | 用例数 | 覆盖 |
|---|---|---|
| `SessionSummaryProjectorTests` | 9 | 摘要投影的预览/动作行/状态条/来源行 |
| `SessionSwitchStateTests` | 2 | 切换时执行态与 CTS 重置 |
| `StreamingRenderTests` | 6 | 流式/完成态的 Markdown 策略 |
| `ContentBlockBuilderTests` | 7 | 分段 Id 唯一性、稳定性、流式标记 |
| `TimelineRevisionTests` | 6 | 版本语义 + `CompleteTurn` 的 VM.Touch 守门 + 仅思考态变化也 Touch + 工具耗时变化也 Touch |
| `MessageTimelineStoreIndexTests` | 8 | 索引化行为锁（同步/认领/幂等/折叠） |
| `RenderCoalescerTests` | 8 | 合并/尾随/异常不外溢/释放语义 |
| `SerialGateTests` | 3 | 互斥/异常后可复用/可重入 |
| `ScrollInteropThrottleTests` | 4 | 限流窗口/重置 |
| `ScrollInteropBudgetTests` | 6 | 贴底与懒加载共享窗口的分配（含「不饿死」守门） |
| `SessionMessageSnapshotTests` | 9 | 快照复用/失效/Id 索引/副本独立性 |
| `SessionRelationModelCacheTests` | 6 | 预计算稳定性与语义等价 |

## 最后审查修复（2026-11-04）

一次全分支审查（fresh reviewer）发现并修复：

- **Critical** 懒加载被限流器饿死 —— `MessageList` 里贴底判定先消费窗口，`TryLoadMoreAsync` 必然被拒，长会话永久无法加载更早消息。抽 `ScrollInteropBudget.Allocate` 让两者共享一次分配，且只在实际需要时消费窗口。守门测试 `ScrollInteropBudgetTests` 6 个用例。
- **Critical** 切换会话时输入草稿与附件跨会话串台 —— `PromptInput` 新增 `SessionKey` 参数，变化时清空 `InputText`/`Attachments`（附件按旧会话目录上传，混入新会话属数据串台）。
- **Important** `IsReasoningComplete` 变化未 `Touch()` → 思考块可能永久停留纯文本。`CompleteTurn` 改为「任何可见字段变化都 Touch」。守门测试 `CompleteTurn_WhenOnlyReasoningCompletenessChanges_ShouldBumpRevision`。
- **Important** `ToolsVisiblyChanged` 漏比对 `Description`/`DurationMs` → 工具卡耗时永久陈旧。补齐比较。守门测试 `SyncAssistantMessage_WhenOnlyToolDurationChanges_ShouldBumpRevision`。
- **Important** 重复 `MessageId` 的 Id 消歧只存在于无调用者的 `BuildLoopSegments`，生产路径 `LoopGroupItem` 会产出重复块 Id。抽 `ContentBlockBuilder.AllocateIdPrefix` 供两条路径共用。
- **Important** 切换会话残留上一会话的错误横幅 / 模型无效红框 / 预算 —— `ResetLocalExecutionState` 一并重置。
- **架构债清理** UI 层不再回写执行期消息字段（`docs/architecture/04` §5.2）：
  - `MessageTimelineStore` 删除两处 `msg.Id = Guid.NewGuid()`（`ResetFromSession` / `ReconcileAppendFromSession`），改为**缺 Id 即跳过**；
  - `MessageViewModelFactory` 删除 `msg.Id ?? Guid.NewGuid()` 的随机兜底，改为 `?? string.Empty`；
  - 理由：Id 由上层写者保证（`ChatEventTracker.EnsureAssistantMessage` 赋 `{loopId}_step{n}`，`SessionMessage` 的 8 个工厂方法均赋 `NewId()`），投影层生成随机 Id 会让同一消息在不同加载路径得到不同 `@key`，导致时间线整段重建。
  - 守门测试：`ResetFromSession_WithEmptyIdMessage_ShouldNotMutateSessionMessage`、`ReconcileAppendFromSession_WithEmptyIdMessage_ShouldNotMutateSessionMessage` RED→GREEN。
- **日志规范** `SessionWindow` 的 3 处 `Debug.WriteLine` 改为 `ILogger`（`Logger.LogDebug` / `LogWarning`）。原因：`Debug.WriteLine` 带 `[Conditional("DEBUG")]`，Release 构建下整条语句被编译掉，等同静默吞异常，违反 `04-development-standards.md` 反模式。
- **并发安全** `SessionState` 的执行态字段收敛到 `_tokenGate` 互斥门；执行锁释放改幂等（`ReleaseExecutionLock` 兜 `SemaphoreFullException` / `ObjectDisposedException`）。
  - 触发条件：`CancelExecution`（UI 线程）、`CompleteExecution`（事件流后台线程）、`ResetExecutionState`（参数更新）三路并发。
  - 修复前实测两个缺陷：① `_cancellationTokenSource` 的「判空 → 调用」TOCTOU → `NullReferenceException`（`CancelExecution`）；② `CurrentCount == 0` 与 `Release()` 之间 TOCTOU → `SemaphoreFullException` 打崩 circuit。
  - 守门测试：`ReleasePaths_HighContention_ShouldNotThrowSemaphoreFull`（300 轮 × 3 线程用 `Barrier` 同步起跑），修复前稳定 FAIL，修复后 PASS。
- **小清理** 删除已成死代码的 `RelationTreeModel.BuildRows()`；`RenderLoop` 补注释说明「生产路径已改 `RenderBlock` 分段，本方法保留为接口契约」；`Conference.razor` 同步改用 `PendingPermissionTotal`；Summary 事件白名单移除无处理逻辑的 `LoopCompleteEvent`。
