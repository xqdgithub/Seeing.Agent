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
- **不变量**：`IsStreaming` 的来源必须是「这条消息**本身**还在流」，而不是「当前会话有别的东西在流」。`SessionWindowTimelineSync.SyncAssistant(isComplete:false)` 只同步属于当前回合的消息（`LoopId` 比对）；`ExecutionStartedEvent` 分支**不同步**助手消息——此刻本回合尚无 assistant 消息，指针仍指上一轮末条，同步它会把已完成回答降级为流式、Markdown 退回纯文本（回归见下方 2026-11-05）。

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
8. **`MessageList` 的挂载上限（`MaxMounted = 20`）只在贴底时生效**：用户滚到中间位置时窗口不再滑动，若持续有消息进来，DOM 挂载数会超过 20，直到用户滚回底部。属既有行为，本次未改动。
9. **全量构建（`ResetFromSession`）仍处理全部消息**，渲染层只画窗口内的 ≤20 条。触发点仅 4 处（会话切换、`SessionUpdatedEvent`【无发布者】、命令刷新、压缩完成/失败），**流式输出期间不触发**。单次耗时与消息数几乎无关（12 条 33ms vs 67 条 1ms —— 差异来自 GC 与 Dispatcher 抢占，非对象构建），因此**不是**热路径。
10. **切换会话的 Painted 成本下限由「必须重建的条目数」决定**：换会话后消息 Id 全变，`@key` 失效，20 个条目必然全部新建。`content-visibility` 对首次渲染无效，故该场景只能靠压低挂载数控制。若需进一步改善，方向是骨架屏（已做）或虚拟滚动（未做）。
11. **`content-visibility: auto` 只在流式输出时生效**：它依赖「已渲染过一次」记住尺寸；切换会话时全部条目首次渲染，不享受此优化。若滚动时出现短暂空白，调大 `contain-intrinsic-size` 或改用 `contain: layout style paint`。

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
- **消除重复全表遍历** 删除 `CompactionCompleted` / `CompactionFailed` / `CommandResultEvent.NeedsRefresh` 三个分支里紧随 `ResetFromSession()` 的 `ReconcileAppendFromSession()`。`ResetFromSession` 是全量重建，其后的增量对账语义上是空操作（所有消息已在时间线内），只会额外付一次 `ToList()` + 摘要倒扫 + 全表遍历。行为锁测试 `SessionWindowTimelineSyncTests`（5 个用例）证明「一次 Reset 后时间线已完整」，删除前后均绿。
- **切换卡顿实测优化（2026-11-04 追加）** 基于浏览器 Performance 实测（Paint 1816ms / Render 1578ms / 服务端切换仅 0~1ms），定位为**浏览器绘制成本**而非服务端计算：
  - `MaxMounted` 80 → 40 → **20**；Paint 降至 1396ms（-23%）。降幅与挂载数近似线性。
  - 长列表加 `content-visibility: auto` + `contain-intrinsic-size: auto 120px`（首条消息类重 DOM 子树）。**注意**：切换会话时所有条目都是新建组件，首次渲染不受此优化影响；它只在流式输出期间生效。
  - `Generation` 语义修正：从「每次 `ResetFromSession` 递增」改为「**仅会话 Id 变化时递增**」。原实现让同会话内的重建（压缩完成、命令刷新）也触发窗口重算 + 强制贴底 + 子树重建。
  - 切换时先渲染**骨架屏**再加载：原实现整个切换过程（加载 + 全量重建 + 首帧）在同一 Dispatcher 批次内同步阻塞，用户看到旧内容僵住 1~1.9s 无任何反馈。骨架动画只用 `opacity`（合成层属性，不触发布局/重绘），并遵循 `prefers-reduced-motion`。
  - `OnWindowsChanged` 与 Summary 窗口加载各加一次 `Task.Yield()`，避免组内 N 个侧栏窗口的 `await` 续体插到主窗首帧之前。
  - `LoadSummaryOnlyAsync` 用组快照的 `ParentSessionId` 替代单独一次 `GetParentAsync()` 往返。
  - 新增 `MessageListWindowTests`（12 个用例），含两条守门：`InitialWindowSize == MaxMounted`（防首屏与上限不一致导致贴底跳动）、`MaxMounted <= 40`（防无意上调挂载数回归 Paint 成本）。

## 回归修复：发新消息后旧回答 Markdown 失效（2026-11-05）

**现象**：会话中发出一条新消息后，**上一轮已经渲染好的回答**排版塌掉——表格、列表、标题、加粗全部变成原样的 Markdown 源码文本。

**根因**：`ExecutionStartedEvent` 分支调用了 `SyncAssistant(isComplete: false)`。

时序上此时本回合还没有任何 assistant 消息，`ResolveCurrentStreamingAssistantMessage()` 走兜底分支 `messages.LastOrDefault(m => m.Role == "assistant")`，拿到的是**上一轮最后一条已完成消息**。以 `isComplete: false` 同步它 → `MessageViewModel.IsComplete` 被改回 `false` → `ContentBlock.CreateText(..., isComplete: false)` → `IsStreaming = true` → `RenderMarkdownPolicy.ShouldUseMarkdown()` 返回 `false` → 走纯文本分支。而且 `MessageViewModel.Revision` 也递增，分段缓存随之重建，坏状态被固化下来，直到下一次全量 `ResetFromSession`（刷新页面）才恢复。

`IsStreaming` 的语义被误用了：它应当表示「**这条消息本身**还在流」，而不是「当前会话有别的东西在流」。

**修复**（两处，均在 `SessionWindowTimelineSync`）：

1. `ExecutionStartedEvent` 分支移除 `SyncAssistant(isComplete: false)`，只保留 `RefreshPendingInputCount()` + `ReconcileAppendFromSession()`（排队消息提升到主时间线本来就是 `Reconcile` 的职责，不依赖那次同步）。
2. `SyncAssistant` 增加回合门闸：`isComplete == false` 时只同步属于当前回合的消息（消息 `LoopId` 与当前 LoopId 比对）。任一 `LoopId` 缺失时放行，避免挡掉「流式早期 LoopId 尚未写入」的正常路径。这条门闸兜住其他调用点将来踩同一个坑。

**未采用的方案**：在 `MessageTimelineStore.SyncAssistantMessage` 里给 `IsComplete` 加 latch（一旦完成就不再回退）。试过，会打破流式路径——`ReconcileAppendFromSession` 恒以 `isComplete: true` 物化消息，之后流式同步必须能把它改回未完成。完成的单向性只在**同一回合内**成立，跨回合不成立，因此门闸放在「回合归属」这一层而非「完成状态」这一层。

**守门测试**（`SessionWindowTimelineSyncTests`，均 RED→GREEN）：

| 用例 | 断言 |
|---|---|
| `ProcessEvent_ExecutionStarted_ShouldNotDowngradePreviousCompletedTurn` | 新回合开始后，上轮消息 `IsComplete` 仍为 `true` |
| `ProcessEvent_StreamStart_ShouldMarkNewStreamingMessageIncomplete` | 真流式消息仍被标为未完成（防止修复过度） |

## 修复：Markdown 表格没有框线（2026-11-05）

**现象**：助手回答里的 Markdown 表格渲染出来了，但没有框线、单元格没有内边距，列与列内容黏连（如「办公耗材20 包」挤在一起）。

**根因**：`.message-content table / th / td` 是表格框线与内边距的唯一来源，但两个 Markdown 容器都没带 `message-content` 类：

| 容器 | 原 class |
|---|---|
| `TextMessageComponent.razor` | 走 Markdown 时是 `class=""`（空） |
| `ReasoningMessageComponent.razor` | `reasoning-content-scroll is-complete` |

CSS 选择器不命中是静默的——页面照常渲染，只是样式全部失效。`TextBlockRenderer`（非生产路径）倒是带了该类，所以只有走组件这条生产路径受影响。

**修复**：

1. 两个容器都补上 `message-content`（正文容器始终带；思考区增加该类以复用同一套框线口径）。
2. 表格样式补 `text-align: left` + `vertical-align: top`（Markdown 表格默认表头居中，与正文左对齐不一致）。
3. 思考区只收紧单元格内边距（`--space-1 --space-2`），**不**重复定义框线与表头底色——避免同一元素两套口径漂移。

**守门测试**（`MessageContentClassContractTests`，3 个用例）：断言两个 Markdown 容器都带独立 `message-content` 类，且 CSS 中表格规则块确实含 `border` / `padding` / `border-collapse`。

> 注意断言写法：不能用 `Contain("message-content")`——`message-content--streaming` 含该子串，会让「漏加类」的回归静默通过。必须用 `message-content(?![\w-])` 匹配独立类名。反向验证（临时删类 → 测试失败）已确认有效。

## 修复：Markdown 表格字号偏大、占用空间过多（2026-11-06）

**现象**：表格内容看起来比正文「大一号」，整张表格竖向占位过多。截图里 7 行工具表几乎占满整个可视区。

**根因**：`.message-content table / th / td` 只定义了框线与内边距，**从未声明字号**。表格因此继承 `body` 的 `16px / line-height 1.75`（`design-system.css` 把 `body` 设为 `--font-size-base: 16px`）。同一屏内其它次级文本都比它小：

| 元素 | 字号 |
|---|---|
| 正文段落 | 16px |
| **表格单元格** | **16px / 行高 28px（继承 body，从未声明）** |
| 单元格内 `内联代码` | 14px |
| 思考区 | 12px |
| 工具卡片 | 11–12px |

叠加单元格 `8px 12px` 内边距后，**单行高 45px**；长参数列一换行，一行就 90px 起。

**修复**（`wwwroot/css/message-item.css`）：

1. `.message-content table` 显式声明 `font-size: var(--font-size-sm)`（14px）+ `line-height: var(--line-height-normal)`（1.5），与单元格内联代码同号，视觉上不再压过正文。
2. 单元格内边距 `8px 12px` → `6px 12px`。
3. **思考区豁免**：`.reasoning-section .message-content table { font-size: inherit; line-height: inherit; }`。思考区容器靠内联 `font-size: 12px` 撑着，若不豁免，正文的 14px 规则会把思考区表格**反向放大**（12 → 14）。

**实测**（Chromium，真实 CSS 层叠，900px 内容宽度）：

| 指标 | 改动前 | 改动后 |
|---|---|---|
| 正文单元格字号 / 行高 | 16px / 28px | 14px / 21px |
| 正文单元格单行高 | 45px | 34px（−24%） |
| 正文表格高（1 行数据） | 91px | 69px |
| 思考区单元格字号 | 12px | 12px（不变） |
| 思考区单元格单行高 | 38px | 30px |

**守门测试**（`MessageContentClassContractTests` 新增 2 个用例）：断言表格规则块必须含 `font-size:` 且取值为 `var(--font-size-sm)`（防止回落成继承 16px，也防止随手改大），以及思考区必须有 `font-size: inherit` 豁免（防止被放大）。

> CSS 缺省是静默的：删掉 `font-size` 不会报错、页面照常渲染，只是表格又悄悄变大。所以这条只能靠源码断言守。

**已知边界**：表格仍是 `width: 100%` + 自动换行，宽表会把长参数列挤成多行（本次未改为横向滚动）。
