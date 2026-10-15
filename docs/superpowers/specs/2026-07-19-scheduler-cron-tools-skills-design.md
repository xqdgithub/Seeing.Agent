# 定时任务 Tools + Skills 与 Session 出站绑定 — 设计方案

**日期:** 2026-07-19  
**状态:** 待实现  
**范围:** `Seeing.Agent.Scheduler` Tools/Skills、`Seeing.Session` 出站字段、投递反查、WebUI 会话/Job 配置  
**约束:** 不 git commit（除非用户另行要求）

**相关既有设计:** `2026-07-19-scheduler-channel-outbound-design.md`（Gateway Channel 出站协议）。本方案将出站目标从 Job.`dispatch.target.channel` **迁移到 Session 正式字段**；出站协议本身不变。

---

## 1. 背景与目标

### 问题

- `Seeing.Agent.Scheduler` 已有完整 `IScheduleManager` CRUD 与 WebUI，但 Agent 侧仅有 `/cron-list`、`/cron-run`、`/heartbeat-run`，**无法通过 LLM 管理定时任务**。
- 出站 `Channel`/`UserId` 写在 Job 配置上，导致 Tool、配置页、会话上下文多方重复关心 channel；与「任务绑会话、会话绑渠道」的直觉不符。

### 目标

1. 在 `AddSeeingScheduler()` 时自注册 **6 个 cron Tools** + **4 个 Skills**，供 Agent 管理日常定时任务。
2. Session 增加正式出站字段；Job 只绑 `SessionId`；投递时从 Session 反查 Channel/UserId。
3. WebUI 会话列表与会话页可展示并编辑 Channel/UserId，便于固定某会话到指定 channel。
4. Job 配置页去掉 Channel/UserId；**不保留** Job 侧旧字段兼容逻辑。

### 非目标（本版）

- `text` 型任务创建、精细 update、执行历史查询
- 斜杠命令 CRUD 扩容（保留现有 `/cron-list` 等不动）
- Gateway 真机出站 E2E 自动化
- 改动 Channel Host / QQ 出站协议本身

---

## 2. 决策摘要

| 项 | 选择 |
|----|------|
| 能力形态 | Tools + Skills（方案 C） |
| 操作范围 | 日常子集：list / create / delete / pause / resume / run |
| Skill 拆分 | 1 总览 + 按场景短 Skill |
| 创建能力 | 仅 `agent`；调度 `cron` / `interval` / `once`；投递默认当前 Session |
| 斜杠命令 | 保留现状，不扩 CRUD |
| Tool 粒度 | 每操作一个 Tool（对齐 Memory） |
| Skill 存放 | 嵌入程序集资源，启动时 `SkillManager.RegisterSkill` |
| Channel 归属 | Session 正式字段；Job 不存 Channel/UserId |
| 旧 Job Channel | **直接忽略并清理**，无 fallback |
| Gateway 回填 | 仅当 Session 字段为空时写入；用户手工设置可覆盖且不被入站覆盖 |
| WebUI | 会话管理/会话页展示可编辑；Job 编辑页移除 Channel/UserId |

---

## 3. 架构

```
AddSeeingScheduler()
        │
        ├─ AddSingleton<ITool> × 6
        │     cron_list / cron_create / cron_delete
        │     cron_pause / cron_resume / cron_run
        │
        └─ HostedService: SchedulerSkillRegistrationHostedService
              └─ SkillManager.RegisterSkill() × 4
                    cron-management
                    cron-create / cron-list-run / cron-lifecycle

SessionData.ChannelId / UserId  ◄── Gateway 空值回填 | WebUI 编辑
        │
        ▼
AgentJob 完成 → Dispatcher 仅从 Session 解析出站目标
```

- Tools 薄封装 `IScheduleManager`，不新增业务服务层。
- Skills 为 Markdown 嵌入资源；`Location` 可用虚拟路径（如 `embedded://scheduler/cron-create`）。
- 不向 `ToolContext` 透传 Channel（Tools 不感知 channel）。

---

## 4. 组件与 API 契约

### 4.1 Session 正式字段

在 `SessionData` 增加：

```csharp
/// <summary>出站渠道 ID（如 qq / wecom）；空表示仅本地 Session</summary>
public string? ChannelId { get; set; }

/// <summary>出站用户/对端 ID（渠道相关）；可空</summary>
public string? UserId { get; set; }
```

字段名固定为 `ChannelId` / `UserId`，与 `ChatOptions` 对齐。

**写入规则**

| 来源 | 行为 |
|------|------|
| Gateway / 入站对话 | 仅当对应字段为空时回填请求值 |
| WebUI 会话编辑 | 用户显式修改并 Save，可覆盖 |

### 4.2 Job 投递模型变更

- `DispatchTarget` **移除** `Channel`、`UserId`（停止序列化；加载旧 JSON 时忽略）。
- `JobDataKeys.DispatchChannel` / `DispatchUserId` 不再写入；加载时忽略。
- `RegisterUserJobAsync` / `AgentJob` / `GatewayScheduleDispatcher`：出站只读 Session。
- `jobs.json`：读入时丢弃 `dispatch.target` 上遗留的 `channel`/`userId`；下次 Save 不再写出。若项目已有 `JobsFile.Version` 迁移惯例，可升版本记录此次清理。

### 4.3 Tools

| Id | 职责 | 关键参数 |
|----|------|----------|
| `cron_list` | 列表 + intent/下次触发 | 无（本版不做 filter） |
| `cron_create` | 创建或替换 agent 任务 | `id?`, `name?`, `prompt`, `agent?`, `schedule` |
| `cron_delete` | 删除 | `id` |
| `cron_pause` | 暂停 | `id` |
| `cron_resume` | 恢复 | `id` |
| `cron_run` | 立即触发一次 | `id` |

`cron_create` 隐式默认：

- `TaskType = agent`
- `Dispatch.Target.SessionId = ToolContext.SessionId`
- Runtime 用模块默认超时等
- **不写** Channel/UserId

`schedule` 参数形状（示例）：

```json
{
  "type": "cron|interval|once",
  "cron": "0 9 * * *",
  "every": "6h",
  "runAt": "2026-07-20T09:00:00",
  "timezone": "Asia/Shanghai"
}
```

校验失败返回 `ToolResult` Failure，附合法示例。

### 4.4 Skills

| Name | 用途 |
|------|------|
| `cron-management` | 总览：何时用哪些工具、绑定 Session 语义 |
| `cron-create` | 收集 prompt/调度、确认后调用 `cron_create` |
| `cron-list-run` | 列表与立即执行 |
| `cron-lifecycle` | 暂停/恢复/删除；删除前先 list 确认 |

实现：`Seeing.Agent.Scheduler` 内嵌资源 + `SchedulerSkillRegistrationHostedService`（类似现有 `SchedulerCommandRegistrationHostedService`）。若 `SkillManager` 未注册（极少见），记录 Warning 并跳过。

### 4.5 WebUI

| 页面 | 变更 |
|------|------|
| `SessionsPage.razor` | 列表展示 ChannelId/UserId；可编辑并保存 |
| `Session.razor` | 设置区展示并可改 ChannelId/UserId，保存持久化 |
| `JobEditorModal.razor` | 移除 Channel/UserId 表单项，仅保留目标 Session |

---

## 5. 数据流

### 创建

```
用户 → Skill(cron-create) → cron_create(...)
  → IScheduleManager.CreateOrReplaceJobAsync
       TaskType=agent, SessionId=当前会话
  → jobs.json + Quartz
```

### 触发与投递

```
Quartz → AgentJob(SessionId)
  → 执行 Agent
  → GatewayScheduleDispatcher
       加载 Session → 取 ChannelId/UserId
       写 Session 消息
       若 ChannelId 非空 → PushChannelOutbound
       若为空 → 仅本地，Debug 日志，不视为任务失败
```

### 会话绑定

```
Gateway 入站（字段空）──回填──► Session.ChannelId/UserId
WebUI 编辑 ──────────覆盖──► Session.ChannelId/UserId
Job 配置 ───────────仅───► SessionId
```

---

## 6. 错误处理

| 场景 | 行为 |
|------|------|
| 调度表达式非法 | `cron_create` Failure + 示例 |
| Job 不存在 | delete/pause/resume/run Failure |
| Job Disabled | `cron_run` Failure（对齐现有 `TriggerResult`） |
| Session 无 Channel | 跳过出站，Debug 日志；任务成功不受影响 |
| 出站无注册 Host | Warning（现有行为）；任务执行可仍成功 |
| 删除 | Tool 直接删；Skill 要求先 list 再确认 |

---

## 7. 测试

### 单元

- 六个 Tool 对 `IScheduleManager` 的调用与错误分支
- `cron_create` 产出 Job 仅含 SessionId，无 Channel/UserId
- Session `ChannelId`/`UserId` 读写与持久化
- Dispatcher：有 Channel → 出站参数正确；无 Channel → 不出站且不失败
- 旧 jobs 加载忽略遗留 Channel/UserId；再保存后字段消失

### 集成（Scheduler.Tests）

- `AddSeeingScheduler` 后可解析到 6 个 `cron_*` ITool
- Skill 注册后 SkillManager 含 4 个 `cron-*`
- 创建 → RunOnce → Dispatcher 使用 Session 绑定（mock）

### WebUI

- 手工清单：SessionsPage / Session 展示编辑；Job 编辑页无 Channel/UserId

---

## 8. 实现落点（WHERE）

| 工作项 | 位置 |
|--------|------|
| Session 字段 | `Seeing.Session/Core/SessionData.cs` |
| 入站回填 | Gateway Orchestrator / Chat 入站路径 |
| 投递反查 | `GatewayScheduleDispatcher`、`AgentJob`、`ScheduleManager.RegisterUserJobAsync` |
| 模型清理 | `DispatchTarget`、`JobDataKeys`、jobs 加载迁移 |
| Tools | `Seeing.Agent.Scheduler/Tools/`（新建） |
| Skills 资源 + 注册 | `Seeing.Agent.Scheduler/Skills/` + HostedService |
| DI | `SchedulerServiceCollectionExtensions.AddSeeingScheduler` |
| WebUI | `SessionsPage.razor`、`Session.razor`、`JobEditorModal.razor` |
| 测试 | `Seeing.Agent.Scheduler.Tests`、必要时 Session/Gateway 单测 |

---

## 9. 成功标准

1. Agent 可通过 Tools + Skills 完成 list/create/delete/pause/resume/run。
2. 新建 Job 的持久化数据不含 Channel/UserId；旧数据加载后被丢弃。
3. 出站目标仅来自 Session；WebUI 可固定某会话的 Channel/UserId。
4. 现有斜杠命令与 Channel 出站协议仍可用（协议层不变）。
