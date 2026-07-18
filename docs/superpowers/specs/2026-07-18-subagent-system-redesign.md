# 子 Agent（Task）系统重设计

**日期:** 2026-07-18  
**状态:** 设计已确认，待实现计划  
**参考:** OpenCode `packages/opencode/src/tool/task.ts`、`agent/subagent-permissions.ts`  
**范围:** 核心引擎契约（Session / Task 工具 / 事件 / 后台）+ WebUI 参考实现；Gateway/ACP/TUI 按同一契约自行接入  

---

## 1. 问题

当前 Seeing 子 Agent 链路存在结构性缺陷，不适合打补丁式修补：

1. **双路径分叉**：`AgentExecutor.HandleSubAgentCallAsync` 特判 `task`，与注册的 `TaskTool` 行为不一致；真实执行绕过工具层。
2. **伪 Session**：Executor 使用合成 `parent@agent@guid`，未作为一等 `SessionData` 入库；续跑 / 打开详情 / 后台 Job 主键无法统一。
3. **事件未下发**：创建了 `SubAgentStarted` 却不 `yield`；子 Loop 事件被吞掉；WebUI `HandleSubAgent` 为空；`SubAgentBlockRenderer` 无数据源。
4. **权限旁路**：特判路径跳过工具权限与 `RequestSubAgentPermission`；Mode 白名单漏掉 `task`。
5. **契约空心**：`background` / `task_id` / `session_id` 等参数被解析或声明但未兑现；`BackgroundTaskManager` 与 Task 未收敛。

用户已确认目标形态：**混合 UI（父消息 Task 卡片可展示当前工具步骤 + 真实子 Session 详情）**、**前台阻塞 + 后台异步均进 v1**、**契约优先**、**子 Session 默认禁止再调 `task`**。

---

## 2. 目标与非目标

### 目标

1. 子 Agent = 带 `ParentSessionId` 的一等 Child Session；`task_id` ≡ Child Session Id。
2. 唯一执行引擎：对任意 Session 跑同一套 Prompt/Loop；`task` 只负责委派与调度。
3. 删除 Executor 对 `task` 的特判；收敛为单一 Task 工具实现。
4. 事件契约完整：`TaskStarted` / `TaskProgress` / `TaskCompleted` / `TaskFailed`，支撑卡片进度投影。
5. 前台阻塞与后台 Job（含 `task_status`、完成后向父 Session 注入结果）均为 v1 能力。
6. 权限派生：父 deny 不可被旁路；默认 deny 子侧 `task` / `todowrite`（除非子 Agent 显式允许）。
7. WebUI 作为契约参考实现：Task 卡片 + 打开详情；其他端不强制同期改完。

### 非目标（v1）

- `@` 提及直接调用子 Agent  
- 子 Session 详情页独立多轮产品化（续跑由主 Agent 再调 `task(task_id=...)` 即可）  
- 无限嵌套（默认禁 `task`）  
- 为 Gateway/ACP/TUI 各写一套编排逻辑  
- 保留旧 `SubAgentEvent` 空壳并行协议（应迁移或删除）

---

## 3. 选定方案

**Session-first（对齐 OpenCode）**

```
主 Session Loop
  └─ task 工具（经 ToolInvoker，无 Executor 特判）
       ├─ 创建/恢复 Child Session + 权限派生
       ├─ foreground: 阻塞执行 Child Loop
       └─ background: BackgroundJob(Id=Child.Id)；立即返回；完成后注入父 Session
事件：Child 完整历史写入 Child Session；父总线收 Task* 投影 → Task 卡片
```

否决：独立双引擎 Orchestrator（易重复管道）；纯事件投影 + 伪 Session（补丁架构）。

---

## 4. Session 模型

在现有 `Seeing.Session.Core.SessionData` 上演进（已有 `ParentSessionId`）：

| 字段 | 子 Agent 语义 |
|------|----------------|
| `Id` | `task_id`（续跑 / 轮询 / 打开详情主键） |
| `ParentSessionId` | 父会话；根会话为 null |
| `SelectedAgent` | 子 Agent 类型（如 `explore`） |
| `Title` | 来自 `description`，建议带 `(@agent)` 后缀 |
| `Status` | 运行态：`Active` / `Idle` / `Completed` / `Error` 等 |
| `Metadata` | 至少：`kind=subagent`、`description`；可选 `originToolCallId` |

`ISessionManager` 需具备：

- `CreateChildAsync(parentId, agent, title, permissionSnapshot)`
- `ListChildrenAsync(parentId)`
- `GetAsync` / 现有加载 API（按 `task_id` 续跑）

会话列表默认只展示根会话；子 Session 经父会话 children 或「打开详情」进入。

**禁止**：合成且不入库的伪 Session Id。

---

## 5. Task 工具与权限

### 5.1 工具

| 工具 | 作用 |
|------|------|
| `task` | 创建/续跑 Child，前台或后台执行 |
| `task_status` | 查询后台任务；`wait=true` 时可阻塞至完成或超时 |

**`task` 参数：**

| 参数 | 必需 | 说明 |
|------|------|------|
| `description` | 是 | 短标题 |
| `prompt` | 是 | 子任务说明（须自洽） |
| `subagent_type` | 是 | 目标 Agent |
| `task_id` | 否 | 续跑同一 Child |
| `command` | 否 | 触发来源 |
| `background` | 否 | `true` 后台执行 |

**返回给主模型（非用户正文）：**

```text
task_id: {childSessionId}
state: running|completed|error

<task_result>
...
</task_result>
```

工具 metadata：`parentSessionId`、`sessionId`、`agent`、`model`、`background`、`originToolCallId`。

### 5.2 可委派目标

- 仅 `Mode ∈ { SubAgent, All }` 且未禁用；拒绝纯 `Primary`。
- 动态工具描述与 Prompt 子 Agent 列表同源（同一查询 API）。

### 5.3 权限三层

1. **父侧调用 `task`**：走统一工具权限；Ask 时可用 `RequestSubAgentPermission(subagent_type, prompt)`；尊重 `AllowedAgents` / `IsDelegableTo`。
2. **Child 会话权限快照**（对齐 OpenCode `deriveSubagentSessionPermission`）：
   - 继承父 Agent 的关键 deny（尤其 edit/写类，防止 Plan 被旁路）
   - 继承父 Session 的 deny / 目录类限制
   - 交集子 Agent 自身策略（子不能比父更宽）
   - **默认**：子未显式允许则 deny `task`、deny `todowrite`
3. **Child Loop 内**：统一权限通道；可复用父 `PermissionChannel`，策略用 Child 快照。

### 5.4 续跑与冲突

- 有效 `task_id`：复用 Child，追加 user prompt，保留历史。
- 同一 `task_id` 已 `running`：拒绝，提示用 `task_status`。
- 权限未通过：不创建 Child。

### 5.5 工具可见性

- Primary（及 All）暴露 `task` / `task_status`。
- SubAgent 默认不暴露 `task`（除非该 Agent 权限显式允许）。
- 修复硬编码 Mode 白名单漏 `task` 的问题；以 Allowed/Denied + 会话快照为准。

---

## 6. 事件契约与调度

### 6.1 原则

- Child 事件写入 Child Session（完整历史真相源）。
- 父侧订阅 **投影事件**，绑定到发起委派的 `task` tool call。
- 跨会话字段：`SessionId`、`ParentSessionId?`、`TaskId`、`OriginToolCallId?`。
- 事件类型挂在现有 `IMessageEvent` / `MessageEventType` 上扩展（新增 `TaskStarted` 等），不另起平行总线；Gateway/UI 继续走统一事件流适配。

### 6.2 任务生命周期事件

| 事件 | 何时 | 主要载荷 |
|------|------|----------|
| `TaskStarted` | Child 开始 | taskId, agent, description, background, originToolCallId |
| `TaskProgress` | 可展示步骤 | stepKind: `stream` \| `tool_pending` \| `tool_running` \| `tool_complete` \| `text`；工具名、短状态、可选短预览 |
| `TaskCompleted` | 成功结束 | taskId, resultText, duration |
| `TaskFailed` | 失败/取消 | taskId, error, cancelled? |

`TaskProgress` 有意降采样，避免整段 stream 打进父总线。打开详情读 Child Session，不靠 Progress 拼历史。

旧 `SubAgentStarted` / `SubAgentCompleted`：迁移到上述事件后删除或作废弃别名，不得长期双轨。

### 6.3 前台

父 Loop → `task` → 权限 → 创建/恢复 Child → `TaskStarted` → 阻塞跑 Child Loop（写 Child + `TaskProgress`）→ `TaskCompleted` + `<task_result>` → 父 Loop 继续。  
父 abort 级联取消 Child，发 `TaskFailed(cancelled)`。

### 6.4 后台

父 Loop → `task(background=true)` → `TaskStarted` → `BackgroundJob.Start(id=Child.Id)` → 立即返回 `state=running`。  
主模型用 `task_status` 查询（`wait` 可选）。  
Job 结束后：向父 Session 注入 synthetic 消息（含 task_id / state / result 或 error）→ `TaskCompleted`/`TaskFailed` → **v1 必做**：若父 Session 当时 idle，自动 resume 父 Loop（对齐 OpenCode）；若父仍 busy，仅注入，待后续轮次消费。  

**Background Job Id ≡ Child Session Id ≡ `task_id`**，禁止并行的 `bg_xxx` 主键体系。

### 6.5 并发与工具事件时序

- 同一父消息多个 `task`：允许并行多个 Child。
- 同一 `task_id` 同时仅一个 running Job。
- 父侧工具执行须先发 Pending/Running，再执行（修复 WhenAll 完成后才发事件的问题）。
- 后台 `task`：启动成功即可 ToolCall Complete（结果后续靠 Progress/Completed/注入消息）。

### 6.6 错误表

| 情况 | 行为 |
|------|------|
| 未知/不可委派 agent | task 失败，不建 Child |
| 权限拒绝 | Rejected，不建 Child |
| Child 异常 | TaskFailed；前台写入 tool error；后台 inject task_error |
| 父取消 | 级联取消 Child + Job |

---

## 7. WebUI 参考实现

### Task 卡片（父消息）

| 区域 | 内容 |
|------|------|
| 标题行 | description + agent + 状态 |
| 进度区 | 订 `TaskProgress`：工具步骤（名、状态、短预览），可折叠 |
| 操作 | 「打开详情」→ Child Session（`task_id`） |
| 完成 | 卡片标完成；**对话正文仍由主 Agent 总结**，不自动把 `<task_result>` 当助手回复 |

绑定键：`originToolCallId` + `taskId`。

退役空 `HandleSubAgent` / 无数据源的 SubAgent 块，改为 `HandleTask*` + Task 卡片组件。

### 其他

- 会话列表默认根会话；详情可列 children。
- 注入父 Session 的 synthetic 消息须标记，UI 可弱化展示。
- 卡片与详情不一致时，以 Child Session 存储为准。

### 可见性（已确认）

- 卡片展示当前执行步骤（含 tool）。
- 完整过程在子 Session。
- 用户可读的结论由主 Agent 在父对话中总结（OpenCode 习惯 + 卡片进度）。

---

## 8. 对外契约表面

稳定表面（供 Gateway/ACP/TUI 接入）：

1. 工具：`task`、`task_status`（schema 与返回格式）
2. Session API：CreateChild / Get / ListChildren / 消息历史
3. 事件：`TaskStarted` / `TaskProgress` / `TaskCompleted` / `TaskFailed`
4. 权限：工具 Ask + 可选子 Agent 委派 Ask

WebUI 仅为订阅者之一。

---

## 9. 迁移策略

1. Child Session API + 权限派生 + Task / task_status 真实现 + Task* 事件  
2. 删除 `HandleSubAgentCallAsync`；Task 走 ToolInvoker  
3. Background Id 与 SessionId 统一  
4. WebUI Task 卡片 + 打开详情  
5. 删除伪 subSessionId、空 SubAgent 事件路径、死代码渲染器（或改造复用）

---

## 10. 测试验收

| 层 | 必测 |
|----|------|
| 单元 | 权限派生；拒绝 Primary；默认禁 task；续跑；running 冲突 |
| 集成 | 前台返回 result；后台立即返回 + task_status；完成后注入；父 idle 时自动 resume |
| 契约 | 事件字段完整；Progress 与 Child 消息可抽样对齐 |
| WebUI | 卡片绑定与进度；打开详情；无「执行中无反馈、结束瞬间闪完」 |

---

## 11. 决策记录

| 项 | 选择 |
|----|------|
| UI 形态 | 混合：卡片进度 + 真实子 Session 详情 |
| 前台/后台 | v1 均支持 |
| 消费端 | 契约优先；WebUI 参考实现 |
| 嵌套 | 默认禁子侧 `task`，需显式权限 |
| 架构 | Session-first（OpenCode 同构） |
| 结果可见性 | 卡片示步骤；详情看全文；正文由主 Agent 总结 |
