# 心跳重构 Release Notes

**日期:** 2026-11-07
**相关模块:** `Seeing.Agent.Scheduler`、`Seeing.Session`、`Seeing.Agent.WebUI`

## 一句话

心跳不再是一条独立的 `HeartbeatJob` + `HeartbeatOptions`，而是调度执行内核上的一个**执行画像**加 `jobs.json` 里的**独立槽位**；上层入口是 `IHeartbeatService`，与定时任务共用内核但不进入对方的集合。

## 架构

```
上层（分开）
  定时任务面  IScheduleManager 用户任务 API / cron_* / /cron-jobs
  心跳面      IHeartbeatService / heartbeat_* / /heartbeat

下层（一套）
  JobsFile（Jobs + Heartbeat 槽位）
  QuartzSchedulerEngine（只注册 AgentJob）
  ScheduleRunProfile（一次执行的身份）
  IScheduledJobDispatcher（按 DispatchLayout 写回）
```

边界约束：

- Quartz、`jobs.json`、`jobs_history`、`_runningJobs` 只由 `ScheduleManager` 写。`IHeartbeatService` 转调它。
- `_heartbeat` 是保留 id：`CreateOrReplaceJobAsync` / `DeleteJobAsync` / `SetJobIntentAsync` 抛 `InvalidOperationException`；`RunJobOnceAsync` 返回 `NotFound`。
- 定时任务面（`ListJobsAsync`、`GetAllJobStatusesAsync`、`cron_*`、`/cron-jobs`、首页、Gateway `/api/admin/jobs`）只遍历 `Jobs`，结构性看不到心跳。

## 修复的行为缺陷

| 缺陷 | 现在 |
|---|---|
| 默认目标下心跳输出被丢弃 | 结果始终写回目标会话（助手消息，已消费） |
| 心跳执行历史从不落盘 | `jobs_history/_heartbeat.json` 正常写入 |
| 心跳状态恒为 `Disabled` | `GetJobStatusAsync` 覆盖槽位，返回真实状态 |
| 活跃时段跳过 / Hook 拦截后「立即执行」永久冲突 | `AgentJob` 每条返回路径都通知监听器，运行锁正常释放 |
| 调度器未启动时「已触发」假成功 | `RunNowAsync` 返回 `NotFound` / `Disabled` 并说明原因 |
| 非法间隔让整个调度器起不来 | 单任务注册失败只记 Error 并跳过 |
| 页面改输入框即改活配置 | 页面绑定快照深拷贝，保存成功后才替换 |
| 活跃时段解析失败等于不限制 | 改用 `ScheduleSpec.Windows`，与 cron 同一套校验 |
| 心跳会话来源是 `User` | 新建会话写 `SessionOrigin.Heartbeat` |
| 每次启动约 5 秒打一次心跳 | 心跳始终携带 `lastRunAt` 锚点，按「上次 + 间隔」起算，misfire 忽略 |
| `last` / `inbox` 目标不可靠 | 删除。目标只有会话与 conversation |
| 定时任务总数含心跳 | 引擎计数排除保留 id |

## 破坏性变更（不做迁移）

- `SchedulerOptions.Heartbeat`、`HeartbeatOptions`、`ActiveHoursOptions`、`HeartbeatTargets` 删除。旧 `seeing.json` 的 `Scheduler.Heartbeat` 被忽略，下次保存调度器设置时从文件消失；**不生成**等价心跳槽位。
- `HeartbeatJob`、`ActiveHoursChecker` 删除。`AgentJob` 是唯一 Job 类型。
- `IScheduleManager.ReloadHeartbeatAsync` 删除。
- `RunJobOnceAsync("_heartbeat")` 返回 `NotFound`。立即执行改走 `IHeartbeatService.RunNowAsync`。
- `scheduler.heartbeat.before` / `after` 不再触发；改用 `scheduler.job.before_execute` / `after_execute`，读载荷 `source=heartbeat`。
- `ISessionManager.EnsureSessionAsync` 增可选参数 `SessionOrigin? origin = null`（仅新建时生效）。实现 `ISessionManager` 的外部类型需补该参数。

## 已知边界

- 心跳写进**已有用户会话**时该会话 `Origin` 保持 `User`；来源区分靠消息 `Kind=heartbeat`。`SessionOrigin` 只承诺本功能新建的会话。
- 心跳只支持间隔 + 生效时段，不接受 cron 表达式与一次性时间（`HeartbeatService.Apply` 强制清空）。
- 带生效时段的心跳走窗口触发器（`DailyTimeInterval`），窗口内按窗口边界对齐，不叠加 `lastRunAt`；重启后从下一个窗口边界重新对齐。
- 投递布局：心跳只落一条 `Presentation=Assistant`（已消费）消息；cron 保持「系统提示 + 助手结果」两条。
- 页面首屏在 `WorkspacePageShell` 加载占位下渲染，与其余管理页一致。

## 测试

`tests/capabilities/Seeing.Agent.Scheduler.Tests` 178 个用例，其中：

- `ScheduleRunProfileTests` — 画像默认值与 JSON 往返
- `AgentJobRunProfileTests` — 画像驱动身份（Hook 来源、提醒 kind、结果 Source、投递布局）
- `SessionScheduleDispatcherLayoutTests` — `AssistantOnly` 只落一条已消费消息
- `HeartbeatReservedIdTests` — 保留 id 在四个 API 上被拒
- `HeartbeatTriggerAnchorTests` — 锚点/无锚点/逾期/未启动删除
- `HeartbeatSlotTests` — 槽位反序列化、注册画像、禁用保留槽位、列表不可见
- `HeartbeatServiceTests` — 快照、局部更新、校验、未配置/未启动语义
- `HeartbeatToolsTests` — 三个工具的输出与分支文案
- `HeartbeatEndToEndTests` — 保存 → 注册 → 执行 → 投递 → 历史 → 隔离闭环

另：`EnsureSessionOriginTests`（`Seeing.Session.Tests`）守护来源只写新建。
