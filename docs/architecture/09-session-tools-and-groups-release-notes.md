# 09 会话工具与会话组 Release Notes

**日期：** 2026-09-18
**分支：** `feature/session-tools-and-groups`
**相关模块：** `Seeing.Session`（会话组）、`Seeing.Agent.Tools.Session`（会话工具：list/search/read/trim/handoff）、WebUI 会话窗口投影

---

## 1. 概要

本次收尾聚焦「会话组 + 会话工具」的并发安全与失败回滚，并补齐以下运维语义：

1. **读 API 快照化**：`ISessionGroupManager` 的公开读（`GetGroupAsync` / `GetGroupForSessionAsync` / `ListMembersAsync` / `GetParentAsync` / `ListChildrenAsync`）一律返回克隆/不可变快照，杜绝与组锁内写者并发遍历 `Members` 导致的 `InvalidOperationException` 与脏读；内部写路径仍持锁操作活对象。
2. **工作区切换清缓存**：`ISessionGroupManager.ClearCache()` 新增；`SessionGroupReloadHandler` 在重定位组存储后清空组缓存与会话→组映射，避免旧工作区组继续命中。
3. **交接幂等与回滚**：`session_handoff` 以源会话 `Metadata["handoff_inflight:{CallId}"]` 做重试预检，同一 CallId 仅创建一个后继、仅提交一次；`SessionGroupManager.CreateHandoffSuccessorAsync` 中途失败自清理，杜绝孤儿会话/组成员。
4. **Minor**：`LoopCompleteEvent.Reason` 透传工具 `TurnDirectiveReason`（无则回退 `turn-directive`）；`session_read` 声明 `output.skip` 并钳制 `max_chars`/`limit`；`session_read`/`session_handoff` 授权与幂等细节修正。

---

## 2. 已知边界（本期容忍，非缺陷）

| # | 边界 | 表现 | 影响 / 规避 |
|---|------|------|-------------|
| 1 | **Fork 不随源删除** | 删除源会话（`RemoveSessionAsync`）仅级联删除 `Relation==Child` 子树；`Relation==Fork` 的分支/备份会话保留（其 `ParentSessionId` 随重挂规则改指被删节点的父），锚点由 `NormalizeAnchor` 归一，不保证 Fork 当选 | 备份语义使然：trim/分支备份需在源删除后仍可取回。调用方如需清理，显式删除分支会话 |
| 2 | **`SessionWindowRegistry.IsExecuting` 恒 false** | WebUI 窗口注册表不持权威执行态，该字段始终返回 `false` | 执行态由各窗口自身 handler 渲染（`SessionEventStreamRouter` / Task 卡片聚合），勿以 registry 字段判定执行中 |
| 3 | **`session_trim` 无跨会话参数** | `ParametersSchema` 仅 `mode` / `message_id` / `from_id` / `to_id`，始终作用于**当前会话** | 跨会话裁剪属后续演进；如需裁剪其他会话，先切换/读取目标会话（`session_read` 可跨组读，受授权约束） |
| 4 | **授权工厂缺失时放行** | `SessionToolBase.AuthorizeAsync` 在 `IPermissionAuthorizerFactory` 未注册时返回 `null`（放行） | 兼容未接入授权链的宿主（Core-only / Embed）；生产宿主须注册授权链以获得跨组审批 |
| 5 | **`Kind.Fork` 移除、`SubAgent=2` 保留** | `SessionKind` 现仅 `Root=0` / `SubAgent=2`；分支/备份/交接关系迁至 `SessionRelation`（`SessionGroup` 承载） | 数值 `2` 保持历史兼容（持久化数据反序列化）；禁止再用 `SessionKind` 表达分支，改用 `SessionRelation.Fork` |
| 6 | **同 CallId 重试的元数据残留** | 交接回滚后源会话 `handoff_inflight:{CallId}` 标记不清理；因目标会话已删除，重试会重新创建 | 幂等预检会校验目标会话是否存在，残留标记不会导致卡死；如需整洁可后续加清理钩子 |
| 7 | **`ReconcileOrphanTaskCardsAsync` 连接中断误判** | 连接中断时可能把在途 Task 卡片标记为取消并落盘 | 用户已确认暂不处理（见关系树规格 §8），后续单独跟进 |

---

## 3. 行为差异（同输入不同结果）

1. **`GetGroupAsync` / `GetGroupForSessionAsync` 返回克隆**：调用方对返回对象 `Members` 的修改不再回写缓存/存储（此前会污染活组）。读语义与 `ListMembersAsync` 对齐。
2. **`ListMembersAsync` / `GetParentAsync` / `ListChildrenAsync` 在组锁内快照**：与并发 `AddMemberAsync` / `RemoveMemberAsync` 竞争时不再抛异常，返回一致快照。
3. **`SessionGroupReloadHandler` 构造签名变更**为 `(ISessionGroupStore, ISessionGroupManager)`；宿主经 DI 自动解析，手工构造需补第二参。
4. **`LoopCompleteEvent.Reason`** 现透传工具给出的原因（如 `handoff`），缺省仍为 `turn-directive`。
5. **`session_read` 的 `max_chars`** 上限为 2000（此前无上限，可被 `ToolOutputLimiterDecorator` 二次截断而丢失续读游标）；`limit` 上限 50。

---

## 4. 兼容性说明

- `ISessionGroupManager` 新增成员 `ClearCache()`：自定义实现需补齐（Moq 桩自动返回默认值）。
- 无其他破坏性契约变更；未引入兼容重载或死代码。

---

## 5. 会话关系语义修订（关系树与展示，2026-09-18 增量）

> 规格：`docs/superpowers/specs/2026-09-18-session-relation-tree-and-display-design.md`。本节收敛上一版"组关系"语义，**旧组不迁移**。

### 5.1 关系枚举与锚点判定

- `SessionRelation` 新增 `HandoffPredecessor = 4`：**已被后续交接取代的主线节点**（链中间节点与被交接的原始根）。新增值对旧数据反序列化安全。
- `HandoffSuccessor = 3` 保留：**当前锚点**（由交接产生）或旧数据中的非锚点后继。链 A→B→C：`A(Predecessor, parent=null) → B(Predecessor, parent=A) → C(HandoffSuccessor + IsAnchor, parent=B)`；"后继是谁"由反向查找推导（`ParentSessionId == 源` 且 `Relation ∈ { HandoffSuccessor, HandoffPredecessor }`，结果唯一）。
- **锚点由 `IsAnchor` 判定**（放宽"锚点 ⟺ `Relation==None`"约束）：锚点 = 主线链末端；交接后锚点后移，原锚点转 `HandoffPredecessor`。
- `ParentSessionId` 统一为"来源"指针（后继.parent=前任、分叉/子代理.parent=来源），不新增字段。

### 5.2 锚点归一（唯一真相函数）

`NormalizeAnchor(group, preferredMemberHint = null)`（组锁内执行）为锚点唯一归一入口，交接 / 删除 / 提升全路径复用。

- **选锚点优先级**：`(a) hint` → `(b) 当前锚点存活则保持`（重跑幂等）→ `(d) 非 Fork 且非 Child` → `(e) Fork 兜底` → `(f) 仅剩 Child 退化`；主线回溯由调用方经 `hint` 承担。
- **锚点 `Relation` 归一**：`Fork` 兜底当选 → `None`；仅剩 `Child` 的退化态 → **保持 `Child`**（避免 `SubAgent + None` 非法态）；`ParentSessionId==null` → `None`；否则 → `HandoffSuccessor`。
- 函数只做内存归一，不 persist / publish；由调用方在组锁内统一一次 `Touch` / `SaveAsync` / 发布 `Changed`（避免中间快照与 `Version` 双增）。

### 5.3 关系感知删除

任何删除 / 重挂后均对锚点重跑 `NormalizeAnchor`（当前锚点存活时为幂等 no-op）：

| 删除对象 | 规则 |
|---|---|
| 锚点（有存活前任） | 前任提升为新锚点，原锚点移除 |
| 锚点（前任已删 / null） | 沿链回溯最近存活祖先 → 非 Fork 非 Child → Fork 兜底 → 仅剩 Child 退化 |
| 中间主线节点 / 头节点 | 主线后继重挂到被删节点的父（头节点则置 `null`），不删除后继 |
| `Fork` / 备份 | 仅删自身 |
| `Child` | 删除自身并递归删除 `Child` 子树 |
| 被删节点下的 Fork / Child | `ParentSessionId` 指向被删节点时重挂到其父（父为 `null` 则置 `null`，UI 显示"来源已删除"） |

`RemoveMemberAsync` 与 `RemoveSessionAsync` 同样执行重挂；`RemoveSessionAsync` 额外删除会话本体。级联取消仍仅限 `Child`（含递归）。

### 5.4 交接后自动导航

`session_handoff` 成功结果元数据携带 `target_session_id`；WebUI 收到**成功**结果时自动 `NavigateTo("/session/{target_session_id}")`。用户点击主线节点仅切换 active，**不改 URL**；兜底同步仅作用于"由交接结果驱动"的 active 变化。

### 5.5 旧组兼容

旧组形态为 `锚点(IsAnchor + None 位于链首) + 非锚点 HandoffSuccessor`：**不迁移**。顶条 / 抽屉以 `IsAnchor` 定位主线，其余 `HandoffSuccessor` / `HandoffPredecessor` 归"主线历史"按 `Order` 平铺（不做链式连线）；侧栏同样归"主线历史"并标"交接后继"。

### 5.6 顶条显示条件与侧栏摘要卡回退（2026-09-18 修订）

- **顶条仅在存在关系时显示**：`RelationTreeModel.HasRelatedSessions`（主线链 > 1 段，或存在派生/子会话）为 true 才渲染；**单一独立会话不显示顶条**。
- **侧栏摘要卡（状态分层）**：
  - **执行中**：大卡 + 脉冲点 + 动作行 + 已运行时长（无进度条），保留预览。
  - **已完成（非激活）**：**两行紧凑卡**、高度自适应、**浅灰底**——行 1 标题 + 关系徽标 + 状态徽标 + 时间，行 2 预览摘要（12~20 字）+ 来源小字；hover 显示完整预览。
  - **排队/失败/取消**：完整卡（标题 + 关系徽标 + 状态徽标 + 预览正文 + 来源行）。
  - 迭代记录：曾实现"单行紧凑 + 灰底只读"，7 个元素争抢单行宽度使 `flex:1` 预览被挤成 0 宽、且 200px 定高产生空白，已废弃为上述"两行 + 自适应 + 浅灰底"。
- **侧栏分区视觉区分**：主线历史=蓝（`--color-primary`）、派生=琥珀（`--color-warning`）、子会话=灰（`--color-text-tertiary`）；左侧竖条 + 标题圆点 + 标题色 + 条目数，不再仅靠标题文字辨识。
