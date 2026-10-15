# 全局权限收件箱与子会话审批冒泡 · 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让任意会话（含 task 子会话）的权限审批在"当前标签"内可见、可批——以显式呈现端（Presenter）契约替代进程级 presence 布尔，并新增全局审批收件箱（侧栏徽标 + 审批中心抽屉 + 父页面内联卡片）；子会话取消硬编码自动批准、继承父/全局策略。

**Architecture:** 新增 `IPermissionPresenter` / `IPermissionPresentationStore` 作为"可交互性"唯一判定入口（`CanSurface(sessionId)`）；`PermissionService` 步骤 6 改判 `CanSurface`；`PermissionRequestManager` 新增 `GetAllPending`/`PendingChanged` 并仅在"呈现端注销"时收敛在途请求；WebUI 以 `SessionWindowRegistry.Windows` 为"当前标签会话集合"唯一来源，`PermissionInbox`(Singleton) + `PermissionInboxView`(Scoped) 驱动三处 UI；Gateway 每订阅注册一个 presenter；`TaskTool` 继承父会话 `AutoApprove`。

**Tech Stack:** .NET 10.0 / Blazor Server / AntDesign Blazor / xUnit + Moq + FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-09-19-global-permission-inbox-design.md`（v3.1，已架构评审 **APPROVE**）

---

## 执行策略（多 Agent 并行 + 关键点审查 + 最后整体审查）

本计划由三个分册组成（已合并于本文件）：

- **分册 A：Core + Abstractions + 工具调用点**（见下文 `## 分册 A`）
- **分册 B：WebUI（sample）**（见下文 `## 分册 B`）
- **分册 C：Gateway + Hosting(TaskTool) + 文档**（见下文 `## 分册 C`）

**并行分组与依赖：**

| 阶段 | 可并行任务 | 前置 |
|------|------------|------|
| 0（串行） | A-Task1（契约冻结 + Store） | 无 |
| 1（并行） | A-Task2、A-Task5、C-Task1、C-Task3、B-Task1 | 阶段 0 |
| 2（并行） | A-Task3、A-Task6、C-Task2、B-Task2、B-Task3 | 阶段 1 |
| 3（并行） | A-Task4、B-Task4 | 阶段 2 |
| 4 | B-Task5 | 阶段 3 |
| 5 | B-Task6、C-Task4 | 阶段 4 |
| 6（并行） | B-Task7、C-Task5、C-Task6、C-Task7、C-Task8 | 阶段 5 |
| 7（串行，最后） | **A-Task7（删除 presence）** | C-Task1/2 + B-Task6（跨分册编译依赖） |
| 8（整体） | 全量构建 + 全量测试 + 整体代码审查 | 全部 |

**关键审查点（仅在关键位置审查，其余快速推进）：**
1. 阶段 0 后：契约冻结确认（`IPermissionPresenter` / `IPermissionPresentationStore` / `GetAllPending` / `PendingChanged` / `ToolContext.PermissionAuthorizer`）。
2. 阶段 7 前：确认 Gateway 与 WebUI 已无 presence 引用（否则 Core Task 7 会因跨项目编译失败）。
3. 阶段 8：最后整体审查（跨分册一致性 + 全量测试证据）。

**运行约定：** 本仓库 MTP 不可用，一律 VSTest；测试命令见各分册（`dotnet build <测试项目>` + `dotnet vstest <dll> --TestCaseFilter:"FullyQualifiedName~<类名>"`）。

**跨分册编译依赖（务必先读）：** 删除 `IPermissionPresenceStore` 会让 `Seeing.Agent.Gateway` 与 WebUI 暂时无法编译；`tests/spine/Seeing.Agent.Tests` 通过 ProjectReference 引用 Gateway，因此 **A-Task7 必须在 C-Task1/C-Task2 与 B-Task6 之后执行**。A-Task1–A-Task6 不受影响。

---

## 分册 A：Core + Abstractions + 工具调用点

# 全局权限收件箱 · Core 分册实施计划（Abstractions + Core + 工具调用点）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Core/Abstractions 落地"呈现端（Presenter）"契约与在途收敛机制，替换 `IPermissionPresenceStore`，并打通执行级授权器到工具。

**Architecture:** 新增 `IPermissionPresenter` / `IPermissionPresentationStore` 作为"可交互性"的唯一判定入口；`PermissionService` 步骤 6 改判 `CanSurface`；`PermissionRequestManager` 增加 `GetAllPending`/`PendingChanged`，并仅对"呈现端注销"（`PresentersChanged` 信号）经 1s 宽限复核后收敛在途请求（surface 内容收缩不收敛）；`ToolContext.PermissionAuthorizer` 由 `ToolManager` 注入，`SessionToolBase`/`AcpTool` 优先消费、缺失回退工厂。

**Tech Stack:** .NET 10.0；xUnit（仓库包 `xunit.v3`，团队文档记为 xUnit 2.9）+ Moq 4.20 + FluentAssertions 6.12。

**设计依据：** `docs/superpowers/specs/2026-09-19-global-permission-inbox-design.md`（v3.1）§4.1–§4.5、§9、§10、§11。

**范围外（不在本分册）：** WebUI（Presenter/Inbox/View/Drawer/Badge）、Gateway presenter、文档（release notes / architecture）。

**运行约定（本仓库 MTP 不可用，一律 VSTest）：**
- 构建 Core 测试：`dotnet build tests/spine/Seeing.Agent.Tests`
- 运行 Core 测试：`dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"`
- 全量 Core 测试：`dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`

---

## 文件结构锁定

| 文件 | 职责 | 动作 |
|------|------|------|
| `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresenter.cs` | 呈现端契约（稳定快照 + 变更事件） | Create |
| `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresentationStore.cs` | 呈现端登记表契约 | Create |
| `src/spine/Seeing.Agent.Core/Core/Permission/PermissionPresentationStore.cs` | 线程安全登记表；`Changed`/`PresentersChanged` | Create |
| `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionRequestManager.cs` | 新增 `GetAllPending`/`PendingChanged` | Modify |
| `src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs` | 实现新增成员 + 注销收敛 | Modify |
| `src/spine/Seeing.Agent.Core/Core/Permission/PermissionService.cs` | 步骤 6 改判 `CanSurface` | Modify |
| `src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs` | DI 注册 | Modify |
| `src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs` | `ToolContext.PermissionAuthorizer` | Modify |
| `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs` | 注入 `PermissionAuthorizer` | Modify |
| `src/capabilities/Seeing.Agent.Tools.Session/SessionToolBase.cs` | 优先 `context.PermissionAuthorizer` | Modify |
| `src/capabilities/Seeing.Agent.Acp/Tools/AcpTool.cs` | 优先 `context.PermissionAuthorizer` | Modify |
| `src/abstractions/.../IPermissionPresenceStore.cs` + `src/spine/.../PermissionPresenceStore.cs` | 删除 | Delete（Task 7） |
| `tests/spine/Seeing.Agent.Tests/Permission/*` | 测试 | Create/Modify/Delete |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs` | 迁移共享测试替身 `FakePermissionRequestManager` | Modify |
| `tests/capabilities/Seeing.Agent.Tools.Session.Tests/*` | SessionToolBase 路径测试 | Modify |
| `tests/capabilities/Seeing.Agent.Acp.Tests/*` | AcpTool 路径测试 | Modify |

> **跨分册依赖说明（Task 7）：** 删除 `IPermissionPresenceStore` / `PermissionPresenceStore` 后，`src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs:76` 与 `samples/Seeing.Agent.WebUI/Services/ActiveSessionTracker.cs` 会暂时无法编译。`tests/spine/Seeing.Agent.Tests` 通过 `ProjectReference` 引用了 `Seeing.Agent.Gateway`，因此 **Task 7 的"运行全量 Core 测试"必须以 Gateway 分册的 presenter 迁移已完成为前提**。Task 1–6 不删除 presence，可在 Gateway 分册之前独立执行。

---

## Task 1: 呈现端契约与 `PermissionPresentationStore`

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresenter.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresentationStore.cs`
- Create: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionPresentationStore.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Permission/PermissionPresentationStoreTests.cs`

**依赖：** 无。

- [ ] **Step 1: 创建两个契约文件（先让测试能编译）**

`src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresenter.cs`：

```csharp
namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 宿主呈现端：声明在自身上下文（含宿主自定义冒泡）中可呈现的会话集合。
/// 由宿主注册到 <see cref="IPermissionPresentationStore"/>；集合可随宿主状态变化。
/// <para>
/// 契约约定：<see cref="SurfaceSessionIds"/> 必须返回<b>已确认的稳定快照</b>（缓存字段），
/// <b>不得</b>实时读取底层易变状态；空集候选须经宿主确认期后才写入缓存。
/// 宿主自行为瞬态去抖，避免短时抖动触发在途收敛或新请求被 fail-closed。
/// </para>
/// </summary>
public interface IPermissionPresenter
{
    /// <summary>当前可呈现的会话集合快照。</summary>
    IReadOnlyCollection<string> SurfaceSessionIds { get; }

    /// <summary>可呈现集合变化（宿主稳定态变化后触发）。</summary>
    event Action? SurfacedChanged;
}
```

`src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresentationStore.cs`：

```csharp
namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 呈现端登记表：判定"是否存在可呈现指定会话的呈现端"的唯一入口。
/// </summary>
public interface IPermissionPresentationStore
{
    void Register(IPermissionPresenter presenter);

    void Unregister(IPermissionPresenter presenter);

    /// <summary>是否存在可呈现指定会话的呈现端。</summary>
    bool CanSurface(string sessionId);

    /// <summary>呈现端集合身份变化（Register/Unregister 成功）；用于在途收敛复核。</summary>
    event Action? PresentersChanged;

    /// <summary>呈现端集合或可呈现集合内容变化；用于 UI/诊断，不触发在途收敛。</summary>
    event Action? Changed;
}
```

- [ ] **Step 2: 写失败测试**

`tests/spine/Seeing.Agent.Tests/Permission/PermissionPresentationStoreTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Permission;
using Xunit;

namespace Seeing.Agent.Tests.Permission;

/// <summary>
/// 呈现端登记表测试（spec §4.1、§11.10）：Register/Unregister 幂等、CanSurface 并集、
/// Changed/PresentersChanged 触发、SurfacedChanged 转发、异常隔离。
/// </summary>
public class PermissionPresentationStoreTests
{
    [Fact]
    public void Register_ThenCanSurface_ShouldBeTrue()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");

        store.CanSurface("s1").Should().BeFalse();
        store.Register(presenter);

        store.CanSurface("s1").Should().BeTrue();
    }

    [Fact]
    public void Unregister_ShouldRemovePresenter()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");

        store.Register(presenter);
        store.Unregister(presenter);

        store.CanSurface("s1").Should().BeFalse();
        var act = () => store.Unregister(presenter);
        act.Should().NotThrow();
    }

    [Fact]
    public void Register_Twice_ShouldBeIdempotent()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        var changed = 0;
        store.Changed += () => changed++;

        store.Register(presenter);
        store.Register(presenter);

        changed.Should().Be(1);
        store.CanSurface("s1").Should().BeTrue();
    }

    [Fact]
    public void CanSurface_MultiplePresenters_ShouldBeUnion()
    {
        var store = new PermissionPresentationStore();
        store.Register(new TestPresenter("s1"));
        store.Register(new TestPresenter("s2"));

        store.CanSurface("s1").Should().BeTrue();
        store.CanSurface("s2").Should().BeTrue();
        store.CanSurface("s3").Should().BeFalse();
    }

    [Fact]
    public void CanSurface_EmptyOrNullSession_ShouldBeFalse()
    {
        var store = new PermissionPresentationStore();
        store.Register(new TestPresenter("s1", ""));

        store.CanSurface("").Should().BeFalse();
        store.CanSurface(null!).Should().BeFalse();
    }

    [Fact]
    public void Register_ShouldRaiseChangedAndPresentersChanged()
    {
        var store = new PermissionPresentationStore();
        var changed = 0;
        var presentersChanged = 0;
        store.Changed += () => changed++;
        store.PresentersChanged += () => presentersChanged++;

        store.Register(new TestPresenter("s1"));

        changed.Should().Be(1);
        presentersChanged.Should().Be(1);
    }

    [Fact]
    public void Unregister_ShouldRaiseChangedAndPresentersChanged()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        store.Register(presenter);

        var changed = 0;
        var presentersChanged = 0;
        store.Changed += () => changed++;
        store.PresentersChanged += () => presentersChanged++;

        store.Unregister(presenter);

        changed.Should().Be(1);
        presentersChanged.Should().Be(1);
    }

    [Fact]
    public void PresenterSurfacedChanged_ShouldRaiseChangedOnly()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        store.Register(presenter);

        var changed = 0;
        var presentersChanged = 0;
        store.Changed += () => changed++;
        store.PresentersChanged += () => presentersChanged++;

        presenter.SetSurface("s1", "s2");

        changed.Should().Be(1);
        presentersChanged.Should().Be(0);
    }

    [Fact]
    public void UnregisteredPresenterSurfacedChanged_ShouldNotRaiseChanged()
    {
        var store = new PermissionPresentationStore();
        var presenter = new TestPresenter("s1");
        store.Register(presenter);
        store.Unregister(presenter);

        var changed = 0;
        store.Changed += () => changed++;
        presenter.SetSurface("s1", "s2");

        changed.Should().Be(0);
    }

    [Fact]
    public void SubscriberThrows_ShouldNotBreakRegister()
    {
        var store = new PermissionPresentationStore();
        store.Changed += () => throw new InvalidOperationException("boom");

        var act = () => store.Register(new TestPresenter("s1"));

        act.Should().NotThrow();
    }

    private sealed class TestPresenter : IPermissionPresenter
    {
        private IReadOnlyCollection<string> _surface;

        public TestPresenter(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void SetSurface(params string[] sessionIds)
        {
            _surface = sessionIds;
            SurfacedChanged?.Invoke();
        }
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

Run:
```powershell
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionPresentationStoreTests"
```
Expected: 编译失败（`PermissionPresentationStore` 不存在）。先 `dotnet build tests/spine/Seeing.Agent.Tests` 会报 CS0246。

- [ ] **Step 4: 实现 `PermissionPresentationStore`**

`src/spine/Seeing.Agent.Core/Core/Permission/PermissionPresentationStore.cs`：

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 呈现端登记表（Singleton）：线程安全；Register/Unregister 幂等；
/// 订阅/退订各 presenter 的 <see cref="IPermissionPresenter.SurfacedChanged"/> 并转发 <see cref="Changed"/>。
/// <para>
/// <see cref="PresentersChanged"/> 为 Core 内部收敛信号：仅在成功 Register/Unregister 后触发，
/// 且<b>先于</b> <see cref="Changed"/> 触发，供 <c>PermissionRequestManager</c> 区分
/// "呈现端注销"与"可呈现集合收缩"（spec §4.4）。
/// </para>
/// </summary>
public sealed class PermissionPresentationStore : IPermissionPresentationStore
{
    private readonly ConcurrentDictionary<IPermissionPresenter, byte> _presenters = new();
    private readonly ILogger<PermissionPresentationStore>? _logger;

    public PermissionPresentationStore(ILogger<PermissionPresentationStore>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>呈现端集合变化（Register/Unregister 成功时触发；先于 <see cref="Changed"/>）。</summary>
    public event Action? PresentersChanged;

    /// <inheritdoc />
    public void Register(IPermissionPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        if (!_presenters.TryAdd(presenter, 0))
            return;

        presenter.SurfacedChanged += OnPresenterSurfacedChanged;
        RaisePresentersChanged();
        RaiseChanged();
    }

    /// <inheritdoc />
    public void Unregister(IPermissionPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);

        if (!_presenters.TryRemove(presenter, out _))
            return;

        presenter.SurfacedChanged -= OnPresenterSurfacedChanged;
        RaisePresentersChanged();
        RaiseChanged();
    }

    /// <inheritdoc />
    public bool CanSurface(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        foreach (var presenter in _presenters.Keys)
        {
            IReadOnlyCollection<string>? surface;
            try
            {
                surface = presenter.SurfaceSessionIds;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "读取呈现端快照失败: {Presenter}", presenter.GetType().Name);
                continue;
            }

            if (surface is null)
                continue;

            foreach (var id in surface)
            {
                if (string.Equals(id, sessionId, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private void OnPresenterSurfacedChanged() => RaiseChanged();

    private void RaisePresentersChanged()
    {
        try
        {
            PresentersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呈现端集合变更通知失败");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "呈现端变更通知失败");
        }
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionPresentationStoreTests"
```
Expected: 全部 PASS（10 个用例）。

- [ ] **Step 6: 提交**

```powershell
git add src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresenter.cs src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresentationStore.cs src/spine/Seeing.Agent.Core/Core/Permission/PermissionPresentationStore.cs tests/spine/Seeing.Agent.Tests/Permission/PermissionPresentationStoreTests.cs
git commit -m "feat(permission): 新增 IPermissionPresenter/IPermissionPresentationStore 与 Core 登记表"
```

---

## Task 2: `IPermissionRequestManager` 新增 `GetAllPending` / `PendingChanged`

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionRequestManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Permission/PermissionRequestManagerTests.cs`
- Modify（共享测试替身迁移）: `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs`

**依赖：** 无（本任务不改 Manager 的 presence 依赖，presence 仍存在）。

- [ ] **Step 1: 写失败测试（追加到 `PermissionRequestManagerTests` 类内，`NewRequest` 之前）**

```csharp
    [Fact]
    public async Task GetAllPending_ShouldReturnUnresolvedAcrossSessions()
    {
        using var harness = new ManagerHarness();
        var first = await harness.Manager.BeginAsync(NewRequest("s1"));
        await harness.Manager.BeginAsync(NewRequest("s2"));
        await harness.Manager.BeginAsync(NewRequest("s2"));

        harness.Manager.GetAllPending().Should().HaveCount(3);
        harness.Manager.GetAllPending().Select(r => r.SessionId).Should().Contain(new[] { "s1", "s2" });

        harness.Manager.TryResolve(first.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();

        harness.Manager.GetAllPending().Should().HaveCount(2);
        harness.Manager.GetAllPending().Select(r => r.SessionId).Should().NotContain("s1");
    }

    [Fact]
    public async Task GetAllPending_QueueFull_ShouldExcludeOverflowEntry()
    {
        using var harness = new ManagerHarness();
        for (var i = 0; i < 32; i++)
            await harness.Manager.BeginAsync(NewRequest($"s{i}"));

        await harness.Manager.BeginAsync(NewRequest("overflow"));

        harness.Manager.GetAllPending().Should().HaveCount(32);
    }

    [Fact]
    public async Task PendingChanged_ShouldRaiseOnBeginResolveAndDispose()
    {
        var harness = new ManagerHarness();
        var count = 0;
        harness.Manager.PendingChanged += () => Interlocked.Increment(ref count);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"));
        count.Should().Be(1);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
        count.Should().Be(2);

        harness.Manager.Dispose();
        count.Should().Be(3);
    }

    [Fact]
    public async Task PendingChanged_ShouldRaiseOnOverflow()
    {
        using var harness = new ManagerHarness();
        for (var i = 0; i < 32; i++)
            await harness.Manager.BeginAsync(NewRequest($"s{i}"));

        var count = 0;
        harness.Manager.PendingChanged += () => Interlocked.Increment(ref count);

        await harness.Manager.BeginAsync(NewRequest("overflow"));

        count.Should().Be(1);
    }

    [Fact]
    public async Task PendingChanged_ShouldRaiseOnWaitTimeout()
    {
        using var harness = new ManagerHarness(TimeSpan.FromMilliseconds(50));
        var count = 0;
        harness.Manager.PendingChanged += () => Interlocked.Increment(ref count);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"));
        count.Should().Be(1);

        await harness.Manager.WaitAsync(ticket);

        count.Should().Be(2);
    }

    [Fact]
    public async Task PendingChanged_SubscriberThrows_ShouldBeIsolated()
    {
        using var harness = new ManagerHarness();
        harness.Manager.PendingChanged += () => throw new InvalidOperationException("boom");

        var act = async () => await harness.Manager.BeginAsync(NewRequest("s1"));

        await act.Should().NotThrowAsync();
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionRequestManagerTests"
```
Expected: 编译失败（`GetAllPending` / `PendingChanged` 不存在，CS1061）。

- [ ] **Step 3: 修改接口**

`src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionRequestManager.cs`（在 `GetPending` 之后新增两个成员）：

```csharp
    IReadOnlyList<PermissionRequest> GetPending(string sessionId);

    /// <summary>全部未完成在途请求（不做会话过滤；最终一致性依据）。</summary>
    IReadOnlyList<PermissionRequest> GetAllPending();

    int PendingCount { get; }

    /// <summary>在途集合变化（尽力通知；订阅者异常须隔离）。</summary>
    event Action? PendingChanged;
```

- [ ] **Step 4: 实现新成员与触发点**

在 `PermissionRequestManager` 中：

1) 在 `PendingCount` 属性之前新增：

```csharp
    /// <inheritdoc />
    public IReadOnlyList<PermissionRequest> GetAllPending() =>
        _pending.Values
            .Where(entry => !entry.Completion.Task.IsCompleted)
            .Select(entry => entry.Request)
            .ToList();
```

2) 在类顶部（`public event ...` 区域，紧挨 `_pending` 字段声明处）新增：

```csharp
    /// <inheritdoc />
    public event Action? PendingChanged;
```

3) 新增私有触发方法（放在 `Deny(...)` 静态方法之后）：

```csharp
    private void RaisePendingChanged()
    {
        try
        {
            PendingChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发布在途变更通知失败");
        }
    }
```

4) `BeginAsync` 溢出分支：在 `PublishResolvedEvent(normalized, overflow);` 之前插入 `RaisePendingChanged();`：

```csharp
            entry.Completion.TrySetResult(overflow);
            _pending[requestId] = entry;
            RaisePendingChanged();
            // 补发终态事件，使溢出拒绝可被 UI/Gateway 观测（事件唯一发布点语义）。
            PublishResolvedEvent(normalized, overflow);
            return Task.FromResult(new PermissionTicket(requestId, normalized.SessionId));
```

5) `BeginAsync` 常规分支：在 `PublishRequestEvent(normalized);` 之前插入 `RaisePendingChanged();`：

```csharp
        _pending[requestId] = entry;
        RaisePendingChanged();

        if (!string.IsNullOrEmpty(normalized.SessionId) && !_presence.CanPresent(normalized.SessionId))
            _logger.LogDebug("会话无可交互呈现端，仍登记在途请求 RequestId={RequestId}", requestId);

        PublishRequestEvent(normalized);
        return Task.FromResult(new PermissionTicket(requestId, normalized.SessionId));
```

6) `TryResolve` 成功分支：在 `PublishResolvedEvent(entry.Request, resolution);` 之后插入 `RaisePendingChanged();`：

```csharp
        if (!entry.Completion.TrySetResult(resolution))
            return false;

        PublishResolvedEvent(entry.Request, resolution);
        RaisePendingChanged();
        _ = BroadcastDismissAsync(resolution);
        return true;
```

7) `Dispose`：在 `_pending.Clear();` 之后插入 `RaisePendingChanged();`：

```csharp
        _pending.Clear();
        RaisePendingChanged();
    }
```

- [ ] **Step 5: 迁移共享测试替身 `FakePermissionRequestManager`**

`tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs` 中，`GetPending` 之后新增（同时避免 CS0067 警告）：

```csharp
    public IReadOnlyList<PermissionRequest> GetAllPending() => _pending.Values.ToList();

#pragma warning disable CS0067 // 测试替身不触发该事件
    public event Action? PendingChanged;
#pragma warning restore CS0067
```

> 说明：该替身虽位于 WebUI.Tests，但其接口由 Core 契约变更驱动，属接口迁移而非 WebUI 功能任务。

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionRequestManagerTests"
```
Expected: 全部 PASS（含既有用例）。

- [ ] **Step 7: 提交**

```powershell
git add src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionRequestManager.cs src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs tests/spine/Seeing.Agent.Tests/Permission/PermissionRequestManagerTests.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs
git commit -m "feat(permission): IPermissionRequestManager 新增 GetAllPending/PendingChanged"
```

---

## Task 3: `PermissionService` 步骤 6 迁移到 `IPermissionPresentationStore`

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionService.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Permission/PermissionAuthorizationTests.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Permission/PermissionServiceTests.cs`

**依赖：** Task 1。

- [ ] **Step 1: 写失败测试（`PermissionServiceTests`）**

先把 `AuthorizeAsync_WithoutPresence_ShouldDenyNoChannel` 重命名为 `AuthorizeAsync_WithoutPresenter_ShouldDenyNoChannel`（断言不变），再新增两个用例。文件顶部补充 `using Moq;`。

```csharp
    [Fact]
    public async Task AuthorizeAsync_WithPresenterNotSurfacingSession_ShouldDenyNoChannel()
    {
        var presentation = new PermissionPresentationStore();
        presentation.Register(new FixedPresenter("other-session"));
        var manager = new Mock<IPermissionRequestManager>();
        var service = new PermissionService(
            NullLogger<PermissionService>.Instance,
            requestManager: manager.Object,
            presentation: presentation);

        var resolution = await service.AuthorizeAsync(new PermissionRequest
        {
            SessionId = "s1",
            PermissionKind = "tool.execute",
            Resource = "bash"
        });

        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        manager.Verify(
            m => m.BeginAsync(It.IsAny<PermissionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class FixedPresenter : IPermissionPresenter
    {
        private readonly IReadOnlyCollection<string> _surface;

        public FixedPresenter(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void Raise() => SurfacedChanged?.Invoke();
    }
```

`PermissionAuthorizationTests.Harness` 改造（把 `Presence` 换成 `Presentation`）：

- 字段：`public Mock<IPermissionPresentationStore> Presentation { get; } = new();`
- 构造函数首行：`Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(true);`
- 构造 `PermissionService` 时第 7 个参数传 `Presentation.Object`。
- 全文把 `h.Presence.Setup(p => p.CanPresent(It.IsAny<string>())).Returns(false);` 替换为 `h.Presentation.Setup(p => p.CanSurface(It.IsAny<string>())).Returns(false);`
- 把 `AuthorizeAsync_NoPresence_ShouldDenyNoChannel` 重命名为 `AuthorizeAsync_NoPresenter_ShouldDenyNoChannel`，`AuthorizeAsync_WithPresence_ShouldPresentToChannelsAndWait` 重命名为 `AuthorizeAsync_CanSurface_ShouldPresentToChannelsAndWait`。

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests
```
Expected: 编译失败（`PermissionService` 无 `presentation` 参数，CS1739/CS1503）。

- [ ] **Step 3: 修改 `PermissionService`**

把字段与构造参数从 `IPermissionPresenceStore? _presence` 改为 `IPermissionPresentationStore? _presentation`：

```csharp
    private readonly IPermissionRequestManager? _requestManager;
    private readonly IPermissionPresentationStore? _presentation;
    private readonly IEnumerable<IPermissionChannel> _channels;
    private readonly IAgentRegistry? _agentRegistry;

    public PermissionService(
        ILogger<PermissionService> logger,
        IPermissionGrantStore? grantStore = null,
        EffectivePermissionPolicy? effectivePolicy = null,
        IWorkspacePathGate? workspaceGate = null,
        IOptionsMonitor<SeeingAgentOptions>? options = null,
        IPermissionRequestManager? requestManager = null,
        IPermissionPresentationStore? presentation = null,
        IEnumerable<IPermissionChannel>? channels = null,
        IAgentRegistry? agentRegistry = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _grantStore = grantStore;
        _effectivePolicy = effectivePolicy;
        _workspaceGate = workspaceGate;
        _options = options;
        _requestManager = requestManager;
        _presentation = presentation;
        _channels = channels ?? Array.Empty<IPermissionChannel>();
        _agentRegistry = agentRegistry;

        // 生成或加载 HMAC 密钥
        _hmacKey = LoadOrGenerateHmacKey();
    }
```

步骤 6（`PermissionService.cs:231`）改为：

```csharp
        // 6. 询问
        if (_presentation is null || !_presentation.CanSurface(sessionId))
            return Result(normalized, PermissionEffect.Deny, PermissionResolvedBy.NoChannel, "无交互通道");
```

- [ ] **Step 4: 更新 DI 注册（本任务只新增，不删除 presence）**

`src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs` 在 `AddPermissionService` 内：

```csharp
        // 授权存储（记忆 + 白名单目录面）与可交互性计数
        services.AddSingleton<IPermissionGrantStore, PermissionGrantStore>();
        services.AddSingleton<IPermissionPresenceStore, PermissionPresenceStore>();

        // 呈现端登记表（Singleton）
        services.AddSingleton<IPermissionPresentationStore, PermissionPresentationStore>();
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionAuthorizationTests|FullyQualifiedName~PermissionServiceTests"
```
Expected: 全部 PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/spine/Seeing.Agent.Core/Core/Permission/PermissionService.cs src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs tests/spine/Seeing.Agent.Tests/Permission/PermissionAuthorizationTests.cs tests/spine/Seeing.Agent.Tests/Permission/PermissionServiceTests.cs
git commit -m "feat(permission): 步骤 6 改用 IPermissionPresentationStore.CanSurface"
```

---

## Task 4: `PermissionRequestManager` 迁移到 `PermissionPresentationStore` + 注销收敛

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Permission/PermissionRequestManagerTests.cs`

**依赖：** Task 1、Task 3。

- [ ] **Step 1: 写失败测试（追加到 `PermissionRequestManagerTests`）**

```csharp
    [Fact]
    public async Task PresenterUnregistered_ShouldConvergePendingToDeny()
    {
        using var harness = new ManagerHarness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var presenter = new TestPresenter("s1");
        harness.Presentation.Register(presenter);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"));
        var waiter = harness.Manager.WaitAsync(ticket);

        harness.Presentation.Unregister(presenter);

        var resolution = await waiter;
        resolution.Decision.Should().Be(PermissionEffect.Deny);
        resolution.ResolvedBy.Should().Be(PermissionResolvedBy.NoChannel);
        resolution.Reason.Should().Be("呈现端已移除");
    }

    [Fact]
    public async Task SurfaceShrink_ShouldNotConverge()
    {
        using var harness = new ManagerHarness(convergenceGrace: TimeSpan.FromMilliseconds(50));
        var presenter = new TestPresenter("s1");
        harness.Presentation.Register(presenter);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"));

        presenter.SetSurface(); // 同一 presenter 集合收缩：仅 SurfacedChanged，不 Unregister

        await Task.Delay(200);

        harness.Manager.PendingCount.Should().Be(1);
        harness.Manager.GetAllPending().Should().ContainSingle();

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
    }

    [Fact]
    public async Task PresenterUnregisteredThenReRegisteredWithinGrace_ShouldNotConverge()
    {
        using var harness = new ManagerHarness(convergenceGrace: TimeSpan.FromMilliseconds(300));
        var presenter = new TestPresenter("s1");
        harness.Presentation.Register(presenter);

        var ticket = await harness.Manager.BeginAsync(NewRequest("s1"));

        harness.Presentation.Unregister(presenter);
        harness.Presentation.Register(new TestPresenter("s1"));

        await Task.Delay(600);

        harness.Manager.PendingCount.Should().Be(1);

        harness.Manager.TryResolve(ticket.RequestId, PermissionEffect.Allow,
            PermissionGrantScope.Once, PermissionResolvedBy.User).Should().BeTrue();
    }
```

同时改造 `ManagerHarness`：

```csharp
        public PermissionPresentationStore Presentation { get; } = new();
```

构造函数签名与赋值改为：

```csharp
        public ManagerHarness(
            TimeSpan? timeout = null,
            IEnumerable<IPermissionChannel>? channels = null,
            TimeSpan? convergenceGrace = null)
        {
            Options = new TestOptionsMonitor(new SeeingAgentOptions());
            Policy = new EffectivePermissionPolicy(Sessions.Object, Options);

            var publisher = new Mock<IExecutionEventPublisher>();
            publisher
                .Setup(p => p.Publish(It.IsAny<string>(), It.IsAny<IMessageEvent>()))
                .Callback<string, IMessageEvent>((_, evt) => PublishedEvents.Add(evt));

            var channelList = (channels ?? Array.Empty<IPermissionChannel>()).ToList();
            Manager = new PermissionRequestManager(
                publisher.Object, Presentation, Policy, channelList, SessionEvents, Options,
                NullLogger<PermissionRequestManager>.Instance,
                timeout ?? TimeSpan.FromMinutes(5),
                convergenceGrace);
        }
```

并在 `ManagerHarness` 之后新增：

```csharp
    private sealed class TestPresenter : IPermissionPresenter
    {
        private IReadOnlyCollection<string> _surface;

        public TestPresenter(params string[] sessionIds) => _surface = sessionIds;

        public IReadOnlyCollection<string> SurfaceSessionIds => _surface;

        public event Action? SurfacedChanged;

        public void SetSurface(params string[] sessionIds)
        {
            _surface = sessionIds;
            SurfacedChanged?.Invoke();
        }
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests
```
Expected: 编译失败（`PermissionRequestManager` 构造签名/内部 ctor 无 `convergenceGrace`，CS1501/CS1729）。

- [ ] **Step 3: 修改 Manager（字段/构造/订阅）**

`PermissionRequestManager.cs`：

1) 字段替换：

```csharp
    private readonly ConcurrentDictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IPermissionChannel> _channels;
    private readonly IExecutionEventPublisher _eventPublisher;
    private readonly IPermissionPresentationStore _presentation;
    private readonly EffectivePermissionPolicy _policy;
    private readonly ILogger<PermissionRequestManager> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _convergenceGrace;
    private readonly Timer _convergenceTimer;
    private readonly IDisposable? _sessionSubscription;
    private readonly IDisposable? _optionsSubscription;
    private int _presentersChangedPending;
    private bool _lastAutoApproveAll;
    private bool _disposed;
```

2) 公开构造：

```csharp
    /// <summary>创建在途管理器（等待超时 5 分钟；收敛宽限 1 秒）。</summary>
    public PermissionRequestManager(
        IExecutionEventPublisher eventPublisher,
        IPermissionPresentationStore presentation,
        EffectivePermissionPolicy policy,
        IEnumerable<IPermissionChannel> channels,
        ISessionEventPublisher sessionEvents,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<PermissionRequestManager> logger)
        : this(eventPublisher, presentation, policy, channels, sessionEvents, options, logger, DefaultTimeout)
    {
    }
```

3) 内部构造：

```csharp
    /// <summary>创建在途管理器（可指定等待超时与收敛宽限；仅供测试注入短值）。</summary>
    internal PermissionRequestManager(
        IExecutionEventPublisher eventPublisher,
        IPermissionPresentationStore presentation,
        EffectivePermissionPolicy policy,
        IEnumerable<IPermissionChannel> channels,
        ISessionEventPublisher sessionEvents,
        IOptionsMonitor<SeeingAgentOptions> options,
        ILogger<PermissionRequestManager> logger,
        TimeSpan timeout,
        TimeSpan? convergenceGrace = null)
    {
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _channels = (channels ?? Array.Empty<IPermissionChannel>()).ToList();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout;
        _convergenceGrace = convergenceGrace ?? TimeSpan.FromSeconds(1);

        _lastAutoApproveAll = options?.CurrentValue.Permission?.AutoApproveAll == true;
        _sessionSubscription = sessionEvents?.Events.Subscribe(OnSessionEvent);
        _optionsSubscription = options?.OnChange(OnOptionsChanged);

        _presentation.PresentersChanged += OnPresentersChanged;
        _presentation.Changed += OnPresentationChanged;
        _convergenceTimer = new Timer(OnConvergenceTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
```

4) `BeginAsync` 中的调试日志改用 `CanSurface`：

```csharp
        if (!string.IsNullOrEmpty(normalized.SessionId) && !_presentation.CanSurface(normalized.SessionId))
            _logger.LogDebug("会话无可交互呈现端，仍登记在途请求 RequestId={RequestId}", requestId);
```

- [ ] **Step 4: 修改 Manager（收敛逻辑与 Dispose）**

新增私有成员（放在 `RaisePendingChanged` 之后）：

```csharp
    // 呈现端集合变更信号：Register/Unregister 先触发 PresentersChanged，再触发 Changed；
    // 仅呈现端集合变化才触发收敛复核，SurfaceSessionIds 内容收缩（仅 Changed）不收敛（spec §4.4）。
    private void OnPresentersChanged() => Interlocked.Exchange(ref _presentersChangedPending, 1);

    private void OnPresentationChanged()
    {
        if (Interlocked.Exchange(ref _presentersChangedPending, 0) == 0)
            return;

        if (_disposed)
            return;

        try
        {
            _convergenceTimer.Change(_convergenceGrace, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnConvergenceTimer(object? state)
    {
        if (_disposed)
            return;

        try
        {
            foreach (var entry in _pending.Values.Where(e => !e.Completion.Task.IsCompleted).ToList())
            {
                var sessionId = entry.Request.SessionId;
                if (!string.IsNullOrEmpty(sessionId) && _presentation.CanSurface(sessionId))
                    continue;

                var requestId = entry.Request.RequestId;
                if (string.IsNullOrEmpty(requestId))
                    continue;

                TryResolve(requestId!, PermissionEffect.Deny, PermissionGrantScope.Once,
                    PermissionResolvedBy.NoChannel, "呈现端已移除", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "在途权限收敛复核失败");
        }
    }
```

`Dispose` 改为：

```csharp
    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _presentation.PresentersChanged -= OnPresentersChanged;
        _presentation.Changed -= OnPresentationChanged;
        _convergenceTimer.Dispose();

        _sessionSubscription?.Dispose();
        _optionsSubscription?.Dispose();

        foreach (var requestId in _pending.Keys)
        {
            // 与溢出路径一致：统一经 TryResolve 发布 PermissionResolvedEvent 并广播 DismissAsync。
            TryResolve(requestId, PermissionEffect.Deny, PermissionGrantScope.Once,
                PermissionResolvedBy.Cancellation, "管理器已释放");
        }

        _pending.Clear();
        RaisePendingChanged();
    }
```

- [ ] **Step 5: 运行测试确认通过**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionRequestManagerTests"
```
Expected: 全部 PASS（含新增收敛用例）。

> 若 `SurfaceShrink_ShouldNotConverge` 偶发不稳定，将 `Task.Delay(200)` 提高到 `Task.Delay(400)`（宽限 50ms，余量仍充足）。

- [ ] **Step 6: 提交**

```powershell
git add src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs tests/spine/Seeing.Agent.Tests/Permission/PermissionRequestManagerTests.cs
git commit -m "feat(permission): Manager 迁移呈现端登记表并实现注销收敛（宽限 1s）"
```

---

## Task 5: `ToolContext.PermissionAuthorizer` + `ToolManager` 注入 + `SessionToolBase` 贯通

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs`
- Modify: `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs:593-601`
- Modify: `src/capabilities/Seeing.Agent.Tools.Session/SessionToolBase.cs:54-79`
- Test: `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionSearchToolTests.cs`
- Test（替身）: `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionToolTestHarness.cs`

**依赖：** 无。

- [ ] **Step 1: 写失败测试**

在 `tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionToolTestHarness.cs` 末尾新增：

```csharp
/// <summary>记录最近一次请求并返回预设决策的直接授权器（用于 context.PermissionAuthorizer 注入测试）。</summary>
internal sealed class RecordingPermissionAuthorizer : IPermissionAuthorizer
{
    private readonly PermissionEffect _decision;

    public RecordingPermissionAuthorizer(PermissionEffect decision = PermissionEffect.Allow)
    {
        _decision = decision;
        SessionId = "context-session";
    }

    public PermissionRequest? LastRequest { get; private set; }

    public string SessionId { get; }

    public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new PermissionResolution
        {
            RequestId = request.RequestId ?? "req-ctx",
            SessionId = request.SessionId,
            CallId = request.CallId,
            Decision = _decision,
            Reason = _decision == PermissionEffect.Allow ? "允许" : "权限被拒绝",
        });
    }
}
```

在 `SessionSearchToolTests.cs` 追加：

```csharp
    [Fact]
    public async Task Search_CrossGroup_ShouldPreferContextPermissionAuthorizer()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        await h.AddMessageAsync(root2.Id, "user", "shared content");
        var authorizer = new RecordingPermissionAuthorizer(PermissionEffect.Allow);
        var tool = CreateTool(h);

        var context = new ToolContext
        {
            SessionId = root1.Id,
            PermissionAuthorizer = authorizer
        };

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root2.Id, pattern = "shared" }), context);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("shared content");
        authorizer.LastRequest.Should().NotBeNull();
        authorizer.LastRequest!.Resource.Should().Be("session_search");
    }

    [Fact]
    public async Task Search_CrossGroup_ContextAuthorizerDeny_ShouldBeatFactoryAllow()
    {
        using var h = new SessionToolTestHarness();
        var root1 = await h.CreateRootGroupedAsync("r1");
        var root2 = await h.CreateRootGroupedAsync("r2");
        await h.AddMessageAsync(root2.Id, "user", "private content");
        var factory = new StubPermissionAuthorizerFactory(PermissionEffect.Allow);
        var tool = CreateTool(h);

        var context = Context(root1.Id, factory);
        context.PermissionAuthorizer = new RecordingPermissionAuthorizer(PermissionEffect.Deny);

        var result = await tool.ExecuteAsync(
            Args(new { session_id = root2.Id, pattern = "private" }), context);

        result.Success.Should().BeFalse();
        result.Output.Should().NotContain("private content");
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet build tests/capabilities/Seeing.Agent.Tools.Session.Tests
```
Expected: 编译失败（`ToolContext.PermissionAuthorizer` 不存在，CS0117）。

- [ ] **Step 3: 修改 `ToolContext`**

`src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs`：
1) 顶部 `using System.Text.Json;` 之后新增 `using Seeing.Agent.Abstractions.Permissions;`
2) `ToolContext` 在 `Services` 属性之后新增：

```csharp
        /// <summary>
        /// 执行级授权器（由 ToolManager 从执行链注入；工具须优先使用，缺失时回退工厂）。
        /// </summary>
        public IPermissionAuthorizer? PermissionAuthorizer { get; set; }
```

- [ ] **Step 4: 修改 `ToolManager` 注入**

`src/spine/Seeing.Agent.Core/Tools/ToolManager.cs:593-601` 的 `new ToolContext { ... }` 增加一项：

```csharp
                var context = new ToolContext
                {
                    SessionId = sessionId,
                    CallId = toolCall.Id,
                    CancellationToken = cancellationToken,
                    EventSink = sink,
                    MetadataSink = sink,
                    Services = _serviceProvider,
                    PermissionAuthorizer = permissionAuthorizer
                };
```

- [ ] **Step 5: 修改 `SessionToolBase.AuthorizeAsync`**

`src/capabilities/Seeing.Agent.Tools.Session/SessionToolBase.cs:61-74` 改为：

```csharp
        var authorizer = context.PermissionAuthorizer;
        if (authorizer is null)
        {
            var factory = context.Services?.GetService<IPermissionAuthorizerFactory>();
            if (factory is null)
                return null;

            authorizer = factory.Create(context.SessionId, null);
        }

        var resolution = await authorizer.AuthorizeAsync(new PermissionRequest
        {
            SessionId = context.SessionId,
            CallId = context.CallId,
            PermissionKind = "tool.execute",
            Resource = resource,
            Message = message,
            Metadata = metadata,
        }, ct).ConfigureAwait(false);

        return resolution.Decision == PermissionEffect.Allow
            ? null
            : Failure(resolution.Reason ?? "权限被拒绝");
```

- [ ] **Step 6: 运行测试确认通过**

Run:
```powershell
dotnet build tests/capabilities/Seeing.Agent.Tools.Session.Tests; dotnet vstest tests/capabilities/Seeing.Agent.Tools.Session.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionSearchToolTests"
```
Expected: 全部 PASS。

- [ ] **Step 7: 回归 Core 测试（ToolContext 变更影响面）**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```
Expected: 全部 PASS。

- [ ] **Step 8: 提交**

```powershell
git add src/abstractions/Seeing.Agent.Abstractions/Tools/ITool.cs src/spine/Seeing.Agent.Core/Tools/ToolManager.cs src/capabilities/Seeing.Agent.Tools.Session/SessionToolBase.cs tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionToolTestHarness.cs tests/capabilities/Seeing.Agent.Tools.Session.Tests/SessionSearchToolTests.cs
git commit -m "feat(permission): ToolContext 注入执行级授权器并贯通 SessionToolBase"
```

---

## Task 6: `AcpTool` 优先消费 `context.PermissionAuthorizer`

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Acp/Tools/AcpTool.cs:92-113`
- Test: `tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs`
- Test（替身）: `tests/capabilities/Seeing.Agent.Acp.Tests/PermissionTestDoubles.cs`

**依赖：** Task 5（`ToolContext.PermissionAuthorizer` 已存在）。

> `AcpPermissionBridge` 保持不变（异步上下文外构造，保留 `RequireInteraction`）。

- [ ] **Step 1: 写失败测试**

在 `tests/capabilities/Seeing.Agent.Acp.Tests/PermissionTestDoubles.cs` 末尾新增：

```csharp
/// <summary>记录最近一次请求并返回预设决策的直接授权器（用于 context.PermissionAuthorizer 注入测试）。</summary>
internal sealed class CapturingPermissionAuthorizer : IPermissionAuthorizer
{
    private readonly PermissionEffect _decision;

    public CapturingPermissionAuthorizer(PermissionEffect decision = PermissionEffect.Allow, string reason = "允许")
    {
        _decision = decision;
        Reason = reason;
        SessionId = "context-session";
    }

    public string Reason { get; }

    public PermissionRequest? LastRequest { get; private set; }

    public string SessionId { get; }

    public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new PermissionResolution
        {
            RequestId = request.RequestId ?? "req-ctx",
            SessionId = string.IsNullOrEmpty(request.SessionId) ? SessionId : request.SessionId,
            CallId = request.CallId,
            Decision = _decision,
            Reason = Reason
        });
    }
}
```

在 `AcpToolTests.cs` 追加：

```csharp
    [Fact]
    public async Task ExecuteAsync_ContextAuthorizer_ShouldBePreferredOverFactory()
    {
        var runner = new Mock<IAcpSessionRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcpRunResult { Text = "done", Success = true });

        var factory = new CapturingPermissionAuthorizerFactory { Decision = PermissionEffect.Allow };
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionAuthorizerFactory>(factory);
        using var sp = services.BuildServiceProvider();

        var contextAuthorizer = new CapturingPermissionAuthorizer(PermissionEffect.Allow);

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp,
            PermissionAuthorizer = contextAuthorizer
        });

        result.Success.Should().BeTrue();
        contextAuthorizer.LastRequest.Should().NotBeNull();
        contextAuthorizer.LastRequest!.Resource.Should().Be("acp");
        factory.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ContextAuthorizerDeny_ShouldFailWithoutCallingFactory()
    {
        var runner = new Mock<IAcpSessionRunner>();
        var factory = new CapturingPermissionAuthorizerFactory { Decision = PermissionEffect.Allow };
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionAuthorizerFactory>(factory);
        using var sp = services.BuildServiceProvider();

        var contextAuthorizer = new CapturingPermissionAuthorizer(PermissionEffect.Deny, "上下文拒绝");

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp,
            PermissionAuthorizer = contextAuthorizer
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("上下文拒绝");
        factory.LastRequest.Should().BeNull();
        runner.Verify(
            r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run:
```powershell
dotnet build tests/capabilities/Seeing.Agent.Acp.Tests
```
Expected: 编译失败（`PermissionAuthorizer` 未使用前 AcpTool 仍走工厂，`factory.LastRequest.Should().BeNull()` 会失败；先运行用例确认失败）。

- [ ] **Step 3: 修改 `AcpTool`**

`src/capabilities/Seeing.Agent.Acp/Tools/AcpTool.cs:92-113` 改为：

```csharp
        var authorizer = context.PermissionAuthorizer;
        if (authorizer is null)
        {
            var authorizerFactory = context.Services?.GetService<IPermissionAuthorizerFactory>();
            if (authorizerFactory is not null)
                authorizer = authorizerFactory.Create(context.SessionId);
        }

        if (authorizer is not null)
        {
            var resolution = await authorizer.AuthorizeAsync(new PermissionRequest
            {
                SessionId = context.SessionId,
                CallId = context.CallId,
                AgentName = Seeing.Agent.Acp.Hosting.AcpDynamicAgentRegistrar.GetAgentName(backend),
                PermissionKind = "tool.execute",
                Resource = "acp",
                Metadata = new Dictionary<string, object>
                {
                    ["description"] = description,
                    ["backend"] = backend,
                    ["prompt"] = prompt
                }
            }, context.CancellationToken);

            if (resolution.Decision != PermissionEffect.Allow)
                return Failure(resolution.Reason ?? "权限被拒绝");
        }
```

- [ ] **Step 4: 运行测试确认通过**

Run:
```powershell
dotnet build tests/capabilities/Seeing.Agent.Acp.Tests; dotnet vstest tests/capabilities/Seeing.Agent.Acp.Tests/bin/Debug/net10.0/Seeing.Agent.Acp.Tests.dll --TestCaseFilter:"FullyQualifiedName~AcpToolTests"
```
Expected: 全部 PASS（含既有工厂回退用例）。

- [ ] **Step 5: 提交**

```powershell
git add src/capabilities/Seeing.Agent.Acp/Tools/AcpTool.cs tests/capabilities/Seeing.Agent.Acp.Tests/AcpToolTests.cs tests/capabilities/Seeing.Agent.Acp.Tests/PermissionTestDoubles.cs
git commit -m "feat(permission): AcpTool 优先消费 context.PermissionAuthorizer"
```

---

## Task 7: 删除 `IPermissionPresenceStore` / `PermissionPresenceStore` 与遗留测试

**Files:**
- Delete: `src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresenceStore.cs`
- Delete: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionPresenceStore.cs`
- Delete: `tests/spine/Seeing.Agent.Tests/Permission/PermissionPresenceStoreTests.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs`

**依赖：** Task 3、Task 4（Core 已无 presence 引用）；**并且** Gateway 分册的 presenter 迁移已执行（否则 `tests/spine/Seeing.Agent.Tests` 因 `Seeing.Agent.Gateway` 引用被删类型而编译失败——见文首跨分册依赖说明）。

- [ ] **Step 1: 删除 presence 文件与测试**

```powershell
git rm src/abstractions/Seeing.Agent.Abstractions/Permissions/IPermissionPresenceStore.cs src/spine/Seeing.Agent.Core/Core/Permission/PermissionPresenceStore.cs tests/spine/Seeing.Agent.Tests/Permission/PermissionPresenceStoreTests.cs
```

- [ ] **Step 2: 移除 DI 注册**

`src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs`：删除 `services.AddSingleton<IPermissionPresenceStore, PermissionPresenceStore>();` 一行，结果应为：

```csharp
        // 授权存储（记忆 + 白名单目录面）
        services.AddSingleton<IPermissionGrantStore, PermissionGrantStore>();

        // 呈现端登记表（Singleton）
        services.AddSingleton<IPermissionPresentationStore, PermissionPresentationStore>();
```

- [ ] **Step 3: 确认 Core 已无 presence 引用**

Run:
```powershell
rg -n "IPermissionPresenceStore|PermissionPresenceStore" src/spine src/abstractions tests/spine
```
Expected: 无输出（`src/gateway` 与 `samples` 的命中由各自分册处理，不在本检查范围）。

- [ ] **Step 4: 构建并运行全量 Core 测试**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests; dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```
Expected: 全部 PASS（`PermissionPresentationStore` 覆盖原 presence 测试职责；`PermissionPresenceStoreTests` 已删除）。

> 若 Gateway 分册尚未执行，此步会因 `Seeing.Agent.Gateway` 编译失败而中止——先执行 Gateway 分册再回到本步。

- [ ] **Step 5: 提交**

```powershell
git add -A src/spine/Seeing.Agent.Core/Extensions/PermissionServiceExtensions.cs
git commit -m "refactor(permission): 删除 IPermissionPresenceStore/PermissionPresenceStore，统一呈现端登记表"
```

---

## Self-Review（计划自检）

**1. Spec 覆盖：**
- §4.1 呈现端抽象 + Store → Task 1。
- §4.2 `GetAllPending`/`PendingChanged` → Task 2。
- §4.3 步骤 6 `CanSurface` → Task 3。
- §4.4 仅呈现端注销收敛（宽限 1s、surface 收缩不收敛）→ Task 4。
- §4.5 执行级授权器贯通（ToolContext/ToolManager/SessionToolBase/AcpTool）→ Task 5、Task 6；`AcpPermissionBridge` 保持不动（未修改）。
- §9 Core 测试（Store、step6、N1 回归、GetAllPending/PendingChanged、迁移 Fake、删除 PresenceStoreTests）→ Task 1–4、Task 7。
- §10 契约删除/新增 → Task 1、2、3、5、7。
- §11.10 Register/Unregister 触发 `Changed` → Task 1。
- 范围外：§4.6 Gateway、§6 WebUI、§7 UI、§10 文档（由对应分册负责）。

**2. 占位符扫描：** 无 TBD/TODO/"类似上文"；每个实现与测试步骤均给出完整代码。

**3. 类型一致性：** `IPermissionPresenter.SurfaceSessionIds`/`SurfacedChanged`、`IPermissionPresentationStore.Register/Unregister/CanSurface/PresentersChanged/Changed`、`IPermissionRequestManager.GetAllPending/PendingChanged`、`ToolContext.PermissionAuthorizer` 在各 Task 中命名与签名一致。


---

## 分册 B：WebUI（sample/Seeing.Agent.WebUI）

# WebUI 全局权限收件箱接线实施计划（分册）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `samples/Seeing.Agent.WebUI` 与 `Seeing.Agent.Hosting.Web` 中接线「当前标签内全局审批收件箱」——新增 `PermissionInbox`（Singleton 投影）、`PermissionInboxView`（Scoped 标签视图）、`WebUiPermissionPresenter`（Scoped 呈现端），删除旧的 `PermissionCardAggregator`/`ActiveSessionTracker`，并把侧栏徽标、审批中心抽屉、子会话内联卡片接到统一权威在途态。

**Architecture:** 在途真相源仍是 `IPermissionRequestManager`；`PermissionInbox`（Singleton）订阅 `PendingChanged` 全量重建 `PermissionCardModel` 投影；`PermissionInboxView`（Scoped，circuit 维度）以 `SessionWindowRegistry.Windows` 为唯一作用域来源做过滤；`WebUiPermissionPresenter`（Scoped）经同一 registry 实例向 Core `IPermissionPresentationStore` 声明「已确认稳定快照」的可呈现会话集合（空集确认 1s、变化去抖 250ms）。UI 三处入口（工具卡槽位 / task 卡 / 审批中心抽屉）共享 View。

**Tech Stack:** .NET 10 / Blazor Server（`Microsoft.NET.Sdk.Web`）/ AntDesign Blazor 1.6.2 / xUnit v3 + Moq 4.20 + FluentAssertions 8。

**依赖 Core 契约 Task（必须先完成 Core 分册）：**
- `Seeing.Agent.Abstractions.Permissions.IPermissionPresenter`（`SurfaceSessionIds` + `SurfacedChanged`）。
- `Seeing.Agent.Abstractions.Permissions.IPermissionPresentationStore`（`Register`/`Unregister`/`CanSurface`/`Changed`）。
- `IPermissionRequestManager.GetAllPending()` + `event Action? PendingChanged`。
- Core 已在 `AddSeeingCore` 注册 `IPermissionPresentationStore`（Singleton）。本分册不实现这些契约。

**统一命令（本分册全程使用）：**
```bash
# 构建（WebUI 测试项目引用 WebUI，构建测试即构建 WebUI）
dotnet build tests/apps/Seeing.Agent.WebUI.Tests

# 运行单个测试类（注意：MTP 在本环境无法发现测试，一律用 VSTest）
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"
```

---

## 文件结构总览

| 文件 | 责任 | 动作 |
|------|------|------|
| `samples/Seeing.Agent.WebUI/Services/PermissionInbox.cs` | Singleton 投影：`GetAllPending` → `PermissionCardModel` + `Changed` | Create |
| `samples/Seeing.Agent.WebUI/Services/PermissionInboxView.cs` | Scoped 标签视图：按 `registry.Windows` 过滤 + 合并重算 | Create |
| `samples/Seeing.Agent.WebUI/Services/WebUiPermissionPresenter.cs` | Scoped 呈现端：稳定快照 + 去抖/空集确认 | Create |
| `samples/Seeing.Agent.WebUI/Components/PermissionCenterDrawer.razor` | 审批中心抽屉：按会话分组 + 就地批准/拒绝 + 跳转 | Create |
| `samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor` | 删除权限绑定逻辑 | Modify |
| `samples/Seeing.Agent.WebUI/Components/Messaging/ToolPermissionSlot.razor` | 改用 `PermissionInboxView.GetByCallId` | Modify |
| `samples/Seeing.Agent.WebUI/Components/Messaging/TaskMessageComponent.razor` | 展开区新增子会话在途卡 | Modify |
| `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor` | 审批中心菜单项 + 承载抽屉 + 悬浮入口方法 | Modify |
| `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor` | 移动端悬浮按钮 | Modify |
| `samples/Seeing.Agent.WebUI/Pages/Session.razor` | 徽标 + `PendingPermissions` + `View.Changed` 订阅 + 移除 tracker | Modify |
| `samples/Seeing.Agent.WebUI/Pages/Conference.razor` | 徽标 + `PendingPermissions` + `View.Changed` 订阅 | Modify |
| `samples/Seeing.Agent.WebUI/Services/SessionSidebarView.cs` | `SidebarEntry` 增 `int PendingPermissions` | Modify |
| `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs` | P5：`Permission*` 事件不进子流聚合 | Modify |
| `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs` | 移除 `ActiveSessionTracker` 依赖 | Modify |
| `samples/Seeing.Agent.WebUI/Program.cs` | DI 注册调整 | Modify |
| `samples/Seeing.Agent.WebUI/Services/PermissionCardAggregator.cs` | 删除 | Delete |
| `samples/Seeing.Agent.WebUI/Services/ActiveSessionTracker.cs` | 删除 | Delete |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/FakePermissionRequestManager.cs` | 共享测试替身（从被删测试迁出 + 扩展） | Create |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxTests.cs` | Inbox 测试 | Create |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxViewTests.cs` | View 测试 | Create |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/WebUiPermissionPresenterTests.cs` | Presenter 测试 | Create |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInteractionServiceTests.cs` | 适配 Inbox | Modify |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs` | 删除 | Delete |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/ActiveSessionTrackerTests.cs` | 删除 | Delete |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSidebarViewTests.cs` | 增 `PendingPermissions` 覆盖 | Modify |
| `tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs` | P5 回归测试 | Modify |

---

## Task 1: PermissionInbox（Singleton 投影）

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/PermissionInbox.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/FakePermissionRequestManager.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxTests.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`

**依赖 Core 契约 Task**：`IPermissionRequestManager.GetAllPending()` + `PendingChanged`。

- [ ] **Step 1: 写失败测试（共享 Fake + Inbox 测试）**

创建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/FakePermissionRequestManager.cs`：

```csharp
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>测试替身：在途请求管理器（GetAllPending 可对账；Seed/Remove 触发 PendingChanged）。</summary>
internal sealed class FakePermissionRequestManager : IPermissionRequestManager
{
    private readonly Dictionary<string, PermissionRequest> _pending = new(StringComparer.Ordinal);

    public List<ResolveCall> ResolveCalls { get; } = new();

    public bool ResolveResult { get; set; } = true;

    public event Action? PendingChanged;

    public void Seed(params PermissionRequest[] requests)
    {
        foreach (var request in requests)
        {
            if (!string.IsNullOrEmpty(request.RequestId))
                _pending[request.RequestId!] = request;
        }
        PendingChanged?.Invoke();
    }

    public bool Remove(string requestId)
    {
        var removed = _pending.Remove(requestId);
        if (removed)
            PendingChanged?.Invoke();
        return removed;
    }

    public Task<PermissionTicket> BeginAsync(PermissionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<PermissionResolution> WaitAsync(PermissionTicket ticket, CancellationToken ct = default)
        => throw new NotSupportedException();

    public bool TryResolve(
        string requestId,
        PermissionEffect decision,
        PermissionGrantScope scope,
        PermissionResolvedBy resolvedBy,
        string? reason = null,
        string? expectedSessionId = null)
    {
        ResolveCalls.Add(new ResolveCall(requestId, decision, scope, resolvedBy, reason, expectedSessionId));
        if (!ResolveResult)
            return false;
        if (_pending.Remove(requestId))
            PendingChanged?.Invoke();
        return true;
    }

    public IReadOnlyList<PermissionRequest> GetPending(string sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? Array.Empty<PermissionRequest>()
            : _pending.Values
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();

    public IReadOnlyList<PermissionRequest> GetAllPending() => _pending.Values.ToList();

    public int PendingCount => _pending.Count;

    public void Dispose()
    {
    }

    public sealed record ResolveCall(
        string RequestId,
        PermissionEffect Decision,
        PermissionGrantScope Scope,
        PermissionResolvedBy ResolvedBy,
        string? Reason,
        string? ExpectedSessionId);
}
```

创建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionInboxTests
{
    private const string Session = "s1";

    private static PermissionRequest Pending(
        string requestId,
        string? callId = "call-1",
        string sessionId = Session,
        IReadOnlyList<PermissionGrantScope>? scopes = null)
        => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            CallId = callId,
            PermissionKind = "tool.execute",
            Message = "需要确认",
            AllowedScopes = scopes ?? Array.Empty<PermissionGrantScope>()
        };

    [Fact]
    public void GetAll_ShouldProjectPendingRequests()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-1"), Pending("r2", "call-2"));

        var inbox = new PermissionInbox(manager);

        inbox.GetAll().Select(c => c.RequestId).Should().Equal("r1", "r2");
        inbox.TotalCount.Should().Be(2);
        inbox.GetAll().Should().OnlyContain(c => c.IsPending);
    }

    [Fact]
    public void GetAll_EmptyAllowedScopes_ShouldDefaultToOnceAndSession()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new PermissionInbox(manager);

        var card = inbox.GetAll().Single();

        card.Allows(PermissionGrantScope.Once).Should().BeTrue();
        card.Allows(PermissionGrantScope.Session).Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeFalse();
    }

    [Fact]
    public void GetBySession_And_CountBySession_ShouldFilterBySession()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", sessionId: "s1"), Pending("r2", sessionId: "s2"), Pending("r3", sessionId: "s1"));
        var inbox = new PermissionInbox(manager);

        inbox.GetBySession("s1").Select(c => c.RequestId).Should().BeEquivalentTo(new[] { "r1", "r3" });
        inbox.CountBySession("s1").Should().Be(2);
        inbox.CountBySession("nope").Should().Be(0);
        inbox.CountBySession(null).Should().Be(0);
    }

    [Fact]
    public void GetByCallId_ShouldFilterByCallId()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", "call-x"), Pending("r2", "call-y"));
        var inbox = new PermissionInbox(manager);

        inbox.GetByCallId("call-x").Should().ContainSingle(c => c.RequestId == "r1");
        inbox.GetByCallId(null).Should().BeEmpty();
    }

    [Fact]
    public void PendingChanged_ShouldRebuildProjection()
    {
        var manager = new FakePermissionRequestManager();
        var inbox = new PermissionInbox(manager);
        inbox.TotalCount.Should().Be(0);

        manager.Seed(Pending("r1"));

        inbox.TotalCount.Should().Be(1);
        inbox.GetAll().Single().RequestId.Should().Be("r1");
    }

    [Fact]
    public void PendingChanged_ShouldFireChanged()
    {
        var manager = new FakePermissionRequestManager();
        var inbox = new PermissionInbox(manager);
        var fired = 0;
        inbox.Changed += () => fired++;

        manager.Seed(Pending("r1"));
        manager.Remove("r1");

        fired.Should().Be(2);
        inbox.TotalCount.Should().Be(0);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionInboxTests"
```
Expected: 编译失败（`PermissionInbox` 未定义、`FakePermissionRequestManager` 缺 `GetAllPending`）。

- [ ] **Step 3: 实现 `PermissionInbox`**

创建 `samples/Seeing.Agent.WebUI/Services/PermissionInbox.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 全局权限收件箱（Singleton，只读投影，非权威）。
/// <para>
/// 输入：<see cref="IPermissionRequestManager.GetAllPending"/> + <see cref="IPermissionRequestManager.PendingChanged"/>；
/// 输出：<see cref="PermissionCardModel"/> 全量投影。每次通知全量重建（在途 ≤32），天然对账。
/// </para>
/// <para>并发契约：内部 <c>_gate</c> 串行写；<see cref="Changed"/> 锁外触发；不持有任何 Scoped 引用。</para>
/// </summary>
public sealed class PermissionInbox : IDisposable
{
    private static readonly IReadOnlyList<PermissionGrantScope> DefaultScopes =
        new[] { PermissionGrantScope.Once, PermissionGrantScope.Session };

    private readonly IPermissionRequestManager _manager;
    private readonly ILogger<PermissionInbox>? _logger;
    private readonly object _gate = new();

    private IReadOnlyList<PermissionCardModel> _cards = Array.Empty<PermissionCardModel>();
    private bool _disposed;

    public PermissionInbox(IPermissionRequestManager manager, ILogger<PermissionInbox>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
        _manager.PendingChanged += OnPendingChanged;
        Rebuild();
    }

    /// <summary>投影变更通知（锁外触发）。</summary>
    public event Action? Changed;

    /// <summary>全部在途卡片快照（按创建时间升序）。</summary>
    public IReadOnlyList<PermissionCardModel> GetAll()
    {
        lock (_gate)
            return _cards;
    }

    /// <summary>指定会话的在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> GetBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>指定工具调用关联的在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> GetByCallId(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                .ToList();
    }

    /// <summary>指定会话的在途卡片数。</summary>
    public int CountBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        lock (_gate)
            return _cards.Count(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal));
    }

    /// <summary>全部在途卡片数。</summary>
    public int TotalCount
    {
        get { lock (_gate) return _cards.Count; }
    }

    private void OnPendingChanged() => Rebuild();

    private void Rebuild()
    {
        if (_disposed)
            return;

        IReadOnlyList<PermissionRequest> pending;
        try
        {
            pending = _manager.GetAllPending();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取全局在途权限请求失败");
            return;
        }

        var cards = pending
            .Where(p => !string.IsNullOrEmpty(p.RequestId))
            .OrderBy(p => p.CreatedAt)
            .Select(ToCard)
            .ToList();

        var changed = false;
        lock (_gate)
        {
            if (!SameIds(_cards, cards))
            {
                _cards = cards;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    private static bool SameIds(
        IReadOnlyList<PermissionCardModel> current, IReadOnlyList<PermissionCardModel> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].RequestId, next[i].RequestId, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static PermissionCardModel ToCard(PermissionRequest request) => new()
    {
        RequestId = request.RequestId!,
        SessionId = request.SessionId,
        CallId = request.CallId,
        LoopId = request.LoopId,
        PermissionKind = request.PermissionKind,
        Resource = request.Resource,
        Arguments = request.Arguments,
        Message = request.Message,
        RiskLevel = request.RiskLevel,
        AllowedScopes = request.AllowedScopes is { Count: > 0 } ? request.AllowedScopes : DefaultScopes,
        CreatedAt = request.CreatedAt,
        IsPending = true
    };

    private void NotifyChanged()
    {
        if (_disposed)
            return;

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "权限收件箱变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.PendingChanged -= OnPendingChanged;
        Changed = null;
        lock (_gate)
            _cards = Array.Empty<PermissionCardModel>();
    }
}
```

- [ ] **Step 4: 注册 DI（Program.cs）**

在 `samples/Seeing.Agent.WebUI/Program.cs` 的 `builder.Services.AddSingleton<SessionEventStreamRouter>();` 之后追加：

```csharp
// 权限全局收件箱（Singleton 投影：GetAllPending → PermissionCardModel + Changed）
builder.Services.AddSingleton<PermissionInbox>();
```

- [ ] **Step 5: 运行测试，确认通过**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionInboxTests"
```
Expected: PASS（6 个 Fact）。

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/PermissionInbox.cs samples/Seeing.Agent.WebUI/Program.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/FakePermissionRequestManager.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxTests.cs
git commit -m "feat(webui): 新增全局权限收件箱 PermissionInbox 投影"
```

## Task 2: PermissionInboxView（Scoped 标签视图）

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/PermissionInboxView.cs`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`（加 `InternalsVisibleTo`）
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxViewTests.cs`

**依赖 Core 契约 Task**；依赖 Task 1 的 `PermissionInbox`。

- [ ] **Step 1: 写失败测试**

创建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxViewTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionInboxViewTests
{
    private const string Anchor = "anchor";
    private const string Child = "child";
    private const string Outsider = "outsider";

    private static PermissionRequest Pending(string requestId, string sessionId, string? callId = null)
        => new()
        {
            RequestId = requestId,
            SessionId = sessionId,
            CallId = callId,
            PermissionKind = "tool.execute"
        };

    private static SessionGroup AnchorAndChildGroup() => new()
    {
        Id = "g1",
        AnchorSessionId = Anchor,
        ActiveSessionId = Anchor,
        Version = 1,
        Members = new List<SessionGroupMember>
        {
            new() { SessionId = Anchor, Relation = SessionRelation.None, IsAnchor = true, Order = 0 },
            new() { SessionId = Child, Relation = SessionRelation.Child, ParentSessionId = Anchor, Order = 1 }
        }
    };

    private static SessionWindowRegistry CreateRegistry(SessionGroup group)
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        gm.Setup(m => m.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        return new SessionWindowRegistry(
            gm.Object, new ChannelSessionGroupEventBus(), Mock.Of<ISessionManager>(),
            NullLogger<SessionWindowRegistry>.Instance);
    }

    private static async Task<SessionWindowRegistry> ReadyRegistryAsync()
    {
        var registry = CreateRegistry(AnchorAndChildGroup());
        registry.Rebind(Anchor);
        await Task.Delay(200);
        return registry;
    }

    [Fact]
    public async Task CardsForScope_ShouldIncludeOnlyRegistryWindows()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(
            Pending("r-anchor", Anchor, "call-1"),
            Pending("r-child", Child, "call-2"),
            Pending("r-out", Outsider, "call-3"));
        var inbox = new PermissionInbox(manager);
        var registry = await ReadyRegistryAsync();

        using var view = new PermissionInboxView(
            inbox, () => registry, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);

        view.CardsForScope.Select(c => c.RequestId)
            .Should().BeEquivalentTo(new[] { "r-anchor", "r-child" });
        view.TotalCount.Should().Be(2);
        view.CountBySession(Child).Should().Be(1);
        view.GetBySession(Child).Should().ContainSingle(c => c.RequestId == "r-child");
    }

    [Fact]
    public async Task GetByCallId_ShouldReturnScopedCard()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r-child", Child, "call-2"), Pending("r-out", Outsider, "call-3"));
        var inbox = new PermissionInbox(manager);
        var registry = await ReadyRegistryAsync();

        using var view = new PermissionInboxView(
            inbox, () => registry, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);

        view.GetByCallId("call-2").Should().ContainSingle(c => c.RequestId == "r-child");
        view.GetByCallId("call-3").Should().BeEmpty();
    }

    [Fact]
    public void Prerender_NullRegistry_ShouldBeEmptyAndNeverNotify()
    {
        var manager = new FakePermissionRequestManager();
        var inbox = new PermissionInbox(manager);
        using var view = new PermissionInboxView(
            inbox, () => null, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);
        var fired = 0;
        view.Changed += () => fired++;

        manager.Seed(Pending("r1", Anchor, "call-1"));
        Thread.Sleep(80);

        view.TotalCount.Should().Be(0);
        fired.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_ShouldUnsubscribe()
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(Pending("r1", Anchor, "call-1"));
        var inbox = new PermissionInbox(manager);
        var registry = await ReadyRegistryAsync();
        var view = new PermissionInboxView(
            inbox, () => registry, TimeSpan.Zero, NullLogger<PermissionInboxView>.Instance);
        var fired = 0;
        view.Changed += () => fired++;

        view.Dispose();
        manager.Seed(Pending("r2", Anchor, "call-2"));
        await Task.Delay(100);

        fired.Should().Be(0);
        view.TotalCount.Should().Be(0);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionInboxViewTests"
```
Expected: 编译失败（`PermissionInboxView` 未定义）。

- [ ] **Step 3: 加 `InternalsVisibleTo`（供内部测试构造器）**

修改 `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`，在 `</Project>` 前追加：

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Agent.WebUI.Tests" />
  </ItemGroup>
```

- [ ] **Step 4: 实现 `PermissionInboxView`**

创建 `samples/Seeing.Agent.WebUI/Services/PermissionInboxView.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Hosting.Web.Circuits;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 当前标签（circuit）权限收件箱视图（Scoped，IDisposable）。
/// <para>
/// 作用域 = <see cref="SessionWindowRegistry.Windows"/> 的 SessionId 集合（唯一来源，已含子会话）；
/// 订阅 <see cref="PermissionInbox.Changed"/> 与 <see cref="SessionWindowRegistry.WindowsChanged"/>，
/// 经 <c>Interlocked</c> 单飞标志 + 短延时的微任务合并重算，锁外触发 <see cref="Changed"/>。
/// </para>
/// <para>prerender（空 circuitId）守卫：不订阅、不创建 registry、恒为空。</para>
/// </summary>
public sealed class PermissionInboxView : IDisposable
{
    private static readonly TimeSpan DefaultRecomputeDelay = TimeSpan.FromMilliseconds(50);

    private readonly PermissionInbox _inbox;
    private readonly Func<SessionWindowRegistry?> _registryAccessor;
    private readonly TimeSpan _recomputeDelay;
    private readonly ILogger<PermissionInboxView>? _logger;
    private readonly object _gate = new();

    private SessionWindowRegistry? _registry;
    private Action? _onInboxChanged;
    private Action? _onRegistryChanged;
    private IReadOnlyList<PermissionCardModel> _cards = Array.Empty<PermissionCardModel>();
    private int _dirty;
    private int _recomputeScheduled;
    private bool _enabled;
    private bool _disposed;

    public PermissionInboxView(
        PermissionInbox inbox,
        SessionEventStreamRouter router,
        CircuitContext circuitContext,
        ILogger<PermissionInboxView>? logger = null)
        : this(inbox, CreateAccessor(router, circuitContext), DefaultRecomputeDelay, logger)
    {
    }

    internal PermissionInboxView(
        PermissionInbox inbox,
        Func<SessionWindowRegistry?> registryAccessor,
        TimeSpan recomputeDelay,
        ILogger<PermissionInboxView>? logger = null)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _registryAccessor = registryAccessor ?? throw new ArgumentNullException(nameof(registryAccessor));
        _recomputeDelay = recomputeDelay;
        _logger = logger;

        _registry = _registryAccessor();
        if (_registry is null)
            return; // prerender / 无 circuit：不订阅、不上报

        _enabled = true;
        _onInboxChanged = OnInboxChanged;
        _onRegistryChanged = OnRegistryChanged;
        _inbox.Changed += _onInboxChanged;
        _registry.WindowsChanged += _onRegistryChanged;
        RecomputeOnce();
    }

    private static Func<SessionWindowRegistry?> CreateAccessor(
        SessionEventStreamRouter router, CircuitContext circuitContext)
    {
        var circuitId = circuitContext.Id;
        if (string.IsNullOrEmpty(circuitId))
            return static () => null;
        return () => router.GetOrCreateCircuitConsumer<SessionWindowRegistry>(circuitId);
    }

    /// <summary>当前作用域内在途卡片快照。</summary>
    public IReadOnlyList<PermissionCardModel> CardsForScope
    {
        get { lock (_gate) return _cards; }
    }

    /// <summary>当前作用域内在途卡片总数。</summary>
    public int TotalCount
    {
        get { lock (_gate) return _cards.Count; }
    }

    /// <summary>投影变更通知（锁外触发；订阅组件在 InvokeAsync 内消费）。</summary>
    public event Action? Changed;

    public IReadOnlyList<PermissionCardModel> GetByCallId(string? callId)
    {
        if (string.IsNullOrEmpty(callId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.CallId, callId, StringComparison.Ordinal))
                .ToList();
    }

    public IReadOnlyList<PermissionCardModel> GetBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return Array.Empty<PermissionCardModel>();

        lock (_gate)
            return _cards
                .Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal))
                .ToList();
    }

    public int CountBySession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return 0;

        lock (_gate)
            return _cards.Count(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal));
    }

    private void OnInboxChanged() => ScheduleRecompute();

    private void OnRegistryChanged() => ScheduleRecompute();

    private void ScheduleRecompute()
    {
        if (_disposed || !_enabled)
            return;

        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _recomputeScheduled, 1, 0) == 1)
            return;

        _ = Task.Run(RunRecomputeAsync);
    }

    private async Task RunRecomputeAsync()
    {
        try
        {
            if (_recomputeDelay > TimeSpan.Zero)
                await Task.Delay(_recomputeDelay).ConfigureAwait(false);

            while (Interlocked.Exchange(ref _dirty, 0) == 1)
                RecomputeOnce();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "权限收件箱视图重算失败");
        }
        finally
        {
            Interlocked.Exchange(ref _recomputeScheduled, 0);
            if (!_disposed && Volatile.Read(ref _dirty) == 1)
                ScheduleRecompute();
        }
    }

    private void RecomputeOnce()
    {
        if (_disposed)
            return;

        var windows = _registry?.Windows;
        IReadOnlyList<PermissionCardModel> cards;
        if (windows is null || windows.Count == 0)
        {
            cards = Array.Empty<PermissionCardModel>();
        }
        else
        {
            var scope = new HashSet<string>(windows.Select(w => w.SessionId), StringComparer.Ordinal);
            cards = _inbox.GetAll().Where(c => scope.Contains(c.SessionId)).ToList();
        }

        var changed = false;
        lock (_gate)
        {
            if (!SameIds(_cards, cards))
            {
                _cards = cards;
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    private static bool SameIds(
        IReadOnlyList<PermissionCardModel> current, IReadOnlyList<PermissionCardModel> next)
    {
        if (current.Count != next.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].RequestId, next[i].RequestId, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private void NotifyChanged()
    {
        if (_disposed)
            return;

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "权限收件箱视图变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_enabled)
        {
            if (_onInboxChanged is not null)
                _inbox.Changed -= _onInboxChanged;
            if (_registry is not null && _onRegistryChanged is not null)
                _registry.WindowsChanged -= _onRegistryChanged;
        }

        Changed = null;
        lock (_gate)
            _cards = Array.Empty<PermissionCardModel>();
    }
}
```

- [ ] **Step 5: 注册 DI（Program.cs）**

在 Program.cs 的 `builder.Services.AddSingleton<PermissionInbox>();` 之后追加：

```csharp
// 当前标签权限收件箱视图（Scoped，circuit 维度；作用域 = SessionWindowRegistry.Windows）
builder.Services.AddScoped<PermissionInboxView>();
```

- [ ] **Step 6: 运行测试，确认通过**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionInboxViewTests"
```
Expected: PASS（4 个 Fact）。

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/PermissionInboxView.cs samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj samples/Seeing.Agent.WebUI/Program.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInboxViewTests.cs
git commit -m "feat(webui): 新增 PermissionInboxView 当前标签权限视图"
```

## Task 3: WebUiPermissionPresenter（Scoped 呈现端）

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/WebUiPermissionPresenter.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/Services/WebUiPermissionPresenterTests.cs`

**依赖 Core 契约 Task**：`IPermissionPresenter` / `IPermissionPresentationStore`。

- [ ] **Step 1: 写失败测试**

创建 `tests/apps/Seeing.Agent.WebUI.Tests/Services/WebUiPermissionPresenterTests.cs`：

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class WebUiPermissionPresenterTests
{
    private static SessionGroup AnchorAndChildGroup() => new()
    {
        Id = "g1",
        AnchorSessionId = "anchor",
        ActiveSessionId = "anchor",
        Version = 1,
        Members = new List<SessionGroupMember>
        {
            new() { SessionId = "anchor", Relation = SessionRelation.None, IsAnchor = true, Order = 0 },
            new() { SessionId = "child", Relation = SessionRelation.Child, ParentSessionId = "anchor", Order = 1 }
        }
    };

    private static SessionWindowRegistry CreateRegistry(SessionGroup group)
    {
        var gm = new Mock<ISessionGroupManager>();
        gm.Setup(m => m.GetGroupForSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        gm.Setup(m => m.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        return new SessionWindowRegistry(
            gm.Object, new ChannelSessionGroupEventBus(), Mock.Of<ISessionManager>(),
            NullLogger<SessionWindowRegistry>.Instance);
    }

    private static async Task<SessionWindowRegistry> ReadyRegistryAsync()
    {
        var registry = CreateRegistry(AnchorAndChildGroup());
        registry.Rebind("anchor");
        await Task.Delay(200);
        return registry;
    }

    [Fact]
    public async Task Surface_ShouldIncludeWindowsAndAnchor()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionPresentationStore();

        using var presenter = new WebUiPermissionPresenter(
            store, () => registry,
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200),
            NullLogger<WebUiPermissionPresenter>.Instance);

        presenter.SurfaceSessionIds.Should().BeEquivalentTo(new[] { "anchor", "child" });
        store.RegisterCount.Should().Be(1);
        store.CanSurface("child").Should().BeTrue();
    }

    [Fact]
    public async Task TransientEmptyWithinConfirmWindow_ShouldNotReportEmpty()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionPresentationStore();
        using var presenter = new WebUiPermissionPresenter(
            store, () => registry,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(400),
            NullLogger<WebUiPermissionPresenter>.Instance);
        var reports = new List<IReadOnlyCollection<string>>();
        presenter.SurfacedChanged += () => reports.Add(presenter.SurfaceSessionIds.ToArray());

        registry.Rebind(string.Empty);       // 瞬态空集候选
        await Task.Delay(40);                // 已过去抖，但未到 400ms 确认期
        registry.Rebind("anchor");           // 期内恢复非空

        await Task.Delay(200);

        presenter.SurfaceSessionIds.Should().Contain("anchor");
        reports.Should().NotContain(r => r.Count == 0);
    }

    [Fact]
    public async Task Dispose_ShouldUnregisterAndStopReporting()
    {
        var registry = await ReadyRegistryAsync();
        var store = new FakePermissionPresentationStore();
        var presenter = new WebUiPermissionPresenter(
            store, () => registry,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100),
            NullLogger<WebUiPermissionPresenter>.Instance);
        var reports = 0;
        presenter.SurfacedChanged += () => Interlocked.Increment(ref reports);

        presenter.Dispose();

        store.UnregisterCount.Should().Be(1);
        registry.Rebind("other");
        await Task.Delay(120);

        reports.Should().Be(0);
        presenter.SurfaceSessionIds.Should().BeEmpty();
    }

    [Fact]
    public void Prerender_NullRegistry_ShouldNotRegister()
    {
        var store = new FakePermissionPresentationStore();
        using var presenter = new WebUiPermissionPresenter(
            store, () => null,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10),
            NullLogger<WebUiPermissionPresenter>.Instance);

        store.RegisterCount.Should().Be(0);
        presenter.SurfaceSessionIds.Should().BeEmpty();
    }
}

/// <summary>测试替身：呈现端登记表。</summary>
internal sealed class FakePermissionPresentationStore : IPermissionPresentationStore
{
    private readonly HashSet<IPermissionPresenter> _presenters = new();

    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

    public event Action? Changed;

    public void Register(IPermissionPresenter presenter)
    {
        if (_presenters.Add(presenter))
            RegisterCount++;
        Changed?.Invoke();
    }

    public void Unregister(IPermissionPresenter presenter)
    {
        if (_presenters.Remove(presenter))
            UnregisterCount++;
        Changed?.Invoke();
    }

    public bool CanSurface(string sessionId)
        => _presenters.Any(p => p.SurfaceSessionIds.Contains(sessionId));
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~WebUiPermissionPresenterTests"
```
Expected: 编译失败（`WebUiPermissionPresenter` 未定义）。

- [ ] **Step 3: 实现 `WebUiPermissionPresenter`**

创建 `samples/Seeing.Agent.WebUI/Services/WebUiPermissionPresenter.cs`：

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Hosting.Web.Circuits;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// WebUI circuit 呈现端（Scoped）：向 <see cref="IPermissionPresentationStore"/> 声明
/// 当前标签（circuit）可呈现的会话集合。
/// <para>
/// <see cref="SurfaceSessionIds"/> 返回<b>已确认的稳定快照</b>（缓存字段），非实时读取；
/// 计算集合 = <see cref="SessionWindowRegistry.Windows"/> 的 SessionId ∪ AnchorSessionId；
/// 变化经短去抖（默认 250ms）合并后上报；空集候选经确认期（默认 1s）后才落定。
/// </para>
/// <para>prerender（空 circuitId）守卫：不注册、不创建 registry、不上报；Register/Unregister 幂等。</para>
/// </summary>
public sealed class WebUiPermissionPresenter : IPermissionPresenter, IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultEmptyConfirm = TimeSpan.FromSeconds(1);

    private readonly IPermissionPresentationStore _store;
    private readonly Func<SessionWindowRegistry?> _registryAccessor;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _emptyConfirm;
    private readonly ILogger<WebUiPermissionPresenter>? _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);

    private SessionWindowRegistry? _registry;
    private Timer? _debounceTimer;
    private Timer? _emptyTimer;
    private bool _registered;
    private bool _disposed;

    public WebUiPermissionPresenter(
        IPermissionPresentationStore store,
        SessionEventStreamRouter router,
        CircuitContext circuitContext,
        ILogger<WebUiPermissionPresenter>? logger = null)
        : this(store, CreateAccessor(router, circuitContext), DefaultDebounce, DefaultEmptyConfirm, logger)
    {
    }

    internal WebUiPermissionPresenter(
        IPermissionPresentationStore store,
        Func<SessionWindowRegistry?> registryAccessor,
        TimeSpan debounce,
        TimeSpan emptyConfirm,
        ILogger<WebUiPermissionPresenter>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registryAccessor = registryAccessor ?? throw new ArgumentNullException(nameof(registryAccessor));
        _debounce = debounce;
        _emptyConfirm = emptyConfirm;
        _logger = logger;

        _registry = _registryAccessor();
        if (_registry is null)
            return; // prerender / 无 circuit：不注册、不创建 registry

        _debounceTimer = new Timer(
            _ => OnDebounceElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _emptyTimer = new Timer(
            _ => OnEmptyConfirmElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _registry.WindowsChanged += OnRegistryChanged;

        var initial = ComputeSurface();
        lock (_gate)
        {
            if (initial.Count > 0)
                _confirmed.UnionWith(initial);
            else
                StartEmptyConfirmLocked();
        }

        _store.Register(this);
        _registered = true;
    }

    private static Func<SessionWindowRegistry?> CreateAccessor(
        SessionEventStreamRouter router, CircuitContext circuitContext)
    {
        var circuitId = circuitContext.Id;
        if (string.IsNullOrEmpty(circuitId))
            return static () => null;
        return () => router.GetOrCreateCircuitConsumer<SessionWindowRegistry>(circuitId);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SurfaceSessionIds
    {
        get
        {
            lock (_gate)
                return _confirmed.ToArray();
        }
    }

    /// <inheritdoc />
    public event Action? SurfacedChanged;

    private void OnRegistryChanged()
    {
        if (_disposed)
            return;

        _debounceTimer?.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceElapsed()
    {
        if (_disposed)
            return;

        var candidate = ComputeSurface();
        bool report;
        lock (_gate)
        {
            if (candidate.Count > 0)
            {
                CancelEmptyConfirmLocked();
                report = !_confirmed.SetEquals(candidate);
                _confirmed.Clear();
                _confirmed.UnionWith(candidate);
            }
            else
            {
                report = false;
                StartEmptyConfirmLocked();
            }
        }

        if (report)
            NotifySurfacedChanged();
    }

    private void OnEmptyConfirmElapsed()
    {
        if (_disposed)
            return;

        var candidate = ComputeSurface();
        bool report;
        lock (_gate)
        {
            if (candidate.Count > 0)
            {
                report = !_confirmed.SetEquals(candidate);
                _confirmed.Clear();
                _confirmed.UnionWith(candidate);
            }
            else
            {
                report = _confirmed.Count > 0;
                _confirmed.Clear();
            }
        }

        if (report)
            NotifySurfacedChanged();
    }

    private HashSet<string> ComputeSurface()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var registry = _registry;
        if (registry is null)
            return result;

        foreach (var window in registry.Windows)
            result.Add(window.SessionId);

        var anchor = registry.AnchorSessionId;
        if (!string.IsNullOrEmpty(anchor))
            result.Add(anchor);

        return result;
    }

    private void StartEmptyConfirmLocked()
        => _emptyTimer?.Change(_emptyConfirm, Timeout.InfiniteTimeSpan);

    private void CancelEmptyConfirmLocked()
        => _emptyTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void NotifySurfacedChanged()
    {
        try
        {
            SurfacedChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "呈现端 surface 变更通知失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_registry is not null)
            _registry.WindowsChanged -= OnRegistryChanged;

        _debounceTimer?.Dispose();
        _emptyTimer?.Dispose();
        _debounceTimer = null;
        _emptyTimer = null;

        if (_registered)
        {
            _registered = false;
            try
            {
                _store.Unregister(this);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "注销呈现端失败");
            }
        }

        SurfacedChanged = null;
        lock (_gate)
            _confirmed.Clear();
    }
}
```

- [ ] **Step 4: 注册 DI（Program.cs）**

在 Program.cs 的 `builder.Services.AddScoped<PermissionInboxView>();` 之后追加：

```csharp
// WebUI circuit 呈现端（Scoped）：向 IPermissionPresentationStore 声明可呈现会话集合
builder.Services.AddScoped<WebUiPermissionPresenter>();
```

- [ ] **Step 5: 运行测试，确认通过**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~WebUiPermissionPresenterTests"
```
Expected: PASS（4 个 Fact）。

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/WebUiPermissionPresenter.cs samples/Seeing.Agent.WebUI/Program.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/WebUiPermissionPresenterTests.cs
git commit -m "feat(webui): 新增 WebUiPermissionPresenter 呈现端"
```

## Task 4: 内联卡片改接 View + TaskCardAggregator P5

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/Messaging/ToolPermissionSlot.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/Messaging/TaskMessageComponent.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs`

**依赖 Task 2 的 `PermissionInboxView`。**

- [ ] **Step 1: 写 P5 失败测试**

在 `tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs` 类内（`OnEvent_ChildTerminalEvent_ShouldStopAggregating` 之后）插入：

```csharp
    [Fact]
    public async Task OnEvent_ChildPermissionEvent_ShouldNotAffectTaskStepsOrNotify()
    {
        var parentId = "parent1";
        var childId = "child1";
        var parent = CreateParentWithTaskCall(parentId, "call-1");
        var child = CreateChild(childId, "call-1");
        var parentChannel = Channel.CreateUnbounded<IMessageEvent>();
        var childChannel = Channel.CreateUnbounded<IMessageEvent>();
        var sm = CreateSessionManagerMock(parent, child);
        var gm = CreateGroupManager(new() { [parentId] = new[] { child } });
        var orchestrator = CreateOrchestratorMock(new Dictionary<string, Channel<IMessageEvent>>
        {
            [parentId] = parentChannel,
            [childId] = childChannel
        });

        using var router = CreateRouter(orchestrator);
        var aggregator = CreateAggregator(router, sm, gm);
        var assistantChanged = 0;
        aggregator.AssistantChanged += _ => Interlocked.Increment(ref assistantChanged);
        aggregator.Rebind(parentId);

        await parentChannel.Writer.WriteAsync(new ToolCallEvent
        {
            SessionId = parentId, Type = MessageEventType.ToolCallRunning,
            ToolCallId = "call-1", ToolName = "task", Status = ToolCallStatus.Running
        });
        await Task.Delay(200);
        var before = Interlocked.CompareExchange(ref assistantChanged, 0, 0);

        await childChannel.Writer.WriteAsync(new PermissionRequestEvent
        {
            SessionId = childId, RequestId = "req-1", CallId = "ct-x",
            PermissionKind = "tool.execute"
        });
        await Task.Delay(200);

        var toolCall = parent.Messages[0].ToolCalls[0];
        toolCall.TaskSteps.Should().BeNullOrEmpty();
        Interlocked.CompareExchange(ref assistantChanged, 0, 0).Should().Be(before);

        parentChannel.Writer.TryComplete();
        childChannel.Writer.TryComplete();
    }
```

- [ ] **Step 2: 运行测试，确认失败**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~OnEvent_ChildPermissionEvent_ShouldNotAffectTaskStepsOrNotify"
```
Expected: FAIL（默认分支 `_ => true` 触发 `AssistantChanged`）。

- [ ] **Step 3: 修 `TaskCardAggregator`（精确插入点）**

在 `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs` 的 `OnEvent` 中，父分支 `return;` 之后、子流分支判断之前插入：

```csharp
        // P5：权限请求/结果事件仅用于展示与时间线，不进入子流聚合（避免默认分支触发虚假落盘/重渲染）
        if (evt is PermissionRequestEvent or PermissionResolvedEvent)
            return;

```

插入后上下文应为：

```csharp
        if (string.Equals(sessionId, ParentSessionId, StringComparison.Ordinal))
        {
            // ...父流逻辑...
            return;
        }

        // P5：权限请求/结果事件仅用于展示与时间线，不进入子流聚合（避免默认分支触发虚假落盘/重渲染）
        if (evt is PermissionRequestEvent or PermissionResolvedEvent)
            return;

        // 子流：聚合（C1：MergeStep/FailStep 读-改-写整体串行化）
        if (_tasks.TryGetValue(sessionId, out var state))
```

> `PermissionRequestEvent`/`PermissionResolvedEvent` 已在文件顶部 `using Seeing.Agent.Abstractions.Events;` 内。

- [ ] **Step 4: 运行测试，确认通过**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskCardAggregatorTests"
```
Expected: PASS（含新的 P5 Fact，既有测试不回归）。

- [ ] **Step 5: 重写 `LoopGroupItem.razor`（删除权限绑定逻辑）**

用以下完整内容替换 `samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor`：

```razor
@using AntDesign
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.Models.Messaging
@using Seeing.Agent.WebUI.Rendering.Abstractions
@inject IMessageRenderPipeline Pipeline

@* Loop 分组组件 - 使用渲染管线 *@
@_body

@code {
    /// <summary>
    /// Loop 分组数据
    /// </summary>
    [Parameter]
    public LoopGroupViewModel Loop { get; set; } = new();

    /// <summary>
    /// 与 TimelineItem.Revision 同步；变更时重建内容块。
    /// </summary>
    [Parameter]
    public int ContentRevision { get; set; }

    /// <summary>
    /// 工具点击事件回调
    /// </summary>
    [Parameter]
    public EventCallback<ToolCallViewModel> OnToolClick { get; set; }

    private int _blocksRevision = int.MinValue;
    private RenderFragment? _body;

    protected override void OnParametersSet()
    {
        if (_body == null || _blocksRevision != ContentRevision)
        {
            _blocksRevision = ContentRevision;
            _body = Pipeline.RenderLoop(Loop, GetRenderOptions(), null, OnToolClick);
        }
    }

    /// <summary>
    /// 获取渲染选项
    /// </summary>
    private RenderOptions GetRenderOptions()
    {
        return new RenderOptions
        {
            ShowReasoning = true,
            ExpandReasoningByDefault = false
        };
    }
}
```

- [ ] **Step 6: 重写 `ToolPermissionSlot.razor`（改用 View.GetByCallId）**

用以下完整内容替换 `samples/Seeing.Agent.WebUI/Components/Messaging/ToolPermissionSlot.razor`：

```razor
@using Seeing.Agent.Abstractions.Permissions
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.Services
@inject PermissionInboxView PermissionView
@inject PermissionInteractionService PermissionActions
@implements IDisposable

@* 工具级权限内联槽位：按 CallId 关联本工具调用的在途审批卡片，紧随工具卡下方渲染（决议后即消失） *@
@if (_cards.Count > 0)
{
    <div class="tool-permission-slot" style="display: flex; flex-direction: column; gap: 6px;">
        @foreach (var card in _cards)
        {
            <PermissionCard @key="card.RequestId"
                            Card="card"
                            OnAllowOnce="OnAllowOnceAsync"
                            OnAllowSession="OnAllowSessionAsync"
                            OnAllowSessionDirectory="OnAllowSessionDirectoryAsync"
                            OnDenyOnce="OnDenyOnceAsync"
                            OnDenySession="OnDenySessionAsync" />
        }
    </div>
}

@code {
    /// <summary>关联的工具调用 ID（内联卡片关联键）。</summary>
    [Parameter]
    public string? CallId { get; set; }

    private IReadOnlyList<PermissionCardModel> _cards = Array.Empty<PermissionCardModel>();

    protected override void OnInitialized()
    {
        PermissionView.Changed += OnCardsChanged;
        Refresh();
    }

    protected override void OnParametersSet() => Refresh();

    private void OnCardsChanged()
    {
        Refresh();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>取本工具调用关联的在途卡片（决议后从槽位移除）。</summary>
    private void Refresh()
    {
        if (string.IsNullOrEmpty(CallId))
        {
            _cards = Array.Empty<PermissionCardModel>();
            return;
        }

        _cards = PermissionView.GetByCallId(CallId)
            .Where(c => c.IsPending)
            .ToList();
    }

    private Task OnAllowOnceAsync(PermissionCardModel card)
    {
        PermissionActions.AllowOnce(card);
        return Task.CompletedTask;
    }

    private Task OnAllowSessionAsync(PermissionCardModel card)
    {
        PermissionActions.AllowSession(card);
        return Task.CompletedTask;
    }

    private Task OnAllowSessionDirectoryAsync(PermissionCardModel card)
    {
        PermissionActions.AllowSessionDirectory(card);
        return Task.CompletedTask;
    }

    private Task OnDenyOnceAsync(PermissionCardModel card)
    {
        PermissionActions.DenyOnce(card);
        return Task.CompletedTask;
    }

    private Task OnDenySessionAsync(PermissionCardModel card)
    {
        PermissionActions.DenySession(card);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        PermissionView.Changed -= OnCardsChanged;
    }
}
```

- [ ] **Step 7: `TaskMessageComponent.razor` 加子会话在途卡**

修改 `samples/Seeing.Agent.WebUI/Components/Messaging/TaskMessageComponent.razor`：

(a) 顶部 using / inject（在现有 `@using Seeing.Session.Core` 之后）追加：

```razor
@using Seeing.Agent.WebUI.Services
@inject PermissionInboxView PermissionView
@inject PermissionInteractionService PermissionActions
@implements IDisposable
```

(b) 在展开区（`@if (_isExpanded) { <div class="tool-call-details expanded">`）内、「子会话 ID」区块之后、「进度」区块之前插入：

```razor
            @if (_childCards.Count > 0)
            {
                <div class="tool-call-section">
                    <div class="tool-call-section-header">
                        <span class="tool-call-section-title" style="color: #faad14;">子任务待审批</span>
                    </div>
                    @foreach (var card in _childCards)
                    {
                        <PermissionCard @key="card.RequestId"
                                        Card="card"
                                        OnAllowOnce="OnAllowOnceAsync"
                                        OnAllowSession="OnAllowSessionAsync"
                                        OnAllowSessionDirectory="OnAllowSessionDirectoryAsync"
                                        OnDenyOnce="OnDenyOnceAsync"
                                        OnDenySession="OnDenySessionAsync" />
                    }
                </div>
            }
```

(c) 在 `@code { ... }` 内追加字段/生命周期/动作方法（可放在 `OnParametersSet` 之后）：

```csharp
    private IReadOnlyList<PermissionCardModel> _childCards = Array.Empty<PermissionCardModel>();

    protected override void OnInitialized()
    {
        PermissionView.Changed += OnPermissionChanged;
        RefreshChildCards();
    }

    private void OnPermissionChanged()
    {
        RefreshChildCards();
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>取子会话（ToolCall.TaskId）在途权限卡片。</summary>
    private void RefreshChildCards()
    {
        _childCards = string.IsNullOrEmpty(ToolCall.TaskId)
            ? Array.Empty<PermissionCardModel>()
            : PermissionView.GetBySession(ToolCall.TaskId!);
    }

    private Task OnAllowOnceAsync(PermissionCardModel card)
    {
        PermissionActions.AllowOnce(card);
        return Task.CompletedTask;
    }

    private Task OnAllowSessionAsync(PermissionCardModel card)
    {
        PermissionActions.AllowSession(card);
        return Task.CompletedTask;
    }

    private Task OnAllowSessionDirectoryAsync(PermissionCardModel card)
    {
        PermissionActions.AllowSessionDirectory(card);
        return Task.CompletedTask;
    }

    private Task OnDenyOnceAsync(PermissionCardModel card)
    {
        PermissionActions.DenyOnce(card);
        return Task.CompletedTask;
    }

    private Task OnDenySessionAsync(PermissionCardModel card)
    {
        PermissionActions.DenySession(card);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        PermissionView.Changed -= OnPermissionChanged;
    }
```

(d) 在既有 `OnParametersSet` 末尾追加 `RefreshChildCards();`（保证 TaskId 变化后刷新）。

- [ ] **Step 8: 构建 + 运行相关测试**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskCardAggregatorTests"
```
Expected: 构建成功；TaskCardAggregatorTests PASS。

- [ ] **Step 9: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor samples/Seeing.Agent.WebUI/Components/Messaging/ToolPermissionSlot.razor samples/Seeing.Agent.WebUI/Components/Messaging/TaskMessageComponent.razor samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs
git commit -m "fix(webui): 内联权限卡改接收件箱视图并修复 TaskCardAggregator 权限事件处理"
```

## Task 5: 审批中心抽屉 + AppSidebar + 移动端入口

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/PermissionCenterDrawer.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor`
- Modify: `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`

**依赖 Task 2（View）与 Task 3（Presenter）。**

- [ ] **Step 1: 创建 `PermissionCenterDrawer.razor`**

创建 `samples/Seeing.Agent.WebUI/Components/PermissionCenterDrawer.razor`：

```razor
@using AntDesign
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.Services
@using Seeing.Session.Core
@inject PermissionInboxView PermissionView
@inject PermissionInteractionService PermissionActions
@inject ISessionManager SessionManager
@inject NavigationManager NavigationManager
@implements IDisposable

@* 全局审批中心抽屉：当前标签内所有会话的在途审批，按会话分组 + 就地批准/拒绝 *@
<Drawer Closable="true"
        @bind-Visible="Open"
        Placement="@DrawerPlacement.Right"
        Width="420"
        Title="@("审批中心")">
    @if (Groups.Count == 0)
    {
        <Empty Description="@("当前没有待审批请求")" />
    }
    else
    {
        @foreach (var group in Groups)
        {
            <div class="permission-center-group" @key="group.Key">
                <div class="permission-center-group__header">
                    <span class="permission-center-group__title">@GroupTitle(group.Key)</span>
                    <Button Type="@ButtonType.Link"
                            Size="@ButtonSize.Small"
                            OnClick="@(() => NavigateToSession(group.Key))">跳转会话</Button>
                </div>
                @foreach (var card in group)
                {
                    <PermissionCard @key="card.RequestId"
                                    Card="card"
                                    OnAllowOnce="OnAllowOnceAsync"
                                    OnAllowSession="OnAllowSessionAsync"
                                    OnAllowSessionDirectory="OnAllowSessionDirectoryAsync"
                                    OnDenyOnce="OnDenyOnceAsync"
                                    OnDenySession="OnDenySessionAsync" />
                }
            </div>
        }
    }
</Drawer>

@code {
    [Parameter] public bool Open { get; set; }

    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    private IReadOnlyList<IGrouping<string, PermissionCardModel>> Groups =>
        PermissionView.CardsForScope
            .GroupBy(c => c.SessionId)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

    protected override void OnInitialized()
    {
        PermissionView.Changed += OnPermissionChanged;
    }

    public void Dispose()
    {
        PermissionView.Changed -= OnPermissionChanged;
    }

    private void OnPermissionChanged() => _ = InvokeAsync(StateHasChanged);

    private string GroupTitle(string sessionId)
    {
        var title = SessionManager.Get(sessionId)?.Title;
        return string.IsNullOrEmpty(title) ? sessionId : title!;
    }

    private void NavigateToSession(string sessionId)
    {
        NavigationManager.NavigateTo($"/session/{sessionId}");
        _ = OpenChanged.InvokeAsync(false);
    }

    private Task OnAllowOnceAsync(PermissionCardModel card)
    {
        PermissionActions.AllowOnce(card);
        return Task.CompletedTask;
    }

    private Task OnAllowSessionAsync(PermissionCardModel card)
    {
        PermissionActions.AllowSession(card);
        return Task.CompletedTask;
    }

    private Task OnAllowSessionDirectoryAsync(PermissionCardModel card)
    {
        PermissionActions.AllowSessionDirectory(card);
        return Task.CompletedTask;
    }

    private Task OnDenyOnceAsync(PermissionCardModel card)
    {
        PermissionActions.DenyOnce(card);
        return Task.CompletedTask;
    }

    private Task OnDenySessionAsync(PermissionCardModel card)
    {
        PermissionActions.DenySession(card);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: `AppSidebar.razor` 加菜单项 + 承载抽屉 + 悬浮入口**

修改 `samples/Seeing.Agent.WebUI/Components/AppSidebar.razor`：

(a) 顶部 inject 追加：

```razor
@inject PermissionInboxView PermissionView
@inject WebUiPermissionPresenter PermissionPresenter
```

> `PermissionPresenter` 仅用于确保 circuit scope 内呈现端被构造（全局组件注入点）。若报「未使用」警告，在 `@code` 内保留一行引用：`private object PresenterKeepAlive => PermissionPresenter;`。

(b) 在 `<MenuItem Key="chat" ...>...</MenuItem>` 之后插入审批中心菜单项：

```razor
        <MenuItem Key="permission-center" OnClick="OpenPermissionCenter">
            <Icon Type="@IconType.Outline.SafetyCertificate" />
            <Badge Count="@PermissionView.TotalCount">
                <span>审批中心</span>
            </Badge>
        </MenuItem>
```

(c) 在 `</Sider>` 之后追加抽屉：

```razor
<PermissionCenterDrawer Open="_permissionCenterOpen"
                        OpenChanged="@(open => _permissionCenterOpen = open)" />
```

(d) `@code` 内加字段与方法：

```csharp
    private bool _permissionCenterOpen;

    /// <summary>打开审批中心抽屉（供移动端悬浮按钮经 @ref 调用）。</summary>
    public void OpenPermissionCenter() => _ = InvokeAsync(() =>
    {
        _permissionCenterOpen = true;
        StateHasChanged();
    });

    private void OnPermissionChanged() => _ = InvokeAsync(StateHasChanged);
```

(e) `OnInitialized` 追加 `PermissionView.Changed += OnPermissionChanged;`；`Dispose` 追加 `PermissionView.Changed -= OnPermissionChanged;`。

- [ ] **Step 3: `MainLayout.razor` 移动端悬浮按钮**

修改 `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`：

(a) inject 追加：

```razor
@inject PermissionInboxView PermissionView
```

(b) 在现有 `@if (AppState.IsMobile) { <Button Class="mobile-hamburger" ... /> }` 之后追加：

```razor
    @if (AppState.IsMobile)
    {
        <Badge Count="@PermissionView.TotalCount"
               Style="position: fixed; right: 16px; bottom: 88px; z-index: 150;">
            <Button Icon="@IconType.Outline.SafetyCertificate"
                    Shape="@ButtonShape.Circle"
                    Size="@ButtonSize.Large"
                    OnClick="OpenPermissionCenter"
                    OnClickStopPropagation="true" />
        </Badge>
    }
```

(c) `@code` 内加：

```csharp
    private void OpenPermissionCenter()
        => _sidebarRef?.OpenPermissionCenter();

    private void OnPermissionChanged() => InvokeAsync(StateHasChanged);
```

(d) `OnInitializedAsync` 追加 `PermissionView.Changed += OnPermissionChanged;`；`Dispose` 追加 `PermissionView.Changed -= OnPermissionChanged;`。

- [ ] **Step 4: 构建验证**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
```
Expected: 构建成功（无编译错误）。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/PermissionCenterDrawer.razor samples/Seeing.Agent.WebUI/Components/AppSidebar.razor samples/Seeing.Agent.WebUI/Shared/MainLayout.razor
git commit -m "feat(webui): 新增审批中心抽屉与移动端悬浮入口"
```

## Task 6: 删除旧聚合器/追踪器 + Router/Program/Session 接线清理

**Files:**
- Delete: `samples/Seeing.Agent.WebUI/Services/PermissionCardAggregator.cs`
- Delete: `samples/Seeing.Agent.WebUI/Services/ActiveSessionTracker.cs`
- Delete: `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs`
- Delete: `tests/apps/Seeing.Agent.WebUI.Tests/Services/ActiveSessionTrackerTests.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- Modify: `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInteractionServiceTests.cs`

**依赖 Task 1 的 `PermissionInbox`（供交互测试构建卡片）。**

- [ ] **Step 1: 删除文件（Fake 已在 Task 1 迁出）**

```bash
git rm samples/Seeing.Agent.WebUI/Services/PermissionCardAggregator.cs
git rm samples/Seeing.Agent.WebUI/Services/ActiveSessionTracker.cs
git rm tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs
git rm tests/apps/Seeing.Agent.WebUI.Tests/Services/ActiveSessionTrackerTests.cs
```

- [ ] **Step 2: `SessionEventStreamRouter` 移除 `ActiveSessionTracker` 依赖**

在 `samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`：

(a) 删除字段（约 `:52`）：

```csharp
    private readonly ActiveSessionTracker? _activeSessions;
```

(b) 构造参数（约 `:77`）由：

```csharp
        ILogger<SessionEventStreamRouter> logger,
        ActiveSessionTracker? activeSessions = null)
```

改为：

```csharp
        ILogger<SessionEventStreamRouter> logger)
```

(c) 删除构造体内赋值 `_activeSessions = activeSessions;`。

(d) 删除 `DetachAllForCircuit` 内（约 `:262`）：

```csharp
        _activeSessions?.DetachCircuit(circuitId);
```

- [ ] **Step 3: `Program.cs` 移除旧注册**

在 `samples/Seeing.Agent.WebUI/Program.cs` 删除：

```csharp
builder.Services.AddSingleton<ActiveSessionTracker>();
```

以及：

```csharp
// 权限内联卡片投影聚合器 + 交互服务（Scoped，circuit 维度，与 TaskCardAggregator 同范式）
builder.Services.AddScoped<PermissionCardAggregator>();
```

保留 `builder.Services.AddScoped<PermissionInteractionService>();`。

- [ ] **Step 4: `Session.razor` 移除 `ActiveSessionTracker` 引用**

在 `samples/Seeing.Agent.WebUI/Pages/Session.razor`：

(a) 删除 inject（约 `:36`）：

```razor
@inject Services.ActiveSessionTracker ActiveSessionTracker
```

(b) 删除 `OnInitializedAsync` 与 `OnParametersSetAsync` 中的两处：

```csharp
        ActiveSessionTracker.Attach(mainId, _circuitId);
```

```csharp
        ActiveSessionTracker.Attach(SessionId, _circuitId);
```

(c) 删除字段 `private string _circuitId = string.Empty;`（约 `:207`）与 `OnInitializedAsync` 内赋值 `_circuitId = CircuitContext.Id ?? string.Empty;`（约 `:233`）；`InitRegistry` 已直接读 `CircuitContext.Id`。

(d) 删除 `Dispose` 内（约 `:637`）：

```csharp
        ActiveSessionTracker.DetachCircuit(_circuitId);
```

- [ ] **Step 5: 更新 `PermissionInteractionServiceTests`**

用以下完整内容替换 `tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInteractionServiceTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PermissionInteractionServiceTests
{
    private const string Session = "s1";

    private static PermissionRequest Request(
        string kind = "tool.execute",
        IReadOnlyList<PermissionGrantScope>? scopes = null)
        => new()
        {
            RequestId = "r1",
            SessionId = Session,
            CallId = "call-1",
            LoopId = "loop-1",
            PermissionKind = kind,
            Resource = "read",
            AllowedScopes = scopes ?? new[] { PermissionGrantScope.Once, PermissionGrantScope.Session }
        };

    private static (PermissionInbox Inbox, FakePermissionRequestManager Manager, PermissionInteractionService Service)
        Setup(PermissionRequest request)
    {
        var manager = new FakePermissionRequestManager();
        manager.Seed(request);
        var inbox = new PermissionInbox(manager);
        var service = new PermissionInteractionService(manager);
        return (inbox, manager, service);
    }

    [Fact]
    public void AllowOnce_ShouldResolveWithOnceScopeAndUser()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowOnce(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        var call = manager.ResolveCalls[0];
        call.RequestId.Should().Be("r1");
        call.Decision.Should().Be(PermissionEffect.Allow);
        call.Scope.Should().Be(PermissionGrantScope.Once);
        call.ResolvedBy.Should().Be(PermissionResolvedBy.User);
        call.ExpectedSessionId.Should().Be(Session);
    }

    [Fact]
    public void AllowSession_ShouldResolveWithSessionScope()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowSession(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Allow);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public void AllowSessionDirectory_FileSystem_ShouldResolveWithDirectoryScope()
    {
        var request = Request("filesystem.write", new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session,
            PermissionGrantScope.SessionDirectory
        });
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowSessionDirectory(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Allow);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.SessionDirectory);
    }

    [Fact]
    public void AllowSessionDirectory_WhenScopeNotAllowed_ShouldNoOp()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.AllowSessionDirectory(inbox.GetBySession(Session).Single());

        result.Should().BeFalse();
        manager.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public void DenyOnce_ShouldResolveWithDenyAndOnce()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.DenyOnce(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Deny);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Once);
        manager.ResolveCalls[0].ResolvedBy.Should().Be(PermissionResolvedBy.User);
    }

    [Fact]
    public void DenySession_ShouldResolveWithDenyAndSession()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);

        var result = service.DenySession(inbox.GetBySession(Session).Single());

        result.Should().BeTrue();
        manager.ResolveCalls.Should().ContainSingle();
        manager.ResolveCalls[0].Decision.Should().Be(PermissionEffect.Deny);
        manager.ResolveCalls[0].Scope.Should().Be(PermissionGrantScope.Session);
    }

    [Fact]
    public void Action_WhenCardAlreadyResolved_ShouldNoOp()
    {
        var manager = new FakePermissionRequestManager();
        var service = new PermissionInteractionService(manager);
        var resolvedCard = new PermissionCardModel
        {
            RequestId = "r1",
            SessionId = Session,
            PermissionKind = "tool.execute",
            AllowedScopes = new[] { PermissionGrantScope.Once, PermissionGrantScope.Session },
            IsPending = false
        };

        var result = service.AllowOnce(resolvedCard);

        result.Should().BeFalse();
        manager.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public void Action_WhenManagerRejects_ShouldReturnFalse()
    {
        var request = Request();
        var (inbox, manager, service) = Setup(request);
        manager.ResolveResult = false;

        var result = service.AllowOnce(inbox.GetBySession(Session).Single());

        result.Should().BeFalse();
        manager.ResolveCalls.Should().ContainSingle();
    }

    [Fact]
    public void PendingCard_ShouldExposeAllActionScopes()
    {
        var request = Request("filesystem.write", new[]
        {
            PermissionGrantScope.Once,
            PermissionGrantScope.Session,
            PermissionGrantScope.SessionDirectory
        });
        var (inbox, _, _) = Setup(request);

        var card = inbox.GetBySession(Session).Single();

        card.CanAllowOnce.Should().BeTrue();
        card.CanAllowSession.Should().BeTrue();
        card.CanAllowSessionDirectory.Should().BeTrue();
        card.CanDenyOnce.Should().BeTrue();
        card.CanDenySession.Should().BeTrue();
    }
}
```

> `PermissionCardModel.IsPending` 为 `internal set`，测试可写依赖 Task 2 已加 `InternalsVisibleTo`。

- [ ] **Step 6: 构建 + 运行相关测试**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PermissionInteractionServiceTests"
```
Expected: 构建成功；PermissionInteractionServiceTests PASS（9 个 Fact）。

- [ ] **Step 7: 提交**

```bash
git add -A samples/Seeing.Agent.WebUI/Services/PermissionCardAggregator.cs samples/Seeing.Agent.WebUI/Services/ActiveSessionTracker.cs samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs samples/Seeing.Agent.WebUI/Program.cs samples/Seeing.Agent.WebUI/Pages/Session.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionInteractionServiceTests.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/PermissionCardAggregatorTests.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/ActiveSessionTrackerTests.cs
git commit -m "refactor(webui): 删除 PermissionCardAggregator/ActiveSessionTracker 并清理接线"
```

## Task 7: 侧栏徽标 + Session/Conference 接线 + SidebarEntry.PendingPermissions

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/SessionSidebarView.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Conference.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSidebarViewTests.cs`

**依赖 Task 2 的 `PermissionInboxView`；Task 6 已完成 `Session.razor` tracker 清理。**

- [ ] **Step 1: 写失败测试（`PendingPermissions` 保留）**

在 `tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSidebarViewTests.cs` 类内追加：

```csharp
    [Fact]
    public void Build_ShouldPreservePendingPermissions()
    {
        var entry = new SidebarEntry(
            "s1", "会话", SessionRelation.Fork, IsAnchor: false, IsExecuting: false,
            ParentSessionId: null, Label: null, IsActive: false, PendingPermissions: 3);

        var built = SessionSidebarView.Build(new[] { entry }).Single().Entries.Single();

        built.PendingPermissions.Should().Be(3);
    }
```

- [ ] **Step 2: 运行测试，确认失败**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~Build_ShouldPreservePendingPermissions"
```
Expected: 编译失败（`SidebarEntry` 无 `PendingPermissions`）。

- [ ] **Step 3: `SidebarEntry` 增字段**

修改 `samples/Seeing.Agent.WebUI/Services/SessionSidebarView.cs` 的 `SidebarEntry` 记录，在 `bool IsActive` 之后追加带默认值的参数：

```csharp
public sealed record SidebarEntry(
    string SessionId,
    string Title,
    SessionRelation Relation,
    bool IsAnchor,
    bool IsExecuting,
    string? ParentSessionId,
    string? Label,
    bool IsActive,
    int PendingPermissions = 0);
```

并在 XML 文档注释的 `<param>` 列表补一行：

```csharp
/// <param name="PendingPermissions">该会话在途审批数（0 时不渲染徽标）。</param>
```

- [ ] **Step 4: 运行测试，确认通过**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionSidebarViewTests"
```
Expected: PASS（含新 Fact）。

- [ ] **Step 5: `Session.razor` 填充 `PendingPermissions` + 徽标 + 订阅**

修改 `samples/Seeing.Agent.WebUI/Pages/Session.razor`：

(a) 在 `@inject CircuitContext CircuitContext` 之后追加：

```razor
@inject Services.PermissionInboxView PermissionView
```

(b) `OnInitializedAsync` 内 `AppState.OnChange += OnAppStateChange;` 之后追加：

```csharp
        PermissionView.Changed += OnPermissionChanged;
```

(c) `Dispose()` 内追加：

```csharp
        PermissionView.Changed -= OnPermissionChanged;
```

(d) 在 `Dispose` 之前加方法：

```csharp
    private void OnPermissionChanged() => _ = InvokeAsync(StateHasChanged);
```

(e) `RebuildSidebar()` 的 `new SidebarEntry(...)` 追加第 9 个参数 `PermissionView.CountBySession(w.SessionId)`，完整结果：

```csharp
        var entries = _windows
            .Where(w => !string.Equals(w.SessionId, active, StringComparison.Ordinal))
            .Select(w => new SidebarEntry(
                w.SessionId,
                w.Title,
                w.Relation,
                w.IsAnchor,
                IsExecuting(w.SessionId),
                FindParentSessionId(w.SessionId),
                w.Label,
                w.IsActive,
                PermissionView.CountBySession(w.SessionId)));
```

(f) 侧栏标题计数（约 `:154`）改为显示分区待批合计：

```razor
                        <div class="session-side-section__title">
                            <span class="session-side-section__dot" aria-hidden="true"></span>
                            <span>@sec.Title</span>
                            <span class="session-side-section__count">
                                @sec.Entries.Count
                                @if (sec.Entries.Any(e => e.PendingPermissions > 0))
                                {
                                    <text> · 待批 @sec.Entries.Sum(e => e.PendingPermissions)</text>
                                }
                            </span>
                        </div>
```

(g) 用 `Badge` 包裹侧栏 `SessionWindow`（约 `:158-166`）：

```razor
                            <ErrorBoundary>
                                <Badge Count="@entry.PendingPermissions" Style="width: 100%;">
                                    <SessionWindow @key="@entry.SessionId"
                                                   SessionId="@entry.SessionId"
                                                   ViewMode="Services.SessionWindowMode.Summary"
                                                   AggregatorSessionId="@_mainSessionId"
                                                   GlobalSessionState="@GlobalSessionState"
                                                   OnClick="@OnTileClick"
                                                   OnExecutionStateChanged="@OnExecutionStateChanged" />
                                </Badge>
                            </ErrorBoundary>
```

- [ ] **Step 6: `Conference.razor` 同构接线**

修改 `samples/Seeing.Agent.WebUI/Pages/Conference.razor`：

(a) 在 `@inject CircuitContext CircuitContext` 之后追加：

```razor
@inject PermissionInboxView PermissionView
```

(b) `OnInitializedAsync` 起始处追加 `PermissionView.Changed += OnPermissionChanged;`；`Dispose` 内追加 `PermissionView.Changed -= OnPermissionChanged;`；加方法：

```csharp
    private void OnPermissionChanged() => _ = InvokeAsync(StateHasChanged);
```

(c) `RebuildSidebar()` 的 `new SidebarEntry(...)` 追加 `PermissionView.CountBySession(w.SessionId)`（同 Session.razor）。

(d) 侧栏分区标题计数与 `Badge` 包裹方式同 Step 5(f)(g)（类名替换为 `conference-side-section__count`）。

- [ ] **Step 7: 构建 + 运行相关测试**

Run:
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionSidebarViewTests"
```
Expected: 构建成功；SessionSidebarViewTests PASS。

- [ ] **Step 8: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/SessionSidebarView.cs samples/Seeing.Agent.WebUI/Pages/Session.razor samples/Seeing.Agent.WebUI/Pages/Conference.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/SessionSidebarViewTests.cs
git commit -m "feat(webui): 侧栏会话卡展示在途审批徽标"
```

---

## 完成校验（全量）

- [ ] **全量构建**
```bash
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
```
Expected: 0 error。

- [ ] **全量 WebUI 测试（VSTest）**
```bash
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
```
Expected: 无失败；`PermissionCardAggregatorTests`/`ActiveSessionTrackerTests` 已不存在。

- [ ] **确认无遗留引用**
```bash
rg -n "PermissionCardAggregator|ActiveSessionTracker|IPermissionPresenceStore|PermissionPresenceStore" samples/Seeing.Agent.WebUI tests/apps/Seeing.Agent.WebUI.Tests
```
Expected: 无匹配（除本计划文件外）。

---

## 交付契约点（评审签署意见复核）

- `PermissionInbox`：Singleton、无 Scoped 引用、锁外 `Changed`、全量重建。
- `PermissionInboxView`：scope = `registry.Windows`；`Interlocked` 单飞 + 微任务合并；锁外 `Changed`；`Dispose` 退订；prerender 守卫。
- `WebUiPermissionPresenter`：`SurfaceSessionIds` 返回已确认缓存快照；计算并入 `AnchorSessionId`；短去抖 250ms、空集确认 1s、相同不上报；circuit 打开 `Register`、`Dispose` `Unregister`（幂等）；prerender 守卫；经 `Router.GetOrCreateCircuitConsumer<SessionWindowRegistry>(circuitId)` 与页面同实例。
- `TaskCardAggregator`：`OnEvent` 父分支 `return` 之后、子流分支判断之前插入 `Permission*` 早退。
- UI 三处入口（工具卡槽位 / task 卡 / 审批中心抽屉）共享 `PermissionInboxView`；`SidebarEntry.PendingPermissions` 驱动徽标。



---

## 分册 C：Gateway + Hosting(TaskTool) + 文档

# 全局权限收件箱（Gateway + Hosting + 文档）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Gateway 执行订阅注册"每订阅一实例"的呈现端（`IPermissionPresenter`），并让 `TaskTool` 子会话继承父会话审批策略；同步既有架构文档/发布说明。

**Architecture:** Gateway 在 `GatewayOrchestratorV2.SubscribeExecutionEventsAsync` 中对根容器解析 `IPermissionPresentationStore`，进入订阅时 `Register` 一个 `GatewaySubscriptionPresenter(sessionId)`、`finally` 中 `Unregister`（幂等，等价原引用计数语义）；`TaskTool` 在新建/续跑两个分支统一经 `ISessionManager.GetOrLoadAsync` 取得父会话，以 `parent.AutoApprove` 覆盖原先硬编码的 `Enabled`。本计划只覆盖 Gateway + Hosting + 文档；Core 契约与 WebUI 呈现由其它分册负责。

**Tech Stack:** .NET 10.0、C#、xUnit v3 + Moq + FluentAssertions、Microsoft.Extensions.DependencyInjection、Blazor（仅文档引用）。

**前置依赖（其它分册交付，本计划假定已完成）：**

- Core 分册已新增 `Seeing.Agent.Abstractions.Permissions.IPermissionPresenter`、`IPermissionPresentationStore`，并删除 `IPermissionPresenceStore` / `PermissionPresenceStore`。
- 若 Core 尚未交付，本计划 Task 1–3 无法编译——先合并 Core 分册。
- 设计规格：`docs/superpowers/specs/2026-09-19-global-permission-inbox-design.md`（v3.1，§4.1/§4.2/§4.6/§5/§7.1/§7.2/§8/§10）。

**范围外（勿在本计划实现）：** Core 的 `PermissionPresentationStore`/`PermissionRequestManager` 变更、WebUI 的 `PermissionInbox`/`PermissionInboxView`/`WebUiPermissionPresenter`/`TaskCardAggregator` 变更、删 `ActiveSessionTracker`。

---

## 文件结构

| 文件 | 责任 |
|------|------|
| `src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs` | 新增：Gateway 订阅呈现端（固定单会话快照） |
| `src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs` | 修改：每订阅 Register/Unregister presenter |
| `tests/gateway/Seeing.Gateway.Tests/Permission/GatewaySubscriptionPresenterTests.cs` | 新增：presenter 契约单测 |
| `tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresentationTests.cs` | 新增：迁移 + 并发订阅用例（替换旧 PresenceTests） |
| `tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresenceTests.cs` | 删除：引用已删接口 |
| `src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs` | 修改：子会话继承父会话 AutoApprove |
| `tests/spine/Seeing.Agent.Tests/Tools/TaskToolAutoApproveInheritanceTests.cs` | 新增：继承用例 |
| `tests/spine/Seeing.Agent.Tests/Tools/TaskToolTests.cs` | 修改：fixture 适配 `GetOrLoadAsync` |
| `docs/architecture/08-permission-authorization-release-notes.md` | 修改：破坏性清单 / 已知边界 |
| `AGENTS.md` | 修改：权限契约位置 / DI 生命周期 / 关闭已知边界 |
| `docs/architecture/02-modules.md` | 修改：权限归属行 |
| `docs/architecture/04-development-standards.md` | 修改：Aggregator 示例 |
| `docs/architecture/06-compliance-audit.md` | 修改：单一真相源行 |
| `src/spine/Seeing.Agent.Core/Core/AGENTS.md` | 修改：Permission/ 文件清单 |
| `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` | 修改：XML 注释标注（仅注释） |

**测试命令约定（仓库实际方式）：** MTP 在本环境无法发现测试，统一 `dotnet build <测试项目>` + `dotnet vstest <dll> --TestCaseFilter:...`。

---

## Task 1: Gateway 订阅呈现端 `GatewaySubscriptionPresenter`

**Files:**
- Create: `src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs`
- Test: `tests/gateway/Seeing.Gateway.Tests/Permission/GatewaySubscriptionPresenterTests.cs`

- [ ] **Step 1: 写失败测试（presenter 契约）**

创建 `tests/gateway/Seeing.Gateway.Tests/Permission/GatewaySubscriptionPresenterTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Gateway.Permission;
using Xunit;

namespace Seeing.Gateway.Tests.Permission;

/// <summary>
/// GatewaySubscriptionPresenter：固定单会话快照；SurfacedChanged 永不触发。
/// </summary>
public class GatewaySubscriptionPresenterTests
{
    [Fact]
    public void SurfaceSessionIds_ShouldReturnConstructorSessionSnapshot()
    {
        var presenter = new GatewaySubscriptionPresenter("ses_1");

        presenter.SurfaceSessionIds.Should().BeEquivalentTo(new[] { "ses_1" });
    }

    [Fact]
    public void SurfaceSessionIds_ShouldBeStableAcrossReads()
    {
        var presenter = new GatewaySubscriptionPresenter("ses_1");

        presenter.SurfaceSessionIds.Should().BeSameAs(presenter.SurfaceSessionIds);
    }

    [Fact]
    public void SurfacedChanged_ShouldNeverBeRaised()
    {
        var presenter = new GatewaySubscriptionPresenter("ses_1");
        Action handler = () => throw new Xunit.Sdk.XunitException("SurfacedChanged 不应触发");
        presenter.SurfacedChanged += handler;

        // 无任何内部状态变化入口；附加/移除订阅本身不应触发事件
        presenter.SurfacedChanged -= handler;
    }
}
```

- [ ] **Step 2: 运行测试，确认编译失败**

Run: `dotnet build tests/gateway/Seeing.Gateway.Tests`
Expected: FAIL — `GatewaySubscriptionPresenter` / `Seeing.Agent.Gateway.Permission` 命名空间不存在。

- [ ] **Step 3: 创建实现**

创建 `src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs`：

```csharp
using Seeing.Agent.Abstractions.Permissions;

namespace Seeing.Agent.Gateway.Permission;

/// <summary>
/// Gateway 订阅呈现端：每个执行订阅注册一个实例，声明该订阅会话可呈现。
/// <para>订阅生命周期内固定单会话，<see cref="SurfaceSessionIds"/> 为已确认稳定快照，
/// <see cref="SurfacedChanged"/> 永不触发（固定集合无变化）。</para>
/// </summary>
public sealed class GatewaySubscriptionPresenter : IPermissionPresenter
{
    private readonly string[] _surfaceSessionIds;

    public GatewaySubscriptionPresenter(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _surfaceSessionIds = new[] { sessionId };
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> SurfaceSessionIds => _surfaceSessionIds;

    /// <inheritdoc />
    public event Action? SurfacedChanged
    {
        add { }
        remove { }
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet build tests/gateway/Seeing.Gateway.Tests && dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll --TestCaseFilter:"FullyQualifiedName~GatewaySubscriptionPresenterTests"`
Expected: PASS（3 个用例）。

- [ ] **Step 5: 提交**

```bash
git add src/gateway/Seeing.Agent.Gateway/Permission/GatewaySubscriptionPresenter.cs tests/gateway/Seeing.Gateway.Tests/Permission/GatewaySubscriptionPresenterTests.cs
git commit -m "feat(gateway): 新增 GatewaySubscriptionPresenter 订阅呈现端"
```

---

## Task 2: `GatewayOrchestratorV2` 接入呈现端

**Files:**
- Modify: `src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs:71-129`
- Modify: `tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresentationTests.cs`（Create）
- Delete: `tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresenceTests.cs`

- [ ] **Step 1: 写失败测试（迁移 + 并发订阅）**

删除旧测试并创建新文件。

```bash
git rm tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresenceTests.cs
```

创建 `tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresentationTests.cs`：

```csharp
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Chat;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Gateway.Configuration;
using Seeing.Agent.Gateway.Core;
using Seeing.Gateway.Models;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Gateway.Tests.Core;

/// <summary>
/// Gateway 订阅呈现端：每订阅注册一实例，注册/注销与订阅同生命周期；同会话多订阅互不影响。
/// </summary>
public class GatewayOrchestratorPresentationTests
{
    [Fact]
    public async Task SubscribeExecutionEvents_ShouldRegisterAndUnregisterPresenter()
    {
        var store = new FakePermissionPresentationStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = CreateOrchestrator(BuildProvider(store, _ => GatedEventsAsync(gate.Task)));

        var enumerator = orchestrator
            .SubscribeExecutionEventsAsync("ses_1", "exec_1", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var moveTask = enumerator.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => store.CanSurface("ses_1"), "presenter 未登记");

        gate.SetResult();
        (await moveTask).Should().BeFalse();
        store.CanSurface("ses_1").Should().BeFalse("订阅结束后应注销 presenter");

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task SubscribeExecutionEvents_WhenStreamFaults_ShouldStillUnregisterPresenter()
    {
        var store = new FakePermissionPresentationStore();
        var orchestrator = CreateOrchestrator(
            BuildProvider(store, _ => ThrowingEventsAsync(TestContext.Current.CancellationToken)));

        var events = new List<GatewayEvent>();
        await foreach (var e in orchestrator.SubscribeExecutionEventsAsync("ses_1", "exec_1", TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        events.Should().Contain(e => e.Object == GatewayEventObject.Error);
        store.CanSurface("ses_1").Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentSubscriptions_SameSession_UnregisteringOneShouldNotAffectOther()
    {
        var store = new FakePermissionPresentationStore();
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gates = new[] { gateA.Task, gateB.Task };
        var orchestrator = CreateOrchestrator(BuildProvider(store, i => GatedEventsAsync(gates[i])));

        var enumA = orchestrator
            .SubscribeExecutionEventsAsync("ses_1", "exec_a", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var enumB = orchestrator
            .SubscribeExecutionEventsAsync("ses_1", "exec_b", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var moveA = enumA.MoveNextAsync().AsTask();
        var moveB = enumB.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => store.PresenterCount == 2, "两个订阅未同时登记 presenter");
        store.CanSurface("ses_1").Should().BeTrue();

        gateA.SetResult();
        (await moveA).Should().BeFalse();
        store.PresenterCount.Should().Be(1, "注销一个订阅不应影响另一个");
        store.CanSurface("ses_1").Should().BeTrue();

        gateB.SetResult();
        (await moveB).Should().BeFalse();
        store.PresenterCount.Should().Be(0);
        store.CanSurface("ses_1").Should().BeFalse();

        await enumA.DisposeAsync();
        await enumB.DisposeAsync();
    }

    private static ServiceProvider BuildProvider(
        FakePermissionPresentationStore store,
        Func<int, IAsyncEnumerable<IMessageEvent>> eventsFactory)
    {
        var call = 0;
        var chatOrchestrator = new Mock<IChatOrchestrator>();
        chatOrchestrator
            .Setup(c => c.SubscribeEvents(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => eventsFactory(Interlocked.Increment(ref call) - 1));
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IPermissionPresentationStore>(store);
        services.AddSingleton(chatOrchestrator.Object);
        services.AddSingleton(sessionManager.Object);
        return services.BuildServiceProvider();
    }

    private static GatewayOrchestratorV2 CreateOrchestrator(IServiceProvider provider) =>
        new(
            provider,
            new GatewayOptions(),
            new GatewayRunTracker(),
            new SessionExecutionQueue(),
            NullLogger<GatewayOrchestratorV2>.Instance);

    private static async IAsyncEnumerable<IMessageEvent> GatedEventsAsync(
        Task gate, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<IMessageEvent> ThrowingEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new InvalidOperationException("stream boom");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > 5000)
                throw new TimeoutException(because);
            await Task.Delay(10);
        }
    }

    private sealed class FakePermissionPresentationStore : IPermissionPresentationStore
    {
        private readonly ConcurrentDictionary<IPermissionPresenter, byte> _presenters = new();

        public int PresenterCount => _presenters.Count;

        public event Action? Changed;

        public void Register(IPermissionPresenter presenter)
        {
            _presenters[presenter] = 0;
            Changed?.Invoke();
        }

        public void Unregister(IPermissionPresenter presenter)
        {
            _presenters.TryRemove(presenter, out _);
            Changed?.Invoke();
        }

        public bool CanSurface(string sessionId) =>
            _presenters.Keys.Any(p => p.SurfaceSessionIds.Contains(sessionId));
    }
}
```

- [ ] **Step 2: 构建，确认失败**

Run: `dotnet build tests/gateway/Seeing.Gateway.Tests`
Expected: FAIL — `GatewayOrchestratorV2` 仍引用已删除的 `IPermissionPresenceStore`（Core 分册已删），且未注册 presenter。

- [ ] **Step 3: 修改 `GatewayOrchestratorV2`**

在文件顶部 usings 增加 `using Seeing.Agent.Gateway.Permission;`（置于 `using Seeing.Agent.Gateway.Configuration;` 之后）。

将 `SubscribeExecutionEventsAsync` 方法体（当前 `:71-129`）替换为：

```csharp
    /// <summary>订阅指定 execution 的 Gateway 事件，直到匹配的 ExecutionComplete。</summary>
    public async IAsyncEnumerable<GatewayEvent> SubscribeExecutionEventsAsync(
        string sessionId,
        string executionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 每订阅一个独立呈现端实例：注册/注销与订阅同生命周期，等价于原引用计数语义；
        // 同 session 多订阅各持一份，任一订阅结束不影响其它（spec §4.6 / N2）。
        var presentation = _services.GetRequiredService<IPermissionPresentationStore>();
        var presenter = new GatewaySubscriptionPresenter(sessionId);
        presentation.Register(presenter);
        try
        {
            var runCts = _runTracker.RegisterRun(executionId, sessionId);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCts.Token);

            var runState = new ChatRunState();
            Exception? fault = null;
            var cancelled = false;

            await using var enumerator = StreamExecutionEventsAsync(sessionId, executionId, linkedCts.Token, runState)
                .GetAsyncEnumerator(linkedCts.Token);

            while (true)
            {
                var hasNext = false;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    fault = ex;
                    break;
                }

                if (!hasNext)
                    break;

                yield return enumerator.Current with { ExecutionId = executionId };
            }

            _runTracker.UnregisterRun(executionId);

            if (cancelled)
                yield return await BuildCancelledEventAsync(sessionId, runState) with { ExecutionId = executionId };
            else if (fault != null)
            {
                _logger.LogError(fault, "Gateway 执行订阅失败: SessionId={SessionId}, ExecutionId={ExecutionId}",
                    sessionId, executionId);
                yield return await BuildErrorEventAsync(sessionId, runState, fault) with { ExecutionId = executionId };
            }
        }
        finally
        {
            presentation.Unregister(presenter);
        }
    }
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet build tests/gateway/Seeing.Gateway.Tests && dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll --TestCaseFilter:"FullyQualifiedName~GatewayOrchestratorPresentationTests"`
Expected: PASS（3 个用例）。

- [ ] **Step 5: 确认 `GatewayPermissionChannel.TryAutoApprove` 未改变**

Run: `dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll --TestCaseFilter:"FullyQualifiedName~GatewayPermissionChannelTests"`
Expected: PASS（`auto_approve` 短路行为不变，无需修改 `src/gateway/Seeing.Agent.Gateway/Permission/GatewayPermissionChannel.cs`）。

- [ ] **Step 6: 提交**

```bash
git add src/gateway/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresentationTests.cs
git rm tests/gateway/Seeing.Gateway.Tests/Core/GatewayOrchestratorPresenceTests.cs
git commit -m "refactor(gateway): 订阅呈现改为每订阅一 presenter 实例"
```

---

## Task 3: `TaskTool` 继承用例与 fixture 适配（先失败）

**Files:**
- Create: `tests/spine/Seeing.Agent.Tests/Tools/TaskToolAutoApproveInheritanceTests.cs`
- Modify: `tests/spine/Seeing.Agent.Tests/Tools/TaskToolTests.cs`（fixture 增加 `GetOrLoadAsync` 设置；场景用例改用它，为 Task 4 的 `GetOrLoadAsync` 切换预置）

- [ ] **Step 1: 新增继承用例（会失败）**

创建 `tests/spine/Seeing.Agent.Tests/Tools/TaskToolAutoApproveInheritanceTests.cs`：

```csharp
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Hosting.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// TaskTool 审批策略继承：子会话 AutoApprove 继承父会话三态，不再硬编码 Enabled。
/// </summary>
public class TaskToolAutoApproveInheritanceTests
{
    [Theory]
    [InlineData(SessionAutoApprove.Disabled)]
    [InlineData(SessionAutoApprove.Enabled)]
    [InlineData(SessionAutoApprove.FollowGlobal)]
    public async Task ExecuteAsync_NewChild_ShouldInheritParentAutoApprove(SessionAutoApprove parentAutoApprove)
    {
        var fixture = new Fixture(parentAutoApprove);

        var result = await fixture.Tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth",
                subagent_type = "explore"
            }),
            new ToolContext { SessionId = fixture.ParentId, CallId = "call-new" });

        result.Success.Should().BeTrue();
        fixture.SubmittedOptions.Should().NotBeNull();
        fixture.SubmittedOptions!.AutoApprove.Should().Be(parentAutoApprove);
    }

    [Theory]
    [InlineData(SessionAutoApprove.Disabled)]
    [InlineData(SessionAutoApprove.Enabled)]
    [InlineData(SessionAutoApprove.FollowGlobal)]
    public async Task ExecuteAsync_ResumeTask_ShouldInheritParentAutoApprove(SessionAutoApprove parentAutoApprove)
    {
        var fixture = new Fixture(parentAutoApprove);

        var result = await fixture.Tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "continue",
                subagent_type = "explore",
                task_id = fixture.Child.Id
            }),
            new ToolContext { SessionId = fixture.ParentId, CallId = "call-resume" });

        result.Success.Should().BeTrue();
        fixture.SubmittedOptions.Should().NotBeNull();
        fixture.SubmittedOptions!.AutoApprove.Should().Be(parentAutoApprove);
    }

    private sealed class Fixture
    {
        public string ParentId { get; } = "parent-1";
        public SessionData Parent { get; }
        public SessionData Child { get; }
        public Mock<ISessionManager> SessionManager { get; } = new();
        public Mock<ISessionGroupManager> GroupManager { get; } = new();
        public Mock<IAgentRegistry> AgentRegistry { get; } = new();
        public Mock<IAgentLoopScheduler> LoopScheduler { get; } = new();
        public Mock<IExecutionStatusProvider> StatusProvider { get; } = new();
        public Mock<IExecutionSubmitter> Submitter { get; } = new();
        public Mock<IExecutionEventPublisher> EventPublisher { get; } = new();
        public ChatOptions? SubmittedOptions { get; private set; }
        public TaskTool Tool { get; }

        public Fixture(SessionAutoApprove parentAutoApprove)
        {
            Parent = new SessionData
            {
                Id = ParentId,
                Kind = SessionKind.Root,
                SelectedAgent = string.Empty,
                AutoApprove = parentAutoApprove,
                Metadata = new Dictionary<string, string>()
            };
            Child = new SessionData
            {
                Id = "child-1",
                Kind = SessionKind.SubAgent,
                SelectedAgent = "explore",
                SelectedModel = string.Empty,
                Metadata = new Dictionary<string, string>()
            };

            var agentDef = new AgentDefinition
            {
                Name = "explore",
                Mode = AgentMode.SubAgent,
                Runtime = AgentRuntime.Native,
                Description = "explore agent"
            };

            SessionManager.Setup(s => s.GetOrLoadAsync(ParentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Parent);
            SessionManager.Setup(s => s.Get(Child.Id)).Returns(Child);
            SessionManager.Setup(s => s.LoadAsync(It.IsAny<string>())).ReturnsAsync((SessionData?)null);
            SessionManager.Setup(s => s.AddMessageAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionMessage>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

            GroupManager.Setup(g => g.CreateChildAsync(
                    ParentId,
                    agentDef.Name,
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<SessionPermissionRule>>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Child);
            GroupManager.Setup(g => g.GetParentAsync(Child.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ParentId);
            GroupManager.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SessionData> { Child });

            AgentRegistry.Setup(r => r.GetAgentAsync("explore")).ReturnsAsync(agentDef);

            LoopScheduler.Setup(l => l.IsLoopBusy(It.IsAny<string>())).Returns(false);

            StatusProvider.Setup(p => p.GetOverview(It.IsAny<string>()))
                .Returns(new SessionExecutionOverview());

            Submitter.Setup(s => s.SubmitAsync(
                    It.IsAny<string>(),
                    It.IsAny<ChatInput>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, ChatInput, ChatOptions?, CancellationToken>(
                    (_, _, options, _) => SubmittedOptions = options)
                .ReturnsAsync(ExecutionSubmitResult.Succeeded("exec-1"));
            Submitter.Setup(s => s.WaitForExecutionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Tool = new TaskTool(
                NullLogger<TaskTool>.Instance,
                SessionManager.Object,
                GroupManager.Object,
                AgentRegistry.Object,
                LoopScheduler.Object,
                Submitter.Object,
                StatusProvider.Object,
                EventPublisher.Object);
        }
    }
}
```

- [ ] **Step 2: 适配既有 `TaskToolTests` fixture（预置 `GetOrLoadAsync`）**

在 `tests/spine/Seeing.Agent.Tests/Tools/TaskToolTests.cs` 的 `TaskToolFixture` 构造函数中，紧随现有 `SessionManager.Setup(s => s.Get(ParentId)).Returns((SessionData?)null);`（当前 `:344`）之后新增：

```csharp
            SessionManager.Setup(s => s.GetOrLoadAsync(ParentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionData { Id = ParentId, Kind = SessionKind.Root });
```

并把 `ExecuteAsync_CreateChild_ShouldPassParentScenario`（当前 `:143`）的：

```csharp
        fixture.SessionManager.Setup(s => s.Get(fixture.ParentId)).Returns(parent);
```

改为：

```csharp
        fixture.SessionManager.Setup(s => s.GetOrLoadAsync(fixture.ParentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parent);
```

- [ ] **Step 3: 运行新用例，确认失败**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskToolAutoApproveInheritanceTests"`
Expected: `Disabled` / `FollowGlobal` 两轮 FAIL（TaskTool 仍硬编码 `Enabled`）；`Enabled` 轮 PASS。

- [ ] **Step 4: 运行既有 TaskTool 用例，确认未被破坏**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskToolTests"`
Expected: PASS（TaskTool 尚未改动，fixture 新增设置不影响旧路径）。

- [ ] **Step 5: 提交（红阶段）**

```bash
git add tests/spine/Seeing.Agent.Tests/Tools/TaskToolAutoApproveInheritanceTests.cs tests/spine/Seeing.Agent.Tests/Tools/TaskToolTests.cs
git commit -m "test(task): 覆盖子会话审批策略继承父会话（红）"
```

---

## Task 4: `TaskTool` 继承父会话审批策略（实现）

**Files:**
- Modify: `src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs:102-184`

- [ ] **Step 1: 在 `try` 起始处统一取得父会话**

将 `TaskTool.ExecuteAsync` 中（当前 `:102-104`）：

```csharp
        try
        {
            SessionData session;
```

改为：

```csharp
        try
        {
            // 统一取得父会话：GetOrLoadAsync 避免缓存未命中退化为 FollowGlobal；
            // 新建与续跑分支共用，用于 AutoApprove 继承与权限快照派生
            var parent = await _sessionManager.GetOrLoadAsync(context.SessionId, context.CancellationToken);

            SessionData session;
```

- [ ] **Step 2: 删除新建分支内旧的父会话读取**

将（当前 `:130-131`）：

```csharp
            else
            {
                var parent = _sessionManager.Get(context.SessionId);
                AgentDefinition? parentDef = null;
```

改为（复用 Step 1 的 `parent`）：

```csharp
            else
            {
                AgentDefinition? parentDef = null;
```

- [ ] **Step 3: 将硬编码 `Enabled` 改为继承**

将（当前 `:182-183`）：

```csharp
                // 子代理工具调用默认自动批准（与旧 RunAgentAsync 的 AutoApproveInstance 语义一致）
                AutoApprove = SessionAutoApprove.Enabled
```

改为：

```csharp
                // 子会话默认继承父会话审批策略（spec §5：与 EffectivePermissionPolicy.Resolve 一致）
                AutoApprove = parent?.AutoApprove ?? SessionAutoApprove.FollowGlobal
```

> 续跑（`task_id`）分支无独立 `submitOptions` 代码——`submitOptions` 在分支合并后统一构造（当前 `:177`），因此 Step 1 的 `parent` 同时覆盖续跑分支。

- [ ] **Step 4: 运行继承用例，确认通过**

Run: `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskToolAutoApproveInheritanceTests"`
Expected: PASS（6 个用例）。

- [ ] **Step 5: 运行 TaskTool 全部用例，确认无回归**

Run: `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~TaskTool"`
Expected: PASS（`TaskToolTests` 与 `TaskToolAutoApproveInheritanceTests`；`TaskStatusToolTests` 亦命中 `TaskTool` 前缀）。

- [ ] **Step 6: 提交**

```bash
git add src/hosting/Seeing.Agent.Hosting/Tools/TaskTool.cs
git commit -m "fix(task): 子会话继承父会话 AutoApprove，移除硬编码自动批准"
```

---

## Task 5: 更新 `08-permission-authorization-release-notes.md`

**Files:**
- Modify: `docs/architecture/08-permission-authorization-release-notes.md`

- [ ] **Step 1: 更新 §1 单一真相源的"可交互性"**

将（当前 `:17`）：

```markdown
- 单一真相源：在途状态 → `IPermissionRequestManager`；记忆/白名单 → `IPermissionGrantStore`；可交互性 → `IPermissionPresenceStore`；生效策略 → `EffectivePermissionPolicy`（实时解析）。
```

改为：

```markdown
- 单一真相源：在途状态 → `IPermissionRequestManager`；记忆/白名单 → `IPermissionGrantStore`；可交互性 → `IPermissionPresentationStore`（2026-09-19 收敛重构后）；生效策略 → `EffectivePermissionPolicy`（实时解析）。
```

- [ ] **Step 2: 追加破坏性清单 §2.3**

在 `### 2.2 新增契约（供迁移对照）` 小节末（当前 `:53` 之后、`---` 之前）插入：

```markdown
### 2.3 全局权限收件箱重构（2026-09-19）

设计规格：[`2026-09-19-global-permission-inbox-design.md`](../superpowers/specs/2026-09-19-global-permission-inbox-design.md)（v3.1）。

| 类型 / 成员 | 位置 | 处置 | 替代 |
|-------------|------|------|------|
| `IPermissionPresenceStore` / `PermissionPresenceStore` | `Abstractions/Permissions/`、Core | **删除** | `IPermissionPresenter`（宿主声明的会话集合快照）+ `IPermissionPresentationStore`（登记表 + `CanSurface` + `Changed`） |
| `PermissionCardAggregator` | WebUI（sample） | **删除** | `PermissionInbox` / `PermissionInboxView`（唯一权威仍在 `IPermissionRequestManager`） |
| `ActiveSessionTracker` | WebUI（sample） | **删除** | 呈现端 `WebUiPermissionPresenter`（circuit 维度） |
| `IPermissionPresenceStore.CanPresent` 决策点 | `PermissionService` 步骤 6 | **改名/改语义** | `IPermissionPresentationStore.CanSurface(sessionId)` |

**新增契约（供迁移对照）：**

- Abstractions：`IPermissionPresenter`（`SurfaceSessionIds` + `SurfacedChanged`）、`IPermissionPresentationStore`（`Register`/`Unregister`/`CanSurface`/`Changed`）。
- `IPermissionRequestManager`：新增 `GetAllPending()` 与 `PendingChanged`（在途变更尽力通知；`BeginAsync`/`TryResolve`/`WaitAsync` 超时取消/`Dispose` 触发）。
- `ToolContext.PermissionAuthorizer`（`IPermissionAuthorizer?`，执行级 override 贯通，注入点在 `ToolManager.ExecuteAsync`）。
- 收敛策略：**仅呈现端注销时收敛**（宽限 1s 复核）；`SurfaceSessionIds` 内容收缩不收敛，由 300s 超时兜底（已知边界）。
```

- [ ] **Step 3: 关闭 §5 已知边界中的 Conference 多窗口条目**

将（当前 `:83`）：

```markdown
| Conference 页同 circuit 多窗口共用 `PermissionCardAggregator` | 聚合器为 circuit 维度单实例（单 `_sessionId`），多会话同屏需按父会话分 key；本期容忍 |
```

改为：

```markdown
| ~~Conference 页同 circuit 多窗口共用 `PermissionCardAggregator`~~ | **已关闭**（2026-09-19：`PermissionCardAggregator` 删除，`PermissionInboxView` 按 `registry.Windows` 会话集合投影，多会话同屏按会话分组） |
```

- [ ] **Step 4: 更新 §6 迁移指引的宿主表述**

将（当前 `:93`）：

```markdown
- **宿主**：不要设置 `ChatOptions.PermissionChannel`；改为注册 `IPermissionChannel`（Host Shape）并在需要交互时由活动会话维护 `IPermissionPresenceStore`。
```

改为：

```markdown
- **宿主**：不要设置 `ChatOptions.PermissionChannel`；改为注册 `IPermissionChannel`（Host Shape），并向 `IPermissionPresentationStore` 注册 `IPermissionPresenter` 声明可呈现的会话集合。
```

- [ ] **Step 5: 提交**

```bash
git add docs/architecture/08-permission-authorization-release-notes.md
git commit -m "docs(permission): 追加全局收件箱破坏性清单并关闭已知边界"
```

---

## Task 6: 更新根 `AGENTS.md`

**Files:**
- Modify: `AGENTS.md`

- [ ] **Step 1: 更新权限 WHERE TO LOOK 行**

将（当前 `:82`）：

```markdown
| 权限（授权/审批） | 引擎/Manager/Store 在 `src/spine/Seeing.Agent.Core/Core/Permission/`；契约在 `src/abstractions/Seeing.Agent.Abstractions/Permissions/`；宿主通道 `Hosting.Web`/`Gateway` | `IPermissionAuthorizer` 决策链；在途真相源 `IPermissionRequestManager`；可交互 `IPermissionPresenceStore`；见 `docs/architecture/08` |
```

改为：

```markdown
| 权限（授权/审批） | 引擎/Manager/Store 在 `src/spine/Seeing.Agent.Core/Core/Permission/`；契约在 `src/abstractions/Seeing.Agent.Abstractions/Permissions/`；宿主通道 `Hosting.Web`/`Gateway` | `IPermissionAuthorizer` 决策链；在途真相源 `IPermissionRequestManager`；呈现判定 `IPermissionPresentationStore.CanSurface` + 宿主呈现端 `IPermissionPresenter`；见 `docs/architecture/08` |
```

- [ ] **Step 2: 更新 DI 生命周期行**

将（当前 `:119`）：

```markdown
| ToolManager, HookManager, PermissionService, PermissionRequestManager, PermissionGrantStore, PermissionPresenceStore, EffectivePermissionPolicy, IPermissionAuthorizerFactory, SkillManager, McpClientManager, IToolDecoratorRegistry | Singleton |
```

改为：

```markdown
| ToolManager, HookManager, PermissionService, PermissionRequestManager, PermissionGrantStore, PermissionPresentationStore, EffectivePermissionPolicy, IPermissionAuthorizerFactory, SkillManager, McpClientManager, IToolDecoratorRegistry | Singleton |
```

- [ ] **Step 3: 关闭 Conference 已知边界**

将（当前 `:223`）：

```markdown
| **P3** | 权限：Conference 页同 circuit 多窗口共用 `PermissionCardAggregator`（单 `_sessionId`） | 已知边界（多会话同屏需按父会话分 key；本期容忍） |
```

改为：

```markdown
| **P3** | 权限：Conference 页同 circuit 多窗口共用 `PermissionCardAggregator`（单 `_sessionId`） | **已完成**（2026-09-19：删除 `PermissionCardAggregator`，改由 `PermissionInboxView` 按 `registry.Windows` 会话集合投影分组） |
```

- [ ] **Step 4: 提交**

```bash
git add AGENTS.md
git commit -m "docs(agents): 同步权限呈现契约位置并关闭 Conference 已知边界"
```

---

## Task 7: 同步架构文档与 Core `AGENTS.md`

**Files:**
- Modify: `docs/architecture/02-modules.md`
- Modify: `docs/architecture/04-development-standards.md`
- Modify: `docs/architecture/06-compliance-audit.md`
- Modify: `src/spine/Seeing.Agent.Core/Core/AGENTS.md`

- [ ] **Step 1: `02-modules.md` 权限归属**

将（当前 `:75`）：

```markdown
- **Abstractions（契约）**：`IPermissionService`、`IPermissionAuthorizer`（+`IPermissionAuthorizerFactory`）、`IPermissionRequestManager`、`IPermissionGrantStore`、`IPermissionPresenceStore`、`IPermissionChannel`、请求/结果/授权模型。
```

改为：

```markdown
- **Abstractions（契约）**：`IPermissionService`、`IPermissionAuthorizer`（+`IPermissionAuthorizerFactory`）、`IPermissionRequestManager`、`IPermissionGrantStore`、`IPermissionPresenter`、`IPermissionPresentationStore`、`IPermissionChannel`、请求/结果/授权模型。
```

将（当前 `:76`）：

```markdown
- **Core（脊柱）**：`PermissionService`（规则/策略 + `AuthorizeAsync` 资源门编排）、`PermissionRequestManager`（在途唯一权威）、`PermissionPresenceStore`、`PermissionGrantStore`、`EffectivePermissionPolicy`、`ExecutionContextPermissionAuthorizer` + `DefaultPermissionAuthorizerFactory`、`DenyAllPermissionChannel`、`PermissionKindMapper`。
```

改为：

```markdown
- **Core（脊柱）**：`PermissionService`（规则/策略 + `AuthorizeAsync` 资源门编排）、`PermissionRequestManager`（在途唯一权威）、`PermissionPresentationStore`（呈现端登记表）、`PermissionGrantStore`、`EffectivePermissionPolicy`、`ExecutionContextPermissionAuthorizer` + `DefaultPermissionAuthorizerFactory`、`DenyAllPermissionChannel`、`PermissionKindMapper`。
```

将（当前 `:78`）：

```markdown
- **Gateway**：`GatewayPermissionChannel`（`TryAutoApprove` 读 `GatewayOptions.PermissionMode`；拒绝仅在无 Presence 时）。
```

改为：

```markdown
- **Gateway**：`GatewayPermissionChannel`（`TryAutoApprove` 读 `GatewayOptions.PermissionMode`）；每个执行订阅注册 `GatewaySubscriptionPresenter`（固定单会话呈现端，随订阅注销）。
```

将（当前 `:79`）：

```markdown
- **WebUI（sample）**：`PermissionCardAggregator`（投影聚合）、`PermissionInteractionService`、`ActiveSessionTracker`（活动会话 Presence 记账）。
```

改为：

```markdown
- **WebUI（sample）**：`PermissionInbox`（全局在途投影，Singleton）、`PermissionInboxView`（scoped 投影 + 合并）、`WebUiPermissionPresenter`（circuit 维度呈现端）、`PermissionInteractionService`。
```

- [ ] **Step 2: `04-development-standards.md` Aggregator 示例**

将（当前 `:6`）：

```markdown
- `Aggregator`：UI 投影聚合（Scoped/circuit 维度，不持权威执行态，允许落盘 UI 缓存），如 `PermissionCardAggregator`/`TaskCardAggregator`。  
```

改为：

```markdown
- `Aggregator`：UI 投影聚合（Scoped/circuit 维度，不持权威执行态，允许落盘 UI 缓存），如 `TaskCardAggregator`。  
```

- [ ] **Step 3: `06-compliance-audit.md` 单一真相源行**

将（当前 `:97`）：

```markdown
| 单一真相源 | ✅ | 在途 → `IPermissionRequestManager`；记忆/白名单 → `IPermissionGrantStore`；可交互性 → `IPermissionPresenceStore`；审批事件 → `IExecutionEventPublisher` |
```

改为：

```markdown
| 单一真相源 | ✅ | 在途 → `IPermissionRequestManager`；记忆/白名单 → `IPermissionGrantStore`；可交互性 → `IPermissionPresentationStore`；审批事件 → `IExecutionEventPublisher` |
```

- [ ] **Step 4: Core `AGENTS.md` Permission/ 清单**

将（当前 `:42`）：

```markdown
│   ├── PermissionPresenceStore.cs  # 可交互性
```

改为：

```markdown
│   ├── PermissionPresentationStore.cs # 呈现端登记表（CanSurface + Changed）
```

- [ ] **Step 5: 提交**

```bash
git add docs/architecture/02-modules.md docs/architecture/04-development-standards.md docs/architecture/06-compliance-audit.md src/spine/Seeing.Agent.Core/Core/AGENTS.md
git commit -m "docs(architecture): 同步权限呈现契约与删除项"
```

---

## Task 8: 标注 `EventStreamHandler` 权限回调仅展示

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:141-149`

> 仅新增 XML 注释，不改行为（审批交互已由 `PermissionInbox`/`PermissionInboxView` 承接）。

- [ ] **Step 1: 更新两处 XML 注释**

将（当前 `:141-149`）：

```csharp
        /// <summary>
        /// 权限请求回调
        /// </summary>
        public event Action<PermissionRequestEvent>? OnPermissionRequest;

        /// <summary>
        /// 权限收敛回调
        /// </summary>
        public event Action<PermissionResolvedEvent>? OnPermissionResolved;
```

改为：

```csharp
        /// <summary>
        /// 权限请求回调。仅展示/时间线刷新用途，无生产消费者
        /// （在途审批交互由 <c>PermissionInbox</c>/<c>PermissionInboxView</c> 承接）。
        /// </summary>
        public event Action<PermissionRequestEvent>? OnPermissionRequest;

        /// <summary>
        /// 权限收敛回调。仅展示/时间线刷新用途，无生产消费者
        /// （在途审批交互由 <c>PermissionInbox</c>/<c>PermissionInboxView</c> 承接）。
        /// </summary>
        public event Action<PermissionResolvedEvent>? OnPermissionResolved;
```

- [ ] **Step 2: 编译 WebUI 宿主，确认注释未破坏构建**

Run: `dotnet build samples/Seeing.Agent.WebUI`
Expected: Build succeeded。

> 说明：`PermissionInbox`/`PermissionInboxView` 由 WebUI 分册实现；若尚未合并，注释中的 `<c>` 引用仍安全（XML 注释不校验类型）。

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs
git commit -m "docs(webui): 标注权限事件回调仅展示无生产消费者"
```

---

## 自审记录（自检）

**1. 规格覆盖：**

- §4.1 呈现端抽象（Gateway 侧消费）：Task 1/2 ✅
- §4.6 Gateway 每订阅一实例（Register/Unregister，try/finally 幂等）：Task 2 ✅
- §8「同 session 多 Gateway 订阅注销互不影响」：Task 2 并发用例 ✅
- §5 策略继承（新建 + 续跑统一 `GetOrLoadAsync`，`FollowGlobal` 兜底）：Task 3/4 ✅
- §9 Gateway 测试（迁移 PresenceTests、并发订阅）：Task 2 ✅
- §9 集成 TaskTool 继承（Disabled/Enabled/FollowGlobal）：Task 3/4 ✅（在 `ChatOptions.AutoApprove` 最小可测层断言）
- §10 文档（release notes 破坏性清单、根 AGENTS.md、02/03/04/06、Core AGENTS.md、EventStreamHandler 标注）：Task 5/6/7/8 ✅
- `GatewayPermissionChannel.TryAutoApprove` 不变：Task 2 Step 5 ✅
- §10 `ActiveSessionTracker`/`PermissionCardAggregator` 删除：不在本分册（Core/WebUI 分册），文档仅按目标陈述。

**2. 占位扫描：** 无 TBD/TODO；所有代码步骤含完整代码；所有命令含预期结果。

**3. 类型一致性：** `IPermissionPresenter`（`SurfaceSessionIds`/`SurfacedChanged`）、`IPermissionPresentationStore`（`Register`/`Unregister`/`CanSurface`/`Changed`）、`GatewaySubscriptionPresenter(string sessionId)`、`SessionAutoApprove`（`FollowGlobal`/`Enabled`/`Disabled`）、`ChatOptions.AutoApprove`、`ISessionManager.GetOrLoadAsync(string, CancellationToken)` 在全计划命名一致。

**4. 依赖顺序：** Task 1 → 2（Gateway）；Task 3 → 4（TaskTool，先红后绿）；Task 5–8 文档依赖前序实现合并。


---

## 整体审查（阶段 8）

全部任务完成后执行：

1. 全量构建：`dotnet build Seeing.Agent.slnx` 须零错误。
2. 全量测试（逐项目 VSTest，MTP 不可用）：
   - `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`
   - `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
   - `dotnet vstest tests/gateway/Seeing.Gateway.Tests/bin/Debug/net10.0/Seeing.Gateway.Tests.dll`
   - `dotnet vstest tests/capabilities/Seeing.Agent.Tools.Session.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Session.Tests.dll`
   - `dotnet vstest tests/capabilities/Seeing.Agent.Acp.Tests/bin/Debug/net10.0/Seeing.Agent.Acp.Tests.dll`
3. 残留检查（应无输出）：
   - `rg -n "IPermissionPresenceStore|PermissionPresenceStore" src tests`
   - `rg -n "PermissionCardAggregator|ActiveSessionTracker" src samples tests`
4. 契约一致性：`IPermissionPresenter.SurfaceSessionIds/SurfacedChanged`、`IPermissionPresentationStore.Register/Unregister/CanSurface/PresentersChanged/Changed`、`IPermissionRequestManager.GetAllPending/PendingChanged`、`ToolContext.PermissionAuthorizer` 全仓库命名与签名一致。
5. 文档同步：`docs/architecture/08-permission-authorization-release-notes.md`、根 `AGENTS.md` 与架构文档均已更新；`EventStreamHandler` 权限回调已标注仅展示。
6. 关键回归：子会话（task）在父会话页面打开时产生审批 → 不秒拒、侧栏徽标可见、审批中心抽屉与 task 卡内联可批；父 `Disabled` 子 Ask、父 `Enabled` 子 Allow。
