# 浏览器工作台：共享浏览器宿主与统一渲染管线

**日期：** 2026-11-19
**状态：** 设计待批准（第四轮：架构审查后的可行性修正）
**触发问题：**
① 浏览器遗留进程占用 profile，导致后续无法启动；
② 画面不够清晰、不够流畅；
③ 多个 Seeing 实例需要同时驱动同一个浏览器（各自操作不同网页）。

---

## 1. 调研结论（本机实测 / 日志证据）

环境：Windows 11、Edge 157、PuppeteerSharp 25.15.0、net10.0。

### 1.1 进程与多实例

| 实验 | 结果 |
|---|---|
| 同一 profile 第二次启动 | 退出码 21；Puppeteer 抛 `The browser is already running...` |
| 宿主被杀，默认 `--remote-debugging-port` | 浏览器**仍存活** |
| 宿主被杀，`Pipe = true` | 浏览器退出（但 Pipe 不暴露 TCP，多客户端无法连接） |
| 两个独立进程 `ConnectAsync` 同一 endpoint，各自 `NewPageAsync` | ✅ 成立：targetId 不同，互相可操作，关自己的页不影响对方 |
| 客户端 `browser.Disconnect()` | ✅ 只断开；edge 进程数不变；第二个客户端仍能连 |
| 客户端 `browser.DisposeAsync()`（连接态） | ✅ 也不关浏览器 |
| 客户端 `browser.CloseAsync()` | ❌ 瞬间杀光所有 Chromium 进程 |

**结论**：共享宿主必须用 TCP endpoint；“宿主死 → 浏览器死”由 Job Object（Windows）/ watchdog（POSIX）保证。客户端唯一的杀手是 `CloseAsync`，必须禁用。

### 1.2 页面与 screencast（推翻旧假设）

| 实验 | 结果 |
|---|---|
| 默认标签页模式，A 开 screencast，B `NewPageAsync` | A 掉到 **0 帧**；`bringToFront` 切回 A，B 掉到 0 |
| `Target.createTarget{newWindow:true}`（每页一窗口） | A/B 各 **~60fps**，与焦点无关 |
| 隔离 `BrowserContext` 的页面（本身独立窗口） | 各 ~60fps |
| 同一 page 上两个 CDP session 各自 `startScreencast` | 各自 90/89 帧，**互不顶掉** |
| 加 `--disable-backgrounding-occluded-windows` 等 | **不能**救回标签页模式 |

**结论**：
1. **每页必须是一个独立窗口**，否则后台页零帧。
2. `Page.startScreencast` **不是页级单例**（旧设计文档写错了）；“每客户端只对自己的 page 开流”仍然正确，但理由是成本与 ack 归属，不是互斥。

### 1.3 画面清晰度边界

| 场景 | 视口 / dpr | screencast 帧 |
|---|---|---|
| 800×600, dsf=1.25 | 800×600 / 1.25 | **800×600** |
| 1000×750, dsf=1.25 | 1000×750 / 1.25 | **1000×750** |
| `override.scale=2` / `pageScaleFactor=1.25` | 800×600 | **800×600** |

screencast 永远按 CSS 视口出图。抓帧（滚动到 y=1000）：

| 调用 | 输出 | 内容 |
|---|---|---|
| 无 clip | 800×600 | 当前视口 |
| `clip{x:0,y:0,scale:1.25}` | 1000×750 | **文档顶部（错）** |
| `clip{x:pageX,y:pageY,scale:1.25}` | 1000×750 | 当前视口 ✅ |
| `ScreenshotDataAsync()` | 1000×750 | 当前视口 ✅ |

抓帧与 flow 并行实测 5/5 成功（91–129ms），期间 flow 正常。

### 1.4 空闲判定

| 实验 | 结果 |
|---|---|
| 新拉起浏览器 `PagesAsync().Length` | **1**（自带 about:blank），永远不会为 0 |
| 客户端进程被强杀后 `FileShare.None` 锁释放 | **58ms** |
| 心跳文件 + 2 分钟过期 | 休眠/长 GC > 2 分钟会误判为无客户端 |

**结论**：用客户端文件锁判活，不用心跳；宿主退出判据不能包含“页面数为 0”。

### 1.5 安全边界（订正）

`GET http://127.0.0.1:<port>/json/version` **无认证**返回 `webSocketDebuggerUrl`。因此：

> **同机任意进程都能完全控制该浏览器（任意 JS 执行、读取全部 cookie）。** 发现文件权限只能“减少无意发现”，不构成安全边界。真正的隔离只有 `--remote-debugging-pipe` 或本地 token 代理。

这是共享方案已接受的本地风险，必须如实记录。

### 1.6 现有传输问题

base64 + JSON 文本帧、双重丢帧、ack 过早、`PixelRatio` 取整、`ResizeAsync` 未设 `DeviceScaleFactor`、`RenderWidth` 死逻辑、`_seq` 流重建后归零导致前端丢帧。

---

## 2. 目标 / 非目标

### 目标

**进程与多实例**
- G1：UI 实例退出不影响浏览器；浏览器随**宿主**死亡而被回收。
- G2：多个 Seeing 实例同时使用同一浏览器，各自页面互不干扰（各自 ≥25fps）。
- G3：同一 profile 一个宿主；不同 profile 各自独立。
- G4：宿主崩溃、浏览器单独崩溃，客户端下次调用都能自动恢复。
- G5：旧版本遗留孤儿能清理，不误杀仍在使用的实例。
- G6：UI 常开但不用浏览器时，宿主能释放资源；登录态保留。

**渲染（全平台一套）**
- G7：桌面与 WebUI 共用同一套协议与绘制。
- G8：动画/滚动流畅（≥25fps）。
- G9：静止约 200ms 后达到物理像素，且是当前可见视口。
- G10：默认布局语义不变（视口 = 面板 CSS 尺寸）。
- G11：任一源失败退回另一源，不黑屏。
- G12：流重建后画面继续更新。
- G13：渲染管线可扩展。

### 非目标

- 不做 100% 原生 WebView2 内嵌。
- 不共享同一个 page 句柄给多个实例（同一会话多实例并发不承诺）。
- 不改 agent 工具语义、ref 纪元、会话隔离、`PersistLogin` 的 cookie 策略。

### 约束

- 能力包只依赖 Abstractions + 原语；宿主运行器放在 HostComposition。
- 帧协议可扩展（版本 + epoch）；前后端同版本发布。
- 坐标权威 = `state.viewportWidth/Height`。
- 平台相关能力运行时分支、失败可降级。
- `AtomicFileWrite`、锁内不阻塞、测试禁止固定等待。

---

## 3. 总体架构

```
Seeing 桌面 ─┐
Seeing TUI  ─┼── CDP(loopback) ──► 浏览器宿主（同一可执行文件的 --browser-host 模式）
Seeing WebUI─┘                      每个 profile 一个
                                     ├─ Chromium（每会话一个独立窗口）
                                     ├─ Job/watchdog
                                     └─ 发现文件 + 客户端锁
```

**宿主入口**：不新增独立产物。每个既有可执行（Desktop / WebUI / TUI / Cli）在 `Main` 开头检测 `--browser-host`，转交 `BrowserHostRunner`。客户端用**当前进程的可执行文件**拉起宿主，天然同版本。

---

## 4. Part A：共享浏览器宿主

### 4.1 模型

**一个 profile = 一个宿主 = 一个 Chromium = N 个客户端。**
同 profile 共享登录态，各客户端独立页面；不同 profile 各自独立。

### 4.2 宿主运行器

`BrowserHostRunner.RunAsync(args)`：

| 职责 | 说明 |
|---|---|
| 单例 | `<profile>/.browser-host.lock`（`OpenOrCreate + FileShare.None`）；拿不到直接退出 0。**锁文件永不删除** |
| 拉起 Chromium | `--remote-debugging-port=0`；读 `<profile>/DevToolsActivePort` 得 endpoint |
| 进程约束 | Windows：**先把宿主自己 assign 进 job**（再 spawn Chromium，子进程自动入 job）；POSIX：spawn watchdog |
| 发现文件 | `~/.seeing/browser/host-<hash>.json`（含 `generation`、`lastError`） |
| 客户端存活 | 扫描 `~/.seeing/browser/clients/<profileHash>/*.lock`，抢到锁 = 客户端已死 |
| 页面归属 | 读死客户端的 `pages.json`，`Target.closeTarget` 回收 |
| 空闲退出 | 无存活客户端持续 `HostIdleSeconds` 后退出 |
| 崩溃恢复 | `browser.Disconnected` → 记 `lastError` → 退出（由 Job/watchdog 清进程） |
| 日志 | `~/.seeing/browser/host-<hash>.log`，滚动 5MB × 2 |

**为什么 assign self**：实测两条路都能在宿主被杀后 0.5s 内清空；assign self 不依赖 Puppeteer 的 `Process` 句柄，也不依赖 Chromium 内部实现，且自动覆盖所有后建子进程。

### 4.3 页面创建：每页一个窗口

`BrowserPageFactory.CreateAsync(IBrowser browser, IBrowserContext? context, CancellationToken ct)`：

- 统一使用 `CreatePageOptions { Type = CreatePageType.Window }`（PuppeteerSharp 25.15 原生支持；实测两路各 60fps）。
- DefaultContext（PersistLogin=true）：`browser.NewPageAsync(options)`。
- 隔离上下文（PersistLogin=false）：`context.NewPageAsync(options)`。
- 宿主启动参数加 `--disable-backgrounding-occluded-windows`，避免有头模式下窗口被遮挡后节流。

**语义承认**：有头模式下每个会话会打开一个独立的 OS 窗口（不是标签页）。设置在文案里说明。

### 4.4 客户端存活：文件锁，不用心跳

- 客户端在**第一次创建页面**时获取 `clients/<profileHash>/<pid>.lock`（`FileShare.None`），在**最后一个页面关闭/被回收**时释放；`BrowserClientPresence` 对象本身随进程存活，只按页面计数增减锁句柄，再次开页会重新获取。
- 宿主判定：目录里每个 `*.lock`，尝试以 `FileShare.None` 打开；失败 = 客户端存活；成功 = 已死（立即释放该临时句柄）。
- 无过期阈值、无休眠/GC 误杀。PID 复用由“锁冲突”天然处理。
- `lastSeenAt` 心跳删除；发现文件只在启动、page 归属变化、退出时写。

### 4.5 客户端连接

```
① 读发现文件 → 版本匹配 → 短 TCP 探活（GET /json/version，1s）→ 成功 → ConnectAsync
② 失败/过期/探活失败 → 拉起宿主 → 轮询 15s（每 500ms 重试“读发现 → 探活 → 必要时再拉起”）
③ 版本不匹配 → BrowserHostVersionMismatchException（提示重启浏览器）
④ 连接断开 → 清缓存；下次重连
```

**拉起命令**（宿主级配置必须传过去，否则自定义 Edge 路径 / 无头开关全部失效）：

```
<apphost 或 dotnet> <entry> --browser-host --profile <dir>
  [--idle-seconds N] [--headless <true|false>]
  [--executable <path>] [--user-agent <ua>] [--extra-arg <arg>]...
```

- **apphost 解析**：优先 `AppContext.BaseDirectory` 下与本进程同名的 `.exe`；不存在则 `dotnet <entry.dll>`（覆盖 `dotnet run` / `vstest` 场景，`MainModule.FileName` 在这些场景下是 `dotnet.exe`，不可用）。
- `--watchdog` 也必须被各 `Program.Main` 分发，否则 POSIX 兜底会落到正常 UI 启动。
- 客户端**只用 `Disconnect()`**；代码库禁止客户端调用 `CloseAsync`（架构测试用 grep 守住）。
- 反检测分层：浏览器级（UA/参数）归宿主；页面级只对自己 page 注入，**禁止** `PagesAsync()` 遍历或挂 `TargetCreated`。

### 4.6 页面归属与回收

- 客户端创建/关闭页面后，原子写 `clients/<profileHash>/<pid>.pages.json`：`{ "targetIds": [...] }`。
- 宿主每 30s：对无锁客户端，逐个 `Target.closeTarget(targetId)` 后删除其锁/页面清单。
- 宿主退出时关闭整个浏览器（页面随进程消失）。
- 同一会话被两个实例打开：各自页面，互不影响；不承诺共享 page。

### 4.7 崩溃恢复

| 事件 | 恢复 |
|---|---|
| 宿主崩溃/被杀 | Job/watchdog 清 Chromium；发现文件 `generation` 失效；客户端探活失败 → 重新拉起 |
| 浏览器单独崩溃 | 宿主 `Disconnected` → 写 `lastError` → 主动退出 → 客户端重拉 |
| 客户端崩溃 | OS 释放锁；宿主回收其页面 |
| 版本升级 | 同版本可执行文件天然一致；发现文件 `version` 只用于协议断代 |

### 4.8 空闲退出

- 判据：**无存活客户端** 持续 `HostIdleSeconds`（默认 1800）。**不看页面数**。
- 客户端锁的生命周期 = 持有页面期间，所以“UI 常开但没用浏览器”也会释放。
- 退出前关浏览器、写日志、校验 `hostPid == 自己` 后删发现文件；`.browser-host.lock` 永不删。

### 4.9 发现文件

```json
{
  "version": 1,
  "generation": "8f3c1e2a",
  "hostPid": 23456,
  "hostStartedAt": "2026-11-19T10:23:45+08:00",
  "profileDirectory": "C:\\Users\\me\\.seeing\\browser-profile",
  "browserPid": 31420,
  "browserStartedAt": "2026-11-19T10:23:46+08:00",
  "endpoint": "ws://127.0.0.1:52341/devtools/browser/...",
  "lastError": null,
  "effective": {
    "headless": true,
    "executablePath": "C:\\...\\msedge.exe",
    "userAgent": null,
    "antiDetection": true,
    "extraArgs": [],
    "hostIdleSeconds": 1800
  }
}
```

删除前必须校验 `hostPid == 当前宿主`（防止误删后继者的文件）；存活判定必须同时比对 `hostStartedAt`（防 PID 复用）：`BrowserHostLocator.IsSameHost(info, hostPid, hostStartedAt)`。

`effective` 是“宿主实际生效值”。客户端把自己的 `BrowserOptions` 与它比较：不一致时提示“修改需重启浏览器”，**不自动重启**（避免在用户操作中突然换掉浏览器）。

### 4.10 安全边界（如实记录）

- 绑定 `127.0.0.1` + 随机端口，但 `/json/version` 无认证。
- **同机任意进程可完全控制该浏览器**；发现文件权限只减少无意发现。
- 真正隔离需要 Pipe 或本地 token 代理，不在本次范围。

### 4.11 配置归属

- 宿主：`Headless`、`ExecutablePath`、`ExtraArgs`、`UserAgent`、`AntiDetection`、`HostIdleSeconds`。
- 客户端：`HighResStream`、`IdleSeconds`（页面级回收）、视口。
- **`PersistLogin` 只决定页面上下文**（DefaultContext 复用 cookie / 隔离 BrowserContext），**不决定宿主 user-data-dir**。宿主始终使用固定的 `ProfileDirectory`（默认 `~/.seeing/browser-profile`），否则宿主单例键与发现文件 hash 没有稳定身份。
- 发现文件 `effective` 带实际生效值；设置页标明“由宿主持有，修改需重启”。

### 4.12 迁移

旧版本不写发现文件、不用 Job/watchdog：

1. 旧 `.seeing-engine.pid` 指向的进程存活 → 宿主拒绝启动并提示关闭旧实例。
2. PID 不存在但命令行匹配 profile：父进程已死 → 清理；存活 → 拒绝。
3. 完成后写发现文件。迁移逻辑两个版本后删除。

---

## 5. Part B：统一渲染管线

### 5.1 抽象

```
IFrameSource
  FlowSource   : screencast（动态、CSS 分辨率、持续）
  StillSource  : captureScreenshot（静止、物理像素、按需）

BrowserScreencastHub（客户端每会话）
  - epoch（流重建即变）+ seq
  - 静默 200ms 调 StillSource
  - 统一 Frame（kind/epoch/seq/w/h/payload）
  - 任一源失败保留另一源
```

前端一条绘制路径：`onFrame → 仲裁(epoch/kind/rev) → canvas`。

### 5.2 视口与像素比

`PixelRatio: double`；`ResizeAsync(cssW, cssH, dpr)` 设 `DeviceScaleFactor`；删除 `RenderWidth/RenderHeight`；`maxWidth/maxHeight` = 实际视口（或 4096 上限）。

### 5.3 二进制协议

18 字节头：`kind u8 | version u8 | epoch u32 | seq u32 | width u16 | height u16 | length u32 | payload`。

- flow：`seq` 单调。
- still：**`seq` 字段承载 `rev`**（不新增字段）。前端按 `kind` 分流：
  - flow：`seq <= lastFlowSeq` 丢弃；
  - still：`rev < lastFlowSeq` 丢弃，且不推进 `lastFlowSeq`。

### 5.4 背压与 ack

- 被覆盖的帧立即 ack；被处理的帧在广播完成后 ack；`try/finally` 保证**恰好一次**。
- 空帧、解析失败、无 sessionId 也立即 ack。
- 多观看者以泵为准；sink 队列容量 1（DropOldest）。
- 删除 `_writing` 直接丢帧。
- “每客户端只对自己的 page 开流”：因为同页双流会双倍编码/带宽且 ack 归属复杂，不是因为互斥。

### 5.5 StillSource

- 触发：flow/输入/resize 后静默 200ms。
- 优先 `ScreenshotDataAsync`；降级 `getLayoutMetrics` + `captureScreenshot{clip: pageX/pageY}`。
- 800ms 超时；失败返回 null；前端 2s 清“在途”。
- 隔离 context 下实测同样输出物理像素。

### 5.6 state 与 canvas

`state`：`epoch/viewportWidth/viewportHeight/frameScale/mode`。

- `viewportWidth/Height` = 实际 SetViewport 的 CSS 尺寸（超采样 = 面板 × dpr）；坐标与 backing 都以它为准。
- `frameScale` = 帧像素 / viewport CSS。
- `canvas.width = round(viewportWidth × frameScale)`；backing 只在值变化时改。
- epoch 变化重置 seq。

### 5.7 超采样开关

`Browser.HighResStream`（默认 false）：视口 = `css × dpr`、`dsf=1`、`frameScale=1`、不请求 still。下次重启浏览器生效。

---

## 6. 配置项

| 键 | 默认 | 归属 | 说明 |
|---|---|---|---|
| `Browser.HighResStream` | `false` | 客户端 | 超采样；重启浏览器生效 |
| `Browser.IdleSeconds` | `600` | 客户端 | 页面空闲回收 |
| `Browser.HostIdleSeconds` | `1800` | 宿主 | 无客户端后宿主退出 |

其余为常量。

---

## 7. 影响面

新增：

| 路径 | 职责 |
|---|---|
| `samples/Seeing.Agent.HostComposition/BrowserHostRunner.cs` | `--browser-host` / `--watchdog` 入口、单例、拉起、日志、空闲退出 |
| `src/capabilities/.../Engine/BrowserHostLocator.cs` | 发现文件、探活、拉起、版本 |
| `src/capabilities/.../Engine/BrowserHostLock.cs` | 宿主单例锁 |
| `src/capabilities/.../Engine/BrowserClientPresence.cs` | 客户端存活锁 + 页面清单 |
| `src/capabilities/.../Engine/BrowserPageFactory.cs` | 每页一窗口 |
| `src/capabilities/.../Engine/BrowserProcessHost.cs` | 拥有进程 + Job |
| `src/capabilities/.../Engine/BrowserJobObject.cs` | Job（assign self） |
| `src/capabilities/.../Engine/BrowserProcessProbe.cs` | 迁移期探测 |
| `src/capabilities/.../Screencast/BrowserStillSource.cs` | 静止抓帧 |
| `src/capabilities/.../Screencast/BrowserFrameCodec.cs` | 二进制协议 |

修改：

| 文件 | 改动 |
|---|---|
| `Engine/BrowserEngine.cs` | 连接宿主；只 `Disconnect`；移除反检测遍历 |
| `Engine/BrowserSessionHost.cs` | 用 `BrowserPageFactory`；页面关闭时更新归属、必要时释放客户端锁 |
| `Engine/BrowserPageHandle.cs` | `PixelRatio: double`、dsf、删除 `Render*` |
| `Screencast/BrowserScreencastHub.cs` | epoch、ack、still、state |
| `Screencast/BrowserFrameTypes.cs` | `Kind/Epoch/state` |
| `BrowserOptions.cs` | `HighResStream`、`HostIdleSeconds` |
| `BrowserModule.cs` | 注册 locator/presence/factory |
| `samples/*/Program.cs` | `--browser-host` 分发（Desktop/WebUI/TUI/Cli） |
| `samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs` | 二进制、hires、state |
| `samples/Seeing.Agent.WebUI.Browser/wwwroot/browser-workbench.js` | 二进制、epoch、backing、still |
| `samples/Seeing.Agent.WebUI/Components/Settings/BrowserSettingsCard.razor` | 宿主信息、重启、归属提示 |

---

## 8. 验收标准

**进程与多实例**
1. 桌面 + TUI + WebUI 同时各开不同网页，三路 screencast 各 ≥25fps（每页一窗口）。
2. 关闭/强杀一个 UI 实例，其他页面继续可用、帧率不掉。
3. 客户端断开（热重载/停用模块）不关闭浏览器；第二客户端仍能连。
4. 强杀宿主，5 秒内 Chromium 整棵树退出；下次调用自动重拉。
5. 浏览器单独崩溃 → 宿主退出 → 客户端重拉成功。
6. 同一 profile 一个宿主；不同 profile 各自独立。
7. UI 常开但不用浏览器 → 锁释放 → 超过 `HostIdleSeconds` 宿主退出；登录态保留。
8. 客户端崩溃 → 锁释放、页面被宿主回收，无孤儿。
9. 迁移：旧 PID 存活 → 拒绝；已死 → 清理并启动。
10. 版本不匹配 → 明确提示重启浏览器。
11. 宿主日志包含启动参数、endpoint、连接、退出原因。

**渲染**
12. 桌面与 WebUI 行为一致。
13. 静止 300ms 后画布尺寸 = `round(viewportWidth × frameScale)`。
14. 滚动后 still 帧内容与当前视口一致。
15. 流重建（epoch）后画面继续更新。
16. 拖拽面板无跳变/闪烁/黑帧。
17. 帧带宽下降 ≥25%；滚动 ≥25fps。
18. still 失败 2s 内清在途，之后仍能请求。
19. 同一 page 双流各自出帧（确认不是单例），但产品约束仍为每客户端只开自己的流。

---

## 9. 风险与回滚

| 风险 | 控制 |
|---|---|
| 每页一窗口导致有头模式窗口很多 | 默认无头；设置有文案说明；限制单实例并发页面数 |
| 后台窗口被遮挡节流 | `--disable-backgrounding-occluded-windows`；实测窗口模式与焦点无关 |
| 同机任意进程可控制浏览器 | 明确接受；127.0.0.1 + 用户目录只减少无意发现 |
| 锁文件 unlink 竞态 | `.browser-host.lock` 永不删除；删除发现文件前校验 hostPid |
| 客户端误关共享浏览器 | 只允许 `Disconnect()`；grep 架构测试守 `CloseAsync` |
| 反检测污染别人页面 | 禁止 `PagesAsync()` 遍历/`TargetCreated`；只对自己 page 注入 |
| 宿主日志缺失导致不可诊断 | 宿主自带滚动日志 + `lastError` + 设置页展示 |
| 多源状态机 | 统一 hub + epoch/kind/rev 仲裁 + 单测 |

回滚：关闭 `HighResStream` + 停 still；客户端保留“本地 Launch 独占”开关一段时期。

---

## 10. 实施顺序

| 阶段 | 内容 | 预估 |
|---|---|---|
| 0 | 帧协议 + epoch + 单测 | 0.5 天 |
| 1 | 每页一窗口（PageFactory）+ 多路帧率验收 | 1.5–2 天 |
| 2 | 进程宿主 + Job(self) + watchdog | 2–3 天 |
| 3 | 发现文件 + 宿主锁 + 客户端锁 + 页面归属 | 2–3 天 |
| 4 | 宿主入口（子命令）+ 日志 + 崩溃退出 | 1–1.5 天 |
| 5 | 客户端连接（Disconnect/探活/反检测移除） | 1.5–2 天 |
| 6 | 空闲退出 + 迁移 + 设置卡 | 2–2.5 天 |
| 7 | 二进制传输 + ack | 2.5–3.5 天 |
| 8 | dpr/state + still + 前端绘制 | 2.5–3.5 天 |
| 9 | 超采样 + 验收 | 1–1.5 天 |

合计 **17–22.5 人日**。阶段 1–6 与 7–8 可并行。

---

## 11. 留给 builder 的决定

- `Target.createTarget` 后取 `IPage` 的具体方式（`WaitForTargetAsync` vs `PagesAsync` diff）。
- Job assign self 失败时的降级顺序（assign browser pid → watchdog）。
- 发现文件 `generation` 的生成规则。
- still 阈值与“持续动画页面”的排除策略。
- 客户端锁的生命周期细节（按页面计数还是按迭代替换）。

---

## 12. 已确认的取舍

- **多实例共享同一浏览器**：一个 profile 一个宿主，多客户端各自页面。
- **每页一个窗口**：为了 screencast 在后台也出帧；有头模式窗口数增加是已知代价。
- **单二进制子命令**：不新增产物，版本天然一致。
- **进程生死由 OS/看门狗保证**：Job(assign self) + watchdog。
- **渲染多源单一管线**：flow 保流畅、still 保清晰。
- **布局默认不变**：超采样仅作可选开关。
