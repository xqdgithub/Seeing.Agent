# Interval Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 `interval` 调度增加可选多时段 `windows`：仅在时段内触发、以时段起点对齐间隔、支持跨午夜、禁止交叠；页面 / Tool / Skill 同步支持；展示与配置一律本地墙钟时间。

**Architecture:** 共享 `ScheduleWindowsValidator` 做规范化与交叠校验；`QuartzSchedulerEngine` 在非空 `windows` 时为每时段注册一个（跨午夜拆两个）`DailyTimeIntervalTrigger`，空 `windows` 保持现有 `SimpleSchedule`；UI / `cron_create` / Skill 共用同一模型与语义。

**Tech Stack:** .NET 10、Quartz.NET、xUnit + FluentAssertions、Blazor AntDesign WebUI、Scheduler Skills（嵌入 Markdown）

**Spec:** `docs/superpowers/specs/2026-07-20-interval-windows-design.md`（本地参考，不入仓）

## Global Constraints

- 仅 `type=interval` 可带 `windows`；空/`null`/`[]` = 全天（现有行为）
- `start` 缺省 `00:00`，`end` 缺省 `23:59:59`；允许跨午夜（`start > end`）
- 交叠：48h 轴 + 半开区间 `[start,end)`；端点相触允许
- 下一拍：时段起点 + n×interval 中 ≥ now 的最早一拍
- 用户可见时间一律 `Schedule.Timezone`（缺省 Local）墙钟，禁止展示 UTC/`Z`
- Heartbeat `ActiveHours` 本阶段不改
- **未经用户明确要求，不要 `git commit` / `git push`**

---

## File Structure

| 文件 | 职责 |
|------|------|
| `src/Seeing.Agent.Scheduler/Models/SchedulerModels.cs` | `ScheduleWindow`；`ScheduleSpec.Windows` |
| `src/Seeing.Agent.Scheduler/Engine/ScheduleWindowsValidator.cs` | 规范化、解析、交叠校验（新建） |
| `src/Seeing.Agent.Scheduler/Engine/ScheduleExpressionParser.cs` | `GetNextOccurrence` 支持 windows |
| `src/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs` | 多 Trigger 构建；NextFire 取最小值 |
| `src/Seeing.Agent.Scheduler/Tools/CronScheduleParser.cs` | 校验接入 windows |
| `src/Seeing.Agent.Scheduler/Tools/CronCreateTool.cs` | Schema / Description |
| `src/Seeing.Agent.Scheduler/Skills/cron-create/SKILL.md` | 文档与示例 |
| `samples/.../JobEditorModal.razor` | 时段编辑 UI + 校验 |
| `samples/.../CronJobsPage.razor` / `JobDetailPage.razor` | 摘要展示 |
| `tests/Seeing.Agent.Scheduler.Tests/ScheduleWindowsValidatorTests.cs` | 校验单测（新建） |
| `tests/Seeing.Agent.Scheduler.Tests/ScheduleExpressionParserWindowsTests.cs` | 下一拍单测（新建） |
| `tests/Seeing.Agent.Scheduler.Tests/SchedulerEngineTests.cs` | 引擎多 Trigger 用例 |

---

### Task 1: 模型 + ScheduleWindowsValidator（TDD）

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Models/SchedulerModels.cs`
- Create: `src/Seeing.Agent.Scheduler/Engine/ScheduleWindowsValidator.cs`
- Create: `tests/Seeing.Agent.Scheduler.Tests/ScheduleWindowsValidatorTests.cs`

**Interfaces:**
- Produces:
  - `class ScheduleWindow { string? Start; string? End; }`
  - `ScheduleSpec.Windows : List<ScheduleWindow>?`
  - `ScheduleWindowsValidator.TryNormalize(IReadOnlyList<ScheduleWindow>?, out List<NormalizedWindow> normalized, out string? error)`
  - `readonly record struct NormalizedWindow(TimeSpan Start, TimeSpan End, bool CrossesMidnight)`
  - `ScheduleWindowsValidator.ValidateForScheduleType(string scheduleType, IReadOnlyList<ScheduleWindow>? windows, out string? error)` — cron/once 带非空 windows 失败

- [ ] **Step 1: 写失败测试**

在 `ScheduleWindowsValidatorTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class ScheduleWindowsValidatorTests
{
    [Fact]
    public void Empty_Or_Null_Is_Ok()
    {
        ScheduleWindowsValidator.TryNormalize(null, out var n1, out var e1).Should().BeTrue();
        n1.Should().BeEmpty();
        e1.Should().BeNull();
        ScheduleWindowsValidator.TryNormalize(new List<ScheduleWindow>(), out var n2, out _).Should().BeTrue();
        n2.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_Start_And_End()
    {
        ScheduleWindowsValidator.TryNormalize(
            new[] { new ScheduleWindow() },
            out var n, out var err).Should().BeTrue();
        err.Should().BeNull();
        n.Should().ContainSingle();
        n[0].Start.Should().Be(TimeSpan.Zero);
        n[0].End.Should().Be(new TimeSpan(23, 59, 59));
        n[0].CrossesMidnight.Should().BeFalse();
    }

    [Fact]
    public void Touching_Endpoints_Allowed()
    {
        var ok = ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "09:00", End = "12:00" },
            new ScheduleWindow { Start = "12:00", End = "18:00" }
        }, out _, out var err);
        ok.Should().BeTrue();
        err.Should().BeNull();
    }

    [Fact]
    public void Overlap_Rejected()
    {
        var ok = ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "09:00", End = "12:00" },
            new ScheduleWindow { Start = "11:00", End = "14:00" }
        }, out _, out var err);
        ok.Should().BeFalse();
        err.Should().Contain("交叠");
    }

    [Fact]
    public void Overnight_Allowed()
    {
        ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "22:00", End = "06:00" }
        }, out var n, out _).Should().BeTrue();
        n[0].CrossesMidnight.Should().BeTrue();
    }

    [Fact]
    public void Overnight_Overlapping_SameDay_Rejected()
    {
        // 22:00-06:00 与 05:00-08:00 交叠
        ScheduleWindowsValidator.TryNormalize(new[]
        {
            new ScheduleWindow { Start = "22:00", End = "06:00" },
            new ScheduleWindow { Start = "05:00", End = "08:00" }
        }, out _, out var err).Should().BeFalse();
        err.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Cron_With_Windows_Rejected()
    {
        ScheduleWindowsValidator.ValidateForScheduleType(
            ScheduleTypes.Cron,
            new[] { new ScheduleWindow { Start = "09:00", End = "18:00" } },
            out var err).Should().BeFalse();
        err.Should().Contain("interval");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter ScheduleWindowsValidatorTests
```

Expected: 编译失败或 FAIL（类型不存在）

- [ ] **Step 3: 实现模型与校验器**

在 `SchedulerModels.cs` 的 `ScheduleSpec` 旁增加：

```csharp
public class ScheduleWindow
{
    public string? Start { get; set; }
    public string? End { get; set; }
}
```

并在 `ScheduleSpec` 增加：

```csharp
public List<ScheduleWindow>? Windows { get; set; }
```

实现 `ScheduleWindowsValidator.cs` 要点：

```csharp
public static class ScheduleWindowsValidator
{
    public static readonly TimeSpan DefaultStart = TimeSpan.Zero;
    public static readonly TimeSpan DefaultEnd = new(23, 59, 59);

    public static bool TryParseTime(string? text, out TimeSpan time, out string? error) { /* HH:mm or HH:mm:ss */ }

    public static bool TryNormalize(
        IReadOnlyList<ScheduleWindow>? windows,
        out List<NormalizedWindow> normalized,
        out string? error)
    {
        normalized = new();
        error = null;
        if (windows == null || windows.Count == 0) return true;

        foreach (var w in windows)
        {
            if (!TryParseTime(string.IsNullOrWhiteSpace(w.Start) ? "00:00" : w.Start, out var start, out error))
                return false;
            if (!TryParseTime(string.IsNullOrWhiteSpace(w.End) ? "23:59:59" : w.End, out var end, out error))
                return false;
            normalized.Add(new NormalizedWindow(start, end, start > end));
        }

        // Expand to [day0, day1) segments on 0..48h axis as half-open [start,end)
        // Same-day: [start, end); overnight: [start, 24h) and [24h, 24h+end)
        // Sort segments; if a.End > b.Start => overlap error "生效时段存在交叠"
        return !HasOverlap(normalized, out error);
    }

    public static bool ValidateForScheduleType(string? type, IReadOnlyList<ScheduleWindow>? windows, out string? error)
    {
        error = null;
        if (windows == null || windows.Count == 0) return true;
        if (!string.Equals(type, ScheduleTypes.Interval, StringComparison.OrdinalIgnoreCase))
        {
            error = "windows 仅支持 schedule.type=interval";
            return false;
        }
        return TryNormalize(windows, out _, out error);
    }
}

public readonly record struct NormalizedWindow(TimeSpan Start, TimeSpan End, bool CrossesMidnight);
```

交叠展开伪代码：同日段加入 `(Start, End)`；跨夜加入 `(Start, 24:00)` 与 `(0, End)`，再把「次日」段整体 `+24h` 后再与「当日」段一起排序比较。为检测跨夜与次日早晨段交叠，建议：对每个窗口生成「今天」与「明天」两套展开（共最多 4 段/窗），在 0–48h 上用半开相交判断。

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter ScheduleWindowsValidatorTests
```

Expected: PASS

- [ ] **Step 5: Commit** — **跳过**（除非用户明确要求）

---

### Task 2: GetNextOccurrence 支持 windows（TDD）

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Engine/ScheduleExpressionParser.cs`
- Create: `tests/Seeing.Agent.Scheduler.Tests/ScheduleExpressionParserWindowsTests.cs`

**Interfaces:**
- Consumes: `ScheduleWindowsValidator.TryNormalize`
- Produces: `GetNextOccurrence` 在 `Interval` + 非空 windows 时返回对齐后的本地 `DateTime?`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Interval_Windows_Aligns_To_Window_Start()
{
    var schedule = new ScheduleSpec
    {
        Type = ScheduleTypes.Interval,
        Every = "40m",
        Timezone = "Asia/Shanghai",
        Windows = new List<ScheduleWindow>
        {
            new() { Start = "09:00", End = "23:00" }
        }
    };

    // 固定「现在」：用 from 参数 — 上海本地 10:05
    var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    var fromLocal = new DateTime(2026, 7, 20, 10, 5, 0, DateTimeKind.Unspecified);
    var next = ScheduleExpressionParser.GetNextOccurrence(schedule, fromLocal);
    next.Should().NotBeNull();
    next!.Value.Hour.Should().Be(10);
    next.Value.Minute.Should().Be(20);
}

[Fact]
public void Interval_Empty_Windows_Unchanged()
{
    var schedule = new ScheduleSpec { Type = ScheduleTypes.Interval, Every = "30m" };
    var from = new DateTime(2026, 7, 20, 10, 0, 0);
    ScheduleExpressionParser.GetNextOccurrence(schedule, from)
        .Should().Be(from.AddMinutes(30));
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter ScheduleExpressionParserWindowsTests
```

Expected: 10:20 断言失败（当前实现为 from+40m → 10:45）

- [ ] **Step 3: 实现下一拍算法**

在 `GetNextOccurrence` 的 Interval 分支：

1. 若 windows 空 → 保持 `from + ParseInterval(every)`
2. 否则 `TryNormalize`；失败则抛或返回 null
3. 在 `tz` 下将 `from` 转为本地墙钟
4. 从当天起最多扫描 2–3 个日历日；对每个 `NormalizedWindow`：
   - 同日：候选 `date+Start + n*interval`，且 `< date+End`（半开）且 `> fromLocal`
   - 跨夜：晚上段 `date+Start + n*interval` 直到次日 0 点前；早晨段从 `date+1+00:00` 起算（注意：跨夜逻辑窗的「起点锚」是晚上的 `Start`，拆 Trigger 后早晨段锚为 `00:00`——与引擎一致）
5. 取最早候选；返回 `DateTimeKind.Unspecified` 本地墙钟（或 `SpecifyKind(..., Local)`），**不要**返回 UTC

跨夜拆分与引擎一致：逻辑跨夜窗 → 两段锚点分别为原 `Start` 与 `00:00`。

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter ScheduleExpressionParserWindowsTests
```

Expected: PASS

- [ ] **Step 5: Commit** — **跳过**

---

### Task 3: Quartz 多 Trigger 注册 + NextFire 最小值

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Engine/QuartzSchedulerEngine.cs`
- Modify: `tests/Seeing.Agent.Scheduler.Tests/SchedulerEngineTests.cs`

**Interfaces:**
- Consumes: `ScheduleWindowsValidator`, `ScheduleExpressionParser.ParseInterval` / `ResolveTimeZone`
- Produces: `BuildTriggers(jobId, schedule) -> IReadOnlyList<ITrigger>`；`UpsertJobAsync` 使用 `ScheduleJob(job, triggers, replace: true)` 或先加 Job 再 `ScheduleJob(trigger)` 多次

- [ ] **Step 1: 写失败/行为测试**

```csharp
[Fact]
public async Task UpsertJob_Interval_With_Windows_Has_Multiple_Triggers()
{
    var engine = CreateEngine();
    await engine.StartAsync();
    await engine.UpsertJobAsync("win-job", new ScheduleSpec
    {
        Type = ScheduleTypes.Interval,
        Every = "40m",
        Timezone = TimeZoneInfo.Local.Id,
        Windows = new List<ScheduleWindow>
        {
            new() { Start = "09:00", End = "12:00" },
            new() { Start = "14:00", End = "18:00" }
        }
    }, ScheduleIntent.Active);

    var status = await engine.GetJobStatusAsync("win-job");
    status.State.Should().Be(JobState.Scheduled);
    status.NextFireTime.Should().NotBeNull();
    // NextFireTime 必须是本地墙钟语义（无 Kind=Utc 展示）；与 Local 比较日期合理
    status.NextFireTime!.Value.Kind.Should().NotBe(DateTimeKind.Utc);

    await engine.StopAsync();
}
```

可选：通过 `IScheduler.GetTriggersOfJob` 断言 Trigger 数量为 2（同日两窗）或跨夜为 2。

- [ ] **Step 2: 跑测试**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter UpsertJob_Interval_With_Windows
```

Expected: FAIL（仍单 Trigger / 忽略 windows）

- [ ] **Step 3: 实现 BuildTriggers**

替换 `BuildTrigger` 为返回列表：

**空 windows / 非 interval：** 保持原单 Trigger 逻辑。

**interval + 非空 windows：**

```csharp
// 伪代码
foreach (var (i, w) in normalized.Index())
{
    if (!w.CrossesMidnight)
        triggers.Add(BuildDailyIntervalTrigger($"{jobId}__w{i}", w.Start, w.End, interval, tz));
    else
    {
        triggers.Add(BuildDailyIntervalTrigger($"{jobId}__w{i}_a", w.Start, new TimeSpan(23,59,59), interval, tz));
        triggers.Add(BuildDailyIntervalTrigger($"{jobId}__w{i}_b", TimeSpan.Zero, w.End, interval, tz));
    }
}

ITrigger BuildDailyIntervalTrigger(string name, TimeSpan start, TimeSpan end, TimeSpan every, TimeZoneInfo tz)
{
    var startTod = TimeOfDay.HourMinuteAndSecondOfDay(start.Hours, start.Minutes, start.Seconds);
    var endTod = TimeOfDay.HourMinuteAndSecondOfDay(end.Hours, end.Minutes, end.Seconds);
    return TriggerBuilder.Create()
        .WithIdentity(name, SchedulerConstants.DefaultJobGroup)
        .WithDailyTimeIntervalSchedule(x =>
        {
            x.StartingDailyAt(startTod);
            x.EndingDailyAt(endTod);
            x.WithInterval((int)every.TotalSeconds, IntervalUnit.Second); // 或按单位选择
            x.OnEveryDay();
            x.InTimeZone(tz);
            x.WithMisfireHandlingInstructionDoNothing(); // 或与现有 interval 对齐
        })
        .Build();
}
```

`UpsertJobAsync`：

```csharp
var triggers = BuildTriggers(jobId, schedule);
await _scheduler.ScheduleJob(jobDetail, new HashSet<ITrigger>(triggers), replace: true, ct);
```

**GetJobStatusAsync / GetStatus 列表：** 不要只用 `primaryTrigger`；对 `GetTriggersOfJob` 取所有 `GetNextFireTimeUtc()` 的最小值，再：

```csharp
var utc = minFire;
var local = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, scheduleTz);
// 存为 Unspecified 或 Local，UI 用 ToString("yyyy-MM-dd HH:mm:ss") 无 Z
NextFireTime = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
```

若 status 拿不到 schedule timezone，用 `TimeZoneInfo.Local` 或从 JobData 读取（若已有）；否则至少 `.LocalDateTime` 且文档要求 UI 不显示 Z（现有 `ToString` 已无 Z）。核对现有 `GetNextFireTimeUtc()?.LocalDateTime` 是否在非本地 TZ 下错误——有 `Timezone` 时改为 `ConvertTimeFromUtc(..., ResolveTimeZone(...))`。

校验失败：`UpsertJobAsync` 在 Build 前 `ValidateForScheduleType`，失败抛 `InvalidOperationException`（Manager/Tool 层转成用户可读错误）。

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter "UpsertJob_Interval_With_Windows|QuartzSchedulerEngine_UpsertJob_CreatesJob"
```

Expected: PASS（含无 windows 回归）

- [ ] **Step 5: Commit** — **跳过**

---

### Task 4: Tool + Skill

**Files:**
- Modify: `src/Seeing.Agent.Scheduler/Tools/CronScheduleParser.cs`
- Modify: `src/Seeing.Agent.Scheduler/Tools/CronCreateTool.cs`
- Modify: `src/Seeing.Agent.Scheduler/Skills/cron-create/SKILL.md`
- Modify: `src/Seeing.Agent.Scheduler/Skills/cron-management/SKILL.md`（一行交叉引用即可）

**Interfaces:**
- Consumes: `ScheduleWindowsValidator.ValidateForScheduleType` / `TryNormalize`

- [ ] **Step 1: CronScheduleParser 接入校验**

在 `TryValidate` 的 Interval 分支，`ParseInterval` 之后：

```csharp
if (!ScheduleWindowsValidator.ValidateForScheduleType(ScheduleTypes.Interval, schedule.Windows, out error))
    return false;
```

在 Cron / Once 分支末尾：

```csharp
if (!ScheduleWindowsValidator.ValidateForScheduleType(type, schedule.Windows, out error))
    return false;
```

更新 `ExamplesText`：

```
- interval: {"type":"interval","every":"40m","windows":[{"start":"09:00","end":"23:00"}],"timezone":"Asia/Shanghai"}
```

- [ ] **Step 2: CronCreateTool Schema**

在 `schedule` properties 增加：

```csharp
windows = new {
    type = "array",
    description = "可选生效时段（仅 interval）。空=全天。start 缺省 00:00，end 缺省 23:59:59；允许跨午夜；禁止交叠。时间均为 schedule.timezone 墙钟。",
    items = new {
        type = "object",
        properties = new {
            start = new { type = "string", description = "HH:mm 或 HH:mm:ss" },
            end = new { type = "string", description = "HH:mm 或 HH:mm:ss" }
        }
    }
}
```

Description 中补一句本地时间约束。

- [ ] **Step 3: 更新 SKILL.md**

`cron-create/SKILL.md` 增加：

```markdown
- interval + windows: `{"type":"interval","every":"40m","timezone":"Asia/Shanghai","windows":[{"start":"09:00","end":"23:00"}]}`
- `windows` 可选；空=全天；起点对齐间隔；跨午夜允许；不可交叠；时间均为本地墙钟（timezone）
```

- [ ] **Step 4: 跑相关测试**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests --filter "SchedulerToolRegistrationTests|SchedulerSkillRegistrationTests"
```

Expected: PASS

- [ ] **Step 5: Commit** — **跳过**

---

### Task 5: WebUI 编辑与展示

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/CronJobs/JobEditorModal.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/CronJobsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/JobDetailPage.razor`

**Interfaces:**
- Consumes: `Job.Schedule.Windows`；校验逻辑与后端一致（可复制简化版或调用若 WebUI 引用 Scheduler 程序集——已引用则直接 `ScheduleWindowsValidator`）

- [ ] **Step 1: JobEditorModal — 间隔时段 UI**

在 Interval 的「间隔」`FormItem` 下方增加：

```razor
<FormItem Label="生效时段（可选）">
    <small style="color:#999;">留空表示全天。时间均为上方时区的本地墙钟；开始默认 00:00，结束默认 23:59:59。</small>
    @for (var i = 0; i < (_windows?.Count ?? 0); i++)
    {
        var index = i;
        <Row Gutter="8" Style="margin-bottom:8px;">
            <Col Span="10">
                <TimePicker @bind-Value="@_windowStarts[index]" Format="HH:mm" Placeholder="开始" Style="width:100%;" />
            </Col>
            <Col Span="10">
                <TimePicker @bind-Value="@_windowEnds[index]" Format="HH:mm:ss" Placeholder="结束" Style="width:100%;" />
            </Col>
            <Col Span="4">
                <Button Danger OnClick="() => RemoveWindow(index)">删除</Button>
            </Col>
        </Row>
    }
    <Button Type="@ButtonType.Dashed" OnClick="AddWindow" Block>添加时段</Button>
</FormItem>
```

代码：

- 打开编辑时从 `Job.Schedule.Windows` 填充 `_windowStarts` / `_windowEnds`（`DateTime?` 或 `TimeOnly?`）
- `HandleOk` 前：把列表写回 `Job.Schedule.Windows`（null 开始/结束字段保持 null 以触发缺省，或规范化写 `00:00` / `23:59:59`）
- `ValidateSchedule`：Interval 时调用 `ScheduleWindowsValidator.TryNormalize` / `ValidateForScheduleType`，失败设置 `_validationError`
- **禁止**把 TimePicker 值当 UTC 处理

- [ ] **Step 2: 列表/详情摘要**

`CronJobsPage` / `JobDetailPage` 中 Interval 展示改为例如：

```csharp
ScheduleTypes.Interval => FormatInterval(job.Schedule),

static string FormatInterval(ScheduleSpec s)
{
    var every = s.Every ?? "interval";
    if (s.Windows == null || s.Windows.Count == 0) return every;
    var parts = s.Windows.Select(w => $"{w.Start ?? "00:00"}–{w.End ?? "23:59:59"}");
    return $"{every} · {string.Join(", ", parts)}";
}
```

`NextRunText` 继续 `ToString("yyyy-MM-dd HH:mm")`（无 Z）。确认 `NextFireTime` 来自引擎本地转换后的值。

- [ ] **Step 3: 手动冒烟（或 UI 编译）**

```bash
dotnet build samples/Seeing.Agent.WebUI
```

Expected: 0 Error

- [ ] **Step 4: Commit** — **跳过**

---

### Task 6: 全量回归 + 本地时间核查

**Files:** 无新文件；核查展示路径

- [ ] **Step 1: 跑 Scheduler 全测**

```bash
dotnet test tests/Seeing.Agent.Scheduler.Tests
```

Expected: 全部 PASS

- [ ] **Step 2: 本地时间核查清单（人工）**

| 路径 | 期望 |
|------|------|
| `CronListTool` next 文本 | `yyyy-MM-dd HH:mm:ss`，无 `Z` |
| WebUI 列表 NextRun | 本地墙钟 |
| `runAt` 编辑 | 继续 `DateTimeKind.Local`（已有） |
| Tool 错误信息 | 中文，时段为 `HH:mm` |

若发现 `GetNextFireTimeUtc()?.LocalDateTime` 在 `Timezone != Local` 时偏差，按 Task 3 的 `ConvertTimeFromUtc` 修完再测。

- [ ] **Step 3: Commit** — **跳过**；实现完成后由用户决定是否提交

---

## Spec Coverage Self-Review

| Spec 项 | Task |
|---------|------|
| windows 模型与缺省 | Task 1 |
| 交叠/跨午夜/相触 | Task 1 |
| 起点对齐下一拍 | Task 2–3 |
| DailyTimeIntervalTrigger 多 Trigger | Task 3 |
| 空 windows = 全天 | Task 1–3 |
| 本地墙钟展示 | Task 3、5、6 |
| Tool / Skill | Task 4 |
| WebUI | Task 5 |
| 不改 Heartbeat ActiveHours | 遵守（无 Task） |
| 不自动提交 | 各 Task Step 5 跳过 |

## Placeholder Scan

无 TBD；交叠算法与 Trigger API 已写明；Commit 明确跳过。
