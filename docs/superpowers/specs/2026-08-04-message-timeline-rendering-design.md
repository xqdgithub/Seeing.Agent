# WebUI 消息 Timeline 渲染架构设计

**日期:** 2026-08-04  
**状态:** 实现中（修订：设计缺陷闭环 + 进入会话贴底）  
**范围:** `samples/Seeing.Agent.WebUI` 消息列表渲染稳定性与长会话性能  
**约束:** 不提交本设计文档到 git（按产品方要求）；实现阶段另议

---

## 1. 背景与问题

`Session.razor` + `MessageList.razor` 在流式回复期间曾存在严重体验问题：

1. **助手消息整批消失**：渲染期用可变 HashSet 去重 Loop；自刷新不走 `OnParametersSet` → 助手整批不渲染。
2. **跳顶 / 整页重刷感**：滚动管理器与 Blazor 重渲染互相拉扯；JS「上滚 3 秒后自动恢复贴底」加剧跳动。
3. **长会话性能差**：流式 Clear+全量重建 VM + Session 整页 `StateHasChanged`。
4. **进入会话停在顶部**：外层按 `Items.Count==0` 卸掉 `MessageList`；有消息时再挂载，首次 `forceScroll` 早于布局完成。

用户目标：**稳定性与性能同等重要**；滚动**严格尊重上滚**；贴底滑动窗口。

---

## 2. 目标

| 目标 | 验收要点 |
|------|----------|
| 渲染稳定 | 流式中展开/收起 tool、上滚时，助手不消失、不跳顶、无整页重刷感 |
| 长会话性能 | 100+ 轮 DOM 约 40、贴底上限约 80；父级脏路径由 TailHint 驱动，条目级 O(活跃 turn) |
| 滚动可控 | 离开底部后绝不自动贴底；进入/切会话/压缩/发送必须 force 贴底 |
| 架构清晰 | 持久化与显示时间线分离；渲染路径无副作用 |

## 3. 非目标

- 不引入 Blazor `<Virtualize>` / 像素级虚拟列表 / **per-key 脏集合**（本阶段）
- 不修改 Session 持久化格式
- 不重做消息气泡视觉皮肤
- 不把 Timeline 同步到 Gateway
- 不自动 git commit（由用户另行指示）

---

## 4. 方案选择

| 方案 | 结论 |
|------|------|
| A. 补丁加固 + 尾部窗口 | 否决 |
| **B. TimelineStore + 滑动窗口** | **采用** |
| C. 像素级虚拟列表 | 否决（本阶段） |

脏信号演进：**整表 Changed → TailHint**（Generation + 尾部 Key/Revision），不做 per-key。

---

## 5. 架构

```
SessionMessage (持久化)
        │
        ▼
EventStreamHandler / Session load / Compaction
        │
        ▼
MessageTimelineStore  ←── Reset / Sync / Reconcile / CompleteTurn
  TimelineItem[] (稳定 Key + Revision)
  TailHint: Generation, TailKey, TailRevision
        │
        ├──► MessageList（始终挂载于活动会话；滑动窗口）
        │         └──► TimelineItemView（ShouldRender by Revision）
        │                   └──► Loop / User / System … → Pipeline（Revision 缓存）
        │
        └──► ScrollPolicy.js（pinned 只在 JS；回底按钮 absolute 浮层）
```

### 5.1 硬约束

1. **渲染无副作用**：禁止在 `@foreach` / 渲染回调里用可变 HashSet 去重。
2. **身份稳定**：`TimelineItem.Key` 流式不变；内容变更只递增 `Revision`。
3. **增量脏检查**：流式只 patch 当前 AssistantTurn；可见性清单见 §6.3；已完成 turn 不因父级无谓重绘。
4. **滚动正交**：贴底/上读/回底按钮由 JS 独占显隐；C# 不因按钮显隐对列表 `StateHasChanged`。
5. **滑动窗口**：贴底只挂载最近 N 项；上滚向前扩展；内存保留完整 Timeline。
6. **容器常驻**：活动会话（非「无会话」空态）**始终挂载** `MessageList`；滚动根与 scroll-anchor 常驻。

---

## 6. 数据模型

路径：

- `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItem.cs`
- `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItemKind.cs`
- `samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs`

### 6.1 TimelineItem

```csharp
enum TimelineItemKind { User, AssistantTurn, System, Compaction, Reminder, ProjectInstructions }

sealed class TimelineItem
{
    public required string Key { get; init; }
    public TimelineItemKind Kind { get; init; }
    public int Revision { get; private set; }
    public MessageViewModel? Message { get; private set; }
    public LoopGroupViewModel? Turn { get; private set; }
    public void Touch() => Revision++;
}
```

**Key 规则：**

| Kind | Key |
|------|-----|
| User / System / Compaction / Reminder / ProjectInstructions | `message.Id` |
| AssistantTurn | `loopId`；缺失则 `single-{messageId}` |

### 6.2 本地 UI 态

- Tool 展开写在 **`ToolCallViewModel.IsExpanded`**；`MergeToolCall` / Sync **必须保留**。
- 推理折叠：组件在 incomplete→complete 转换时自动折叠；不得仅靠会随窗口销毁且不写回的私有态充当唯一真相。
- 本地交互 **不得** 调用 `Touch()`。

### 6.3 Sync 可见性清单

以下任一变化 → `Touch` + 通知 Changed：

| 字段 | 说明 |
|------|------|
| Content / Reasoning | 文本 |
| IsComplete / IsReasoningComplete | 完成态；推理在**正文或工具开始后**即为 true，且 **latch** 不回退 |
| Parts | 数量或可见内容变化 |
| ToolCalls | Status、Result、Error、Parameters、Name、TaskSteps、TodoList、及 Task\* 展示字段 |

无可见变更 → 不 `Touch`、不 RaiseChanged。

### 6.4 MessageTimelineStore

- **生命周期：** Scoped（Circuit / Session 页）
- **不持久化：** 可随时 `Reset` 重建
- **TailHint（对外）：**
  - `Generation`：仅 `ResetFromSession` 递增
  - `TailKey` / `TailRevision`：尾项身份与修订（Items 为空时为 null / 0）
  - `event Action? Changed`：仍通知 MessageList；消费方用 TailHint / Generation 决定窗口与贴底，条目靠 Revision
- **Pin：** `RequestPinToBottom()` / `PinRequested` — 发送、进入会话等强制贴底

**API：**

| 方法 | 用途 |
|------|------|
| `ResetFromSession(messages, sessionId)` | 结构重写；`Generation++`；更新 TailHint |
| `SyncAssistantMessage(msg, sessionId, isComplete?)` | 流式原地同步；保留 IsExpanded；无可见变更不 Touch |
| `ReconcileAppendFromSession(messages, sessionId)` | **仅追加**未知 Id；不做 mid-list 改写/删除 |
| `CompleteTurn(loopId)` | 标记 Turn 完成并 Touch |
| `RequestPinToBottom()` | 请求 MessageList force pin |

**Reconcile 语义：** 只追加。压缩 / 清空 / 切会话等结构替换必须 `Reset` + Generation。不做轻量 mid-list rewrite。

---

## 7. 组件与渲染流程

### 7.1 Session.razor

**Reset 白名单（仅这些路径调用 `ResetFromSession`）：**

- 加载会话 / 切换会话
- 压缩（含 fallback）
- 明确的 `SessionUpdated`（若有）
- 清空等结构替换

**禁止：** 同 `SessionId` 的 `OnParametersSetAsync` 无条件 Reset。

**流程：**

- Reset / 进入 / 切会话 / 压缩后：`RequestPinToBottom()`（或依赖 Generation bump + Pin）。
- 流式：`SyncAssistantMessage` / `ReconcileAppend`；**不**每个 token 整页 SHC。
- 活动会话：**始终渲染** `<MessageList>`（内部处理空列表 Empty）；仅「无任何会话」才用页级空态。

### 7.2 MessageList.razor

- 注入 Store；订阅 `Changed` + `PinRequested`。
- 窗口：`InitialWindowSize=40`，`LoadMoreBatch=20`，`MaxMounted=80`。
- Generation bump → 窗口归尾 + force pin。
- TailHint 尾部变化 → 贴底滑动 / `notifyContentGrew`（严格尊重 JS pinned）。
- 非贴底禁止为追 MaxMounted 增大 `_windowStart`。
- force pin：布局未完成时 **二次 AfterRender 重试**（scrollHeight 过小或连续帧）。
- `#message-list-container` + scroll-anchor 常驻；回底按钮为列表层 **absolute** 浮层；`initMessageListScroll` 失败可重试。

### 7.3 TimelineItemView / Pipeline

- `ShouldRender` by Revision；Reset 换实例时 `ReferenceEquals` 清缓存。
- `LoopGroupItem`：`ContentRevision` 变化才重建 blocks。

---

## 8. 滚动策略（ScrollPolicy.js）

| 行为 | 规则 |
|------|------|
| pinned | `distanceToBottom < threshold`（实现可用 `userScrolled` 等价） |
| 上滚 | 立即 unpin；**无** 3 秒自动恢复 |
| 内容增高 | `notifyContentGrew()`；仅 pinned 时滚到底 |
| 回底按钮 | 纯 JS 显隐；不回调 Blazor |
| 点回底 / 滚回底 | pin + force |
| 上滚加载 | `preserveScrollOnPrepend` |
| 进入/切会话/压缩/发送 | C# `forceScrollToBottom`（可重试） |
| JSInterop 未就绪 | 静默跳过；init 返回 false 时下帧重试 |

禁止：每次 render `CheckScrollPosition` + 列表 `StateHasChanged`。

---

## 9. 错误与边界

| 场景 | 处理 |
|------|------|
| 切会话 / 压缩 / 进入有消息会话 | Reset + 窗口归尾 + **必须** force pin（含布局重试） |
| 助手无 LoopId | Key = `single-{messageId}` |
| 重复 message Id | Reset 保留最后一条 |
| Store 与 Session 短暂不一致 | 下次 Reset 或后续 Sync/Reconcile；不双写持久化 |
| Circuit 丢弃 | Store 随 Scoped 释放 |

---

## 10. 测试计划

### 10.1 单元

- Store：Reset 分组、Sync 保留 IsExpanded、推理完成判定与 latch、未变更不 Changed、Generation、TailHint、Reconcile 只追加、Tools/Parts 可见性。
- 窗口：初始窗口、贴底滑动、LoadMore、MaxMounted。

### 10.2 手工验收

1. 流式展开/收起 tool → 助手不消失、不跳顶。
2. 上滚后流式 → 不自动回底；点回底恢复。
3. **进入/切换长会话 → 贴底看到最新消息。**
4. 100+ 轮：初始约 40；贴底 ≤ MaxMounted。
5. 上滚 LoadMore → 位置无明显跳动。
6. 压缩 / 切会话 → Reset + 贴底。
7. 推理结束 → 「已思考」并自动折叠（不必等整轮结束）。
8. 发送消息 → 强制贴底。

---

## 11. 实施顺序（修订后）

1. TimelineStore + MessageList + 删副作用去重（已完成）
2. Sync/Reconcile + ShouldRender + Session 减噪（已完成）
3. ScrollPolicy 严格上滚 + 回底浮层（已完成）
4. 滑动窗口（已完成）
5. **设计缺陷闭环：** TailHint、可见性清单、Reset 白名单、容器常驻、进入贴底重试（本轮）

---

## 12. 关键文件

| 文件 | 变更 |
|------|------|
| `Services/MessageTimelineStore.cs` | Store + TailHint + Pin |
| `Models/Timeline/*` | 模型 |
| `Components/MessageList.razor` | 窗口 / TailHint / pin 重试 |
| `Components/TimelineItemView.razor` | Revision ShouldRender |
| `Pages/Session.razor` | Reset 白名单；常挂 MessageList；Pin |
| `wwwroot/js/app.js` / `css/message-list.css` | ScrollPolicy；按钮 absolute |
| `LoopGroupItem.razor` | Revision blocks 缓存 |
| `tests/Seeing.Agent.WebUI.Tests/*` | 单测 |

---

## 13. 决策记录

| 决策 | 选择 |
|------|------|
| 成功标准 | 稳定性 + 性能同等重要 |
| 滚动策略 | 严格尊重上滚；进入/发送/压缩强制贴底 |
| 脏信号 | TailHint（非 per-key） |
| 窗口策略 | 贴底滑动窗口 |
| 架构方案 | B — TimelineStore + 滑动窗口 |
| Reconcile | 只追加；结构替换走 Reset |
| Git | 设计文档落盘但不自动提交 |
