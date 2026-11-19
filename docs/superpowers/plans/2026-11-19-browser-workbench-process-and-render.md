# 共享浏览器宿主与统一渲染管线 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每个浏览器 profile 由一个宿主持有 Chromium，多个 Seeing 实例通过 CDP 连接、各自开独立窗口页面；画面改成 flow + still 双源、单一绘制管线。

**Architecture:** 不新增独立产物——各宿主程序在 `Main` 开头识别 `--browser-host`，交给 `BrowserHostRunner`。宿主用 `.browser-host.lock` 单例、`--remote-debugging-port=0` 暴露 loopback endpoint、Windows Job(assign self) / POSIX watchdog 保证宿主死则浏览器死。客户端只 `ConnectAsync`，只 `Disconnect()`。页面统一用 `CreatePageOptions{Type=Window}` 创建，否则后台页 screencast 零帧。

**Tech Stack:** .NET 10、PuppeteerSharp 25.15.0、Blazor Server、原生 JS、xUnit + FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-19-browser-workbench-process-and-render-design.md`

## Global Constraints

- 能力包只依赖 Abstractions + 原语；`BrowserHostRunner` 放在 `samples/Seeing.Agent.HostComposition`。
- 帧协议：小端、18 字节头、`version = 1`；still 帧的 `seq` 字段承载 `rev`。
- 坐标权威 = `state.viewportWidth/Height`（实际 SetViewport 的 CSS 尺寸）。
- 客户端**禁止** `browser.CloseAsync()`；只用 `Disconnect()`。
- 客户端**禁止**遍历 `browser.PagesAsync()` 或挂 `TargetCreated` 做反检测。
- `AtomicFileWrite`、锁内不阻塞、测试禁止固定等待。
- 本地只 `git add` / `git commit`，不 push。
- 测试约定：
  - 真实浏览器/宿主用例：`[Trait("Category","RequiresBrowser")]`，`SEEING_BROWSER_TESTS=1` 才执行（沿用现有门禁）。
  - Windows-only 用例运行时 `return`。
  - 共享辅助 `tests/.../TestSupport/TestWait.cs`、`TestHost.cs`。
  - 运行：`dotnet build tests/capabilities/Seeing.Agent.Tools.Browser.Tests`，再
    `dotnet vstest tests/capabilities/Seeing.Agent.Tools.Browser.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Browser.Tests.dll --TestCaseFilter:"FullyQualifiedName~<类名>"`

## Review Focus

1. 两个实例各开页面时，两路 screencast 都 ≥25fps（每页一窗口）。
2. 客户端断开/热重载不得关闭共享浏览器。
3. 宿主被强杀后 Chromium 整棵树退出（Job assign self / watchdog）。
4. UI 常开但不用浏览器时，宿主能在 `HostIdleSeconds` 后退出（锁探测，不用心跳）。
5. 滚动后 still 帧抓到当前视口；流重建后画面继续更新（epoch）。
6. 同机任意进程可控制浏览器——文档与文案如实说明。

---

## 里程碑 A：共享宿主

### Task A1: 每页一窗口

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserPageFactory.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserSessionHost.cs:198-213`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserPageFactoryTests.cs`（新建）；`BrowserMultiWindowFrameRateTests.cs`（新建，RequiresBrowser）

**Interfaces:**
- Produces:
  - `public static class BrowserPageFactory`
  - `public static CreatePageOptions Options()` → `new CreatePageOptions { Type = CreatePageType.Window }`
  - `public static Task<IPage> CreateAsync(IBrowser browser, IBrowserContext? context, CancellationToken ct)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Options_ShouldCreateWindow()
    => BrowserPageFactory.Options().Type.Should().Be(CreatePageType.Window);
```

- [ ] **Step 2: 运行确认失败**

Expected: FAIL，类型不存在。

- [ ] **Step 3: 实现并接入**

`CreateAsync`：`context is null ? browser.NewPageAsync(Options()) : context.NewPageAsync(Options())`。
`BrowserSessionHost.GetPageAsync` 的两处建页（DefaultContext / 隔离 context）改用它；移除直接 `NewPageAsync()`。

- [ ] **Step 4: 多路帧率验收**

RequiresBrowser 用例：同一 browser 建两个窗口页，各开 screencast、各 2 秒统计帧数，断言两路都 ≥25fps（后台页不掉到 0）。

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser/Engine tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserPageFactoryTests.cs tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserMultiWindowFrameRateTests.cs
git commit -m "feat(browser): create one window per page so background screencast keeps streaming"
```

---

### Task A2: 进程宿主 + Job(assign self)

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserProcessHost.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserJobObject.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserProcessHostTests.cs`（新建）

**Interfaces:**
- Produces:
  - `public sealed class BrowserProcessHost : IAsyncDisposable`
  - `public static Task<BrowserProcessHost> StartAsync(BrowserOptions options, CancellationToken ct)`
  - `public IBrowser Browser { get; }` / `string Endpoint` / `int BrowserPid` / `DateTimeOffset BrowserStartedAt`
  - `internal static string? ParseDevToolsEndpoint(string fileContent)`
  - `public sealed class BrowserJobObject : IDisposable`
  - `public static BrowserJobObject? Create()`（Windows；`KILL_ON_JOB_CLOSE`）
  - `public bool TryAssign(int processId)`
  - `public static BrowserJobObject? AssignCurrentProcess()`（仅宿主进程调用）

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ParseDevToolsEndpoint_ShouldBuildLoopbackUrl()
    => BrowserProcessHost.ParseDevToolsEndpoint("52341\n/devtools/browser/abc")
        .Should().Be("ws://127.0.0.1:52341/devtools/browser/abc");

[Fact]
public async Task JobObject_WhenDisposed_ShouldKillAssignedChild()
{
    if (!OperatingSystem.IsWindows()) return;
    using var job = BrowserJobObject.Create();
    job.Should().NotBeNull();
    using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 60 /nobreak")
    { CreateNoWindow = true, UseShellExecute = false })!;
    job!.TryAssign(child.Id).Should().BeTrue();
    job.Dispose();
    (await TestWait.UntilAsync(() => child.HasExited, TimeSpan.FromSeconds(5))).Should().BeTrue();
}
```

> **测试不得 `AssignCurrentProcess`**：把测试进程放进带 `KILL_ON_JOB_CLOSE` 的 job，dispose 会杀死测试进程自己。

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- 宿主进程启动时先 `BrowserJobObject.AssignCurrentProcess()`（失败记 Warning，POSIX 由 watchdog 兜底）。
- `StartAsync`：`LaunchOptions { Headless, Args, ExecutablePath, UserDataDir, Pipe = false }`；轮询 `<profile>/DevToolsActivePort`（≤10s）解析 endpoint。
- `DisposeAsync`：`CloseAsync(2s)` → 未退出 `Kill(true)` 等 3s → 释放 job。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserProcessHost.cs src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserJobObject.cs tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserProcessHostTests.cs
git commit -m "feat(browser): process host with tcp endpoint and self-assigned job"
```

---

### Task A3: 发现文件、宿主锁、客户端锁与页面归属

**Files:**
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserHostLocator.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserHostLock.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserClientPresence.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserHostLocatorTests.cs`（新建）

**Interfaces:**
- Produces:
  - `public sealed record BrowserHostInfo(int Version, string Generation, int HostPid, DateTimeOffset HostStartedAt, string ProfileDirectory, int? BrowserPid, DateTimeOffset? BrowserStartedAt, string Endpoint, string? LastError, BrowserHostEffective? Effective)`
  - `public sealed record BrowserHostEffective(bool Headless, string? ExecutablePath, string? UserAgent, bool AntiDetection, string[] ExtraArgs, int HostIdleSeconds)`
  - `BrowserHostLocator(string profileDirectory, string? baseDirectory = null)`：`Read/Write/TryDelete/ComputeInfoPath/VersionMatches/IsSameHost(info, hostPid, hostStartedAt)`
  - `BrowserHostLock.TryAcquire(string profileDirectory) → BrowserHostLock?`
  - `BrowserClientPresence`：`static Start(profileDirectory)`、`AcquirePage(string targetId)`、`ReleasePage(string targetId)`、`Dispose()`、`static IReadOnlyList<int> AliveClientPids(string profileDirectory)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void HostLock_WhenHeld_SecondAcquireFails()
{
    var dir = NewDir();
    using var first = BrowserHostLock.TryAcquire(dir);
    first.Should().NotBeNull();
    BrowserHostLock.TryAcquire(dir).Should().BeNull();
}

[Fact]
public void ClientPresence_WhenDisposed_ShouldBeDetectedAsDead()
{
    var dir = NewDir();
    var presence = BrowserClientPresence.Start(dir);
    var pid = Environment.ProcessId;
    BrowserClientPresence.AliveClientPids(dir).Should().Contain(pid);
    presence.Dispose();
    BrowserClientPresence.AliveClientPids(dir).Should().NotContain(pid);
}

[Fact]
public void InfoPath_ShouldBeStablePerProfile()
    => BrowserHostLocator.ComputeInfoPath(@"C:\p\A", @"C:\base")
        .Should().Be(BrowserHostLocator.ComputeInfoPath(@"C:\p\A", @"C:\base"));
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- 宿主锁：`<profile>/.browser-host.lock`，`OpenOrCreate + FileShare.None`，**永不删除**。
- 客户端锁：`~/.seeing/browser/clients/<profileHash>/<pid>.lock`，`FileShare.None`；释放即删除。
- 页面清单：同目录 `<pid>.pages.json`，原子写 `{ "targetIds": [] }`。
- 发现文件：`~/.seeing/browser/host-<profileHash>.json`，含 `generation`/`lastError`/`effective`；`TryDelete` 校验 `HostPid == 当前进程`；`IsSameHost` 同时比对 PID 与 `HostStartedAt`。
- `ComputeProfileHash` 基于**绝对 profile 路径**（宿主始终有固定目录，与 `PersistLogin` 无关）。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserHostLocator.cs src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserHostLock.cs src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserClientPresence.cs tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserHostLocatorTests.cs
git commit -m "feat(browser): discovery, host lock and client presence"
```

---

### Task A4: 宿主运行器、子命令、日志、崩溃退出

**Files:**
- Create: `samples/Seeing.Agent.HostComposition/BrowserHostRunner.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserProcessProbe.cs`
- Create: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/TestSupport/TestHost.cs`
- Modify: `samples/Seeing.Agent.Desktop/Program.cs`、`samples/Seeing.Agent.WebUI/Program.cs`、`samples/Seeing.Agent.Tui/Program.cs`、`samples/Seeing.Agent.Cli/Program.cs`（`Main` 开头分发）
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserHostSmokeTests.cs`（新建，RequiresBrowser）

**Interfaces:**
- Produces:
  - `public static Task<int> BrowserHostRunner.RunAsync(string[] args, CancellationToken ct)`
  - CLI：`--browser-host --profile <dir> [--idle-seconds N] [--headless <bool>] [--executable <path>] [--user-agent <ua>] [--extra-arg <arg>]...`；`--watchdog <hostPid> <browserPid>`
  - `TestHost.Start(string profileDirectory, int idleSeconds = 300) → Process`；`TestHost.Cleanup(string profileDirectory)`
  - 日志：`~/.seeing/browser/host-<hash>.log`（滚动 5MB × 2）

- [ ] **Step 1: 写失败测试（集成）**

```csharp
[Trait("Category", "RequiresBrowser")]
public async Task Host_ShouldPublishAndDieWithBrowser()
{
    if (!BrowserTestGate.Enabled) return;
    var profile = NewProfile();
    using var host = TestHost.Start(profile);   // 内部：dotnet run --project samples/Seeing.Agent.Cli -- --browser-host --profile <dir> --idle-seconds 300
    var locator = new BrowserHostLocator(profile, profile);
    var info = await TestWait.UntilAsync(() => locator.Read(), TimeSpan.FromSeconds(20));
    info.Should().NotBeNull();

    var browserPid = info!.BrowserPid!.Value;
    host.Kill(false);
    (await TestWait.UntilAsync(() => !ProcessExists(browserPid), TimeSpan.FromSeconds(5)))
        .Should().BeTrue();
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- `RunAsync`：watchdog 分支；正常分支：`BrowserHostLock.TryAcquire`（失败退出 0）→ 从参数解析 `BrowserHostEffective` → `BrowserProcessHost.StartAsync`（先 assign self job）→ 写发现文件（含 `effective`）→ 日志 → 每 30s 回收死客户端页面 → 空闲判定 → `finally` 校验后删发现文件。
- **profile 锁恢复（I1）**：`StartAsync` 抛 `ProcessException` 且消息含 `already running` / `ProcessSingleton` 时，用 `BrowserProcessProbe.FindByProfileDirectory(profile)` 找占用进程：父进程已死 → 结束整树后重试（最多 15s）；父进程存活 → 退出 1，把 PID 写进 `lastError`。不得把原始 “Failed to launch browser!” 直接透出。
- `browser.Disconnected` → 写 `lastError` → 返回非 0（进程退出，Job/watchdog 清浏览器）。
- 各 `Program.cs`：`if (args.Contains("--browser-host") || args.Contains("--watchdog")) return await BrowserHostRunner.RunAsync(args, default);`
- POSIX：spawn 同一 exe `--watchdog <hostPid> <browserPid>`。
- `TestHost.Start`：`dotnet run --project samples/Seeing.Agent.Cli --no-build -- --browser-host --profile <dir> --idle-seconds <n>`；`Cleanup`：读发现文件拿 hostPid → Kill → 等进程消失（条件轮询）。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.HostComposition samples tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserHostSmokeTests.cs tests/capabilities/Seeing.Agent.Tools.Browser.Tests/TestSupport/TestHost.cs
git commit -m "feat(browser): --browser-host entry with logging and crash exit"
```

---

### Task A5: 客户端连接与断开

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserEngine.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/BrowserModule.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserSessionHost.cs`（页面创建/关闭时更新 presence）
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserEngineConnectTests.cs`（新建，RequiresBrowser）

**Interfaces:**
- Produces:
  - `BrowserEngine.GetBrowserAsync`：读发现 → 1s TCP 探活 → `ConnectAsync`；失败拉起宿主 → 15s 内每 500ms 重试（读发现 → 探活 → 必要时再拉起）
  - `BrowserEngine.ShutdownAsync`：`browser.Disconnect()`
  - `internal static (string FileName, string[] PrefixArgs) ResolveHostCommand(string profileDirectory)`：优先 `AppContext.BaseDirectory` 下同名 apphost；否则 `dotnet <entry.dll>`；参数含 `--browser-host --profile` 与宿主级配置
  - `BrowserClientPresence` 对象随进程存活；锁句柄按页面计数获取/释放

- [ ] **Step 1: 写失败测试**

```csharp
[Trait("Category", "RequiresBrowser")]
[Fact]
public async Task Shutdown_ShouldNotCloseSharedBrowser()
{
    if (!BrowserTestGate.Enabled) return;
    var profile = NewProfile();
    await using var engine = new BrowserEngine(Options(profile));
    await engine.GetBrowserAsync();
    var endpoint = engine.CurrentEndpointForTest!;

    await engine.ShutdownAsync();
    await using var second = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = endpoint });
    (await second.NewPageAsync()).Should().NotBeNull();
    TestHost.Cleanup(profile);
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- 删除客户端 `LaunchAsync` 主路径与 `ApplyAntiDetectionAsync(browser, options)`（遍历全浏览器会污染别人）；UA 由宿主参数传入。
- `DisposeBrowserQuietlyAsync` → `browser.Disconnect()`；这是唯一释放通道。
- 拉起宿主：`ResolveHostCommand(profile)`（apphost 或 `dotnet <entry.dll>`）+ 宿主级配置参数，`CreateNoWindow`。拉起后进入 15s 重试循环，**每次都重新读发现→探活→必要时再拉起**（覆盖“旧宿主正在退出、锁未释放”的窗口）。
- `BrowserSessionHost` 在创建/关闭页面时 `BrowserClientPresence.AcquirePage/ReleasePage`；presence 对象随进程，值为 0 时释放锁句柄，再次开页重新获取。
- 客户端把 `effective` 与本地 `BrowserOptions` 比较；不一致只提示“修改需重启浏览器”，不自动重启。`ComputeSignature` 改为基于 `BrowserHostEffective` 的比较，`BrowserRestartSignatureTests` 同步改为“effective 变化 → 提示重启”。
- `BrowserLifetimeService.StopAsync` 只断开。

- [ ] **Step 4: 运行确认通过；改 `BrowserPersistLoginEndToEndTests` 为“断开重连”并在 finally 清理宿主**

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser tests/capabilities/Seeing.Agent.Tools.Browser.Tests
git commit -m "feat(browser): connect to shared host and never close it"
```

---

### Task A6: 空闲退出、迁移、设置卡

**Files:**
- Modify: `samples/Seeing.Agent.HostComposition/BrowserHostRunner.cs`
- Create: `samples/Seeing.Agent.WebUI/Components/Settings/BrowserSettingsCard.razor`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserMigrationCleanupTests.cs`（新建）

**Interfaces:**
- Produces:
  - `BrowserProcessProbe.FindByProfileDirectory(...)`、`CommandLineUsesProfile(...)`
  - 设置卡：显示 profile/宿主 PID/generation/endpoint/lastError；按钮“重启浏览器”

- [ ] **Step 1: 写失败测试**

```csharp
[Theory]
[InlineData("msedge.exe --user-data-dir=C:\\p\\prof", @"C:\p\prof", true)]
[InlineData("msedge.exe --user-data-dir=C:\\other", @"C:\p\prof", false)]
public void CommandLineUsesProfile_ShouldMatch(string cmd, string dir, bool expected)
    => BrowserProcessProbe.CommandLineUsesProfile(cmd, dir).Should().Be(expected);
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- 空闲：`AliveClientPids().Count == 0` 持续 `HostIdleSeconds` → 退出；**不判断页面数**。
- 迁移：旧 `.seeing-engine.pid` 存活 → 退出 1；父进程已死的孤儿 → 清理。
- 设置卡：`POST /browser/host/restart`（杀宿主 PID，下次 `browser_open` 重拉）；文案标明浏览器级设置由宿主持有。

- [ ] **Step 4: 手工验收**

UI 常开但不开浏览器 → 30 分钟后宿主退出；旧版本残留能清理；设置页显示宿主状态。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.HostComposition src/capabilities/Seeing.Agent.Tools.Browser samples/Seeing.Agent.WebUI tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserMigrationCleanupTests.cs
git commit -m "feat(browser): idle exit, migration and settings card"
```

---

## 里程碑 B：统一渲染管线

### Task B1: 帧协议与 epoch

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Screencast/BrowserFrameTypes.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Screencast/BrowserFrameCodec.cs`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserFrameProtocolTests.cs`（新建）

**Interfaces:**
- Produces:
  - `public enum BrowserFrameKind : byte { Flow = 1, Still = 2 }`
  - `public sealed record BrowserFrame(byte[] Data, long Seq, int Width, int Height, BrowserFrameKind Kind = BrowserFrameKind.Flow, uint Epoch = 0)`
  - `BrowserFrameCodec.HeaderSize = 18`；`Encode(BrowserFrame)`；`TryDecode(ReadOnlySpan<byte>, out BrowserFrame)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Encode_ShouldWriteLittleEndianHeader()
{
    var b = BrowserFrameCodec.Encode(new BrowserFrame([1,2,3], 7, 1000, 750, BrowserFrameKind.Still, 9));
    b.Length.Should().Be(21);
    b[0].Should().Be(2);
    b[1].Should().Be(1);
    BitConverter.ToUInt32(b, 2).Should().Be(9);
    BitConverter.ToUInt32(b, 6).Should().Be(7);
    BitConverter.ToUInt16(b, 10).Should().Be(1000);
    BitConverter.ToUInt16(b, 12).Should().Be(750);
    BitConverter.ToUInt32(b, 14).Should().Be(3);
}

[Fact]
public void TryDecode_ShouldRoundTrip()
{
    var f = new BrowserFrame([9,8], 1, 10, 20, BrowserFrameKind.Flow, 3);
    BrowserFrameCodec.TryDecode(BrowserFrameCodec.Encode(f), out var back).Should().BeTrue();
    back.Should().Be(f);
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**（`BinaryPrimitives`，小端）

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser/Screencast tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserFrameProtocolTests.cs
git commit -m "feat(browser): binary frame protocol with epoch"
```

---

### Task B2: epoch、still 源、ack 精确一次、二进制发送

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Screencast/BrowserScreencastHub.cs`
- Create: `src/capabilities/Seeing.Agent.Tools.Browser/Screencast/BrowserStillSource.cs`
- Modify: `samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs:663-751`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserScreencastPipelineTests.cs`（新建）

**Interfaces:**
- Produces:
  - `SessionStream.Epoch`（全局递增，**构造 flow 帧与 state 时必须填**）
  - `BrowserScreencastHub.TryCaptureStillAsync(sessionId, rev, ct)`
  - `FramePump.Offer(frame, ack)`；`FramePump` 改 `internal` + `CreateForTest`
  - `WebSocketFrameSink` 用 `Channel(1, DropOldest)`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task FramePump_EveryFrame_ShouldAckExactlyOnce()
{
    var acks = 0;
    var pump = FramePump.CreateForTest(_ => { });
    pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks));
    pump.Offer(new BrowserFrame([2], 2, 1, 1), () => Interlocked.Increment(ref acks));
    await pump.DrainAsync();
    acks.Should().Be(2);
}

[Fact]
public async Task FramePump_WhenBroadcastThrows_ShouldStillAck()
{
    var acks = 0;
    var pump = FramePump.CreateForTest(_ => throw new InvalidOperationException());
    pump.Offer(new BrowserFrame([1], 1, 1, 1), () => Interlocked.Increment(ref acks));
    await pump.DrainAsync();
    acks.Should().Be(1);
}

[Fact]
public async Task TryCaptureStill_WhenHighResStream_ShouldReturnNull()
{
    var hub = new BrowserScreencastHub(new TestOptionsMonitor<BrowserOptions>(
        new BrowserOptions { HighResStream = true }));
    (await hub.TryCaptureStillAsync("x", 1, CancellationToken.None)).Should().BeNull();
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- `SessionStream.Epoch`；flow 帧构造传 epoch；`PublishState`/`StateOf` 填 epoch。
- `BrowserStillSource`：优先 `ScreenshotDataAsync`；降级 `getLayoutMetrics` + `CaptureScreenshot{clip: pageX/pageY}`；800ms；宽高 = `round(ViewportWidth × PixelRatio)`。
- ack：解析失败/空帧/无 sessionId 立即 ack；成功帧 `try/finally` 恰好一次；覆盖时 ack 旧帧。
- 端点：Binary 发送 `BrowserFrameCodec.Encode`。

- [ ] **Step 4: 运行确认通过**

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser/Screencast samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserScreencastPipelineTests.cs
git commit -m "feat(browser): epoch, still source and exactly-once ack"
```

---

### Task B3: dpr、state、hub 的 maxWidth

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Engine/BrowserPageHandle.cs:239-255, 851-871`
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Screencast/BrowserScreencastHub.cs:342-354`
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/BrowserOptions.cs`
- Modify: `src/capabilities/Seeing.Agent.Tools.Browser/Screencast/BrowserFrameTypes.cs`
- Modify: `samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs:473-493, 753-757`
- Test: `tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserViewportResolutionTests.cs`（新建）

**Interfaces:**
- Produces:
  - `BrowserPageHandle.PixelRatio: double`；`ResizeAsync(int, int, double, ct)`
  - `internal static (int ViewportWidth, int ViewportHeight, double FrameScale) ResolveViewport(int, int, double, bool highRes)`
  - `BrowserPageState(..., uint Epoch = 0, int ViewportWidth = 0, int ViewportHeight = 0, double FrameScale = 1, string Mode = "normal")`

- [ ] **Step 1: 写失败测试**

```csharp
[Theory]
[InlineData(800, 600, 1.25, false, 800, 600, 1.25)]
[InlineData(800, 600, 1.25, true, 1000, 750, 1.0)]
public void ResolveViewport_ShouldHonorMode(int w, int h, double dpr, bool hr, int ew, int eh, double es)
{
    var r = BrowserPageHandle.ResolveViewport(w, h, dpr, hr);
    r.ViewportWidth.Should().Be(ew);
    r.ViewportHeight.Should().Be(eh);
    r.FrameScale.Should().Be(es);
}
```

- [ ] **Step 2: 运行确认失败**

- [ ] **Step 3: 实现**

- `PixelRatio` → `double`；`ResizeAsync` 设 `DeviceScaleFactor`；删除 `Render*`。
- hub `StartCoreAsync` 的 `maxWidth/maxHeight` 改用 `page.ViewportWidth/Height`。
- endpoints 不 round；state 写 `epoch/viewportWidth/viewportHeight/frameScale/mode`。

- [ ] **Step 4: 运行确认通过；跑 `BrowserRestartSignatureTests`**

- [ ] **Step 5: 提交**

```bash
git add src/capabilities/Seeing.Agent.Tools.Browser samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs tests/capabilities/Seeing.Agent.Tools.Browser.Tests/BrowserViewportResolutionTests.cs
git commit -m "feat(browser): fractional dpr and viewport state"
```

---

### Task B4: 前端绘制、still 请求、超采样、验收

**Files:**
- Modify: `samples/Seeing.Agent.WebUI.Browser/wwwroot/browser-workbench.js`（`getState`/`onmessage`/`drawFrame`/`sendResize`）
- Modify: `samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs`（`hires` 分支）
- Modify: `docs/architecture/42-browser-workbench-release-notes.md`
- Create: `docs/superpowers/plans/2026-11-19-browser-workbench-acceptance.md`

- [ ] **Step 1: 手工基线**

DevTools 确认当前是 JSON 文本帧。

- [ ] **Step 2: 解析 state 与二进制帧**

- `getState` 增加 `epoch/viewportWidth/viewportHeight/frameScale/mode/hiresPendingAt`。
- `onmessage`：`ArrayBuffer` → 解析 18 字节头；`version !== 1 || 18+len !== byteLength` 丢弃；epoch 变化重置 seq。
- kind 分流：flow `seq <= lastFlowSeq` 丢弃并更新；still `seq(rev) < lastFlowSeq` 丢弃，不更新。
- 绘制：`createImageBitmap(new Blob([bytes],{type:'image/jpeg'}))`；backing = `round(viewportWidth × frameScale)`，**只在变化时赋值**。

- [ ] **Step 3: still 请求与超采样**

- `markActivity` 200ms 去抖；`mode === 'normal'` 且 `hiresPendingAt === 0` 时发 `{type:'hires', rev}`；2s 或收到 still 后清零。
- 服务端 `case "hires"` → `TryCaptureStillAsync` → 非 null 推帧。
- `HighResStream` 由 state 的 `mode` 反映；开启时不发 hires。

- [ ] **Step 4: 手工验收 + 文档**

多实例各开页面（两路 ≥25fps）；125% DPI 静止清晰；滚动 still 正确；关页重开继续更新；拖拽无跳变。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI.Browser samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs docs
git commit -m "feat(webui): binary frames, still frames and high-res mode"
```

---

## 依赖顺序

- A1 → A2 → A3 → A4 → A5 → A6
- B1 → B2 → B3 → B4
- A 与 B 可并行；B2 依赖 B1，B4 依赖 B2/B3。

## Review Focus 归属

| Review Focus | 归属任务 |
|---|---|
| 1. 两路画面都 ≥25fps | A1（多窗口帧率验收） |
| 2. 断开不关浏览器 | A5 |
| 3. 宿主死→浏览器死 | A2（Job）、A4（smoke） |
| 4. 空闲退出（锁探测） | A3（presence）、A6 |
| 5. still 视口 / epoch | B2、B3、B4 |
| 6. 安全边界如实说明 | A4（文档）、B4（验收文档） |
