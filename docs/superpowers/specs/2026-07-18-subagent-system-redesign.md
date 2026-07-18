# 子 Agent（Task）系统重设计

**日期:** 2026-07-18  
**状态:** 设计已确认（含架构评审硬契约补丁），待实现计划  
**参考:** OpenCode `packages/opencode/src/tool/task.ts`、`agent/subagent-permissions.ts`、`tool/task_status.ts`  
**范围:** 核心引擎契约（Session / Task 工具 / 事件 / 后台）+ WebUI 参考实现；Gateway/ACP/TUI 按同一契约自行接入  
**架构评审:** Approve with changes → 已按推荐默认值合入本文 §4–§6、§12  

---

## 1. 问题

当前 Seeing 子 Agent 链路存在结构性缺陷，不适合打补丁式修补：

1. **双路径分叉**：`AgentExecutor.HandleSubAgentCallAsync` 特判 `task`，与注册的 `TaskTool` 行为不一致；真实执行绕过工具层。
2. **伪 Session**：Executor 使用合成 `parent@agent@guid`，未作为一等 `SessionData` 入库；续跑 / 打开详情 / 后台 Job 主键无法统一。
3. **事件未下发**：创建了 `SubAgentStarted` 却不 `yield`；子 Loop 事件被吞掉；WebUI `HandleSubAgent` 为空；`SubAgentBlockRenderer` 无数据源。
4. **权限旁路**：特判路径跳过工具权限与 `RequestSubAgentPermission`；Mode 白名单漏掉 `task`。
5. **契约空心**：`background` / `task_id` / `session_id` 等参数被解析或声明但未兑现；`BackgroundTaskManager` 与 Task 未收敛（`bg_*` 双主键）。

用户已确认目标形态：**混合 UI（父消息 Task 卡片可展示当前工具步骤 + 真实子 Session「打开详情」）**、**前台阻塞 + 后台异步均进 v1**、**契约优先**、**子 Session 默认禁止再调 `task`**。

---

## 2. 目标与非目标

### 目标

1. 子 Agent = `SessionKind.SubAgent` 的一等 Child Session；`task_id` ≡ Child Session Id。
2. 唯一执行引擎：对任意 Session 跑同一套 Prompt/Loop；`task` 只负责委派与调度。
3. 删除 Executor 对 `task` 的特判；收敛为单一 Task 工具实现。
4. 事件契约完整：`TaskStarted` / `TaskProgress` / `TaskCompleted` / `TaskFailed`，经**唯一投影器**进入父事件流，支撑卡片进度。
5. 前台阻塞与后台 Job（含完整 `task_status`、inject、父 idle 自动 resume）均为 v1 能力。
6. 权限派生：**严格对齐 OpenCode `deriveSubagentSessionPermission`**（见 §5.3）；快照可序列化并持久化在 Child Session。
7. WebUI 作为契约参考实现：Task 卡片 + 打开详情；其他端不强制同期改完。

### 非目标（v1）

- `@` 提及直接调用子 Agent  
- 子 Session 详情页独立多轮产品化（续跑由主 Agent 再调 `task(task_id=...)`）  
- 「全量权限交集、子不能比父更宽」的额外收紧（列为后续选项，v1 不做）  
- 为 Gateway/ACP/TUI 各写一套编排逻辑  
- 保留旧 `SubAgentEvent` / `bg_*` 双轨协议  
- 多实例跨进程 Background Job 粘滞/调度（v1 单进程语义；见 §6.4）

---

## 3. 选定方案

**Session-first（对齐 OpenCode）**

```
主 Session Loop
  └─ task 工具（经 ToolInvoker，无 Executor 特判）
       ├─ CreateChild / 续跑 + 权限快照持久化
       ├─ foreground: 阻塞 Child Loop；投影器向父流 yield Task*
       └─ background: Job(Id=Child.Id) → 结束后 Inject + TryResumeWhenIdle
Child 完整历史 → Child Session（真相源）
父总线 Task* → Task 卡片（降采样投影）
```

否决：独立双引擎 Orchestrator；纯事件投影 + 伪 Session。

---

## 4. Session 模型（硬契约）

### 4.1 SessionKind（解决 Fork 与 SubAgent 撞车）

在 `SessionData` 增加一等字段（推荐枚举，而非仅靠 Metadata）：

```text
SessionKind: Root | Fork | SubAgent
```

| Kind | ParentSessionId | 用途 |
|------|-----------------|------|
| `Root` | null | 用户主会话；列表默认展示 |
| `Fork` | 指向源会话 | 现有 Fork 分支；**不是** Task |
| `SubAgent` | 指向委派方会话 | Task Child；`Id` = `task_id` |

API 过滤必须带 kind：

- `ListRootsAsync()` → 仅 `Root` 且未归档  
- `ListChildrenAsync(parentId, kind?)` → 默认可只取 `SubAgent`；Fork 走既有 Fork API  
- **禁止**用 `ForkAsync` 冒充 SubAgent；**禁止**用 `CreateChildAsync` 做消息拷贝 Fork

### 4.2 SubAgent 字段语义

| 字段 | 语义 |
|------|------|
| `Id` | `task_id` |
| `Kind` | `SubAgent` |
| `ParentSessionId` | 父会话 |
| `SelectedAgent` | 子 Agent 类型 |
| `SelectedModel` / Provider | 默认继承父侧发起委派时的模型（与 OpenCode 一致）；子 Agent 定义若自带 Model 则覆盖 |
| `Title` | 来自 `description`，建议 `"{description} (@{agent})"` |
| `WorkingDirectory` / `PartitionId` | **强制继承**父会话 |
| `PermissionSnapshot` | 可序列化规则集（见 §5.3）；持久化在 Session |
| `Metadata` | `description`、`originToolCallId`（可选冗余） |

### 4.3 CreateChild vs Fork

`CreateChildAsync(parentId, agent, title, permissionSnapshot)`：

- 空消息历史  
- 写入一条即将执行的 user prompt（或由 Task 工具在启动 Loop 前写入）  
- **不**复制父消息  
- 设置 `Kind=SubAgent`、快照、继承目录/分区  

`ForkAsync` 保持现有语义，`Kind=Fork`。

### 4.4 状态分层（三套状态，禁止混用）

| 层 | 名称 | 含义 | 谁更新 |
|----|------|------|--------|
| 持久 Session | `SessionStatus` | Created / Active / Idle / Error / Archived 等**会话生命周期**；**不用** Completed 表示「单次 task 跑完」 | SessionManager |
| 运行时 Loop | `LoopBusy`（进程内） | 该 Session 是否有进行中的 Prompt/Loop | LoopScheduler |
| 后台 Job | `BackgroundJobStatus` | pending / running / completed / error / cancelled | BackgroundJob 服务 |

`task_status` 解析优先级（对齐 OpenCode）：

1. 进程内 Job 状态（若存在）  
2. 否则 `LoopBusy` / Session 运行时指示  
3. 否则回落 Child 消息 / 错误文案  

---

## 5. Task 工具与权限

### 5.1 工具

| 工具 | 作用 |
|------|------|
| `task` | 创建/续跑 Child，前台或后台执行 |
| `task_status` | 查询；可选阻塞等待 |

**`task` 参数：**

| 参数 | 必需 | 说明 |
|------|------|------|
| `description` | 是 | 短标题 |
| `prompt` | 是 | 子任务说明（须自洽） |
| `subagent_type` | 是 | 目标 Agent |
| `task_id` | 否 | 续跑同一 Child |
| `command` | 否 | 触发来源（写入 metadata / 遥测） |
| `background` | 否 | `true` 后台执行 |

**废弃旧参数（迁移期）：**

| 旧名 | 策略 |
|------|------|
| `run_in_background` | 接受为 `background` 别名一期，其后拒绝 |
| `session_id` | **拒绝**（禁止与 `task_id` 双主键）；文档提示改用 `task_id` |
| `load_skills` | v1 不支持；出现则忽略或明确失败（实现计划中二选一，默认忽略并打日志） |

**返回给主模型（非用户正文）：**

```text
task_id: {childSessionId}
state: running|completed|error|cancelled

<task_result>
...
</task_result>
```

前台成功时 `state=completed`，`<task_result>` 为**子 Loop 最后一条 assistant 文本 part**（对齐 OpenCode；无文本则占位说明）。  
后台启动成功时 `state=running`，结果稍后由 inject / `task_status` 提供。

工具 metadata：`parentSessionId`、`sessionId`、`agent`、`model`、`background`、`originToolCallId`。

### 5.2 `task_status` 完整契约

| 参数 | 说明 |
|------|------|
| `task_id` | 必需 |
| `wait` | 默认 false；true 时阻塞至终态或超时 |
| `timeout_ms` | 可选；默认实现定义（建议 600000）；超时返回 `state=timeout`（任务本身不取消，除非另有 cancel API） |

返回态枚举：`running` | `completed` | `error` | `cancelled` | `timeout` | `not_found`。

进程内无 Job（例如进程重启后）：按 §4.4 回落 Child Session 消息/错误；**不**假装跨进程调度（v1 单进程）。

### 5.3 可委派目标

- 仅 `Mode ∈ { SubAgent, All }` 且未禁用；拒绝纯 `Primary`。  
- 动态工具描述与 Prompt 子 Agent 列表同源。  
- 嵌套：仅当**子 Agent 自身规则显式允许 `task`** 时，派生快照才不默认 deny；父委派调用**不**额外覆盖放开。

### 5.4 权限三层

**1. 父侧调用 `task`**

- 统一工具权限；Ask 走 `RequestToolPermission` 和/或 `RequestSubAgentPermission(subagent_type, prompt)`。  
- 尊重 `AllowedAgents` / `IsDelegableTo`。  
- **Permission Ask 全局串行**（进程内锁/队列）：多并行 `task` 不得并发弹多个互抢的 Ask UI。

**2. Child 会话权限快照（严格 OpenCode 同构）**

算法 `DeriveSubagentSessionPermission(parentSessionRules, parentAgent, subagent)`：

1. 父 **Agent** 中 `action=deny` 且属于 **edit 类**的规则（Seeing 映射：`edit` / `write` / 等价写文件工具的 deny；防止 Plan 旁路）  
2. 父 **Session** 快照中的 `deny` 规则 + 目录/external_directory 类限制  
3. 若子 Agent **未**显式允许 `todowrite` → 追加 deny `todowrite`  
4. 若子 Agent **未**显式允许 `task` → 追加 deny `task`  
5. **不做**「父与子 AllowedTools 全量交集」；全量收紧列为后续选项  

持久化：

- 形状：可序列化 `PermissionRuleEntry[]`（或与现网等价的 Ruleset DTO）  
- 挂在 Child Session 的 `PermissionSnapshot`  
- **`task_id` 续跑复用快照，不重算**（除非未来显式 `refresh_permissions`，v1 无）

**3. Child Loop 内**

- 统一权限通道；复用父 `PermissionChannel`（Ask 冒泡到同一 UI，受全局串行约束）；策略读 Child 快照。

### 5.5 续跑与冲突

- 有效 `task_id` 且 `Kind=SubAgent`、父关系合法：复用，追加 user prompt。  
- 同一 `task_id` 已 running（Job 或 LoopBusy）：拒绝，提示 `task_status`。  
- 权限未通过：不创建 Child。

### 5.6 工具可见性

- Primary / All：暴露 `task`、`task_status`。  
- SubAgent：默认不暴露 `task`（除非 Agent 规则显式允许）；与快照默认 deny 双保险。  
- 修复 Mode 硬编码白名单漏 `task`。

---

## 6. 事件投影、调度与后台（硬契约）

### 6.1 唯一投影通道

禁止 Tool / UI 私拉第二总线。唯一桥接：

```text
ITaskEventProjector
  输入: Child Loop 的 IMessageEvent + TaskProjectionContext
       (parentSessionId, taskId, originToolCallId, background)
  输出: 父侧 TaskStarted | TaskProgress | TaskCompleted | TaskFailed
```

**投递方式（v1 钉死）：嵌套 yield 进入父 `IAsyncEnumerable<IMessageEvent>`。**

- 前台：`task` 执行期间，父工具管道必须能**交错**收到：父 `ToolCallRunning` + 投影出的 `TaskProgress`（不可等 Child 全部结束再批量补发）。  
- 实现挂点：ToolInvoker / 工具执行管道在跑 `task` 时消费 Child 事件流 → Projector → 向父流推送（Channel/`IAsyncEnumerable` 合并）。  
- **禁止** Projector 直接写 UI 状态。

`TaskProgress` 降采样规则（v1）：

- 默认：**不**把 Child 的原始 `StreamDelta` 全文镜像进父总线  
- 映射：工具 pending/running/complete → Progress（工具名、状态、≤200 字符预览）  
- 可选低频 text 心跳；同类连续 tool 事件可合并  
- 完整内容只在 Child Session

### 6.2 任务生命周期事件

| 事件 | 何时 | 主要载荷 |
|------|------|----------|
| `TaskStarted` | Child 开始 | taskId, agent, description, background, originToolCallId, parentSessionId |
| `TaskProgress` | 可展示步骤 | stepKind, toolName?, status?, preview? |
| `TaskCompleted` | 成功 | taskId, resultText, duration |
| `TaskFailed` | 失败/取消 | taskId, error, cancelled? |

挂在现有 `IMessageEvent` / `MessageEventType`。  
旧 `SubAgentStarted`/`SubAgentCompleted`：迁移后删除；Hook `SubagentStarted/Completed` 同步改名或一期双发后删除（见 §9）。

### 6.3 前台

父 Loop → `task` → 权限 → CreateChild/续跑 → `TaskStarted` → 阻塞 Child Loop（写 Child + 投影 Progress）→ `TaskCompleted` + tool result → 父继续。

取消拓扑：

```text
parentLoopCT → taskCT → childLoopCT
                     └→ jobCT（若后台）
```

父 abort → 取消该次 task 下所有 Child/Job，发 `TaskFailed(cancelled=true)`。  
v1 可选：按 `task_id` 单独取消（管理 API）；不做则仅级联父取消。

### 6.4 后台与 LoopScheduler

**废弃**现有 `BackgroundTaskManager` 的 `bg_*` Id 与独立 `agent.ExecuteAsync` 旁路。Job 只调度：**对 Child Session 跑同一套 Prompt/Loop**。

引擎级（命名可调整，职责固定）：

```text
IAgentLoopScheduler
  InjectSyntheticAsync(parentSessionId, message)  // synthetic 标记必填
  TryResumeWhenIdleAsync(parentSessionId)         // 仅 idle 时开新 Loop
  IsLoopBusy(sessionId)
```

**idle 定义：** 该 Session 无进行中的 Prompt/Loop，且无等待中的工具 Ask。  

后台流程：

1. `TaskStarted` → `Job.Start(id=Child.Id)` → tool 立即 Complete（`state=running`）  
2. Job 跑 Child Loop（写 Child + 仍向**父事件流**投影 Progress；父若已结束当轮 Loop，宿主/总线仍须能投递到 Session 订阅者，供卡片/Gateway 更新）  
3. 结束 → `InjectSyntheticAsync`（含 task_id / state / result|error）→ `TaskCompleted`/`TaskFailed`  
4. **`TryResumeWhenIdleAsync`**：父 idle 则引擎自动 resume（权威在引擎，**不是** WebUI）；父 busy 则仅注入  

v1：**单进程** Job；多实例下 `task_status` 无本地 Job 时回落 Session，不保证跨节点 resume。

### 6.5 并发与工具事件时序

- 同一父消息多个 `task`：允许并行多个 Child。  
- 同一 `task_id`：至多一个 running。  
- **所有工具**（含非 task）：启动即 yield Pending → Running，再执行；**禁止** `WhenAll` 结束后再补发状态事件。  
- Permission Ask：**全局串行**。  
- 前台 `task` 并行度：跟随「同消息多 tool_call 并行」模型（与 OpenCode 一致），不另加更严上限。

### 6.6 错误表

| 情况 | 行为 |
|------|------|
| 未知/不可委派 agent | 失败，不建 Child |
| 权限拒绝 | Rejected，不建 Child |
| Child 异常 | TaskFailed；前台 tool error；后台 inject task_error |
| 父取消 | 级联取消 + TaskFailed(cancelled) |
| task_status 超时 | 返回 timeout；不默认杀 Job |

---

## 7. WebUI 参考实现

### Task 卡片

| 区域 | 内容 |
|------|------|
| 标题行 | description + agent + 状态 |
| 进度区 | `TaskProgress`（工具步骤），可折叠 |
| 操作 | 「打开详情」→ `Kind=SubAgent` 的 Child |
| 完成 | 卡片完成；正文仍由主 Agent 总结 |

绑定：`originToolCallId` + `taskId`。  
后台 tool 已 Complete 后，卡片仍按 **`taskId`** 订阅后续 `TaskProgress`/`TaskCompleted`（勿只绑 tool call 生命周期）。

退役空 `HandleSubAgent` / 无数据源 SubAgent 块 → `HandleTask*` + Task 卡片。

Synthetic 注入消息：SessionMessage 上明确布尔/`Metadata["synthetic"]=true`；UI 弱化展示。

会话列表：`ListRoots`；详情列 `ListChildren(kind=SubAgent)`。

---

## 8. 对外契约表面

1. 工具：`task`、`task_status`（含 timeout / 状态枚举）  
2. Session：`CreateChild` / `Get` / `ListRoots` / `ListChildren(kind)` / 消息历史  
3. 事件：`TaskStarted` / `TaskProgress` / `TaskCompleted` / `TaskFailed`  
4. 调度：`InjectSynthetic` / `TryResumeWhenIdle`（引擎内部；宿主不应私自再实现一套）  
5. 权限：工具 Ask + 子 Agent 委派 Ask（串行）

---

## 9. 迁移策略

1. `SessionKind` + `PermissionSnapshot` + `CreateChildAsync`  
2. `ITaskEventProjector` + `IAgentLoopScheduler`；Task / task_status 真实现  
3. 删除 `HandleSubAgentCallAsync`；Task 仅走 ToolInvoker  
4. 收敛/替换 `BackgroundTaskManager`（Id = SessionId；统一 Loop）  
5. WebUI Task 卡片 + 打开详情  
6. 删除伪 subSessionId、旧 SubAgent 事件；Hook 迁移表（一期可双发）  
7. 旧参数别名一期后删除  
8. 历史伪 Id：**不迁移**；只保证新链路正确  

---

## 10. 测试验收

| 层 | 必测 |
|----|------|
| 单元 | OpenCode 同构派生向量（Plan edit deny、父 Session deny、默认禁 task/todowrite、子显式允许 task）；拒绝 Primary；续跑复用快照；running 冲突；Fork≠SubAgent 列表过滤 |
| 集成 | 前台交错 Progress；后台立即返回 + task_status；inject；idle 自动 resume；busy 仅注入；Ask 串行 |
| 契约 | 事件字段；Job Id = task_id；无 bg_* |
| WebUI | 卡片按 taskId 更新；打开详情；执行中有步骤反馈 |

---

## 11. 决策记录

| 项 | 选择 |
|----|------|
| UI 形态 | 混合：卡片进度 + 真实子 Session 详情 |
| 前台/后台 | v1 均支持；后台默认启用（相对 OpenCode experimental：**有意偏离**） |
| 消费端 | 契约优先；WebUI 参考实现 |
| 嵌套 | 默认禁子侧 `task`；仅子 Agent 显式允许才放开 |
| 架构 | Session-first |
| 结果可见性 | 卡片示步骤；详情全文；正文主 Agent 总结 |
| Session 关系 | **SessionKind: Root \| Fork \| SubAgent** |
| 权限派生 | **严格 OpenCode 同构**；全量交集延后 |
| 投影通道 | **ITaskEventProjector + 嵌套 yield 父流** |
| Resume | **引擎 LoopScheduler 权威**；非 UI |
| Ask | **全局串行** |
| 多实例 Job | v1 单进程语义 |

---

## 12. 架构评审闭合

| 评审项 | 规格落点 |
|--------|----------|
| Fork 与 SubAgent 撞车 | §4.1 SessionKind |
| 权限快照无持久化 | §4.2 / §5.4 |
| Task\* 投影通道 | §6.1 |
| 自动 resume / BTM 收敛 | §6.4 |
| 工具并发与 Ask | §5.4 / §6.5 |
| 状态三层 / task_status | §4.4 / §5.2 |
| CreateChild≠Fork | §4.3 |
| 结果抽取与模型继承 | §5.1 / §4.2 |
| 取消级联 | §6.3 |
| 旧参数 | §5.1 |

**剩余有意延后（非阻塞实现计划）：** 跨进程 Job、详情页独立多轮、`@` 提及、全量权限交集。
