# 心跳功能重构设计

**日期:** 2026-11-07
**状态:** 已按架构审查修订（一期交付）
**范围:** 调度执行内核的身份与投递参数化；心跳作为独立上层入口。定时任务的调度语义、列表与工具面保持不变。

---

## 1. 问题

心跳是挂在调度器上的一条旁路：独立的 `HeartbeatJob`、独立的 `HeartbeatOptions`、独立的活跃时段检查。定时任务已经收敛到 Quartz、`ScheduledJobSpec`、投递目标矩阵和执行历史，心跳没有跟上，于是出现这些可复现的行为错误：

| # | 现象 | 原因 |
|---|---|---|
| 1 | 默认目标下心跳跑完什么都不留 | `HeartbeatJob` 在 `Target=main` 时跳过投递 |
| 2 | 没有执行历史 | `OnJobExecutedAsync` 跳过 `_` 前缀 |
| 3 | 状态查询恒为 Disabled | 心跳不在 `_jobsFile.Jobs`，查找失败直接返回 Disabled |
| 4 | 「立即执行」会永久冲突 | 活跃时段跳过和 Hook 拦截不通知监听器，`_runningJobs` 残留 |
| 5 | 调度器没启动时按钮仍报已触发 | `TriggerJobAsync` 在 scheduler 为 null 时直接 return |
| 6 | 一个非法间隔能让整个调度器起不来 | 心跳注册异常没有和用户任务隔离开 |
| 7 | 页面改输入框就改了活配置 | 表单绑定的是配置缓存上的同一对象 |
| 8 | 活跃时段写错等于不限制 | `ActiveHoursChecker` 解析失败时返回 true |
| 9 | 心跳会话来源仍是 User | `SessionOrigin.Heartbeat` 没有任何赋值点 |
| 10 | 每次进程启动约 5 秒就打一次心跳 | interval 触发器固定 `StartAt(now+5s)` |
| 11 | `last` / `inbox` 不可用 | `last` 依赖未接线的目录；`inbox` 会创建名为 `_inbox` 的普通会话 |
| 12 | 定时任务页的任务总数比列表多 1 | Quartz 计数包含 `_heartbeat` |

---

## 2. 架构

两层，一期一起交付。下层是调度执行内核，cron 和心跳共用。上层是两个互不进入对方集合的入口。

```
上层（分开，各自的 API / 工具 / 页面）
  定时任务面    IScheduleManager 的用户任务 API
                cron_* 工具、/cron-jobs、首页任务行、Gateway /api/admin/jobs
  心跳面        IHeartbeatService
                heartbeat_* 工具、/heartbeat、/heartbeat-run

下层（一套设施，唯一写入者是 ScheduleManager）
  JobsFile                 Jobs 列表 + Heartbeat 槽位
  QuartzSchedulerEngine    只注册 AgentJob
  ScheduleRunProfile       一次执行的身份（来源、提醒 kind、投递布局、新建会话来源）
  AgentJob                 只读画像，不再写死 cron
  IScheduledJobDispatcher  按画像选择投递布局
  执行历史 / 运行锁 / Quartz 状态
```

约束：

- Quartz、`jobs.json`、`jobs_history`、`_runningJobs` 只由 `ScheduleManager` 写。`IHeartbeatService` 是心跳的唯一外部 API，内部转调管理器，不自己注册触发器。
- 定时任务面只遍历 `JobsFile.Jobs`。心跳不在这个集合里。
- 保留 id `_heartbeat` 不能经用户任务 API 创建、替换、删除、改意图或触发。`cron_create` / `cron_run` 因此无法碰到心跳。
- 心跳页和定时任务页不互相投影。进度事件仍从管理器发出，定时任务页按「id 属于 Jobs」过滤后再弹通知。

---

## 3. 决策

| 项 | 决定 |
|---|---|
| 存储 | `JobsFile.Heartbeat` 独立槽位，类型仍是 `ScheduledJobSpec`，id 固定 `_heartbeat` |
| 未配置 | 槽位为 null。不在首次访问时创建默认配置，也不因此注册 Quartz 任务 |
| 执行类型 | 删除 `HeartbeatJob`。引擎只注册 `AgentJob` |
| 身份 | `ScheduleRunProfile` 随 JobData 下发。缺画像时等于今天的 cron 行为 |
| 投递 | 内核支持两种布局。cron 保持现状；心跳只用「一条已消费的助手消息」 |
| 窗口 | 心跳用 `ScheduleSpec.Windows`。删除 `ActiveHoursChecker` |
| 目标 | 会话 id，或出站 conversation。删除 `main` / `last` / `inbox` |
| 节律 | 只改心跳触发器的起算点。用户 interval 任务仍是启动后约 5 秒、misfire 立即补一次 |
| 配置迁移 | 不做。`Scheduler.Heartbeat` 删除后，旧 JSON 被忽略 |
| 模块 | 仍在 `scheduler` 模块内 |
| Hook | 只走 `scheduler.job.before_execute` / `after_execute`，载荷带 `source`。心跳专用 Hook 不再触发 |
| 交付 | 一期。实现顺序是内核、槽位、服务、页面，同一次交付 |

---

## 4. 共享内核

### 4.1 执行画像

`Seeing.Agent.Scheduler.Models.ScheduleRunProfile`。注册任务时序列化进 `JobDataKeys.RunProfile`。`AgentJob` 启动时读一次；缺键或解析失败则使用 cron 默认画像，用户任务行为不变。

| 字段 | cron 默认 | 心跳 |
|---|---|---|
| `Source` | `cron` | `heartbeat` |
| `ReminderKind` | `cron` | `heartbeat` |
| `SenderId` | `cron` | `heartbeat` |
| `SessionKeyPrefix` | `cron` | `hb` |
| `OriginWhenCreated` | `SessionOrigin.Scheduler` | `SessionOrigin.Heartbeat` |
| `Layout` | `PromptAndResult` | `AssistantOnly` |

`AgentJob` 用画像替换现在写死的常量，覆盖这些位置：

- Hook 载荷里的 `source`（Hook 点仍是 `SchedulerJobBeforeExecute` / `SchedulerJobAfterExecute`）
- `JobExecutionResult.Source`，从而执行历史的 `Source` 正确
- 发给模型的提醒信封 kind
- `AgentContext.Metadata["source"]`
- `DispatchRequest` 的 `Source`、`ReminderKind`、`SenderId`、`Layout`
- `ScheduleConversationResolver` 新建会话时的 id 前缀、标题前缀和 `Origin`

`scheduler.heartbeat.before` / `scheduler.heartbeat.after` 标记为过时，本期内不再触发。仓库内没有订阅者。替代方式是任务 Hook 载荷中的 `source=heartbeat`。

### 4.2 投递布局

`DispatchRequest`（Abstractions）增加三个字段，默认值等于今天的 cron：

```csharp
public string ReminderKind { get; init; } = SystemReminder.Kinds.Cron;
public string SenderId { get; init; } = "cron";
public DispatchLayout Layout { get; init; } = DispatchLayout.PromptAndResult;
```

`DispatchLayout`：

| 值 | 行为 |
|---|---|
| `PromptAndResult` | 现状。先写一条 `Presentation=System` 的任务提示（未读，下次 Wake 会带上），再写一条 `Presentation=Assistant` 的结果（生来已消费） |
| `AssistantOnly` | 只写一条助手结果。不写系统提示，因此不会在目标会话留下未读输入 |

`SessionScheduleDispatcher` 按 `Layout` 分支。助手消息的 `Kind` 和 `Sender.Id` 用请求上的字段，不再写死 `cron`。`PromptAndResult` 的系统提示继续从信封解包 kind（cron 路径不变）。

`ChannelDeliveryScheduleDispatcher` 仍只出站结果正文，来源字符串用 `scheduler.{Source}`，心跳即为 `scheduler.heartbeat`。它不写会话，不看 `Layout`。

心跳选择 `AssistantOnly` 的原因：心跳会写进用户正在使用的会话。系统提示是未读输入，下一次用户发消息会把心跳指令交给主链。助手结果是已消费记录，只留痕、不唤醒、不进下一轮输入。

这不改变 cron。cron 的「提示 + 结果」是现有契约，本次不动。

### 4.3 新建会话的来源

`ISessionManager.EnsureSessionAsync` 增加可选参数 `SessionOrigin? origin = null`。只在**这次调用新建**会话时写入。缓存或磁盘上已有的会话保持原 `Origin`，并发 Ensure 仍由先进入的那次创建参数生效。

`ScheduleConversationResolver` 在「仅 conversation、每次新建会话」这条路径把画像的 `OriginWhenCreated` 和 `SessionKeyPrefix` 传进去。会话 id 形如 `{prefix}_{jobId}_{timestamp}_{suffix}`，心跳前缀是 `hb`。

已有用户会话被心跳写回时，`Origin` 保持 `User`。来源标记只承诺「本功能新建的会话」。写进用户会话的区分靠消息 `Kind=heartbeat`，不靠把用户会话改成心跳会话。因此这条消息不会把用户会话从任务视图里拿掉，也不会错误地关掉该会话的反思资格；反思看的是会话来源，心跳内容靠 kind 识别。

### 4.4 保留 id

`ScheduleManager` 把 `_heartbeat` 视为保留 id：

| 用户任务 API | 行为 |
|---|---|
| `CreateOrReplaceJobAsync` | 拒绝，不写入 `Jobs` |
| `DeleteJobAsync` / `SetJobIntentAsync` | 拒绝 |
| `RunJobOnceAsync` | 返回 `NotFound` |
| `GetJobAsync` / `ListJobsAsync` | 看不到心跳 |
| `GetJobStatusAsync` | 先查 `Jobs`，再查心跳槽位。供心跳服务取真实 Quartz 状态，定时任务列表不会按这个 id 查询 |
| `GetAllJobStatusesAsync` | 只含 `Jobs` |

立即执行心跳只走 `IHeartbeatService.RunNowAsync` → 管理器内部方法。内部方法在调度器未启动或槽位为 null 时不返回 `Accepted`。

`cron_run` 今天在 `GetJobAsync` 为空时仍会 `RunJobOnceAsync`。保留 id 在 `RunJobOnceAsync` 返回 `NotFound` 之后，这条路径不再触发心跳。

### 4.5 历史、状态、计数

- 去掉 `OnJobExecutedAsync` 里 `jobId.StartsWith('_')` 的跳过。`_heartbeat.json` 是合法文件名。
- 回写 `LastRunAt` 时，id 命中心跳槽位则写槽位，不要求它出现在 `Jobs` 里。
- `GetJobStatusAsync` 能查到槽位后，心跳不再恒为 Disabled。`AgentJob` 每条返回路径都通知监听器，`_runningJobs` 不再残留。
- `QuartzSchedulerEngine.GetStatusAsync` 与引擎的 `GetAllJobStatusesAsync` 在计数和枚举时排除 `_heartbeat`。心跳自己的状态只经 `IHeartbeatService`。

### 4.6 心跳触发器的起算

只在注册心跳时传入锚点。`BuildSingleTrigger` 对用户任务的 interval 分支保持 `StartAt(now+5s)` 和 `NextWithExistingCount`。

心跳 interval（无窗口）：

- misfire 使用 `DoNothing`（错过的节拍丢弃，不补跑）
- 有 `LastRunAt` 且 `LastRunAt + every` 仍在将来：`StartAt` 该时刻
- 否则：`StartAt(now + every)`，不 `StartNow`

`LastRunAt` 按本地墙钟解释，与 `ScheduleSpec.Timezone` 一致，不用 UTC 混算。

心跳 interval（有窗口）：走现有 `BuildWindowIntervalTriggers`（`DailyTimeInterval` + `DoNothing`）。窗口本身就是锚点，不再叠加 `LastRunAt`。重启不会在窗口外立刻触发。

启动顺序：取得 Quartz 实例后、`Start` 之前，删除持久化存储里的 `_heartbeat`。然后 `Start`，再按槽位 Upsert。这样升级后的第一次启动不会先按旧触发器 misfire 一次、再按新触发器打一次。用户任务的持久化触发器不动。

单个任务注册失败（非法 cron、非法间隔）只记错误并跳过该任务，不中断 `StartAsync`。这条同时保护用户任务和心跳。

---

## 5. 心跳上层

### 5.1 槽位

```csharp
public class JobsFile
{
    public int Version { get; set; } = 0;
    public List<ScheduledJobSpec> Jobs { get; set; } = new();
    public ScheduledJobSpec? Heartbeat { get; set; }
}
```

不把 `Version` 默认值改成 3，也不新增迁移步骤。旧文件没有该属性时反序列化为 null。

槽位为 null 表示未配置：不注册 Quartz 任务，页面是空状态，`RunNowAsync` 返回「未配置」。只有 `UpdateAsync` 成功后才写入槽位并注册。

`ScheduledJobSpec` 不新增字段。心跳使用其中已有的 `Intent`、`Prompt`、`Agent`、`Model`、`Mode`、`Schedule`（type 固定 interval）、`Dispatch`、`Runtime`、`LastRunAt`。

删除 `SchedulerOptions.Heartbeat`、`HeartbeatOptions`、`ActiveHoursOptions`、`HeartbeatTargets`、`JobDataKeys.ActiveHours`、`JobDataKeys.HeartbeatTarget`。

### 5.2 `IHeartbeatService`

心跳的唯一外部 API。页面、三个工具、`/heartbeat-run` 只依赖它。

```csharp
public interface IHeartbeatService
{
    Task<HeartbeatSnapshot> GetAsync(CancellationToken ct = default);
    Task<HeartbeatSnapshot> UpdateAsync(HeartbeatUpdate update, CancellationToken ct = default);
    Task<TriggerResult> RunNowAsync(CancellationToken ct = default);
    Task<bool> CancelAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(
        int limit, int offset = 0, CancellationToken ct = default);
}
```

`HeartbeatSnapshot` 含：槽位深拷贝（null 表示未配置）、Quartz `JobStatus`、最近一条历史、调度器是否已启动。

`HeartbeatUpdate` 是局部更新：`Enabled`、`Prompt`、`Every`、`Windows`、`Timezone`、`SessionId`、`Conversation`、`Agent`、`Model`、`TimeoutSeconds`。未出现的字段保持不变。服务在副本上应用变更并校验，通过后才交给管理器写入。

校验：

- `Every` 能被 `ScheduleExpressionParser.ParseInterval` 解析
- `Prompt` 非空
- `SessionId` 与完整 conversation 至少有一项
- `Windows` 通过 `ScheduleWindowsValidator`（与 cron 同一套规则）
- `Schedule.Type` 固定为 interval。心跳不提供 cron 表达式和一次性时间

`Enabled=false` 写成 `ScheduleIntent.Disabled`，管理器从 Quartz 移除该 job，槽位保留。

从 `IScheduleManager` 删除 `ReloadHeartbeatAsync`。调度器设置卡保存的是启用、时区、并发和持久化，不再重载心跳。设置卡上的「重载配置」只重新注册 `Jobs` 里的用户任务。

管理器对服务暴露的内部能力（不进入 `IScheduleManager`）：读槽位副本、保存槽位并注册或移除、内部触发、取消。画像由管理器在注册时按第 4.1 节的心跳列写入，服务不拼 JobData。

### 5.3 工具与技能

三个工具，不并入 `cron_*`。描述遵守 `docs/architecture/04` §9（R1–R9），正文用 `ScheduleTextFormatter` 已有的调度描述，并补最近一次执行的时间、成败和错误摘要。

| 工具 | 行为 |
|---|---|
| `heartbeat_get` | 配置、是否启用、下次触发、上次执行 |
| `heartbeat_update` | 局部更新，返回更新后的描述 |
| `heartbeat_run` | `RunNowAsync`。未配置、调度器未启动、正在执行、已接受，四者文案不同 |

技能 `Skills/heartbeat-management/SKILL.md`，随调度器模块激活注册、停用时注销。`SchedulerModule.ProvidedTools` 加入这三个 id。

`/heartbeat-run` 改为调用 `RunNowAsync`。

### 5.4 页面 `/heartbeat`

绑定对象是 `GetAsync` 返回的深拷贝。保存成功后重新取快照。输入过程不写配置缓存。

三段：

1. **状态。** 启停（`Enabled`）、调度器是否在跑、下次触发（复用 `JobNextRunTimer`）、上次执行的时间与成败。运行中可取消。调度器未启动时不提供「立即执行」，并说明要先启动调度器。
2. **配置。** 间隔、生效时段（复用定时任务编辑器那套窗口编辑和校验）、时区、目标会话、出站 conversation（复用 `ConversationPicker`）、Agent、模型、超时、Prompt。附一份可填入的示例提示。
3. **历史。** `GetHistoryAsync` 的记录表，复用 `ExecutionResultModal`。

未配置时只显示说明和「保存这份配置」的主按钮，不显示假的下次触发时间。

定时任务页订阅执行通知时，忽略不属于 `Jobs` 的 id，避免弹出 `_heartbeat`。首页和 Gateway 任务列表已经只扫 `Jobs`，不用改数据源。

---

## 6. 破坏性变更

不做配置导入。

| 旧内容 | 结果 |
|---|---|
| `seeing.json` 的 `Scheduler.Heartbeat`（含 Prompt、Every、Target、ActiveHours、Agent、SessionId） | 反序列化忽略。下次保存调度器节时该键消失。不生成 `JobsFile.Heartbeat` |
| `main` / `last` / `inbox` | 删除。目标改为会话 id 或 conversation |
| `ActiveHoursChecker` | 删除。时段用 `ScheduleSpec.Windows` |
| `HeartbeatJob` | 删除 |
| `scheduler.heartbeat.before` / `after` | 常量保留并标记过时，不再触发 |
| `IScheduleManager.ReloadHeartbeatAsync` | 删除 |
| `RunJobOnceAsync("_heartbeat")` | 返回 `NotFound` |

需要改写的测试：`SchedulerOptionsProviderTests`、`SchedulerTestWorkspace`、`SchedulerIntegrationTests` 中两条心跳触发用例、`ActiveHoursCheckerTests`、直接 `new HeartbeatJob` 的旁路装配测试（改为经 `AgentJob` 与心跳画像）。

---

## 7. 测试

| 用例 | 断言 |
|---|---|
| 槽位排除 | `ListJobsAsync`、`GetAllJobStatusesAsync`、`cron_list` 不含 `_heartbeat` |
| 保留 id | `cron_create` / `CreateOrReplaceJobAsync` 使用 `_heartbeat` 被拒绝；`cron_run` 返回不存在 |
| 未配置 | 新 `jobs.json` 的槽位为 null；启动后 Quartz 无该 job；`RunNowAsync` 不是 `Accepted` |
| 状态 | 保存并启用后 `GetJobStatusAsync("_heartbeat")` 不是 Disabled，且有下次触发时间 |
| 历史 | 执行结束后历史有一条，`Source=heartbeat`，槽位 `LastRunAt` 已更新 |
| 画像 | 模型消息 kind、历史 Source、出站来源、助手消息 kind 与 Sender 均为 heartbeat |
| 投递布局 | 目标会话只有一条助手消息，没有未读系统提示；cron 任务仍是提示加结果两条 |
| 运行锁 | 窗口外或执行前 Hook 阻断之后，再次 `RunNowAsync` 仍可接受 |
| 未启动 | 调度器未 `Start` 时 `RunNowAsync` 不是 `Accepted` |
| 注册隔离 | 心跳间隔非法时，用户任务仍注册成功 |
| 节律 | 心跳无窗口时下次触发是 `LastRunAt+every` 或 `now+every`，不是 `now+5s`。用户 interval 任务仍约为 5 秒后 |
| 启动 | `Start` 前已删除旧的 `_heartbeat` 触发器，升级后第一次启动不会先 misfire 再按新表触发 |
| 局部更新 | 只改 `Enabled` 时 Prompt、Every、Agent 不变 |
| 来源 | 心跳因 conversation 新建的会话 `Origin=Heartbeat`、id 前缀 `hb_`。已存在的用户会话 Origin 不变 |
| 计数 | 用户任务 N 个且心跳已启用时，调度器状态的任务总数是 N |
| 通知 | 心跳执行完成后，定时任务页的成功/失败通知不出现 |

---

## 8. 实现顺序（同一次交付）

1. 画像、`DispatchRequest` 布局、`AgentJob` 与两个投递器改读画像。cron 默认画像锁住现有行为。
2. 保留 id、状态查找、历史落盘、计数排除、启动前删除旧心跳触发器、心跳专用起算。
3. `JobsFile.Heartbeat`、删除 `HeartbeatJob` 与旧配置类型、按槽位注册。
4. `IHeartbeatService`、三个工具、技能、命令。
5. `/heartbeat` 页面、定时任务页通知过滤、设置卡去掉心跳重载。
6. `EnsureSessionAsync` 的可选 `origin`，以及解析器按画像新建会话。
7. 第 7 节的测试，并改写第 6 节列出的旧测试。

## 9. 文档

- 新增 `docs/architecture/36-heartbeat-rework-release-notes.md`，写明第 6 节的破坏性清单。
- `docs/architecture/02-modules.md` 的 Scheduler 行注明：执行内核共用，心跳与定时任务是两个入口。
- `AGENTS.md` 的 WHERE TO LOOK 增加 `IHeartbeatService` 与 `/heartbeat`。
