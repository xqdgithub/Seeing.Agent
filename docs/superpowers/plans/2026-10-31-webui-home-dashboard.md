# WebUI 主页卡片仪表盘 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 WebUI 主页从「落地页」重写为瀑布流卡片仪表盘，卡片即入口，点击进入对应页面。

**Architecture:** 纯逻辑（相对时间、过期计数、即将触发的任务、空卡判定）抽到无副作用的静态类 `HomeDashboardView` 并用 xUnit 覆盖；UI 层由一个通用卡片壳 `DashboardCard.razor` + 重写后的 `Index.razor` 组成，瀑布流用 CSS `columns` 实现；跨组件打开「审批中心」抽屉经新增的 Scoped `UiOverlayService` 事件接线。

**Tech Stack:** .NET 10、Blazor Server、AntDesign Blazor 2.0.0-nightly-250526143149、xUnit v3 + FluentAssertions

**Spec:** `docs/superpowers/specs/2026-10-31-webui-home-dashboard-design.md`

## Global Constraints

- 只改 `samples/Seeing.Agent.WebUI`（页面/组件/服务）与新增测试项目；不改后端、模块路由、侧边栏导航结构。
- 不新增第三方 UI / 图表 / 测试框架之外的依赖。
- 瀑布流必须用 CSS 多列（`columns` / `break-inside: avoid`），不得手写三列容器。
- 卡片点击目标只允许：`/sessions`、`/session/{id}`、`/cron-jobs`、`/agents`、`/models`、审批中心抽屉。
- 首页禁止轮询/定时器；禁止读写 `AppState.CurrentSessionId`。
- 文案与注释用中文；日期显示统一 `yyyy-MM-dd HH:mm`。

## Review Focus

1. **卡片被列切断**：`break-inside: avoid` 未生效（卡片内部出现 `display:flex` 高度撑破或 `overflow` 异常）时，卡片会跨列撕裂——期望是整卡留在同一列。
2. **空卡噪音**：待审批/待回答/失败三项均为 0 时「待我处理」卡仍渲染空壳——期望整卡隐藏。
3. **行点击冒泡**：点「最近会话」某一行时，若未阻止冒泡会同时触发整卡跳转——期望只进 `/session/{id}`，不进 `/sessions`。
4. **标题空/超长**：会话标题为空或极长时卡片布局被撑破——期望有兜底文案且单行省略。
5. **调度器未启动误判过期**：`JobExpiry.IsExpired` 在 `schedulerStarted = false` 时对非一次性任务返回 false；首页若传 `true` 会把全部任务标成过期。

---

### Task 1: 纯逻辑 `HomeDashboardView` + 测试项目

**Files:**
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj`
- Create: `samples/Seeing.Agent.WebUI/Services/HomeDashboardView.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/HomeDashboardViewTests.cs`
- Modify: `Seeing.Agent.slnx`（挂入新测试项目）

**Interfaces:**
- Consumes: `Seeing.Session.Core.SessionSummary`（`Id`/`Title`/`LastActiveAt`）、`Seeing.Agent.Scheduler.Models.ScheduledJobSpec`、`JobStatus`、`Seeing.Agent.Scheduler.Engine.JobExpiry.IsExpired(job, status, now, schedulerStarted)`、`ScheduleExpressionParser.GetNextOccurrence(schedule, from, fallbackTimezone = null) -> DateTime?`
- Produces:
  - `static string HomeDashboardView.RelativeTime(DateTime time, DateTime now)`
  - `static bool HomeDashboardView.HasActionItems(int approvals, int questions, int failures)`
  - `static string HomeDashboardView.SessionTitle(string? title, string id)`
  - `static IReadOnlyList<UpcomingRun> HomeDashboardView.UpcomingRuns(IReadOnlyList<ScheduledJobSpec> jobs, IReadOnlyDictionary<string, JobStatus> statuses, DateTime now, int take)`
  - `static int HomeDashboardView.CountExpired(IReadOnlyList<ScheduledJobSpec> jobs, IReadOnlyDictionary<string, JobStatus> statuses, DateTime now, bool schedulerStarted)`
  - `sealed record UpcomingRun(string JobId, string Title, DateTime When)`

- [ ] **Step 1: 建测试项目**

`tests/apps/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj`（对照 `Seeing.Agent.WebUI.Files.Tests.csproj` 的写法；WebUI 是 Web SDK 项目，故补 `FrameworkReference`）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.WebUI.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
    <PackageReference Include="xunit.v3" Version="4.0.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" Version="8.10.0" />
    <PackageReference Include="coverlet.collector" Version="10.0.1">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\samples\Seeing.Agent.WebUI\Seeing.Agent.WebUI.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

并在 `Seeing.Agent.slnx` 的 tests 区加入：

```xml
<Project Path="tests/apps/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj" />
```

- [ ] **Step 2: 写失败测试**

`tests/apps/Seeing.Agent.WebUI.Tests/Services/HomeDashboardViewTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class HomeDashboardViewTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 15, 0, 0);

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(3, "3 分钟前")]
    [InlineData(120, "2 小时前")]
    [InlineData(60 * 26, "昨天")]
    [InlineData(60 * 24 * 4, "4 天前")]
    public void RelativeTime_ShouldBucketByElapsed(int minutesAgo, string expected)
    {
        HomeDashboardView.RelativeTime(Now.AddMinutes(-minutesAgo), Now).Should().Be(expected);
    }

    [Fact]
    public void RelativeTime_FutureOrMinusOne_ShouldBeJustNow()
    {
        HomeDashboardView.RelativeTime(Now.AddMinutes(5), Now).Should().Be("刚刚");
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(1, 0, 0, true)]
    [InlineData(0, 2, 0, true)]
    [InlineData(0, 0, 3, true)]
    public void HasActionItems_ShouldBeTrueOnlyWhenAnyNonZero(int a, int q, int f, bool expected)
    {
        HomeDashboardView.HasActionItems(a, q, f).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "ses_1", "未命名会话")]
    [InlineData("   ", "ses_1", "未命名会话")]
    [InlineData("跑测试", "ses_1", "跑测试")]
    public void SessionTitle_ShouldFallbackWhenBlank(string? title, string id, string expected)
    {
        HomeDashboardView.SessionTitle(title, id).Should().Be(expected);
    }

    [Fact]
    public void UpcomingRuns_ShouldSortAscendingSkipExpiredAndTake()
    {
        var cron = new ScheduledJobSpec
        {
            Id = "j1",
            Name = "起床提醒",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 8 * * *", Timezone = "Asia/Shanghai" }
        };
        var expiredOnce = new ScheduledJobSpec
        {
            Id = "j2",
            Name = "过期一次性",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(-1), Timezone = "Asia/Shanghai" }
        };
        var disabled = new ScheduledJobSpec
        {
            Id = "j3",
            Name = "已禁用",
            Intent = ScheduleIntent.Disabled,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Cron, Cron = "0 9 * * *", Timezone = "Asia/Shanghai" }
        };

        var statuses = new Dictionary<string, JobStatus>
        {
            ["j1"] = new() { JobId = "j1", NextFireTime = Now.AddHours(2) },
            ["j2"] = new() { JobId = "j2" },
            ["j3"] = new() { JobId = "j3", NextFireTime = Now.AddHours(1) }
        };

        var runs = HomeDashboardView.UpcomingRuns([cron, expiredOnce, disabled], statuses, Now, take: 3);

        runs.Should().HaveCount(1);
        runs[0].JobId.Should().Be("j1");
        runs[0].Title.Should().Be("起床提醒");
        runs[0].When.Should().Be(Now.AddHours(2));
    }

    [Fact]
    public void UpcomingRuns_WhenStatusMissing_ShouldComputeFromSchedule()
    {
        var once = new ScheduledJobSpec
        {
            Id = "j4",
            Name = "开会",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddHours(3), Timezone = "Asia/Shanghai" }
        };

        var runs = HomeDashboardView.UpcomingRuns([once], new Dictionary<string, JobStatus>(), Now, take: 1);

        runs.Should().HaveCount(1);
        runs[0].When.Should().Be(Now.AddHours(3));
    }

    [Fact]
    public void CountExpired_ShouldFollowJobExpiryRules()
    {
        var once = new ScheduledJobSpec
        {
            Id = "j5",
            Intent = ScheduleIntent.Active,
            Schedule = new ScheduleSpec { Type = ScheduleTypes.Once, RunAt = Now.AddMinutes(-5) }
        };
        var jobs = new List<ScheduledJobSpec> { once };

        HomeDashboardView.CountExpired(jobs, new Dictionary<string, JobStatus>(), Now, schedulerStarted: false)
            .Should().Be(1, "一次性任务的 runAt 已过去，与调度器是否启动无关");
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests` → 期望编译失败（`HomeDashboardView` 不存在）。

- [ ] **Step 4: 实现 `HomeDashboardView`**

`samples/Seeing.Agent.WebUI/Services/HomeDashboardView.cs`：

```csharp
namespace Seeing.Agent.WebUI.Services;

/// <summary>主页仪表盘的纯投影逻辑（无副作用，便于测试）。</summary>
public static class HomeDashboardView
{
    public static string RelativeTime(DateTime time, DateTime now) { /* 见下 */ }
    public static bool HasActionItems(int approvals, int questions, int failures) => approvals > 0 || questions > 0 || failures > 0;
    public static string SessionTitle(string? title, string id) => string.IsNullOrWhiteSpace(title) ? "未命名会话" : title.Trim();
    public static IReadOnlyList<UpcomingRun> UpcomingRuns(...);
    public static int CountExpired(...);
}

public sealed record UpcomingRun(string JobId, string Title, DateTime When);
```

`RelativeTime` 分档（负数按 0 处理）：`< 1 分钟` → `刚刚`；`< 60 分钟` → `N 分钟前`；
`< 24 小时` → `N 小时前`；`< 48 小时` → `昨天`；其余 → `N 天前`。

`UpcomingRuns`：只取 `Intent == ScheduleIntent.Active` 且 `!JobExpiry.IsExpired(job, status, now, schedulerStarted: true)` 的任务；
时间优先 `status.NextFireTime`，缺失则 `ScheduleExpressionParser.GetNextOccurrence(job.Schedule, now)`；
过滤掉无时间者，按时间升序，`Take(take)`；`Title = SessionTitle(job.Name, job.Id)` 同款兜底（用 `job.Name ?? job.Id`，空则 `job.Id`）。

`CountExpired`：`jobs.Count(j => JobExpiry.IsExpired(j, statuses.GetValueOrDefault(j.Id), now, schedulerStarted))`。

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests`
Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
Expected: 全部通过。

- [ ] **Step 6: 提交**

```bash
git add Seeing.Agent.slnx samples/Seeing.Agent.WebUI/Services/HomeDashboardView.cs tests/apps/Seeing.Agent.WebUI.Tests
git commit -m "feat(webui): 新增主页仪表盘纯投影逻辑与测试项目"
```

---

### Task 2: `UiOverlayService` + AppSidebar 接线

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/UiOverlayService.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs:92`（DI，紧跟 `HostRequestDispatcher` 之后）
- Modify: `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor:19`（注入）、`:145`（字段旁）、`:205-215`（订阅/退订）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/UiOverlayServiceTests.cs`

**Interfaces:**
- Consumes: 无。
- Produces: `sealed class UiOverlayService { event Action? PermissionCenterRequested; void RequestPermissionCenter(); }`（Scoped）

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class UiOverlayServiceTests
{
    [Fact]
    public void RequestPermissionCenter_ShouldRaiseEvent()
    {
        var service = new UiOverlayService();
        var raised = 0;
        service.PermissionCenterRequested += () => raised++;

        service.RequestPermissionCenter();

        raised.Should().Be(1);
    }

    [Fact]
    public void RequestPermissionCenter_WithoutSubscriber_ShouldNotThrow()
    {
        var service = new UiOverlayService();
        var act = () => service.RequestPermissionCenter();
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: 运行确认失败**（类型不存在，编译失败）

- [ ] **Step 3: 实现服务并注册**

`UiOverlayService.cs`：

```csharp
namespace Seeing.Agent.WebUI.Services;

/// <summary>跨组件 UI 覆盖层请求（审批中心抽屉等；Scoped = circuit 维度）。</summary>
public sealed class UiOverlayService
{
    /// <summary>请求打开审批中心抽屉。</summary>
    public event Action? PermissionCenterRequested;

    public void RequestPermissionCenter() => PermissionCenterRequested?.Invoke();
}
```

`Program.cs` 在 `builder.Services.AddScoped<HostRequestDispatcher>();` 之后加：

```csharp
// 跨组件 UI 覆盖层请求（Scoped，circuit 维度）：主页卡片 → 审批中心抽屉
builder.Services.AddScoped<Seeing.Agent.WebUI.Services.UiOverlayService>();
```

- [ ] **Step 4: AppSidebar 接线**

`AppSidebar.razor`：

1. `@inject PermissionInboxView PermissionView` 之后加 `@inject UiOverlayService Overlays`；
2. `OnInitialized` 里（与 `PermissionView.Changed += OnPermissionChanged;` 同处）加 `Overlays.PermissionCenterRequested += OpenPermissionCenter;`；
3. `Dispose` 里（与退订同处）加 `Overlays.PermissionCenterRequested -= OpenPermissionCenter;`；
4. 不新增状态字段——`OpenPermissionCenter()` 已是 `public void`，可直接作为事件处理器（签名 `void()` 匹配 `Action`）。

- [ ] **Step 5: 构建 + 测试**

Run: `dotnet build Seeing.Agent.slnx`
Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
Expected: 0 错误；测试全绿。

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/UiOverlayService.cs samples/Seeing.Agent.WebUI/Program.cs samples/Seeing.Agent.WebUI/Components/AppSidebar.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/UiOverlayServiceTests.cs
git commit -m "feat(webui): 新增跨组件覆盖层请求服务并接线审批中心抽屉"
```

---

### Task 3: 卡片外壳 `DashboardCard.razor`

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/Home/DashboardCard.razor`

**Interfaces:**
- Consumes: 无。
- Produces: `<DashboardCard Title="最近会话" LinkText="全部 →" OnLink="..." OnClick="..." Class="...">`，参数：
  `[Parameter] public string Title`、`[Parameter] public string? LinkText`、`[Parameter] public EventCallback OnLink`、
  `[Parameter] public EventCallback OnClick`、`[Parameter] public RenderFragment? ChildContent`、`[Parameter] public string? Class`。

- [ ] **Step 1: 实现组件**

```razor
@using Microsoft.AspNetCore.Components.Web

<div class="dash-card @Class" @onclick="HandleClick">
    <div class="dash-card-head">
        <span class="dash-card-title">@Title</span>
        @if (!string.IsNullOrWhiteSpace(LinkText))
        {
            <a class="dash-card-link" @onclick="HandleLink" @onclick:stopPropagation="true">@LinkText</a>
        }
    </div>
    @ChildContent
</div>
```

配套 `<style>`（组件内，作用域靠类名隔离）要点：
`.dash-card { background:#fff; border:1px solid #e8e8e8; border-radius:8px; padding:16px; margin-bottom:16px; break-inside:avoid; transition:border-color .15s, transform .15s; cursor:pointer; }`
`.dash-card:hover { border-color:#1890ff; transform:translateY(-1px); }`
`.dash-card-head { display:flex; justify-content:space-between; align-items:center; margin-bottom:12px; }`
`.dash-card-title { font-size:13px; font-weight:600; color:#262626; }`
`.dash-card-link { font-size:12px; color:#1890ff; text-decoration:none; }`
`@media (prefers-reduced-motion: reduce) { .dash-card { transition:none; } }`

`HandleClick`：`OnClick.HasDelegate` 时 `await OnClick.InvokeAsync()`；`HandleLink`：`await OnLink.InvokeAsync()`。

- [ ] **Step 2: 构建确认**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: 0 错误。

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/Home/DashboardCard.razor
git commit -m "feat(webui): 新增主页卡片外壳组件"
```

---

### Task 4: 重写 `Index.razor` 为仪表盘

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Index.razor`（整体重写）

**Interfaces:**
- Consumes: Task 1 的 `HomeDashboardView` / `UpcomingRun`；Task 2 的 `UiOverlayService.RequestPermissionCenter()`；Task 3 的 `<DashboardCard>`；
  `ISessionDirectory.ListSummariesAsync()`、`IAgentRegistry.GetPrimaryAgentsAsync()`、`ILlmService.GetAvailableModels()`、
  `SchedulerStatusService.GetStatusAsync()` / `GetAllJobStatusesAsync()`、`PermissionInbox.TotalCount`、`QuestionInbox.TotalCount`。
- Produces: 页面路由行为（`/`）。

- [ ] **Step 1: 重写页面结构与数据加载**

结构（按 DOM 顺序，即瀑布流阅读顺序）：

1. `<div class="home-toolbar">`：左「总览」，右「新建会话」（`ButtonType.Primary`）+「刷新」；
2. `<div class="home-board">`：
   - 最近会话：`DashboardCard Title="最近会话" LinkText="全部 →" OnLink="@(() => NavigationManager.NavigateTo("/sessions"))"`；
     行渲染 `HomeDashboardView.SessionTitle(s.Title, s.Id)` + `HomeDashboardView.RelativeTime(s.LastActiveAt, DateTime.Now)`；
     行 `@onclick` → `/session/{s.Id}` 且 `@onclick:stopPropagation="true"`；空态显示「还没有会话，开始第一个对话」+ 新建按钮。
   - 待我处理：`@if (HomeDashboardView.HasActionItems(_pendingApprovals, _pendingQuestions, _recentFailures))` 才渲染；
     三行 `待审批 N` / `待回答 M` / `最近失败 K`（非零才显示）；`OnClick="@(() => Overlays.RequestPermissionCenter())"`。
   - 定时任务：`Title="定时任务" LinkText="全部 →" OnLink="@(() => NavigationManager.NavigateTo("/cron-jobs"))"`；
     列出 `_upcomingRuns`（`名称` + `yyyy-MM-dd HH:mm`）；底部若 `_expiredCount > 0` 显示「已过期 N 个」；
     `OnClick="@(() => NavigationManager.NavigateTo("/cron-jobs"))"`。
   - 会话 / 智能体 / 模型：三张小卡，`Title` + 大号数字，`OnClick` 分别跳 `/sessions`、`/agents`、`/models`。
3. 加载态：`_loading` 时用 `<Skeleton Active="true" />` 三块占位替代卡片区。

数据加载（`LoadAsync`，`OnInitializedAsync` 调用一次）：

```csharp
var sessions = await SessionDirectory.ListSummariesAsync();
_sessions = sessions
    .Where(s => !s.IsArchived)
    .OrderByDescending(s => s.LastActiveAt)
    .Take(4)
    .ToList();
_sessionCount = sessions.Count(s => !s.IsArchived);

_agentCount = (await AgentRegistry.GetPrimaryAgentsAsync()).Count;
_modelCount = LlmService.GetAvailableModels().Count;

var status = await SchedulerStatusService.GetStatusAsync();
var jobStatuses = (await SchedulerStatusService.GetAllJobStatusesAsync()).ToDictionary(s => s.JobId);
var jobs = await ScheduleManager.ListJobsAsync();
_upcomingRuns = HomeDashboardView.UpcomingRuns(jobs, jobStatuses, DateTime.Now, take: 3);
_expiredCount = HomeDashboardView.CountExpired(jobs, jobStatuses, DateTime.Now, status.IsStarted);

_pendingApprovals = PermissionInbox.TotalCount;
_pendingQuestions = QuestionInbox.TotalCount;
_recentFailures = 0; // 首版固定 0：JobNotificationService 无历史缓存查询接口（见 spec §9.4）
```

注入清单：`ISessionDirectory`、`IAgentRegistry`、`ILlmService`、`SchedulerStatusService`、`IScheduleManager`、
`PermissionInbox`、`QuestionInbox`、`UiOverlayService`、`NavigationManager`、`IWorkspaceProvider`、`IAgentRuntimeManager`。

`新建会话` 沿用现有实现（`RuntimeManager.GetDefaultAgentNameAsync()` + `SessionDirectory.CreateAsync("新会话", agent, WorkspaceProvider.GetProjectRoot())` + 跳 `/session/{id}`）。

**必须删除**：`_recentMessages` 相关全部代码、`AppState` 注入与 `CurrentSessionId` 赋值、旧 `home-*` 样式。

- [ ] **Step 2: 样式（页面 `<style>`）**

```css
.home-toolbar { display:flex; justify-content:space-between; align-items:center; margin-bottom:20px; }
.home-board { column-count:3; column-gap:16px; }
.home-board > * { break-inside: avoid; }
.home-dash-row { display:flex; justify-content:space-between; gap:12px; padding:6px 8px; border-radius:6px; cursor:pointer; font-size:13px; }
.home-dash-row:hover { background:#f5f5f5; }
.home-dash-row .title { color:#262626; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.home-dash-row .meta { color:#8c8c8c; font-size:12px; flex-shrink:0; }
.home-dash-number { font-size:26px; font-weight:600; color:#1890ff; line-height:1.1; }
.home-dash-label { font-size:12px; color:#8c8c8c; margin-top:4px; }
@media (max-width: 900px) { .home-board { column-count:2; } }
@media (max-width: 600px) { .home-board { column-count:1; } }
```

- [ ] **Step 3: 构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 错误（Razor 编译通过）。

- [ ] **Step 4: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/Index.razor
git commit -m "feat(webui): 主页重写为瀑布流卡片仪表盘"
```

---

### Task 5: 端到端验证与收尾

**Files:**
- Modify: 无（仅验证；如发现缺陷回到对应 Task 修）

- [ ] **Step 1: 全量构建 + 全部相关测试**

Run:
```bash
dotnet build Seeing.Agent.slnx
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet vstest tests/capabilities/Seeing.Agent.Scheduler.Tests/bin/Debug/net10.0/Seeing.Agent.Scheduler.Tests.dll
```
Expected: 0 错误；全部通过。

- [ ] **Step 2: 手动验证清单**

`dotnet run --project samples/Seeing.Agent.WebUI`，然后逐项确认：

- 宽屏（≥1600px）三列、缩到 ~800px 两列、~500px 单列，卡片不被列切断；
- 「最近会话」点某行 → 进入 `/session/{id}`（不跳 `/sessions`）；点「全部 →」→ `/sessions`；
- 「待我处理」：无待办时整卡不出现；有审批时点卡打开审批中心抽屉；
- 「定时任务」点卡 → `/cron-jobs`；含「已过期 N 个」时不与列表页计数冲突；
- 会话 / 智能体 / 模型 三卡分别跳 `/sessions`、`/agents`、`/models`；
- 刷新按钮只重载卡片区，不整页 Spin；
- 打开主页后 `AppState.CurrentSessionId` 不被修改（进入某会话再返回 `/` 验证不串会话）。

- [ ] **Step 3: 提交（仅当 Step 2 有修复时）**

```bash
git add -A
git commit -m "fix(webui): 修正主页仪表盘手动验证发现的问题"
```

---

## Self-Review

**Spec coverage：** 布局/断点 → Task 3+4；六卡与点击目标 → Task 4；数据来源 → Task 1（投影）+ Task 4（加载）；
移除伪数据与 `CurrentSessionId` 副作用 → Task 4 Step 1（显式删除项）；审批抽屉 → Task 2；空态/骨架/刷新 → Task 4；
验证清单 → Task 5。spec §9.4「首版不做工作区卡」在 Task 4 中不实现，`_recentFailures` 固定 0 并注明原因。

**类型一致性：** `HomeDashboardView` 的五个方法在 Task 4 的调用处与 Task 1 定义一致；`UpcomingRun.JobId/Title/When` 一致；
`UiOverlayService.PermissionCenterRequested` 在 Task 2 定义与 Task 4 调用一致；`DashboardCard` 参数名（`Title`/`LinkText`/`OnLink`/`OnClick`）在 Task 4 中使用一致。

**Review Focus 落点：** ① 卡片撕裂 → Task 3 样式（`break-inside:avoid`）+ Task 5 宽度检查；② 空卡 → Task 1 `HasActionItems` 测试 + Task 4 条件渲染；
③ 行点击冒泡 → Task 4 `@onclick:stopPropagation` + Task 5 点击校验；④ 标题空/长 → Task 1 `SessionTitle` 测试 + Task 4 `.title` 省略样式；
⑤ 过期误判 → Task 1 `CountExpired` 测试（`schedulerStarted:false` 分支）+ Task 4 传 `status.IsStarted`。
