# 会话关系树与关系展示设计

- **日期**：2026-09-18
- **状态**：待用户审阅（已按三轮架构审查修订 v4）
- **关联**：`docs/superpowers/specs/2026-09-18-session-tools-and-session-groups-design.md`（会话工具与通用会话组，已实现）
- **背景**：上一版把"会话组"落为关系权威，WebUI 仅做"主窗/侧栏"二值呈现，导致 Fork（含 trim 备份）与 HandoffSuccessor 都看起来像"子会话"，且把派生来源显示为"父会话"，误导用户。

---

## 1. 目标 / 非目标

**目标**
1. 关系语义正确、可在 UI 区分：主线（锚点）、已交接（前任）、交接后继、分叉、备份、子代理（只读）。
2. 交接后继提升为**新锚点**（主线转移），交接后页面自动导航到后继。
3. 提供三处**职责不重复**的关系展示：顶部关系条（方向+快捷）、关系抽屉（结构+操作）、侧栏（运行态监控）。
4. 提高"执行中"会话的可见性；已完成会话紧凑化，但**不丢失**原有卡片功能。

**非目标**
- 不做可交互全屏关系图（YAGNI）。
- 不改会话存储格式与工具 schema 单点原则。
- 不做旧组数据迁移（旧组沿用旧展示）。

---

## 2. 关系模型（方案 A：`ParentSessionId` 统一为"来源"）

### 2.1 枚举

`SessionRelation`（`src/primitives/Seeing.Session/Core/SessionGroup.cs`）：

| 值 | 名称 | 含义 |
|---|------|------|
| 0 | `None` | 无外部关系的成员（未被交接的原始根/锚点） |
| 1 | `Child` | 子 Agent / Task 会话（只读） |
| 2 | `Fork` | 分支会话（含 trim 备份） |
| 3 | `HandoffSuccessor` | 交接后继（**当前锚点**，或旧数据中的非锚点后继） |
| 4 | `HandoffPredecessor` | 已被后续交接取代的主线节点（**新增**） |

### 2.2 关键定义

- **边的方向**：`member.ParentSessionId` 统一表示"来源会话"——后继.parent=前任、分叉.parent=来源、子代理.parent=来源。不新增字段。
- **锚点判定**：由 `IsAnchor` 标志判定（**放宽**"锚点 ⟺ `Relation==None`"约束）。锚点 = 主线链末端（最新主线会话）。
- **角色语义（重要）**：`Relation` 表示成员**当前在主链中的角色**，而非"创建来源"：
  - 锚点：`HandoffSuccessor`（由交接产生）或 `None`（未发生交接的原始根）。
  - 链中间节点与被交接的原始根：`HandoffPredecessor`（已被后续交接取代）。
  - 链 A→B→C 示例：`A`(Predecessor, parent=null) → `B`(Predecessor, parent=A) → `C`(HandoffSuccessor+IsAnchor, parent=B)。
  - "被交接给谁"由反向查找推导：`A` 的后继 = `Members` 中满足 `ParentSessionId == A.Id` 且 `Relation ∈ { HandoffSuccessor, HandoffPredecessor }` 的成员（排除 `Fork`/`Child`/`None`）。**在 §2.2.2 前提下结果唯一**。
  - 旧数据（`HandoffSuccessor` 但非锚点）保持原样展示，不迁移。

### 2.2.1 锚点归一（唯一真相函数）

新增并在**交接 / 删除 / 提升**全路径复用的归一函数 `NormalizeAnchor(group, preferredMemberHint = null)`（组锁内执行）：

1. **选定锚点**（优先级，均限存活成员）：
   - (a) `preferredMemberHint`（若存活）；
   - (b) 当前锚点若仍存活则**保持**（无条件重跑时保证幂等 no-op）；
   - (c) 沿主线链（`ParentSessionId` 反向）自被影响节点回溯最近存活成员；
   - (d) 按 `Order`/`CreatedAt` 取**非 `Fork` 且非 `Child`** 成员；
   - (e) 兜底 `Fork` 成员（沿用"优先跳过 `Fork`"语义）；
   - (f) 仅剩 `Child` 的退化兜底：取 `Order` 最早的 `Child`（组保留，UI 标"主线缺失·仅剩子代理"）。
2. **锚点 `Relation` 归一**：
   - 锚点为 `Fork`（(e) 兜底当选）→ `Relation = None`（**沿用现状**，见既有测试 `SessionGroupManagerTests.cs:354-379` 与 release notes 09 边界 #1）；
   - 锚点为 `Child`（(f) 退化兜底）→ **保持 `Relation = Child`**（避免"SubAgent + None"非法态）；
   - 否则：锚点 `ParentSessionId == null` → `Relation = None`（原始根）；`ParentSessionId != null` → `Relation = HandoffSuccessor`（由交接产生）。
3. 其余成员 `IsAnchor=false`；旧数据中非锚点的 `HandoffSuccessor` **保持原值不变**（不迁移）。
4. 重算 `group.AnchorSessionId` 与 `group.Title`（锚点标题）。**`NormalizeAnchor` 自身只做内存归一、不做 persist/publish**；由调用方（交接/删除路径）在组锁内统一**一次** `Touch`/`SaveAsync`/发布 `Changed`（避免发布中间快照与 `Version` 双增）。

### 2.2.2 交接前提（硬约束）

`session_handoff` 的源会话**必须是当前锚点**（`IsAnchor==true`）。**工具侧 + 组管理器侧双重校验**，不满足则失败。目的：防止对前任再次交接产生多后继，破坏"后继唯一"与反向查找。

### 2.3 不变量（调整后）

- `非锚点成员 Relation==Child ⟺ 会话 Kind==SubAgent`（不变）。
- `SubAgent 会话不得挂 Fork / HandoffSuccessor / HandoffPredecessor / None`（不变，含新增值）。
- **移除**"锚点必须 `Relation==None`"约束；锚点 `Relation` 由 §2.2.1 归一为 `None`/`HandoffSuccessor`（`Fork` 兜底 → `None`；仅剩 `Child` 的退化兜底 → 保持 `Child`）。正常路径**不选 `Child` 作锚点**（仅退化情形例外），对应改造 `ValidateMemberInvariant`。

---

## 3. 生命周期规则

### 3.1 交接（`session_handoff` / 组管理器）

1. **前置**：源会话必须是当前锚点（§2.2.2）；否则失败。
2. **组锁内原子完成**（新增/改造 `ISessionGroupManager` 原子方法，`CreateHandoffSuccessorAsync` 内实现）。**实现约束**：`SemaphoreSlim` 不可重入，原子路径必须使用**已持锁的内部 helper**，**不得**再调用公开的 `AddMemberAsync`/`SetActiveAsync`/`EnsureForSessionAsync`（避免自锁与"组锁→会话锁"逆序）：
   - 新建后继会话，`后继.ParentSessionId = 前任(当前锚点)`；
   - `AddMember(后继, Relation=HandoffSuccessor)`；
   - 原锚点成员 `Relation = HandoffPredecessor`；
   - 调 `NormalizeAnchor(group, 后继)` 更新 `AnchorSessionId`/`Title`；**同步 `group.ActiveSessionId = 后继`**（用已持锁内部 helper，不得调用公开 `SetActiveAsync`），保证 `ResolveActiveId()` 与主线一致；
   - 一次 `Touch`/`SaveAsync`/发布 `Changed`（不发布中间快照）。
3. 工具成功结果携带后继 `sessionId`（供导航）。
4. **导航机制（明确）**：不改 Core 事件与分层。工具成功结果元数据已含 `target_session_id`（`SessionHandoffTool.cs:94,149`）；WebUI 侧重用现有结果元数据通道（`EventStreamHandler.cs:487-490` 流入 `toolCall.Metadata`），在收到 `session_handoff` **成功**结果时 `NavigateTo("/session/{target_session_id}")`。**兜底同步仅作用于"由交接结果驱动"的 active 变化**（不得对用户点击切换做 URL 同步，否则与 §4.4"点击只切 active、URL 不变"冲突）。**不依赖** `NavigateEvent`/`CommandResultEvent`（工具不产生二者，`SessionWindow.razor:780` 仅处理这两类）。
5. 失败回滚：删除后继、恢复原锚点（沿用现有幂等/回滚机制）。

### 3.2 删除（关系感知）

删除前：先取消**被删会话自身**的在途执行（并级联其 `Child`），再移除（见 §5 对 `ChatOrchestrator.DeleteSessionAsync` 的修正）。

**全路径约束**：任何删除/重挂完成后，**必须对锚点重跑 `NormalizeAnchor`**（§2.2.1）——尤其删除链首（头节点）后，锚点 `ParentSessionId` 变 null，须归一回 `Relation=None`，否则与 §4.2 徽标判定矛盾。当前锚点存活时该函数为**幂等 no-op**（§2.2.1 (b)），不会因删除 Fork/Child 而误移锚点。

| 删除对象 | 规则 |
|---|---|
| 锚点（有存活前任） | 前任（`锚点.ParentSessionId`）经 `NormalizeAnchor` 提升为新锚点；原锚点移除。 |
| 锚点（前任被删或为 null） | 沿主线链回溯最近**存活**祖先；无则取**非 `Fork` 且非 `Child`** 存活成员（无则兜底 `Fork`，仅剩 `Child` 时走 §2.2.1 (f) 退化）；经 `NormalizeAnchor` 归一。 |
| 中间主线节点（`Relation ∈ {HandoffSuccessor, HandoffPredecessor}`） | 其**主线后继**（`ParentSessionId == 被删节点` 且 `Relation ∈ {HandoffSuccessor, HandoffPredecessor}`）重挂到被删节点的父；不删除后继。 |
| 头节点（`ParentSessionId==null` 的主线节点） | 其主线后继重挂为 `ParentSessionId=null`（成为新链首）。 |
| 旧数据非锚点 `HandoffSuccessor` | 按普通成员移除；若其下游有主线后继，按"中间节点"规则重挂下游。 |
| `Fork` / 备份 | 仅删自身（不递归主线）。 |
| `Child` | 删除自身并**递归删除其 `Child` 子树**。 |
| 被删节点下的 `Fork`/`Child` | 其 `ParentSessionId` 指向被删节点时，重挂到被删节点的父；父为 null 则置 null（UI 来源行显示"来源已删除"）。 |
| 组内最后一个成员 | 组一并移除（沿用现状）。 |

> 注：重挂仅针对主线后继与上述 `Fork`/`Child`；**筛选后继必须显式限定 `Relation`**，不得把 `Fork`/`Child` 当作主线后继重挂为链节点。

### 3.3 取消执行

- 仅级联取消 `Child`（含递归），不影响主线链与分叉（沿用现状）。

### 3.4 迁移

- **不迁移**。旧组按旧形态展示（§6）；仅新建交接组使用新语义。

---

## 4. 展示设计（WebUI）

### 4.0 数据来源与边界（重要）

- **顶条 / 抽屉**：直接以 `ISessionGroupManager.GetGroupAsync` / `GetGroupForSessionAsync` 的**组快照** + §2.2 反向查找为数据源（可拿到 `IsAnchor`、`Label`、`ParentSessionId`、关系）。
- **侧栏**：以 `SessionWindowRegistry` 的 `WindowNode` 列表为骨架（可扩展携带 `IsAnchor`/`Label` 以支持分区与"备份/分叉"区分）。
- **顶条/抽屉的反应式来源**：直接订阅 `SessionWindowRegistry.WindowsChanged`（`Session.razor:292-293` 已有用法）复用其组事件订阅，**不再对 `ISessionGroupEventBus` 另起订阅**（落实 §8 订阅去重）。
- **运行态上报**：现有 `SessionWindow` 仅暴露 `OnClick`/`Context`，无执行态回调。落地时由 `SessionWindow` 新增执行态变更回调（如 `OnExecutionStateChanged(sessionId, isExecuting)`），页面侧收集为 `Dictionary<sessionId,bool>` 供"执行中置顶"与动作行；**不写入 registry**。
- **执行态边界**：注册表**不持权威执行态**（`WindowNode.IsExecuting` 恒 false，`SessionWindowRegistry.cs:11-13`、release notes 边界 #2）。运行态由**各 `SessionWindow` 自身 handler** 渲染与上报；需要"执行中置顶"时由页面侧收集各窗口上报的执行态（回调/事件），**不得**改写注册表语义。

### 4.1 三处职责（原则：同一信息只在一处"详细呈现"）

| 信息 | 详细归属 | 其他处 |
|---|---|---|
| 主线链（前任→锚点） | **顶部关系条**（常驻精简） | 抽屉为完整树 |
| 分叉/备份/子代理 | **侧栏**（监控）+ **抽屉**（结构） | 顶条仅"分支计数 chip" |
| 会话运行状态/预览 | **侧栏**（状态分层） | 顶条/抽屉仅小徽标/点 |
| 节点操作（重命名/删除/取消） | **抽屉** | 侧栏仅点击切换 |
| 当前选中 | 三处一致描边样式 | — |

### 4.2 关系徽标映射（三处共用）

| 关系 | 文案 | 颜色 | 分区 |
|---|---|---|---|
| `IsAnchor`（主线） | 主线（由交接产生时提示"交接后继"） | 蓝 | 主位 |
| `IsAnchor` 且 `Relation==Child`（§2.2.1 (f) 退化态） | 子代理（只读）·主线缺失 | 灰 | 子会话（**不显示**"主线"蓝标） |
| `HandoffPredecessor` | 已交接 → <后继标题>（后继缺失时："已交接"） | 灰 | 主线历史 |
| `HandoffSuccessor`（非锚点，旧数据） | 交接后继 | 蓝绿 | 主线历史 |
| `Fork` 且 `Label` 前缀 `trim-backup` | 备份 | 琥珀 | 派生 |
| `Fork`（其他） | 分叉 | 琥珀 | 派生 |
| `Child` | 子代理（只读） | 灰 | 子会话 |

- "备份 vs 分叉"仅 UI 依据 `member.Label` 前缀判定，不改数据模型。

### 4.3 状态分层（侧栏卡片）

- **执行中**：大卡 + 脉冲点；**保留预览**（沿用 `GetLatestAssistantPreview` 三分支：最新 assistant ≤120 字 → "正在执行工具: X" → "暂无消息"）；新增**当前动作行**（如 `⚙ 正在执行 task · 4 个子代理`）与**已运行时长**；**不使用进度条**。
- **已完成（非激活侧栏卡）**：**两行紧凑卡**，高度自适应（不占 200px），**浅灰底**（`--color-bg-tertiary`）以与执行中/其他状态卡区分——行 1：标题 + 关系徽标 + 状态徽标 + 时间；行 2：预览摘要（12~20 字）+ 来源小字；hover title 显示完整预览（≤120 字）。预览 `flex:1` 撑满并省略、来源限宽，保证预览可见。
- **排队中 / 失败 / 已取消**：与既有卡片一致——标题 + 关系徽标 + 状态徽标 + 预览正文 + 来源行；不做紧凑、不改底色。
- "非激活"是天然的：侧栏只列非 active 会话（active 在主区以 Full 渲染），故"完成态的非激活会话"即侧栏中状态为 `Done` 的卡；执行中卡因需展示动作行/时长而不紧凑。
- 2026-09-18 修订记录：曾实现"单行紧凑 + 灰底只读"——7 个元素争抢单行宽度导致 `flex:1` 的预览被挤成 0 宽（预览不可见），且卡片仍固定 200px 产生大片空白；现改为"两行 + 高度自适应 + 白底"。
- 状态徽标沿用（Default/Error/Warning/Success）与左侧状态条颜色（queued/running/failed/cancelled/done）。
- 主会话淡蓝底（`conference-tile--main`）保留。

### 4.4 顶部关系条（方案 1）

- 内容：**只放主线链**（前任 → … → 锚点；锚点高亮并含"执行中"脉冲）+ **分支计数 chip**（`⑂n · 👤n ▾`，点击开抽屉）+ 右侧「关系 ▸」入口。
- **显示条件**：仅当**存在关系**（主线链 > 1 段，或存在派生/子会话）时渲染；**单一独立会话不显示顶条**（`RelationTreeModel.HasRelatedSessions`）。
- 溢出：链超过 3 个节点时，中间折叠为「… N 个更早」chip（点击开抽屉定位）；分支计数恒为单 chip。
- 数据：组快照 + 反向查找（§4.0）。
- 行为：点击主线节点仅切 active（URL 不变）；锚点为主位。

### 4.5 关系抽屉

- 完整关系树：主线链（含各级前任）+ 各节点下挂的分叉/备份/子代理；节点带关系徽标与状态点。
- 数据：组快照（§4.0）。
- 节点操作（`⋯` 菜单）：切换、重命名、打开新标签、删除（按 §3.2 规则，含确认；删除前按 §3.2 先取消执行）。

### 4.6 侧栏分区

- 分区顺序：**主线历史**（`HandoffPredecessor` / 非锚点 `HandoffSuccessor`）→ **派生**（`Fork`/备份）→ **子会话**（`Child`）。
- **分区视觉区分**（避免只靠标题文字辨识）：每区独立强调色——主线历史=蓝（`--color-primary`）、派生=琥珀（`--color-warning`）、子会话=灰（`--color-text-tertiary`）；以左侧竖条 + 标题圆点 + 标题色呈现，标题右侧显示条目数。
- 状态分层见 §4.3；分区内执行中置顶（执行态由页面侧收集，§4.0），其余按 `Order`/更新时间。
- 交互：点击切换（不做操作菜单）。

### 4.7 必须保留的既有卡片功能

标题 · 状态徽标（排队/执行中/失败/已取消/完成）· 预览三分支 · 左侧状态条颜色（queued/running/failed/cancelled/done）· 来源行（原"父会话：X"按关系改写）· 整卡点击切换 · 主会话淡蓝底。

---

## 5. 影响面与改动点

**原语层**（`src/primitives/Seeing.Session/`）
- `SessionRelation` 新增 `HandoffPredecessor = 4`。
- `SessionGroupManager`：
  - `PromoteAnchor`（`SessionGroupManager.cs:684-703`）重构为 `NormalizeAnchor`（§2.2.1），交接/删除/提升全路径复用；保留"优先跳过 `Fork`"。
  - `ValidateMemberInvariant`（`:710-734`）放宽锚点校验：正常路径锚点归一后 `Relation ∈ { None, HandoffSuccessor }`；`Fork` 兜底当选时已归一为 `None`；`Child` 仅在 §2.2.1 (f) 退化态作锚点（保持 `Child`）。`SubAgent ⟺ Child` 保留。
  - `CreateHandoffSuccessorAsync`（`:570-616`）改为组锁内原子迁移（§3.1）；新增组锁内原子锚点迁移/归一 API 至 `ISessionGroupManager`。
  - 删除路径按 §3.2 补齐重挂/兜底逻辑。
- 存储格式不变（`{groupId}.json`，枚举新增值对旧数据安全）。

**脊柱 / 宿主**
- `session_handoff` 工具（`src/capabilities/Seeing.Agent.Tools.Session/SessionHandoffTool.cs`）：加"源必须为当前锚点"校验；成功结果元数据沿用既有键 `target_session_id`（`SessionHandoffTool.cs:94,149`）供 UI 导航。
- **`SessionTitleService` 修正（真实缺陷）**：`ShouldEnsure` 现以 `!string.IsNullOrEmpty(parentId)` 跳过（`SessionTitleService.cs:96-97`），而新模型中主线后继 `parentSessionId` 非空 → 主线会话不再自动生成标题。改为按 `Relation==Child`（或 `Kind==SubAgent`）判定，而非 `parent != null`。
- **`ChatOrchestrator.DeleteSessionAsync` 修正**：现仅取消 `Child` 执行后 `RemoveSessionAsync`（`ChatOrchestrator.cs:181-199`），未取消**被删会话自身**在途执行，与 §3.2 不符；补充"先取消自身执行"。

**WebUI**（`samples/Seeing.Agent.WebUI/`）
- `SessionWindowRegistry`：`WindowNode` 视需要扩展 `IsAnchor`/`Label`（仅供侧栏分区与徽标）；**不注入执行态**。
- `SessionWindowContext`：暴露 relation（不只 `IsChild`）；`ApplyCapabilities` 按新 relation 复核（`Child` 仍唯一只读）。
- 新增 `SessionRelationBar`（顶部条）与 `SessionRelationDrawer`（关系树）：**以组快照为数据源**，与 `SessionWindowRegistry` 的 circuit 级订阅**去重/避免重复订阅**；生命周期随页面。
- `SessionWindow`（Summary 卡片）：状态分层、关系徽标、来源行改写、已完成紧凑。
- `SessionToolbar`：徽标按 relation（主线/已交接/分叉/子代理只读）。
- `Session.razor` / `Conference.razor`：接线顶条与抽屉；保留侧栏；handoff 导航（§3.1.4）。

**文档 / 注释**
- `docs/architecture/09-session-tools-and-groups-release-notes.md`：更新关系语义/边界；如新增/调整不变量，同步 `04-development-standards.md`。
- 同步更新 `SessionGroupManager.cs:705-708` 的 XML 注释（"锚点成员必须 `Relation==None`"）与旧规格 `2026-09-18-session-tools-and-session-groups-design.md:234`，避免与新不变量冲突。
- README/模块文档如需，标注 `HandoffPredecessor` 语义。

---

## 6. 兼容与边界

- **旧组不迁移**：旧组形态为 `锚点(IsAnchor+None 在链首) + 非锚点 HandoffSuccessor`。顶条/抽屉**以 `IsAnchor` 定位主线**（主线=`IsAnchor` 成员），其余 `HandoffSuccessor`/`HandoffPredecessor` 归"主线历史"，**不做"前任→锚点"链式连线**（按 `Order` 平铺）；侧栏同样归"主线历史"并标"交接后继"。保证新旧形态均可渲染。
- **中间节点 Last-Writer**：链中间节点统一 `HandoffPredecessor`；"后继是谁"由反向查找推导（§2.2）。
- **URL 打开前任会话**：以所属组锚点为主线；主窗展示 active；顶条主线链完整可见。
- **执行中会话被删除**：先取消自身+`Child` 级联执行，再移除（§3.2 / §5）。
- **多次交接链**：支持任意长度；顶条折叠、抽屉树完整展开。
- **Fork/Child 的来源悬挂**：删除主线节点时按 §3.2 重挂；无法重挂则 `ParentSessionId=null`，UI 显示"来源已删除"。

---

## 7. 测试策略

**原语层**（`tests/primitives/Seeing.Session.Tests/`）
- `NormalizeAnchor`：原始根锚点保持 `None`；交接后继锚点 `HandoffSuccessor`；跳过 `Fork`；一次发布（`Version` 仅 +1）。
- 交接：源非锚点 → 失败；正常交接 → 锚点后移、原锚点 `HandoffPredecessor`、无中间快照。
- 删除：锚点（前任存活/前任已删/孤链）、中间节点重挂、头节点、旧数据非锚点后继、`Fork`/`Child` 来源重挂、最后一个成员删组。
- 不变量：锚点 `Relation ∈ { None, HandoffSuccessor }` 合法；`Child` 仅在 §2.2.1 (f) 退化态作锚点（保持 `Child`）；`SubAgent ⟺ Child` 保留。
- 兼容：旧形态组可加载/查询/删除，行为不崩。
- 既有断言处理：
  - `SessionGroupManagerTests.cs:327-351`（`fork(Order0)+child(Order1)`，删根）期望**变更**：新语义下锚点 = `Fork`（优先级 (e)；(d) 已排除 `Child`），`Relation=None`、`Title=备份标题`（原期望为 `child`）。
  - `SessionGroupManagerTests.cs:354-379`（仅 `Fork` 成员）**保持有效**（`Fork` 兜底 → `Relation=None`）。
  - `SessionGroupManagerTests.cs:27,158,347`、`SessionGroupLifecycleTests.cs:33,103`、`SessionGroupTests.cs`、`FileSessionGroupStoreTests.cs`、`SessionWindowRegistryTests.cs` 中"锚点 `Relation==None`"断言按新语义复核（原始根仍 `None`；交接后锚点为 `HandoffSuccessor`）。

**WebUI**（`tests/apps/Seeing.Agent.WebUI.Tests/`）
- 顶条：主线链渲染、溢出折叠 `+N`、分支计数 chip、点击切 active。
- 抽屉：关系树结构（主线链+分支）、节点操作、删除走关系感知。
- 侧栏：三分区与排序、状态分层（执行中大卡保留预览/动作行/时长；其余卡片保留预览正文与来源行）、备份 vs 分叉徽标。
- 顶条显示条件：单一独立会话不渲染；存在主线历史/派生/子会话时渲染（`HasRelatedSessions`）。
- 导航：handoff 成功 → `NavigateTo` 后继；组 `ActiveSessionId` 兜底同步。
- 大屏（Conference）回归：两列布局与侧栏状态分层。

**脊柱 / 宿主**
- `SessionTitleService`：主线后继（parent 非空、`Kind==Root`）仍可自动生成标题。
- `ChatOrchestrator.DeleteSessionAsync`：删除执行中的会话会取消自身在途执行。
- `session_handoff`：源非锚点失败；成功返回后继 id。

---

## 8. 待定 / 风险

- 分支计数 chip 点击默认"展开抽屉并定位到派生区"。
- 抽屉"打开新标签"默认复用 `/session/{id}`。
- 折叠阈值（主线链 3 个）落地时可微调。
- 新增 `SessionRelationBar`/`SessionRelationDrawer` 与 `SessionWindowRegistry` 的订阅去重需在实现时严格处理（先订阅后枚举、按 `Version` 丢乱序）。
- 上一版遗留：`ReconcileOrphanTaskCardsAsync` 在连接中断时可能把在途 Task 卡标记为取消并落盘（`EventStreamHandler.cs:927`），用户已确认暂不处理，后续单独跟进。
