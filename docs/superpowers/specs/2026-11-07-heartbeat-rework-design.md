# 心跳功能重构设计

**日期:** 2026-11-07
**状态:** 待评审
**范围:** `Seeing.Agent.Scheduler` 心跳执行路径、配置模型、可观测接口、Agent 工具、`/heartbeat` 页面

---

## 1. 问题

定时任务（cron）已经过若干轮重构：Quartz 引擎、`ScheduleIntent` 三态、投递目标矩阵（会话 + conversation）、执行历史、工具/技能/详情页/首页看板。**心跳是唯一没有跟进这条演进路径的旁路**，因此积累了一批确定性缺陷（都能复现，不是风格问题）：

| # | 缺陷 | 位置 | 后果 |
|---|---|---|---|
| 1 | main 目标的输出被静默丢弃 | `HeartbeatJob.cs:177-198` | 默认配置下心跳跑完什么都不留 |
| 2 | 心跳执行历史永不落盘 | `ScheduleManager.cs:447-449`（`StartsWith('_')` 跳过） | 无任何执行记录 |
| 3 | 心跳状态恒为 `Disabled` | `ScheduleManager.cs:146-152`（不在 `_jobsFile.Jobs`） | 页面无法展示任何状态 |
| 4 | 活跃时段跳过 / Hook 拦截后 `_runningJobs` 永久残留 | `HeartbeatJob.cs:79-130` 提前 return 不通知 listener | 「立即执行」此后永远 `Conflict`，直到重启 |
| 5 | 调度器未启动时「立即执行」假成功；心跳关闭时抛异常 | `ScheduleManager.cs:247-267` + `QuartzSchedulerEngine.cs:480` | 按钮说已触发/页面崩异常 |
| 6 | 一个坏的 `Every` 让整个调度器起不来 | `RegisterHeartbeatAsync` → `ParseInterval` 抛出，`StartAsync` 无隔离 | cron 一起失效 |
| 7 | 页面绑定活配置对象 | `HeartbeatPage.razor:94-100` | 未保存即生效、取消不回滚、与设置卡互覆盖 |
| 8 | `ActiveHours` 与 cron `Windows` 是两套机制，且失败即放行 | `ActiveHoursChecker.cs:38-41`（`catch { return true; }`） | 同一概念双重实现，配置写错反而放开限制 |
| 9 | `SessionOrigin.Heartbeat/Scheduler` 从未被赋值 | 全仓库仅 `Reflection` 有赋值点 | 心跳会话被当作 `User` 参与各处来源判定 |
| 10 | interval 任务每次启动后 5 秒触发 | `QuartzSchedulerEngine.cs:617-620` | 重启几次就多打几次心跳，节律与「每 6h」不符 |
| 11 | `last` / `inbox` 目标不可靠 | `HeartbeatJob.cs:269-280` | `last` 依赖未接线的 `ISessionCatalog`，常静默回落 main；`inbox` 会真的创建名为 `_inbox` 的普通会话 |
| 12 | 心跳出现在定时任务统计口径里 | `QuartzSchedulerEngine.cs:70`（`TotalJobs`） | 任务总数比列表行数多 1 |

---

## 2. 决策摘要

| 项 | 决策 |
|---|---|
| 心跳身份 | `jobs.json` 内的**系统任务**，固定 `Id = "_heartbeat"`，不作为 `Jobs` 集合元素 |
| 执行路径 | 复用 `AgentJob`；**删除** `HeartbeatJob` |
| 配置来源 | **删除** `SchedulerOptions.Heartbeat` / `HeartbeatOptions`；心跳配置即 `ScheduledJobSpec` |
| 窗口机制 | 统一用 `ScheduleSpec.Windows`；**删除** `ActiveHoursChecker` / `ActiveHoursOptions` |
| 投递目标 | 会话（`Dispatch.Target.SessionId`）+ 可选 conversation；**删除** `main`/`last`/`inbox` 枚举 |
| 结果写入 | 与 cron 一致：assistant 消息 `Inject` 落盘（不唤醒）+ conversation 出站 |
| 可观测 | 新增 `IHeartbeatService`；心跳状态与历史不出现在 cron 面 |
| 模块归属 | 仍在 `scheduler` 模块内，隔离靠**数据结构**而非模块拆分 |
| 兼容 | **不做迁移**（直接移除旧配置键，忽略残留即可） |

---

## 3. 数据模型

### 3.1 `JobsFile` 增独立槽位

```csharp
public class JobsFile
{
    public int Version { get; set; } = 3;

    /// <summary>用户定时任务（cron 面）。心跳不在此集合内。</summary>
    public List<ScheduledJobSpec> Jobs { get; set; } = new();

    /// <summary>心跳系统任务（Id 固定 SchedulerConstants.HeartbeatJobId）。</summary>
    public ScheduledJobSpec? Heartbeat { get; set; }
}
```

**为什么是独立槽位而不是 `IsSystem` 标志**：所有 cron 面出口都遍历 `_jobsFile.Jobs`——`ListJobsAsync`、`GetAllJobStatusesAsync`、`CronListTool`、`CronJobsPage`、首页看板、`AdminEndpoints`。把它们挪进 `Jobs` 再逐处过滤，等于在六七个地方重复同一个谓词，日后必然漂移。放在独立槽位是**结构性排除**：默认不可见，需要时才显式读取。

### 3.2 `ScheduledJobSpec` 原样复用

不新增 spec 类型、不新增字段。`Schedule` / `Dispatch` / `Runtime` / `LastRunAt` / `Intent` 全部现成语义。

### 3.3 移除的类型

- `SchedulerOptions.Heartbeat` 属性
- `HeartbeatOptions` 类
- `ActiveHoursOptions` 类
- `HeartbeatTargets` 静态类（`Main` / `Last` / `Inbox`）
- `JobDataKeys.ActiveHours`、`JobDataKeys.HeartbeatTarget`

---

## 4. 执行路径

### 4.1 引擎只认一种 Job 类型

`QuartzSchedulerEngine.UpsertJobAsync` 中：

```csharp
var jobType = jobId == SchedulerConstants.HeartbeatJobId
    ? typeof(Jobs.HeartbeatJob)
    : typeof(Jobs.AgentJob);
```

→ 恒为 `AgentJob`。`Jobs/HeartbeatJob.cs` 整个删除。

### 4.2 `AgentJob` 的两处参数化

| 用途 | 键 | 取值 | 默认 |
|---|---|---|---|
| 提醒信封 kind | `JobDataKeys.ReminderKind`（新增） | `SystemReminder.Kinds.Cron` / `.Heartbeat` | cron |
| 执行来源 | `JobDataKeys.Source`（现成） | `ScheduleSources.Cron` / `.Heartbeat` | cron |

`AgentJob` 用 `ReminderKind` 替换现在写死的 `SystemReminder.Kinds.Cron`；`Metadata["source"]` 用 `Source` 的实际值替换写死的 `ScheduleSources.Cron`。

下游无需改动：`SessionScheduleDispatcher` 已经从信封解包并沿用原 kind，`JobExecutionRecord.Source` 也直接取 `result.Source`。

### 4.3 继承到的心跳能力

- **每条返回路径都 `NotifyExecutedAsync`** → `_runningJobs` 不再残留（缺陷 4）
- **始终 `DispatchAsync` 写回会话** → main 目标的输出不再丢（缺陷 1）
- schema 单点解析（`ResolveToolSchemasAsync`）+ 只读上下文装配（`SchedulerContextAssembly`）+ 目录基线补偿（`AppendCatalogToBaseline`）
- `Model` / `Mode` 支持（心跳此前缺失）
- conversation 出站目标矩阵（`ChannelDeliveryScheduleDispatcher` 优先级）

### 4.4 启动隔离

`ScheduleManager.StartAsync` 中，`RegisterUserJobAsync` 循环与 `RegisterHeartbeatAsync` 各自 try/catch：单个任务注册失败记 `LogError` 并跳过，不中断整体启动。缺陷 6 消失。

### 4.5 interval 首触发与节律

`BuildSingleTrigger` 的 `interval` 分支：

- 有 `LastRunAt`：`nextDue = LastRunAt + interval`；`nextDue <= now` → 立即补跑一次（`StartNow`），否则 `StartAt(nextDue)`；
- 无 `LastRunAt`：`StartAt(now + interval)`。

`LastRunAt` 由 `ScheduleManager.OnJobExecutedAsync` 回写 spec 时持久化（心跳写 `_jobsFile.Heartbeat.LastRunAt`）。缺陷 10 消失。

> 该行为对 cron interval 任务同样生效，属同一口径修正；cron/once 的首触发逻辑不变。

---

## 5. 可观测接口

新增 `Scheduler.Abstractions/IHeartbeatService.cs`：

```csharp
public interface IHeartbeatService
{
    /// <summary>当前心跳快照：配置副本 + 真实调度状态 + 最近一次执行。</summary>
    Task<HeartbeatSnapshot> GetAsync(CancellationToken ct = default);

    /// <summary>局部更新；未提供的字段保持不变。</summary>
    Task<HeartbeatSnapshot> UpdateAsync(HeartbeatUpdate update, CancellationToken ct = default);

    /// <summary>立即触发一次。</summary>
    Task<TriggerResult> RunNowAsync(CancellationToken ct = default);

    /// <summary>取消正在执行的心跳。</summary>
    Task<bool> CancelAsync(CancellationToken ct = default);

    /// <summary>执行历史（倒序）。</summary>
    Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(
        int limit, int offset = 0, CancellationToken ct = default);
}

public sealed record HeartbeatSnapshot(
    ScheduledJobSpec? Spec,         // 深拷贝，调用方可安全绑定/修改；null = 尚未配置
    JobStatus? Status,              // 真实 Quartz 状态（Running / NextFireTime / PreviousFireTime）
    JobExecutionRecord? LastRun,
    bool SchedulerStarted)
{
    public bool Configured => Spec is not null;
}

public sealed record HeartbeatUpdate
{
    public bool? Enabled { get; init; }
    public string? Prompt { get; init; }
    public string? Every { get; init; }
    public IReadOnlyList<ScheduleWindow>? Windows { get; init; }
    public string? Timezone { get; init; }
    public string? SessionId { get; init; }
    public ConversationSpec? Conversation { get; init; }
    public string? Agent { get; init; }
    public string? Model { get; init; }
    public int? TimeoutSeconds { get; init; }
}
```

实现 `HeartbeatService`（`Scheduler` 包内）：

- `GetAsync`：`ScheduleManager` 暴露内部访问器取 `_jobsFile.Heartbeat` 深拷贝 + `GetJobStatusAsync(HeartbeatJobId)` + `GetHistoryAsync(HeartbeatJobId, 1)`；
- `UpdateAsync`：在副本上应用变更 → 校验（`Every` 可解析、Prompt 非空、目标至少一项）→ `CreateOrReplaceHeartbeatAsync` → `ReloadHeartbeatAsync`；
- `RunNowAsync`：`ScheduleManager.RunJobOnceAsync(HeartbeatJobId)`，并把「未配置 / 调度器未启动 / 调度器已禁用」映射为对应的 `TriggerResult` 分支（不再假 `Accepted`）。

`IScheduleManager` 调整：

| 成员 | 变更 |
|---|---|
| `GetJobStatusAsync` | 查找顺序 `Jobs` → `Heartbeat`（缺陷 3） |
| `OnJobExecutedAsync` | 去掉 `if (!jobId.StartsWith('_'))` 跳过；`LastRunAt` 回写覆盖心跳槽位（缺陷 2） |
| `ListJobsAsync` | 不变（天然不含心跳） |
| `ReloadHeartbeatAsync` | 移到 `IHeartbeatService` 或降为内部方法 |

---

## 6. Agent 工具与技能

三个工具（`src/capabilities/Seeing.Agent.Scheduler/Tools/`），**不并入 `cron_*`**：

| 工具 | 语义 |
|---|---|
| `heartbeat_get` | 返回配置 + intent + 下次触发 + 上次执行（时间/成败/耗时/错误摘要） |
| `heartbeat_update` | 局部更新，未提供字段不动；返回更新后的完整描述 |
| `heartbeat_run` | 立即触发；明确区分 Accepted / Conflict / 调度器未启动 / 未配置 |

文本呈现复用 `ScheduleTextFormatter.DescribeJob` + 新增 `DescribeHeartbeatLastRun`。

技能：`Skills/heartbeat-management/SKILL.md`，由 `SchedulerSkillRegistrar` 与 cron 技能一起注册/注销；`SchedulerModule.ProvidedTools` 追加三个工具 id 并在 `ActivateAsync` 挂载。

`/heartbeat-run` 命令改调 `IHeartbeatService.RunNowAsync`。

---

## 7. 页面重做（`/heartbeat`）

三段式，替换现在的单表单。

### ① 状态条

- 启停开关（= `Intent` Active / Disabled，写操作走 `UpdateAsync`）
- 调度器状态（未启动 / 已停止 / 运行中 / 待机）——未启动时整页给出阻断提示与「去启动」入口
- 下次触发（复用 `JobNextRunTimer`）；上次执行（时间 · 成功/失败 · 耗时 · 错误摘要）
- 运行中显示「取消」（走 `CancelAsync`）

### ② 配置卡

- 调度：`every` 输入 + 生效时段编辑器（复用 `JobEditorModal` 的窗口 UI 与 `ScheduleWindowsValidator`）+ 时区
- 投递：目标会话下拉 + `ConversationPicker`（与 cron 编辑器的 `_autoConversation` 快照语义一致）
- 执行：Agent 选择（`AgentRegistry.GetPrimaryAgentsAsync`）+ 模型（`ModelSelectDropdown`）+ 超时
- Prompt：`TextArea` + 示例模板一键填入

### ③ 历史卡

最近 20 条 `JobExecutionRecord` 表（时间 / 来源 / 状态 / 耗时 / 内容预览）+ 复用 `ExecutionResultModal` 看全文 + 「加载更多」。

### 交互约束

- **绑定副本**：`ScheduledJobCloner.Clone` 后绑定，保存成功才替换；缺陷 7 消失
- **保存前完整校验**：`Every` 格式、Prompt 必填、目标至少一项；错误就地提示，不出「保存成功但调度器起不来」
- **`TriggerResult` 四分支准确文案**，不再出现假 `Accepted`
- **空状态可行动**：未配置时给「生成推荐配置」+「立即执行一次试试」，不是一句「暂无」

---

## 8. 执行来源标记

`ISessionManager.EnsureSessionAsync` 增加可选参数：

```csharp
Task<SessionData> EnsureSessionAsync(
    string id,
    string? selectedAgent = null,
    string? partitionId = null,
    string? scenario = null,
    SessionOrigin? origin = null);
```

**仅在本次调用新建会话时应用**；已存在会话保持原 `Origin`（不覆盖用户会话历史标记）。

调用点：

| 调用方 | origin |
|---|---|
| `ScheduleConversationResolver.ResolveSessionIdAsync`（cron 新建会话） | `SessionOrigin.Scheduler` |
| `HeartbeatService` 确保目标会话存在 | `SessionOrigin.Heartbeat` |

实现落在 `SessionManager.EnsureSessionCoreAsync` 的 `new SessionData { Origin = origin ?? SessionOrigin.User }` 处。

---

## 9. 统计口径

`QuartzSchedulerEngine.GetStatusAsync`：`TotalJobs` / `RunningJobs` / `PausedJobs` 在计数时排除 `SchedulerConstants.HeartbeatJobId`。缺陷 12 消失。

心跳自身状态只经 `IHeartbeatService` 暴露。

---

## 10. 删除清单

| 文件 / 成员 | 说明 |
|---|---|
| `Scheduler/Jobs/HeartbeatJob.cs` | 整体删除，逻辑并入 `AgentJob` |
| `Scheduler/Engine/ActiveHoursChecker.cs` | 由 `ScheduleSpec.Windows` 取代 |
| `samples/Seeing.Gateway.Server/HEARTBEAT.md` | 孤儿文件，代码无任何读取逻辑 |
| `HeartbeatOptions` / `ActiveHoursOptions` / `HeartbeatTargets` | 见 §3.3 |
| `JobDataKeys.ActiveHours` / `.HeartbeatTarget` | 见 §3.3 |
| `SchedulerOptions.Heartbeat` | 见 §3.3 |

兼容策略：**不做迁移**。旧 `seeing.json` 里残留的 `Scheduler.Heartbeat` 键在反序列化时被忽略（未知键不报错）；`jobs.json` 里的 `Version` 保持向前兼容（旧文件无 `Heartbeat` 槽位 → 首次访问创建默认配置）。

---

## 11. 测试

| 用例 | 断言 |
|---|---|
| 结构性排除 | `ListJobsAsync` / `GetAllJobStatusesAsync` / `cron_list` 均不返回 `_heartbeat` |
| 状态查询 | 心跳注册后 `GetJobStatusAsync("_heartbeat")` 不是 `Disabled`，且带 `NextFireTime` |
| 历史落盘 | 心跳执行后 `GetHistoryAsync("_heartbeat", 1)` 有记录，`LastRunAt` 已回写 |
| 投递 | 目标为普通会话时，会话里出现一条 `Presentation=Assistant`、`SourceTag=job`、`Kind=heartbeat` 的消息 |
| 提醒 kind | 信封 kind 为 `heartbeat`（非 `cron`），`SessionScheduleDispatcher` 解包后仍为 `heartbeat` |
| `_runningJobs` 不残留 | 活跃时段外触发 / Hook 拦截后，`RunJobOnceAsync` 再次仍返回 `Accepted` |
| 假成功 | 调度器未启动时 `RunNowAsync` 返回非 `Accepted` 且文案明确 |
| 坏配置隔离 | 心跳 `Every` 非法时 `StartAsync` 仍成功，cron 任务正常注册，日志有 Error |
| 窗口一致性 | 心跳窗口与 cron 窗口经同一 `ScheduleWindowsValidator` 校验；跨午夜窗口 `GetNextOccurrence` 与 Quartz 触发时刻一致 |
| interval 节律 | 有 `LastRunAt` 时首触发 = `LastRunAt + interval`；无则 = `now + interval`（不是 +5s） |
| `heartbeat_update` 局部性 | 只给 `Enabled=false` 时 Prompt / Every / Agent 均不变 |
| `SessionOrigin` | cron 新建会话 `Origin=Scheduler`；心跳目标会话 `Origin=Heartbeat`；已存在会话不被覆盖 |
| 统计口径 | 只配 cron N 个 + 心跳时 `SchedulerStatus.TotalJobs == N` |

---

## 12. 文档

- 新增 `docs/architecture/36-heartbeat-rework-release-notes.md`（含 §1 缺陷表与破坏性清单）
- `docs/architecture/02-modules.md` 的 Scheduler 行补心跳说明
- `AGENTS.md` / `CLAUDE.md` 的 WHERE TO LOOK 增加心跳入口
