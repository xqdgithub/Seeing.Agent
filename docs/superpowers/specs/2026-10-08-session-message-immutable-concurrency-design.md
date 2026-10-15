# SessionMessage 内容不可变并发契约设计

**日期:** 2026-10-08
**状态:** 待实施
**范围:** `Seeing.Session`（primitives）+ `Seeing.Agent.Hosting`（ChatEventTracker）+ `Seeing.Agent.WebUI`（EventStreamHandler / TaskCardAggregator / 消费点）
**触发:** 子 Agent 执行期间 WebUI 渲染树 diff 抛 `NullReferenceException`（`RenderTreeDiffBuilder.AppendDiffEntriesForFramesWithSameSequence`）
**约束:** 按产品方惯例，本设计文档不自动提交 git（与既有 spec 约定一致）

---

## 1. 背景与问题

子 Agent（`task` 工具）执行期间，父/子会话在后台高速产出工具调用。WebUI 侧 `ErrorBoundary` 包裹的 Summary 卡片在渲染时直接枚举 `SessionMessage.ToolCalls`，而同一对象正被后台线程原地修改，触发 `InvalidOperationException: Collection was modified`。该异常在渲染中抛出 → `ErrorBoundary` 重渲染 → 命中 Blazor diff 缺陷 → 表现为 `AppendDiffEntriesForFramesWithSameSequence` 的 `NullReferenceException`（次生异常掩盖了根因）。

根因是**并发契约缺口**：

- `SessionData.Messages` 已实现"锁内快照"（`docs/architecture/06` P1-14，`4451b53`），但快照只覆盖**列表容器**，装入的是**同一批 `SessionMessage` 引用**；
- `SessionMessage.ToolCalls`(`List`)、`Parts`(`List`)、`Metadata`(`Dictionary`)、`SessionToolCall.TaskSteps`(`List`)、`SessionToolCall.Metadata`(`Dictionary`) **无任何同步**，且被多个后台线程原地修改；
- 至少两个写入者：`ChatEventTracker`（执行侧权威）与 `EventStreamHandler`（WebUI 投影）；另有 Dispatcher 渲染期读取。三者对同一会话的同一对象并发访问。

会话隔离只保证"不同会话对象互不共享"，**不保证同一会话对象在"执行/事件线程 vs UI Dispatcher"间串行**。

### 1.1 已验证的写入者（原地结构修改）

| 成员 | 原地写入者 | 备注 |
|------|-----------|------|
| `SessionMessage.ToolCalls` | `ChatEventTracker`、`EventStreamHandler` | 仅此 2 处；已临时改为写时复制（COW） |
| `SessionMessage.Metadata` | `ChatEventTracker`（schema 快照 2 处） | |
| `SessionMessage.Parts` | 无原地结构修改 | 仅赋值 |
| `SessionToolCall.TaskSteps` | `TaskCardAggregator`（已 COW 引用替换） | |
| `SessionToolCall.Metadata` | `ChatEventTracker`/`EventStreamHandler`（均为引用替换） | |

> 结论：不存在任何"外部依赖原地 Add"的代码，迁移面可控。

---

## 2. 目标

| 目标 | 验收要点 |
|------|----------|
| 消除并发读写集合 | 后台写、Dispatcher 读不再可能 `Collection was modified`；子 Agent 执行期无渲染期集合异常 |
| 类型系统强制 | 结构性变更只能通过替换引用完成，原地 `Add/Remove/索引写` 编译不通过 |
| 零读取方改动 | 所有只读消费点（渲染、LLM 出站、落盘、工具、TUI）零改动 |
| 落盘格式不变 | JSON 结构/字段名/层级不变，旧文件可读 |
| 与既有范式一致 | 复用 `ImmutableDictionary`（`ProviderRegistry` 先例）与"快照/不可变"契约（`docs/architecture/09` 会话组） |

## 3. 非目标

- 不引入跨进程写协调。
- 不改造 `SessionData.Metadata/Context/State`、`SessionGroup`、Permission 等其它共享模型（另立项）。
- 不保证 `Metadata` **值**的深不可变（值可为 `JsonElement`/共享对象）；只保证集合结构不可变。
- 不改造 `SessionToolCall` 的标量字段（`Status`/`Result` 等）——它们仍为引用/值原子写，不构成集合破坏。
- 不重做 WebUI 渲染管线（`MessageRenderPipeline`）。
- 不自动 git commit。

---

## 4. 并发契约（核心）

> **消息内容发布即不可变（publish-on-write）**
>
> 1. `SessionMessage` / `SessionToolCall` 的集合成员使用**不可变集合**类型。
> 2. 任何结构变更通过**替换属性引用**完成：`x.ToolCalls = x.ToolCalls.Add(tc)` / `x.Metadata = x.Metadata.SetItem(k, v)`；**禁止**对属性值原地 `Add/Remove/Insert/Clear/索引赋值`。
> 3. 读取方直接持有当前引用枚举，天然一致；无需锁，也永不观察到中途修改。
> 4. 属性写本身是原子引用写；读者要么看到旧快照、要么看到新快照，不会看到撕裂状态。

该契约写入 `docs/architecture/04-development-standards.md`（反模式表 + 并发章）。`SessionData.Messages` 的"锁内快照"契约保持，二者互补：容器级（会话层）+ 元素级（本契约）。

---

## 5. 方案选择

| 方案 | 结论 |
|------|------|
| A. 模型改用不可变集合（`ImmutableList`/`ImmutableDictionary`） | **采用**（类型系统强制，破坏面已验证可控） |
| B. 对外只读接口 + 内部不可变后端 | 否决（强制力弱，仍可强转后原地改） |
| C. 仅 COW + 保留 `List` 类型 | 否决（治标不治本） |

`System.Collections.Immutable` 属共享框架（`Seeing.Agent.Core/Llm/ProviderRegistry.cs` 已在用，无显式包引用），`Seeing.Session` 仍满足"仅 BCL"。

---

## 6. 模型变更（`Seeing.Session`）

| 成员 | 现类型 | 目标类型 |
|------|--------|----------|
| `SessionMessage.ToolCalls` | `List<SessionToolCall>?` | `ImmutableList<SessionToolCall>?` |
| `SessionMessage.Parts` | `List<SessionContentPart>?` | `ImmutableList<SessionContentPart>?` |
| `SessionMessage.Metadata` | `Dictionary<string, object>?` | `ImmutableDictionary<string, object>?` |
| `SessionToolCall.TaskSteps` | `List<SessionTaskStep>?` | `ImmutableList<SessionTaskStep>?` |
| `SessionToolCall.Metadata` | `Dictionary<string, object>?` | `ImmutableDictionary<string, object>?` |

约定：

- 保留公共 setter（STJ 反序列化 + `publish-on-write` 需要）。
- 字段名、`[JsonPropertyName]`、层级一律不变 → **JSON 格式不变**。
- `Clone()` / `DeepCloneMetadata()` 产出不可变集合；元素对象（`SessionToolCall`、`SessionContentPart`、`SessionTaskStep`）仍深拷贝，保持克隆解耦语义。
- `[JsonIgnore]` 辅助属性（`HasToolCalls` 等）依赖 `Count`，不可变集合同样满足。
- 空值语义：`null` 仍表示"无该字段"（JSON 兼容）；写入者按需用 `ImmutableList<T>.Empty` / `ImmutableDictionary<K,V>.Empty`。

---

## 7. 写入者适配

| 文件 | 变更 |
|------|------|
| `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs` | `StreamComplete`/`ToolCallEvent`：`.Find/.Exists`→LINQ `FirstOrDefault/Any`；`??= ImmutableList<T>.Empty`；`= current.Add(tc)`；`Metadata[key]=`→`= (Metadata ?? ImmutableDictionary<string,object>.Empty).SetItem(...)` |
| `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` | `HandleToolCall`：`??= ImmutableList<T>.Empty`；`.Find`→`FirstOrDefault`；`= toolCalls.Add(toolCall)` |
| `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs` | `MergeStep`：`taskCall.TaskSteps = steps.ToImmutableList()` |
| `samples/Seeing.Agent.WebUI/Services/SessionMessageConverter.cs` | 仅 `ToSessionMessage`：`sessionMessage.Parts`/`.ToolCalls` 赋值补 `.ToImmutableList()`；`CreateAssistantMessageWithToolCalls` 内 `sessionToolCalls` 补 `.ToImmutableList()`。`ToChatMessage` 侧目标是 `ChatMessage`，不变 |
| LLM 客户端（`AnthropicClient`/`OpenAiChatClient`/`OpenAiResponsesClient`） | **无需改动**（已核对：其 `message.ToolCalls = toolCalls` 的 `message` 是出站 `ChatMessage`，非 `SessionMessage`） |
| `SessionMessage.Clone()` / `SessionToolCall.Clone()` / `DeepCloneMetadata()` | 构造 `ImmutableList`/`ImmutableDictionary` |
| `SessionMessage` 工厂方法（`AssistantMessageWithToolCalls`/`UserMessageWithParts`/`UserMessageWithImage`） | 集合初值改 `ImmutableList.Create(...)`（入参类型保持 `List`/可枚举，赋值处 `.ToImmutableList()`） |

写入者遵循契约：**只替换引用，不原地改**。`EventStreamHandler` 的 `MarkIncompleteTasksCancelledAsync` / `ReconcileOrphanTaskCardsAsync` 只写元素标量字段（`tc.Status/Error`），不属结构变更，不改。

---

## 8. 读取者（零改动）

`ImmutableList<T>` 实现 `IReadOnlyList<T>`（索引、`Count`、`foreach`、LINQ、`SelectMany`），`ImmutableDictionary<K,V>` 实现 `IReadOnlyDictionary<K,V>`（`TryGetValue/ContainsKey/索引`）。覆盖：

- WebUI：`SessionWindow.GetCurrentActionLine()/GetLatestAssistantPreview()`、`MessageViewModelFactory`、`MessageTimelineStore`、`ContentBlockBuilder`、`TaskCardAggregator`（读路径）、`TaskMessageComponent`、`SessionMessageConverter`（读侧）。
- Hosting/Core：`ExecutionJobService`、`IncompleteToolCallMarker`（读+写元素标量）、`AgentExecutor`、`ChatMessageHistoryBuilder`、`SessionToolBase`。
- 工具/其它：`SessionReadTool`、`TuiViewState`。

例外（编译期微调）：测试中 `msg.ToolCalls = new List<>` / `msg.Parts = ...` 需补 `.ToImmutableList()`。

---

## 9. 序列化兼容

- STJ 原生支持 `ImmutableList<T>` / `ImmutableDictionary<K,V>` 的序列化与反序列化（官方文档已确认）。
- 属性名/层级/`object` 值语义不变 → 旧会话文件可正常反序列化；新文件对集合的 JSON 表达与旧版完全一致。
- `DeepCloneMetadata` 的深拷贝逻辑保留（返回不可变字典）。
- 落盘快照契约（`10-session-persistence-release-notes.md` §4.2）不变。

---

## 10. 测试策略

1. **并发契约压力测试（新增）**：writer 线程循环 `msg.ToolCalls = msg.ToolCalls.Add(tc)`（或 `SetItem`），reader 线程并行枚举/`Select`，持续短暂时间，断言无异常且最终态一致。
2. **STJ 往返测试（新增）**：含 `ToolCalls`/`TaskSteps`/`Metadata`/`Parts` 的 `SessionMessage` 序列化 → 反序列化 → 字段与元素值相等。
3. **既有测试迁移**：`TaskCardAggregatorTests`、`MessageTimelineStoreTests`、`EventStreamHandlerTests`、`IncompleteToolCallMarkerTests`、`SchemaSnapshotExecutionTests`、`GatewayUserMessageComposerTests` 适配不可变集合（读断言不变，写断言补转换）。
4. **回归**：`dotnet vstest` 运行 `Seeing.Agent.WebUI.Tests`、`Seeing.Agent.Tests`（按 `AGENTS.md` 命令约定）。

---

## 11. 破坏性变更 & 迁移

`Seeing.Session` 为独立 NuGet 包，五个集合属性类型变更属**主版本级**。需产出 release notes（`docs/architecture/`）。

迁移对照：

| 旧写法 | 新写法 |
|--------|--------|
| `msg.ToolCalls = new List<SessionToolCall>{...}` | `msg.ToolCalls = ImmutableList.Create(...)` / `.ToImmutableList()` |
| `msg.ToolCalls.Add(tc)` | `msg.ToolCalls = msg.ToolCalls.Add(tc)` |
| `msg.Metadata["k"] = v` | `msg.Metadata = msg.Metadata.SetItem("k", v)` |
| `msg.Parts = someList` | `msg.Parts = someList.ToImmutableList()` |
| `foreach (var t in msg.ToolCalls)` | 不变 |

影响点已枚举（约 6 处生产赋值 + 2 个测试文件 + 内部写入者）。

---

## 12. 已知边界

| 边界 | 说明 |
|------|------|
| `Metadata` 值深不可变 | 只保证字典结构不可变；嵌套对象/`JsonElement` 仍共享引用 |
| 元素标量并发写 | `SessionToolCall.Status` 等为原子引用/值写，允许；不保证跨字段一致性快照 |
| `SessionData` 元数据 | `Metadata/Context/State` 不在本次范围，另行立项 |
| 与临时 COW 补丁的关系 | 本次已落地的 `ChatEventTracker`/`EventStreamHandler` COW 为过渡；实施方案 A 后其逻辑等价于不可变集合的 `Add`（保留并类型化） |
