# Host Request 机制与编辑器命令工具 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **并行策略**：本计划按「工作流（WS）」组织，WS 之间尽量文件不相交，可派发多个子代理并行实施。**仅在 WS 边界与集成点审查**，不在每个小步骤审查。每个 WS 完成即提交。

**Goal:** 新增通用「宿主请求（Host Request）」机制，并以其实现 `editor_command` / `editor_query` / `editor_list_commands` 三工具 + 内置 SKILL，让 agent 能操作用户 WebUI 的 Monaco 编辑器并回读状态。

**Architecture:** 复用既有 `PendingRequestManager<,>` 并发内核与 `ISurfaceRegistry` 呈现端；新增 Abstractions 层通用契约 + Core 薄子类 `HostRequestManager`；WebUI 用「收件箱投影 + 活跃面板登记 + 同步编组派发」交付（不经会回放的事件流）。Step 1 先把 Question/Permission 重复的「呈现端注销收敛」上提基类。

**Tech Stack:** .NET 10 / C#、xUnit + FluentAssertions + Moq、Blazor Server（circuit）、BlazorMonaco 3.5.0、YamlDotNet（内嵌技能解析，镜像 Scheduler）。

**设计依据（已签字）:** `docs/superpowers/specs/2026-10-15-host-request-and-editor-tools-design.md`（v7）

**测试命令（MTP 本环境无法发现测试，一律用 VSTest）:**
```powershell
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~<Filter>"
```

---

## 文件结构（先锁边界）

| 工作流 | 文件 | 责任 |
|---|---|---|
| **WS0** 基类重构 | `src/spine/Seeing.Agent.Core/Core/Interactions/PendingRequestManager.cs`（改）<br>`src/spine/Seeing.Agent.Core/Core/Questions/QuestionRequestManager.cs`（改）<br>`src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs`（改） | 收敛逻辑上提基类 |
| **WS1** 通用契约 | `src/abstractions/Seeing.Agent.Abstractions/Interactions/HostRequest.cs`（新）<br>`.../HostResponse.cs`（新）<br>`.../HostResponseStatus.cs`（新）<br>`.../IHostRequestManager.cs`（新）<br>`.../IHostSurfaceRegistry.cs`（新）<br>`.../Permissions/PermissionKind.cs`（改） | DTO / 契约 / 枚举 |
| **WS2** Core 实现 | `src/spine/Seeing.Agent.Core/Core/Host/HostRequestManager.cs`（新）<br>`.../Core/Host/HostServiceExtensions.cs`（新）<br>`.../Core/Interactions/SurfaceRegistry.cs`（改）<br>`.../Extensions/ServiceCollectionExtensions.cs`（改）<br>`.../Core/Permission/PermissionKindMapper.cs`（改）<br>`.../Core/Tools/BuiltIn/DefaultToolPermissionPolicy.cs`（改） | 薄子类 Manager + DI + 多角色 + 权限映射 |
| **WS3** 能力包 | `src/capabilities/Seeing.Agent.Tools.Editor/*`（全新）<br>`tests/capabilities/Seeing.Agent.Tools.Editor.Tests/*`（全新） | 三工具 + 载荷助手 + 模块 + 内嵌 SKILL |
| **WS4** WebUI 通用交付 | `samples/Seeing.Agent.WebUI/Services/HostRequestInbox.cs`（新）<br>`.../Services/HostRequestActivePanelRegistry.cs`（新）<br>`.../Services/HostRequestDispatcher.cs`（新）<br>`.../Services/WebUiSurfacePresenter.cs`（改） | 投影 + 活跃面板登记 + 派发 |
| **WS5** WebUI 编辑器面板 | `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor`（改）<br>`.../Components/FileEditor.razor`（改）<br>`.../wwwroot/files-panel.js`（改） | 命令/查询/列举执行 + action 枚举 JS |
| **WS6** 集成 | `samples/Seeing.Agent.WebUI/Components/SessionFilePanel.razor`（改）<br>`samples/Seeing.Agent.WebUI/Program.cs`（改）<br>`samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`（改）<br>`Seeing.Agent.slnx`（改） | 接线（面板登记 / DI / 急切解析 / 解决方案） |

**依赖与并行**：
```
WS0 ─┐
     ├─> (WS0/WS1 并行) ─> WS2 ─┐
WS1 ─┘                          ├─> WS6 (集成)
        WS1 ─> WS3 ─────────────┤
        WS1 ─> WS4 ─────────────┤
        WS1 ─> WS5 ─────────────┘
```
- **可并行**：`WS0 ‖ WS1`；随后 `WS2 ‖ WS3 ‖ WS4 ‖ WS5`（文件集互不相交）。
- **必须串行**：`WS0` 先于任何触碰 `PendingRequestManager.cs` 的工作（无其它 WS 触碰它）；`WS1` 先于 `WS2–WS5`；`WS6` 最后。

---

## WS0：Step 1 基类重构（收敛上提，行为不变）

**Files:**
- Modify: `src/spine/Seeing.Agent.Core/Core/Interactions/PendingRequestManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Questions/QuestionRequestManager.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Interactions/PendingRequestManagerConvergenceTests.cs`（新）

- [ ] **Step 1: 写基类收敛失败测试**

新建 `tests/spine/Seeing.Agent.Tests/Interactions/PendingRequestManagerConvergenceTests.cs`，用一个最小 `PendingRequestManager` 测试子类 + 假 `ISurfaceRegistry` 覆盖三种收敛语义：

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Tests.Interactions;

public sealed class PendingRequestManagerConvergenceTests
{
    private sealed record Req(string Id, string SessionId) { public string Id { get; set; } = Id; }
    private sealed record Res(string Id) { public string Status { get; set; } = "pending"; }

    private sealed class TestManager : PendingRequestManager<Req, Res>
    {
        public TestManager(ISurfaceRegistry? registry, TimeSpan grace)
            : base(null, registry, grace) { }
        protected override string GetRequestId(Req r) => r.Id;
        protected override Req AssignRequestId(Req r, string id) { r.Id = id; return r; }
        protected override string GetSessionId(Req r) => r.SessionId;
        protected override Res CreateFallback(Req r, PendingFallbackReason reason)
            => new(r.Id) { Status = reason.ToString() };
        protected override Res CreateMissingResponse(RequestTicket t) => new(t.RequestId) { Status = "missing" };
    }

    private sealed class FakeSurface : ISurfaceRegistry
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        public void Register(ISurfaceProvider p) { }
        public void Unregister(ISurfaceProvider p) { }
        public bool CanSurface(string sessionId) => _ids.Contains(sessionId);
        public event Action? ProviderUnregistered;
        public event Action? Changed;
        public void SetSurface(params string[] ids) { _ids.Clear(); foreach (var i in ids) _ids.Add(i); }
        public void FireUnregistered() => ProviderUnregistered?.Invoke();
    }

    [Fact]
    public async Task ProviderUnregistered_NoLongerSurfaceable_ShouldConvergeToUnavailable()
    {
        var surface = new FakeSurface();
        surface.SetSurface("s1");
        using var mgr = new TestManager(surface, TimeSpan.FromMilliseconds(50));
        var ticket = await mgr.BeginAsync(new Req("r1", "s1"));
        surface.SetSurface();            // 收缩为空
        surface.FireUnregistered();
        var res = await mgr.WaitAsync(ticket);
        res.Status.Should().Be(PendingFallbackReason.Unavailable.ToString());
    }

    [Fact]
    public async Task ProviderUnregistered_StillSurfaceable_ShouldNotConverge()
    {
        var surface = new FakeSurface();
        surface.SetSurface("s1");
        using var mgr = new TestManager(surface, TimeSpan.FromMilliseconds(50));
        var ticket = await mgr.BeginAsync(new Req("r1", "s1"));
        surface.FireUnregistered();      // 仍可呈现
        var resolve = mgr.TryResolve("r1", new Res("r1") { Status = "done" });
        resolve.Should().BeTrue();
        (await mgr.WaitAsync(ticket)).Status.Should().Be("done");
    }

    [Fact]
    public async Task SurfaceShrinkWithoutUnregister_ShouldNotConverge()
    {
        var surface = new FakeSurface();
        surface.SetSurface("s1");
        using var mgr = new TestManager(surface, TimeSpan.FromMilliseconds(50));
        var ticket = await mgr.BeginAsync(new Req("r1", "s1"));
        surface.SetSurface();            // 仅内容收缩，无注销事件
        await Task.Delay(120);
        mgr.PendingCount.Should().Be(1);
    }
}
```

- [ ] **Step 2: 运行确认失败（编译错误：基类无 3 参构造）**

Run: `dotnet build tests/spine/Seeing.Agent.Tests`
Expected: 编译失败（`PendingRequestManager` 无 `(logger, registry, grace)` 构造）

- [ ] **Step 3: 上提收敛逻辑到基类**

改 `PendingRequestManager.cs`：构造增末位可空参数，新增收敛字段/计时器/退订，`OnDisposing` 内退订并释放计时器。要点：

```csharp
private readonly ISurfaceRegistry? _surfaceRegistry;
private readonly TimeSpan _convergenceGrace;
private readonly Timer _convergenceTimer;
private readonly object _convergenceGate = new();
private HashSet<string>? _convergenceCandidates;
private bool _convergenceArmed;

protected PendingRequestManager(
    ILogger? logger = null,
    ISurfaceRegistry? surfaceRegistry = null,
    TimeSpan? convergenceGrace = null)
    : base(logger)   // 注：若基类无此形态，则直接赋值 _logger
{
    _surfaceRegistry = surfaceRegistry;
    _convergenceGrace = convergenceGrace ?? TimeSpan.FromSeconds(1);
    if (_surfaceRegistry is not null)
        _surfaceRegistry.ProviderUnregistered += OnProviderUnregistered;
    _convergenceTimer = new Timer(
        OnConvergenceTimer, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
}
```

> **注意**：现有基类构造是 `protected PendingRequestManager(ILogger? logger = null)`（`PendingRequestManager.cs:21`）。**保持该构造可被现有子类以单参调用**，新增参数置末位并全部可空；把 logger 赋值逻辑保持在原处。

新增方法（从 Question/Permission 平移、统一用 `GetRequestId`）：

```csharp
private void OnProviderUnregistered()
{
    if (_disposed) return;
    lock (_convergenceGate)
    {
        _convergenceCandidates ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var req in GetAllPending()) _convergenceCandidates.Add(GetRequestId(req));
        if (_convergenceArmed) return;
        _convergenceArmed = true;
    }
    try { _convergenceTimer.Change(_convergenceGrace, System.Threading.Timeout.InfiniteTimeSpan); }
    catch (ObjectDisposedException) { }
}

private void OnConvergenceTimer(object? state)
{
    if (_disposed) return;
    HashSet<string>? candidates;
    lock (_convergenceGate)
    {
        candidates = _convergenceCandidates;
        _convergenceCandidates = null;
        _convergenceArmed = false;
    }
    if (candidates is null) return;
    try
    {
        foreach (var req in GetAllPending())
        {
            var id = GetRequestId(req);
            if (!candidates.Contains(id)) continue;
            var sessionId = GetSessionId(req);
            if (!string.IsNullOrEmpty(sessionId) && _surfaceRegistry is not null && _surfaceRegistry.CanSurface(sessionId))
                continue;
            TryResolve(id, CreateFallback(req, PendingFallbackReason.Unavailable), sessionId);
        }
    }
    catch (Exception ex) { _logger?.LogError(ex, "在途请求收敛复核失败"); }
}
```

`OnDisposing` 虚方法（新增；`Dispose()` 已在 `:171` 调用它）内加：

```csharp
protected virtual void OnDisposing()
{
    if (_surfaceRegistry is not null)
        _surfaceRegistry.ProviderUnregistered -= OnProviderUnregistered;
    _convergenceTimer.Dispose();
}
```

> 若基类已有 `OnDisposing` 空实现则替换之；派生类必须 `base.OnDisposing()`。

- [ ] **Step 4: 删 Question/Permission 的收敛拷贝**

`QuestionRequestManager.cs`：删 `_convergence*` 字段、`OnProviderUnregistered`、`OnConvergenceTimer`、`DefaultConvergenceGrace` 常量与相关 ctor 分支；`base(...)` 改为传 `surfaceRegistry` 与 `convergenceGrace`；`OnDisposing` 改为先 `base.OnDisposing()` 再保留自身清理。构造签名保持 `(eventPublisher, surfaceRegistry, logger, timeout, convergenceGrace)` 不变（`internal` 测试构造零改动）。

`PermissionRequestManager.cs`：同上；保留 `_sessionSubscription`/`_optionsSubscription` 退订于 `OnDisposing`，并调用 `base.OnDisposing()`。

- [ ] **Step 5: 运行全部相关测试**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~PendingRequestManagerConvergenceTests|FullyQualifiedName~QuestionRequestManagerTests|FullyQualifiedName~PermissionRequestManagerTests"
```
Expected: 全 PASS（新测试 + 既有收敛/超时/取消/幂等测试）

- [ ] **Step 6: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Core/Interactions/PendingRequestManager.cs src/spine/Seeing.Agent.Core/Core/Questions/QuestionRequestManager.cs src/spine/Seeing.Agent.Core/Core/Permission/PermissionRequestManager.cs tests/spine/Seeing.Agent.Tests/Interactions/PendingRequestManagerConvergenceTests.cs
git commit -m "refactor(interactions): hoist presenter-unregistered convergence into PendingRequestManager base"
```

---

## WS1：Abstractions 通用契约

**Files:**
- Create: `src/abstractions/Seeing.Agent.Abstractions/Interactions/HostRequest.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Interactions/HostResponse.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Interactions/HostResponseStatus.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Interactions/IHostRequestManager.cs`
- Create: `src/abstractions/Seeing.Agent.Abstractions/Interactions/IHostSurfaceRegistry.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Permissions/PermissionKind.cs`

- [ ] **Step 1: 新建 DTO**

`HostRequest.cs`：
```csharp
using System.Text.Json;

namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主能力调用请求（服务端 → 宿主）。</summary>
public sealed class HostRequest
{
    public string RequestId { get; set; } = string.Empty;
    public required string SessionId { get; init; }
    public string? CallId { get; init; }
    /// <summary>能力标识，如 "editor.command"。</summary>
    public required string Kind { get; init; }
    /// <summary>载荷（一律经 JsonSerializer.SerializeToElement 构造，自持 JsonDocument）。</summary>
    public JsonElement? Payload { get; init; }
}
```

`HostResponse.cs`：
```csharp
using System.Text.Json;

namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主能力调用响应（宿主 → 服务端）。</summary>
public sealed class HostResponse
{
    public string RequestId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public HostResponseStatus Status { get; set; } = HostResponseStatus.Unavailable;
    public JsonElement? Payload { get; init; }
    public string? Reason { get; init; }
}
```

`HostResponseStatus.cs`：
```csharp
namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主能力调用结果状态。</summary>
public enum HostResponseStatus
{
    Completed,
    Unavailable,
    Timeout,
    Cancelled,
    Failed
}
```

- [ ] **Step 2: 新建契约**

`IHostRequestManager.cs`：
```csharp
namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主请求在途管理器契约。</summary>
public interface IHostRequestManager : IPendingRequestManager<HostRequest, HostResponse>
{
}
```

`IHostSurfaceRegistry.cs`：
```csharp
namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主请求的可呈现性注册表（单实现多角色）。</summary>
public interface IHostSurfaceRegistry : ISurfaceRegistry
{
}
```

- [ ] **Step 3: 扩展 PermissionKind**

`PermissionKind.cs`：在 `Environment = 7` 后加：
```csharp
    /// <summary>编辑器操作权限 - 编辑器命令执行</summary>
    Editor = 8
```

- [ ] **Step 4: 构建 Abstractions**

Run: `dotnet build src/abstractions/Seeing.Agent.Abstractions`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/abstractions/Seeing.Agent.Abstractions/Interactions/HostRequest.cs src/abstractions/Seeing.Agent.Abstractions/Interactions/HostResponse.cs src/abstractions/Seeing.Agent.Abstractions/Interactions/HostResponseStatus.cs src/abstractions/Seeing.Agent.Abstractions/Interactions/IHostRequestManager.cs src/abstractions/Seeing.Agent.Abstractions/Interactions/IHostSurfaceRegistry.cs src/abstractions/Seeing.Agent.Abstractions/Permissions/PermissionKind.cs
git commit -m "feat(abstractions): add generic HostRequest contracts and PermissionKind.Editor"
```

> **审查点 A**：WS0+WS1 完成后，主代理审查「契约是否满足 WS2–WS5 的引用需求」再放行并行波次。

---

## WS2：Core 实现

**Files:**
- Create: `src/spine/Seeing.Agent.Core/Core/Host/HostRequestManager.cs`
- Create: `src/spine/Seeing.Agent.Core/Core/Host/HostServiceExtensions.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Interactions/SurfaceRegistry.cs`
- Modify: `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/spine/Seeing.Agent.Core/Core/Permission/PermissionKindMapper.cs`
- Modify: `src/spine/Seeing.Agent.Core/Tools/BuiltIn/DefaultToolPermissionPolicy.cs`
- Test: `tests/spine/Seeing.Agent.Tests/Host/HostRequestManagerTests.cs`（新）

- [ ] **Step 1: 实现 HostRequestManager**

`Core/Host/HostRequestManager.cs`（镜像 `QuestionRequestManager`，约 60 行）：
```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Core.Host;

/// <summary>宿主请求在途管理器（Singleton）：通用宿主能力调用的在途权威。</summary>
public sealed class HostRequestManager : PendingRequestManager<HostRequest, HostResponse>, IHostRequestManager
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout;

    public HostRequestManager(IHostSurfaceRegistry surfaceRegistry, ILogger<HostRequestManager>? logger = null)
        : base(logger, surfaceRegistry)
        => _timeout = DefaultTimeout;

    /// <summary>测试注入短超时。</summary>
    internal HostRequestManager(IHostSurfaceRegistry surfaceRegistry, TimeSpan timeout, TimeSpan? convergenceGrace = null, ILogger<HostRequestManager>? logger = null)
        : base(logger, surfaceRegistry, convergenceGrace)
        => _timeout = timeout;

    protected override TimeSpan Timeout => _timeout;

    protected override string GetRequestId(HostRequest request) => request.RequestId;
    protected override HostRequest AssignRequestId(HostRequest request, string id) { request.RequestId = id; return request; }
    protected override string GetSessionId(HostRequest request) => request.SessionId;

    protected override HostResponse CreateFallback(HostRequest request, PendingFallbackReason reason) => new()
    {
        RequestId = request.RequestId,
        SessionId = request.SessionId,
        Status = reason switch
        {
            PendingFallbackReason.Timeout => HostResponseStatus.Timeout,
            PendingFallbackReason.Cancelled => HostResponseStatus.Cancelled,
            PendingFallbackReason.Disposed => HostResponseStatus.Unavailable,
            _ => HostResponseStatus.Unavailable
        }
    };

    protected override HostResponse CreateMissingResponse(RequestTicket ticket) => new()
    {
        RequestId = ticket.RequestId,
        Status = HostResponseStatus.Unavailable
    };
}
```

> 若 WS0 后基类构造签名不同，按其实际签名调整 `base(...)`。

- [ ] **Step 2: 写 HostRequestManager 测试**

`tests/spine/Seeing.Agent.Tests/Host/HostRequestManagerTests.cs`（镜像 `QuestionRequestManagerTests` 的 Harness；用真实 `SurfaceRegistry`）：
```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Host;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Tests.Host;

public sealed class HostRequestManagerTests
{
    private sealed class Harness : IDisposable
    {
        public SurfaceRegistry Registry { get; } = new();
        public HostRequestManager Manager { get; }
        public Harness(TimeSpan? timeout = null, TimeSpan? grace = null)
            => Manager = new HostRequestManager(Registry, timeout ?? TimeSpan.FromSeconds(30), grace);
        public void Dispose() => Manager.Dispose();
    }

    [Fact]
    public async Task WaitAsync_TryResolve_ShouldReturnPayload_AndBeIdempotent()
    {
        using var h = new Harness();
        var ticket = await h.Manager.BeginAsync(new HostRequest { SessionId = "s1", Kind = "editor.command" });
        var resp = new HostResponse { RequestId = ticket.RequestId, SessionId = "s1", Status = HostResponseStatus.Completed };
        h.Manager.TryResolve(ticket.RequestId, resp).Should().BeTrue();
        h.Manager.TryResolve(ticket.RequestId, resp).Should().BeFalse();
        (await h.Manager.WaitAsync(ticket)).Status.Should().Be(HostResponseStatus.Completed);
    }

    [Fact]
    public async Task WaitAsync_Timeout_ShouldReturnTimeout()
    {
        using var h = new Harness(timeout: TimeSpan.FromMilliseconds(50));
        var ticket = await h.Manager.BeginAsync(new HostRequest { SessionId = "s1", Kind = "editor.query" });
        (await h.Manager.WaitAsync(ticket)).Status.Should().Be(HostResponseStatus.Timeout);
    }

    [Fact]
    public async Task WaitAsync_Cancelled_ShouldReturnCancelled()
    {
        using var h = new Harness();
        using var cts = new CancellationTokenSource();
        var ticket = await h.Manager.BeginAsync(new HostRequest { SessionId = "s1", Kind = "editor.query" });
        var wait = h.Manager.WaitAsync(ticket, cts.Token);
        cts.Cancel();
        (await wait).Status.Should().Be(HostResponseStatus.Cancelled);
    }

    [Fact]
    public async Task WaitAsync_UnknownTicket_ShouldReturnUnavailable()
    {
        using var h = new Harness();
        var res = await h.Manager.WaitAsync(new RequestTicket("nope", "s1"));
        res.Status.Should().Be(HostResponseStatus.Unavailable);
    }
}
```

- [ ] **Step 3: SurfaceRegistry 增角色**

`Core/Interactions/SurfaceRegistry.cs:14` 类声明改为：
```csharp
public sealed class SurfaceRegistry : IPermissionSurfaceRegistry, IQuestionSurfaceRegistry, IHostSurfaceRegistry
```

- [ ] **Step 4: DI 扩展**

`Core/Host/HostServiceExtensions.cs`：
```csharp
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Core.Host;

/// <summary>宿主请求服务扩展 - 独立呈现端注册表 + 在途管理器。</summary>
public static class HostServiceExtensions
{
    public static IServiceCollection AddSeeingHostRequests(this IServiceCollection services)
    {
        services.AddSingleton<IHostSurfaceRegistry>(_ => new SurfaceRegistry());
        services.AddSingleton<IHostRequestManager, HostRequestManager>();
        return services;
    }
}
```

在 `Core/Extensions/ServiceCollectionExtensions.cs` 的 `AddSeeingCore`（约 `:362` 附近 `TryAddSingleton` 区块）加入：
```csharp
services.AddSeeingHostRequests();
```
（确保 `using Seeing.Agent.Core.Host;` 存在。）

- [ ] **Step 5: 权限映射**

`Core/Permission/PermissionKindMapper.cs`：在 `mcp.` 分支后加：
```csharp
        if (permissionKind.StartsWith("editor.", StringComparison.OrdinalIgnoreCase))
            return PermissionKind.Editor;
```

`Core/Tools/BuiltIn/DefaultToolPermissionPolicy.cs`：在 `Mappings` 字典（`:25-41`）加：
```csharp
        ["editor_command"]      = new("editor.execute",              "commandId"),
```

- [ ] **Step 6: 运行 Core 测试**

Run:
```powershell
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~HostRequestManagerTests|FullyQualifiedName~PermissionKindMapper|FullyQualifiedName~DefaultToolPermissionPolicy"
```
Expected: 全 PASS

- [ ] **Step 7: 提交**

```bash
git add src/spine/Seeing.Agent.Core/Core/Host src/spine/Seeing.Agent.Core/Core/Interactions/SurfaceRegistry.cs src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs src/spine/Seeing.Agent.Core/Core/Permission/PermissionKindMapper.cs src/spine/Seeing.Agent.Core/Tools/BuiltIn/DefaultToolPermissionPolicy.cs tests/spine/Seeing.Agent.Tests/Host
git commit -m "feat(core): HostRequestManager + host surface registry + editor permission mapping"
```

---

## WS3：能力包 Seeing.Agent.Tools.Editor

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/Seeing.Agent.Tools.Editor.csproj`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/EditorHostRequests.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/EditorCommandTool.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/EditorQueryTool.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/EditorListCommandsTool.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/EditorToolsModule.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/Skills/editor-commands/SKILL.md`
- Create: `src/capabilities/Seeing.Agent.Tools.Editor/Skills/EditorSkillRegistrar.cs`
- Create: `tests/capabilities/Seeing.Agent.Tools.Editor.Tests/Seeing.Agent.Tools.Editor.Tests.csproj`
- Create: `tests/capabilities/Seeing.Agent.Tools.Editor.Tests/EditorToolTests.cs`

- [ ] **Step 1: 新建 csproj（镜像 Question）**

`src/capabilities/Seeing.Agent.Tools.Editor/Seeing.Agent.Tools.Editor.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Tools.Editor</RootNamespace>
    <Description>编辑器命令工具（Host Request 首个使用者）。</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <ProjectReference Include="..\..\abstractions\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />
    <ProjectReference Include="..\Seeing.Agent.Tools.Support\Seeing.Agent.Tools.Support.csproj" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Skills\**\SKILL.md" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Seeing.Agent.Tools.Editor.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 载荷助手**

`EditorHostRequests.cs`（Kind 常量 + 构造/解析，schema 单一来源）：
```csharp
using System.Text.Json;
using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.Tools.Editor;

/// <summary>编辑器 Host Request 载荷构造/解析（schema 单一来源）。</summary>
public static class EditorHostRequests
{
    public const string CommandKind = "editor.command";
    public const string QueryKind = "editor.query";
    public const string ListCommandsKind = "editor.list_commands";

    public static HostRequest Command(string sessionId, string? callId, string commandId, JsonElement? args, string? path)
        => new()
        {
            SessionId = sessionId, CallId = callId, Kind = CommandKind,
            Payload = JsonSerializer.SerializeToElement(new { commandId, args, path })
        };

    public static HostRequest Query(string sessionId, string? callId, string queryKind, string? path)
        => new()
        {
            SessionId = sessionId, CallId = callId, Kind = QueryKind,
            Payload = JsonSerializer.SerializeToElement(new { kind = queryKind, path })
        };

    public static HostRequest ListCommands(string sessionId, string? callId)
        => new() { SessionId = sessionId, CallId = callId, Kind = ListCommandsKind };
}
```

- [ ] **Step 3: 三工具**

`EditorCommandTool.cs`（继承 `BuiltInToolBase`；`Id => "editor_command"`；`Category => ToolCategory.General`；schema：`commandId` 必需、`args` object 可选、`path` string 可选；`ExecuteAsync` 取 `IHostRequestManager` + `IHostSurfaceRegistry`，`CanSurface` false → `Failure("无可用编辑器连接")`，否则 `BeginAsync`/`WaitAsync` 并格式化）。关键骨架：
```csharp
public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
{
    var commandId = GetStringArgument(arguments, "commandId");
    if (string.IsNullOrWhiteSpace(commandId)) return Failure("缺少必需参数: commandId");

    var manager = context.Services?.GetService<IHostRequestManager>();
    if (manager is null) return Failure("宿主请求通道未接入");
    var surfaces = context.Services?.GetService<IHostSurfaceRegistry>();
    if (surfaces is not null && !surfaces.CanSurface(context.SessionId))
        return Failure("无可用编辑器连接（请在 WebUI 打开文件面板）");

    var args = arguments.TryGetProperty("args", out var a) ? a : (JsonElement?)null;
    var path = GetStringArgument(arguments, "path");
    var req = EditorHostRequests.Command(context.SessionId, context.CallId, commandId, args, path);
    var ticket = await manager.BeginAsync(req, context.CancellationToken).ConfigureAwait(false);
    var resp = await manager.WaitAsync(ticket, context.CancellationToken).ConfigureAwait(false);
    return Format(resp);
}

private static ToolResult Format(HostResponse r) => r.Status switch
{
    HostResponseStatus.Completed => new ToolResult { Success = true, Output = $"命令已执行。{r.Payload?.ToString() ?? ""}" },
    HostResponseStatus.Unavailable => ToolResult.Failed("无可用编辑器连接"),
    HostResponseStatus.Timeout => ToolResult.Failed("编辑器未在限定时间内响应"),
    HostResponseStatus.Cancelled => ToolResult.Failed("编辑器操作已取消"),
    _ => ToolResult.Failed(r.Reason ?? "编辑器操作失败")
};
```

`EditorQueryTool.cs`（`Id => "editor_query"`；schema：`kind` enum 必需 `[selection,cursor,diagnostics,visibleRanges,value]`、`path` 可选；只读不做资源级检查；返回用定界包裹）：
```csharp
private const string DataBegin = "<<<EDITOR_DATA_BEGIN>>>";
private const string DataEnd = "<<<EDITOR_DATA_END>>>";
// Format: Completed => Success=true, Output = $"{DataBegin}\n{payload}\n{DataEnd}"
```

`EditorListCommandsTool.cs`（`Id => "editor_list_commands"`；无参数；同样定界包裹）。

- [ ] **Step 4: 内嵌 SKILL + 注册器**

`Skills/editor-commands/SKILL.md`（YAML frontmatter + 正文，含 §10 要求的命令目录/query kinds/无宿主语义/动态列举兜底）：
```markdown
---
name: editor-commands
description: 操作用户 WebUI 中 Monaco 编辑器（格式化/折叠/跳转/选区/多光标等）并查询其状态。需要用户在 WebUI 打开文件面板。
---

# 编辑器命令技能

## 工具
- `editor_command`：执行 Monaco 命令（透传 commandId）。参数：commandId（必需）、args（可选）、path（可选，在当前面板内切换）。
- `editor_query`：读取编辑器状态。kind ∈ selection | cursor | diagnostics | visibleRanges | value。
- `editor_list_commands`：列出当前编辑器支持的全部命令（id/label）。

## 常用命令（可能随 Monaco 版本漂移）
- 视图/导航：editor.action.revealDefinition、editor.action.goToLocations、editor.action.showHover、editor.action.marker.next
- 折叠：editor.foldAll、editor.unfoldAll、editor.foldLevel1
- 选区/多光标：editor.action.selectHighlights、editor.action.insertCursorAbove、editor.action.insertCursorBelow、editor.action.addSelectionToNextFindMatch
- 编辑（改内容，会标记 dirty 但不自动保存）：editor.action.formatDocument、editor.action.commentLine、editor.action.moveLinesUpAction、editor.action.moveLinesDownAction、editor.action.indentLines、editor.action.outdentLines
- 查找：actions.find、editor.action.startFindReplaceAction

## 注意
- 仅在用户当前激活的文件面板可用；无面板 → 快速失败。
- 命令改内容后需用户 Ctrl+S 保存。
- 目录查不到时用 `editor_list_commands` 动态探测。
```

`Skills/EditorSkillRegistrar.cs`（镜像 `SchedulerSkillRegistrar`，`internal static class`，`Register(ISkillManager, ILogger)` 扫 manifest 资源中 `.Skills.` 且以 `SKILL.md` 结尾者，解析 frontmatter 后 `RegisterEmbeddedSkill`，`Location = $"editor/{name}"`；`Unregister(ISkillManager, names)`）。

- [ ] **Step 5: 模块**

`EditorToolsModule.cs`（镜像 `QuestionToolsModule` + Scheduler 的技能注册）：`Id => "editor.tools"`，`ProvidedTools => ["editor_command","editor_query","editor_list_commands"]`；`ConfigureServices` 注册三工具；`ActivateAsync` 注册工具 + 调用 `EditorSkillRegistrar.Register`；`DeactivateAsync` 注销工具 + `Unregister` 技能。

- [ ] **Step 6: 测试工程 + 工具测试**

`tests/capabilities/Seeing.Agent.Tools.Editor.Tests/Seeing.Agent.Tools.Editor.Tests.csproj`（复制 `Seeing.Agent.Tools.Question.Tests.csproj`，改 RootNamespace 与 ProjectReference 指向 `Seeing.Agent.Tools.Editor`）。

`EditorToolTests.cs`：用 Moq 的 `IHostRequestManager`（`BeginAsync` 返回 `RequestTicket`，`WaitAsync` 返回构造的 `HostResponse`）与 `IHostSurfaceRegistry`，覆盖：
- `commandId` 缺失 → Failure；
- `CanSurface=false` → Failure；
- `Completed` → Success 且 Output 含定界；
- `Unavailable/Timeout` → Failure。

- [ ] **Step 7: 运行测试**

Run:
```powershell
dotnet build tests/capabilities/Seeing.Agent.Tools.Editor.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Editor.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Editor.Tests.dll
```
Expected: PASS

- [ ] **Step 8: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Editor tests/capabilities/Seeing.Agent.Tools.Editor.Tests
git commit -m "feat(tools.editor): editor_command/query/list_commands tools + embedded skill"
```

---

## WS4：WebUI 通用交付

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/HostRequestInbox.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/HostRequestActivePanelRegistry.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/HostRequestDispatcher.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/WebUiSurfacePresenter.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/HostRequestDispatcherTests.cs`（新）

- [ ] **Step 1: HostRequestInbox（镜像 QuestionInbox）**

`HostRequestInbox.cs`：Singleton，订阅 `IHostRequestManager.PendingChanged`，`Rebuild()` 拉取 `GetAllPending()` 生成 `IReadOnlyList<HostRequest>` 快照，`SameIds` 变更时触发 `Changed`；`GetAll()`；`Dispose` 退订。

- [ ] **Step 2: HostRequestActivePanelRegistry（Singleton）**

```csharp
using System.Collections.Concurrent;
using Seeing.Agent.Abstractions.Interactions;

namespace Seeing.Agent.WebUI.Services;

/// <summary>活跃文件面板登记（Singleton）：按 (sessionId, circuitId) 存可编组委托。</summary>
public sealed class HostRequestActivePanelRegistry
{
    private readonly ConcurrentDictionary<(string SessionId, string CircuitId), Func<HostRequest, CancellationToken, Task<HostResponse>>> _panels = new();

    public void Register(string sessionId, string circuitId, Func<HostRequest, CancellationToken, Task<HostResponse>> handler)
        => _panels[(sessionId, circuitId)] = handler;

    public void Unregister(string sessionId, string circuitId)
        => _panels.TryRemove((sessionId, circuitId), out _);

    public bool TryGetLocal(string sessionId, string circuitId, out Func<HostRequest, CancellationToken, Task<HostResponse>>? handler)
        => _panels.TryGetValue((sessionId, circuitId), out handler);

    public bool HasAny(string sessionId)
    {
        foreach (var key in _panels.Keys)
            if (string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
                return true;
        return false;
    }
}
```

- [ ] **Step 3: HostRequestDispatcher（Scoped，镜像 QuestionInboxView 单飞）**

`HostRequestDispatcher.cs`：
```csharp
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Hosting.Web.Circuits;

namespace Seeing.Agent.WebUI.Services;

/// <summary>宿主请求派发（Scoped，circuit 维度）：收件箱投影 → 活跃面板 → 同步编组执行 → 回传。</summary>
public sealed class HostRequestDispatcher : IDisposable
{
    private static readonly TimeSpan DefaultDispatchDelay = TimeSpan.FromMilliseconds(20);

    private readonly HostRequestInbox _inbox;
    private readonly HostRequestActivePanelRegistry _panels;
    private readonly IHostRequestManager _manager;
    private readonly TimeSpan _delay;
    private readonly ILogger<HostRequestDispatcher>? _logger;
    private readonly string _circuitId;
    private readonly bool _enabled;

    private int _dirty;
    private int _scheduled;
    private bool _disposed;

    public HostRequestDispatcher(
        HostRequestInbox inbox,
        HostRequestActivePanelRegistry panels,
        IHostRequestManager manager,
        CircuitContext circuitContext,
        ILogger<HostRequestDispatcher>? logger = null)
        : this(inbox, panels, manager, circuitContext.Id ?? "", DefaultDispatchDelay, logger) { }

    internal HostRequestDispatcher(
        HostRequestInbox inbox,
        HostRequestActivePanelRegistry panels,
        IHostRequestManager manager,
        string circuitId,
        TimeSpan delay,
        ILogger<HostRequestDispatcher>? logger = null)
    {
        _inbox = inbox; _panels = panels; _manager = manager;
        _circuitId = circuitId; _delay = delay; _logger = logger;
        if (string.IsNullOrEmpty(circuitId)) return; // prerender 守卫
        _enabled = true;
        _inbox.Changed += OnChanged;
        Schedule(); // 构造时初始派发
    }

    private void OnChanged() => Schedule();

    private void Schedule()
    {
        if (_disposed || !_enabled) return;
        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) == 1) return;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay).ConfigureAwait(false);
            while (Interlocked.Exchange(ref _dirty, 0) == 1)
                await DispatchOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "宿主请求派发失败"); }
        finally
        {
            Interlocked.Exchange(ref _scheduled, 0);
            if (!_disposed && Volatile.Read(ref _dirty) == 1) Schedule();
        }
    }

    private async Task DispatchOnceAsync()
    {
        foreach (var req in _inbox.GetAll())
        {
            if (_panels.TryGetLocal(req.SessionId, _circuitId, out var target) && target is not null)
            {
                HostResponse resp;
                try { resp = await target(req, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    resp = new HostResponse { RequestId = req.RequestId, SessionId = req.SessionId, Status = HostResponseStatus.Failed, Reason = ex.Message };
                }
                _manager.TryResolve(req.RequestId, resp, req.SessionId);
            }
            else if (!_panels.HasAny(req.SessionId))
            {
                _manager.TryResolve(req.RequestId, new HostResponse
                {
                    RequestId = req.RequestId, SessionId = req.SessionId, Status = HostResponseStatus.Unavailable
                }, req.SessionId);
            }
            // else: 他 circuit 持有 → 跳过
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_enabled) _inbox.Changed -= OnChanged;
    }
}
```

- [ ] **Step 4: WebUiSurfacePresenter 接入 host registry**

`WebUiSurfacePresenter.cs`：构造注入 `IHostSurfaceRegistry hostStore`（加参数与字段）；`:82-84` 区域加 `_hostStore.Register(this);`；`:231-236` 加 `Unregister(_hostStore);`。保持 `ISurfaceProvider` 三注册并列。

- [ ] **Step 5: 测试**

`tests/apps/Seeing.Agent.WebUI.Tests/Services/HostRequestDispatcherTests.cs`：用假 `HostRequestInbox`（可手动触发）、真实 `HostRequestActivePanelRegistry`、假 `IHostRequestManager`（记录 `TryResolve`），断言：
- 本 circuit 有面板 → 委托被调 + `TryResolve(Completed)`；
- 他 circuit 有面板 → 跳过（不 resolve）；
- 全局无面板 → `TryResolve(Unavailable)`；
- 空 circuitId → 不订阅、不派发。

> 测试构造用 `internal` 构造注入 `circuitId` 与短 `delay`。

- [ ] **Step 6: 运行测试**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~HostRequestDispatcherTests"
```
Expected: PASS

- [ ] **Step 7: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/HostRequestInbox.cs samples/Seeing.Agent.WebUI/Services/HostRequestActivePanelRegistry.cs samples/Seeing.Agent.WebUI/Services/HostRequestDispatcher.cs samples/Seeing.Agent.WebUI/Services/WebUiSurfacePresenter.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/HostRequestDispatcherTests.cs
git commit -m "feat(webui): host request inbox + active-panel registry + dispatcher"
```

---

## WS5：WebUI 编辑器面板（命令/查询执行）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor`
- Modify: `samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.js`

- [ ] **Step 1: FileEditor 增命令/查询/列举方法**

在 `FileEditor.razor` 的 `@code` 增（用 BlazorMonaco C# API）：
```csharp
/// <summary>执行 Monaco 命令（透传 commandId/payload）。</summary>
public async Task<CommandOutcome> ExecuteCommandAsync(string commandId, System.Text.Json.JsonElement? args)
{
    if (_editor is null) return CommandOutcome.NoEditor;
    try
    {
        await _editor.Trigger("seeing-agent", commandId, args);
        return CommandOutcome.Ok;
    }
    catch { return CommandOutcome.Failed; }
}

/// <summary>查询编辑器状态。</summary>
public async Task<string?> QueryAsync(string kind)
{
    if (_editor is null) return null;
    return kind switch
    {
        "selection" => await _editor.GetSelection() is { } sel
            ? System.Text.Json.JsonSerializer.Serialize(new { sel.StartLineNumber, sel.StartColumn, sel.EndLineNumber, sel.EndColumn })
            : null,
        "cursor" => await _editor.GetPosition() is { } pos
            ? System.Text.Json.JsonSerializer.Serialize(new { pos.LineNumber, pos.Column })
            : null,
        "value" => await _editor.GetValue(),
        "visibleRanges" => System.Text.Json.JsonSerializer.Serialize(await _editor.GetVisibleRanges()),
        "diagnostics" => await GetDiagnosticsJsonAsync(),
        _ => null
    };
}

private async Task<string> GetDiagnosticsJsonAsync()
{
    // GetModelMarkers 在静态 Global 上
    var markers = await BlazorMonaco.Editor.Global.GetModelMarkers(JS, null);
    return System.Text.Json.JsonSerializer.Serialize(markers);
}

/// <summary>列举当前编辑器支持的命令。</summary>
public Task<string> ListCommandsAsync() => JS.InvokeAsync<string>("seeingEditor.getActions", _editorId);

private string _editorId => $"file-editor-{Sanitize(ActiveFile?.Id.RelativePath ?? "")}";
```
> 字段 `_editorId` 需与模板 `Id="@($"file-editor-{Sanitize(f.Id.RelativePath)}")"`（`:53`）一致；`Sanitize` 已存在（`:104`）。

- [ ] **Step 2: files-panel.js 增 action 枚举**

在 `files-panel.js` 的 IIFE 内追加：
```javascript
    window.seeingEditor = window.seeingEditor || {};
    window.seeingEditor.getActions = function (editorId) {
        var holder = (window.blazorMonaco && window.blazorMonaco.editors || [])
            .find(function (h) { return h.id === editorId; });
        if (!holder || !holder.editor || typeof holder.editor.getSupportedActions !== 'function') return '[]';
        return JSON.stringify(holder.editor.getSupportedActions().map(function (a) {
            return { id: a.id, label: a.label };
        }));
    };
```

- [ ] **Step 3: FilePanelHost 增 public 转发**

`FilePanelHost.razor`：把 `OpenOrActivateAsync`（`:231`）改为 `public`；新增：
```csharp
/// <summary>宿主请求入口：执行编辑器命令。</summary>
public async Task<HostResponsePayload> ExecuteEditorCommandAsync(string commandId, System.Text.Json.JsonElement? args, string? path)
{
    if (_workspace is null) return HostResponsePayload.Unavailable;
    if (path is not null) await OpenOrActivateAsync(_workspace.GetResourceId(path), refreshIfActive: false);
    var f = _tabs.OpenFiles.FirstOrDefault(x => x.Id.ToString() == _tabs.ActiveKey);
    if (f is null || f.IsReadOnly || f.IsDeleted || f.Kind is FileContentKind.Image or FileContentKind.Binary)
        return HostResponsePayload.Unavailable;
    if (_editor is null) return HostResponsePayload.Unavailable;
    var outcome = await _editor.ExecuteCommandAsync(commandId, args);
    return outcome == CommandOutcome.Ok ? HostResponsePayload.Ok() : HostResponsePayload.Unavailable;
}

/// <summary>宿主请求入口：查询编辑器状态。</summary>
public async Task<HostResponsePayload> QueryEditorAsync(string kind, string? path)
{
    if (_workspace is null || _editor is null) return HostResponsePayload.Unavailable;
    if (path is not null) await OpenOrActivateAsync(_workspace.GetResourceId(path), refreshIfActive: false);
    var json = await _editor.QueryAsync(kind);
    return json is null ? HostResponsePayload.Unavailable : HostResponsePayload.Ok(json);
}

/// <summary>宿主请求入口：列举命令。</summary>
public async Task<HostResponsePayload> ListEditorCommandsAsync()
{
    if (_editor is null) return HostResponsePayload.Unavailable;
    return HostResponsePayload.Ok(await _editor.ListCommandsAsync());
}
```
并定义轻量结果类型（可放同文件 `@code` 内或 `WebUI.Files/Services/`）：
```csharp
public readonly record struct HostResponsePayload(bool Available, string? Json)
{
    public static HostResponsePayload Unavailable => new(false, null);
    public static HostResponsePayload Ok(string? json = null) => new(true, json);
}
public enum CommandOutcome { Ok, NoEditor, Failed }
```

- [ ] **Step 4: 构建 WebUI.Files**

Run: `dotnet build samples/Seeing.Agent.WebUI.Files`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Files/Components/FileEditor.razor samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor samples/Seeing.Agent.WebUI.Files/wwwroot/files-panel.js
git commit -m "feat(webui.files): editor command/query/list execution + action enumeration JS"
```

> **审查点 B**：WS2–WS5 完成后，主代理审查「各 WS 接口能否对接」再进入 WS6 集成。

---

## WS6：集成（接线）

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionFilePanel.razor`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Modify: `samples/Seeing.Agent.WebUI/Shared/MainLayout.razor`
- Modify: `Seeing.Agent.slnx`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`（加 ProjectReference）

- [ ] **Step 1: slnx + csproj**

`Seeing.Agent.slnx` 的 `/src/capabilities/` 段加：
```xml
    <Project Path="src/capabilities/Seeing.Agent.Tools.Editor/Seeing.Agent.Tools.Editor.csproj" />
```
（tests 段同理加 `tests/capabilities/Seeing.Agent.Tools.Editor.Tests/...`。）

`samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` 加：
```xml
    <ProjectReference Include="..\..\src\capabilities\Seeing.Agent.Tools.Editor\Seeing.Agent.Tools.Editor.csproj" />
```

- [ ] **Step 2: SessionFilePanel 登记活跃面板**

`SessionFilePanel.razor`：注入 `HostRequestActivePanelRegistry PanelRegistry` 与 `CircuitContext CircuitContext`；新增 `ShouldServe` 与注册/注销：

```csharp
private bool ShouldServe =>
    !string.IsNullOrEmpty(SessionId)
    && PanelState.IsOpen
    && string.Equals(PanelState.SessionId, SessionId, StringComparison.Ordinal)
    && _host is not null;

private void SyncPanelRegistration()
{
    var circuitId = CircuitContext.Id ?? "";
    if (string.IsNullOrEmpty(SessionId) || string.IsNullOrEmpty(circuitId)) return;
    if (ShouldServe)
        PanelRegistry.Register(SessionId, circuitId, HandleHostRequestAsync);
    else
        PanelRegistry.Unregister(SessionId, circuitId);
}

private Task<HostResponse> HandleHostRequestAsync(HostRequest req, CancellationToken ct)
    => InvokeAsync(async () =>
    {
        var host = _host;
        if (host is null)
            return new HostResponse { RequestId = req.RequestId, SessionId = req.SessionId, Status = HostResponseStatus.Unavailable };
        try
        {
            var kind = req.Payload?.TryGetProperty("kind", out var k) == true ? k.GetString() : null;
            HostResponsePayload payload = req.Kind switch
            {
                "editor.command" => await host.ExecuteEditorCommandAsync(
                    req.Payload!.Value.GetProperty("commandId").GetString()!, 
                    req.Payload!.Value.TryGetProperty("args", out var a) && a.ValueKind != System.Text.Json.JsonValueKind.Null ? a : null,
                    req.Payload!.Value.TryGetProperty("path", out var p) ? p.GetString() : null),
                "editor.query" => await host.QueryEditorAsync(kind ?? "value",
                    req.Payload!.Value.TryGetProperty("path", out var p2) ? p2.GetString() : null),
                "editor.list_commands" => await host.ListEditorCommandsAsync(),
                _ => HostResponsePayload.Unavailable
            };
            return payload.Available
                ? new HostResponse { RequestId = req.RequestId, SessionId = req.SessionId, Status = HostResponseStatus.Completed,
                    Payload = payload.Json is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(payload.Json) }
                : new HostResponse { RequestId = req.RequestId, SessionId = req.SessionId, Status = HostResponseStatus.Unavailable };
        }
        catch (Exception ex)
        {
            return new HostResponse { RequestId = req.RequestId, SessionId = req.SessionId, Status = HostResponseStatus.Failed, Reason = ex.Message };
        }
    });
```

生命周期接线：
- `OnAfterRenderAsync`（`:55`）末尾调 `SyncPanelRegistration()`。
- `OnPanelChanged`（`:74`）末尾调 `SyncPanelRegistration()`。
- `OnInitialized`（`:43`）订阅 `PanelState.Changed` 已有；补 `SyncPanelRegistration()` 不在此（`_host` 未就绪），保持仅渲染期。
- `Dispose`（`:101`）：`PanelRegistry.Unregister(SessionId ?? "", CircuitContext.Id ?? "")`。

> 注：`_host` 在面板关闭后不自动置 null，`ShouldServe` 含 `PanelState.IsOpen && SessionId 匹配` 双保险；`SyncPanelRegistration` 幂等（Register 覆盖/Unregister 幂等）。

- [ ] **Step 3: Program.cs 注册服务 + 模块**

在 `:123-128` 附近加：
```csharp
builder.Services.AddSingleton<HostRequestInbox>();
builder.Services.AddSingleton<HostRequestActivePanelRegistry>();
builder.Services.AddScoped<HostRequestDispatcher>();
```
在模块装配区（`:62` 附近 `AddSeeingModule<FileSystemModule>` 处）加：
```csharp
builder.Services.AddSeeingModule<Seeing.Agent.Tools.Editor.EditorToolsModule>(registry);
```
（补 `using Seeing.Agent.WebUI.Services;` 与 `using Seeing.Agent.Tools.Editor;`。）

- [ ] **Step 4: MainLayout 急切解析 Dispatcher**

`Shared/MainLayout.razor`：在注入区（`:13-20`）加：
```razor
@inject Seeing.Agent.WebUI.Services.HostRequestDispatcher HostDispatcher
```
（仅注入即可触发 Scoped 构造与订阅；`HostDispatcher` 未被引用，用 `@* 急切解析 *@` 注释说明；如需消除未使用告警，可在 `@code` 中保留 `_ = HostDispatcher;`。）

- [ ] **Step 5: 全量构建 + 关键测试**

Run:
```powershell
dotnet build Seeing.Agent.slnx
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~HostRequest|FullyQualifiedName~QuestionRequestManager|FullyQualifiedName~PermissionRequestManager"
dotnet vstest tests/capabilities/Seeing.Agent.Tools.Editor.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Editor.Tests.dll
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~HostRequest"
```
Expected: 全 PASS

- [ ] **Step 6: 手工冒烟（启动 WebUI）**

Run: `dotnet run --project samples/Seeing.Agent.WebUI`
手工验证：打开会话 → 打开文件面板 → 打开一个 `.cs` 文件 → 在 agent 中触发 `editor_list_commands`（应返回命令列表）→ 触发 `editor_command` 且 `commandId=editor.action.formatDocument`（应格式化并标记 dirty）→ `editor_query` `kind=selection`（应返回选区 JSON）。

- [ ] **Step 7: 提交**

```bash
git add Seeing.Agent.slnx samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj samples/Seeing.Agent.WebUI/Components/SessionFilePanel.razor samples/Seeing.Agent.WebUI/Program.cs samples/Seeing.Agent.WebUI/Shared/MainLayout.razor
git commit -m "feat(webui): wire host request dispatcher, editor panel registration and module"
```

---

## 关键审查点（仅此处审查，不逐步骤审查）

| 审查点 | 时机 | 审查内容 |
|---|---|---|
| **A** | WS0+WS1 完成 | 基类重构回归全绿；Abstractions 契约签名满足 WS2–WS5 |
| **B** | WS2–WS5 完成 | 各 WS 接口可对接（`IHostRequestManager` / `HostRequestActivePanelRegistry` / `FilePanelHost` 转发方法） |
| **C** | WS6 完成 | 全量构建 + 三组测试 + 冒烟 |

## 自审记录（写计划后核对）

- **Spec 覆盖**：§4/§5/§6（机制）→ WS1–WS4、WS6；§7（工具契约）→ WS3；§9（权限）→ WS2；§10（SKILL）→ WS3；Step 1（§2.1）→ WS0；B1（presenter 接入）→ WS4 Step 4；B2（登记所有者）→ WS6 Step 2。均有点对点任务。
- **占位扫描**：无 TBD/TODO；每个改码步骤均给出代码。
- **类型一致性**：`HostRequest`/`HostResponse`/`HostResponseStatus`/`IHostRequestManager`/`IHostSurfaceRegistry` 在 WS1 定义，WS2–WS6 引用一致；`TryGetLocal`/`HasAny` 在 WS4 定义、WS6 使用一致；`HostResponsePayload`/`CommandOutcome` 在 WS5 定义、WS6 使用一致。
- **已知偏差**：`FileEditor.QueryAsync` 的 BlazorMonaco API 名称（`GetSelection`/`GetPosition`/`GetValue`/`GetVisibleRanges`/`Global.GetModelMarkers`）以实施时编译为准，若签名差异按实际调整（WS5 为唯一触点）。
