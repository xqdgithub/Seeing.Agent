# 12 SessionMessage 内容不可变并发契约 Release Notes

**日期：** 2026-10-08
**设计规格：** [`2026-10-08-session-message-immutable-concurrency-design.md`](../superpowers/specs/2026-10-08-session-message-immutable-concurrency-design.md)
**实施计划：** [`2026-10-08-session-message-immutable-concurrency.md`](../superpowers/plans/2026-10-08-session-message-immutable-concurrency.md)
**相关模块：** `Seeing.Session`（独立 NuGet 包）、`Seeing.Agent.Hosting`、`Seeing.Agent.WebUI`

---

## 1. 概要

将 `SessionMessage` / `SessionToolCall` 的集合成员改为**不可变集合**，确立 **publish-on-write** 并发契约，消除"后台写 + Dispatcher 读"同一集合导致的 `InvalidOperationException: Collection was modified`，以及由此在 `ErrorBoundary` 重渲染路径上触发的 Blazor 渲染树 `NullReferenceException`（`RenderTreeDiffBuilder.AppendDiffEntriesForFramesWithSameSequence`）。

## 2. 背景

`SessionData.Messages` 已有"锁内快照"（见 [06 合规审查](06-compliance-audit.md) P1-14），但快照只覆盖**列表容器**，装入的是同一批 `SessionMessage` 引用；其集合成员仍为活 `List`/`Dictionary`，被 `ChatEventTracker`（执行侧）与 `EventStreamHandler`（WebUI 投影）**双写者**在后台线程原地修改，同时 Dispatcher 渲染期读取。

## 3. 并发契约（publish-on-write）

- 集合成员为不可变集合；结构变更只能**整体替换属性引用**（`= x.Add(...)` / `= x.SetItem(...)`），禁止原地 `Add/Remove/Insert/Clear/索引写`。
- 读取方直接持引用枚举，无需锁；后台写者发布新快照，读者持旧快照不受影响。
- 元素标量字段（`SessionToolCall.Status/Result` 等）仍为原子引用/值写。
- 契约写入 [`04 开发规范](04-development-standards.md)` §5.1 与反模式表。

## 4. 破坏性变更清单（主版本级）

`Seeing.Session` 为独立 NuGet 包，以下属性类型变更属主版本级：

| 类型 / 成员 | 旧类型 | 新类型 |
|-------------|--------|--------|
| `SessionMessage.ToolCalls` | `List<SessionToolCall>?` | `ImmutableList<SessionToolCall>?` |
| `SessionMessage.Parts` | `List<SessionContentPart>?` | `ImmutableList<SessionContentPart>?` |
| `SessionMessage.Metadata` | `Dictionary<string, object>?` | `ImmutableDictionary<string, object>?` |
| `SessionToolCall.TaskSteps` | `List<SessionTaskStep>?` | `ImmutableList<SessionTaskStep>?` |
| `SessionToolCall.Metadata` | `Dictionary<string, object>?` | `ImmutableDictionary<string, object>?` |

**JSON 格式不变**（字段名/层级不变；`System.Text.Json` 原生支持不可变集合），旧会话文件可正常读写。

## 5. 迁移对照

| 旧写法 | 新写法 |
|--------|--------|
| `msg.ToolCalls = new List<SessionToolCall>{...}` | `msg.ToolCalls = ImmutableList.Create(...)` |
| `msg.ToolCalls.Add(tc)` | `msg.ToolCalls = msg.ToolCalls.Add(tc)` |
| `msg.Metadata["k"] = v` | `msg.Metadata = msg.Metadata.SetItem("k", v)` |
| `msg.Parts = someList` | `msg.Parts = someList.ToImmutableList()` |
| `msg.Metadata = someDict` | `msg.Metadata = someDict.ToImmutableDictionary()` |
| `foreach (var t in msg.ToolCalls)` | 不变 |

## 6. 适配的写入者 / 消费点

- 写入者：`ChatEventTracker`、`EventStreamHandler`、`TaskCardAggregator`、`SessionMessageConverter`、`AgentLoopScheduler`、`GatewayUserMessageComposer`、`SessionMessage.Clone`/工厂。
- 读取者：全部零改动（`ImmutableList` 实现 `IReadOnlyList`、`ImmutableDictionary` 实现 `IReadOnlyDictionary`）。

## 7. 验证

- `Seeing.Session.Tests` 219 通过（含新增并发压力 + STJ 往返测试）。
- `Seeing.Agent.WebUI.Tests` 291 通过。
- `Seeing.Agent.Tests` 1106 通过。
- 全解决方案 `dotnet build Seeing.Agent.slnx` 0 error。

## 8. 已知边界

- 只保证集合**结构**不可变；`Metadata` 的**值**（可为 `JsonElement`/共享对象）不保证深不可变。
- 本次**不合并双写者**（`EventStreamHandler` 退化为纯读投影）——该事项已在后续 [`13 单一权威写入者 Release Notes`](13-single-writer-release-notes.md) 完成；不可变契约与写者数量正交，已独立修复本缺陷。
- `SessionData.Metadata/Context/State`、`SessionGroup` 等其它共享模型不在本次范围。
