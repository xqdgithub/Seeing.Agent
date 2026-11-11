# 内嵌浏览器工作台设计（Embedded Browser Workbench）

**日期:** 2026-11-11
**状态:** 已确认决策，待实施
**范围:** 新增能力包 `Seeing.Agent.Tools.Browser`、新增 UI 类库组件 `Seeing.Agent.WebUI.Browser`、新增契约端口 `Abstractions/Sessions/ISessionArtifactStore.cs`、`Seeing.Agent.Files` 之外的第二个 `<c>workbench.dock</c>` 贡献（`browser`）、WebUI 新增 WebSocket 端点与 `Program/WebUiHost` 接线、`BuiltInCapabilitySets.Full` + `FullCapabilityComposition`、文档 42
**前置:** [`2026-11-11-tool-image-parts-design.md`](2026-11-11-tool-image-parts-design.md)（工具产出图片 → `ToolResult.Parts`，会话只存 `LocalPath`，框架出站前物化）
**触发:** 「我想实现一个内嵌的浏览器工作台，agent 可以打开网页，并且操作这个浏览器，类似 cursor 操作浏览器那种的」

---

## 1. 结论先行

**可以实现，且不需要改动工作台平台本体。** 工作台平台（`docs/architecture/41`）已经把「一个带 `WorkbenchViewContext` 参数的组件 + 一条 `WorkbenchContribution` 声明 + 一行 ViewKey 登记」做成全部接入成本；本期新增的只是**一个能力包 + 一个 UI 类库 + 一个 WebSocket 端点**。

Phase 1 已把「工具产出图片」打通（`ToolResult.Parts` → `SessionToolCall.Parts` → `IImagePartResolver` → 三个 client 各自序列化），所以 `browser_screenshot` 可以直接把 PNG 交给多模态模型看，不需要任何新契约。

**本机探针已实测通过**（PuppeteerSharp 25.15.0 + 系统 Edge，见 §11）：无障碍树、`data-seeing-ref` 打标点击、坐标点击、键盘输入、CDP `Page.startScreencast` 帧推送（含 ack）、`DOMContentLoaded` 等待策略全部可用。

---

## 2. 为什么不是 iframe

| 方案 | 致命问题 |
|------|---------|
| **iframe 内嵌** | ① 绝大多数站点发 `X-Frame-Options: DENY` / CSP `frame-ancestors`，页面根本加载不出来；② 跨源 iframe 受同源策略保护，父页面（Blazor Server 服务端）**无法**读取其 DOM、无法派发真实点击事件 |
| **服务端反向代理页面再注入** | 需要重写全部 HTML/CSS/JS、处理 CORS/CSP/WebSocket/证书/登录态，等于自研浏览器；维护成本无上限 |
| **WebView2（桌面专用）** | 只能跑在 Windows 桌面宿主，WebUI（浏览器访问）、Gateway.Server（headless）全部不可用；且 WebView2 不能被 agent 以 CDP 方式驱动到与 puppeteer 同等能力 |

**选定方案：服务端无头（可选有头）真浏览器 + 画面流 + 输入转发。** 这是 browser-use / Playwright MCP / Cursor 浏览器工具的同一条路径：agent 驱动的是真实 Chromium 内核，任何网站都能开，DOM 能力完整。

---

## 3. 引擎选型（已定）

| 项 | 决策 | 理由 |
|----|------|------|
| 驱动库 | **PuppeteerSharp 25.15.0** | 纯 .NET（无 Node 依赖），`lib/net10.0` 原生目标；提供 `Page.ClickAsync` / `ScreenshotAsync` / `EvaluateFunctionAsync`，同时 `CreateCDPSessionAsync` 可下沉到裸 CDP 拿 `Accessibility.getFullAXTree` 与 `Page.startScreencast` |
| 浏览器来源 | **系统 Edge 优先 → Chrome → 已下载的 Chrome-for-Testing** | Windows 10/11 预装 Edge，零下载；不引入 ~200MB 浏览器包。路径探测失败才提示用户配置 |
| 默认模式 | **headless** | 宿主进程无桌面环境（Gateway/服务）也能跑；调试期可通过配置切 headed 观察真实窗口 |
| 进程隔离 | **每会话一个 `BrowserContext`**（同一 browser 进程内隔离 cookie/缓存）；进程级一个 `Browser` | 启动一个 Chromium 进程约 200–400ms、内存 ~80MB，每会话起一个进程在并发会话下不可接受 |

**为什么不 `Puppeteer.LaunchAsync` 每会话一进程：** 会话数一多就是内存炸弹。`Browser.CreateBrowserContextAsync()` 提供等同的 cookie/存储隔离，代价只是共享进程。

---

## 4. 分层与包结构

```
src/abstractions/Seeing.Agent.Abstractions/
  Sessions/ISessionArtifactStore.cs      ← 新契约端口（工具产出二进制落会话资源目录）

src/capabilities/Seeing.Agent.Tools.Browser/          ← 新能力包（模块 id: browser）
  Seeing.Agent.Tools.Browser.csproj
  BrowserModule.cs                        ← ISeeingModule + IUiContributor（声明 workbench 贡献）
  BrowserOptions.cs                       ← 配置节 Browser（headless / executablePath / idleSeconds / viewport / allowPrivateHosts）
  Engine/BrowserEngine.cs                 ← 进程级 Browser 生命周期、可执行文件探测、启动/关闭
  Engine/BrowserSessionHost.cs            ← 每会话 BrowserContext + Page 池、导航状态、idle 回收
  Engine/BrowserPageHandle.cs             ← 单页封装：导航/快照/点击/输入/滚动/等待/截图/求值/前进后退
  Snapshot/BrowserSnapshot.cs             ← 快照模型（元素数组 + 文本渲染）
  Snapshot/BrowserSnapshotBuilder.cs      ← 注入 JS 打标 + 采集（纯逻辑，可单测部分）
  Screencast/BrowserFrameTypes.cs         ← 帧/状态/观看者端口
  Screencast/BrowserScreencastHub.cs      ← 每会话 1 个 CDP screencast 会话 + 帧广播 + 输入注入
  Tools/BrowserOpenTool.cs                ← browser_open
  Tools/BrowserSnapshotTool.cs            ← browser_snapshot
  Tools/BrowserClickTool.cs               ← browser_click
  Tools/BrowserTypeTool.cs                ← browser_type
  Tools/BrowserPressTool.cs               ← browser_press
  Tools/BrowserScrollTool.cs              ← browser_scroll
  Tools/BrowserWaitForTool.cs             ← browser_wait_for
  Tools/BrowserScreenshotTool.cs          ← browser_screenshot
  Tools/BrowserEvalTool.cs                ← browser_eval
  Tools/BrowserBackTool.cs                ← browser_back
  Tools/BrowserForwardTool.cs             ← browser_forward
  Tools/BrowserCloseTool.cs               ← browser_close
  Tools/BrowserToolBase.cs                ← 共享：解析会话页、标准失败文案

samples/Seeing.Agent.WebUI.Browser/                    ← 新 Razor 类库（工作台视图）
  Seeing.Agent.WebUI.Browser.csproj
  _Imports.razor
  Components/BrowserWorkbench.razor        ← 画布 + 地址栏 + 工具栏（WS 客户端逻辑在 JS 模块内）
  wwwroot/browser-workbench.js             ← WS 连接 / 帧解码到 canvas / 鼠标键盘采集与坐标换算
  wwwroot/browser-workbench.css

  （WS 端点与帧服务在 WebUI 宿主；见 §7）

samples/Seeing.Agent.WebUI/
  Endpoints/BrowserStreamEndpoints.cs      ← /api/browser/{sessionId}/stream（WebSocket）
  Services/SessionArtifactStore.cs         ← ISessionArtifactStore 的宿主实现（委托 IToolOutputStore）
  Shell/WebUiWorkbenchViews.cs             ← 加一行 browser 视图登记
  Hosting/WebUiHost.cs                     ← 类库 / DI / 端点 / 视图登记接线
```

**依赖方向：**

| 项目 | 依赖 | 说明 |
|------|------|------|
| `ISessionArtifactStore`（契约） | 仅 BCL | 能力包不能引用 Core 的 `IToolOutputStore`，必须有这条端口 |
| `Seeing.Agent.Tools.Browser` | Abstractions + Tools.Support + **PuppeteerSharp** | 唯一新增第三方依赖；不引用 Core / Hosting |
| `Seeing.Agent.WebUI.Browser` | Abstractions + AntDesign | 不引用能力包（只经 WS + 工作台上下文通信） |
| `Seeing.Agent.WebUI` | 以上 + 已有全部 | 端点与视图登记 |

---

## 5. 契约层新增：`ISessionArtifactStore`

**问题：** `browser_screenshot` 要把 PNG 落盘并交给模型。Phase 1 的规则是「图片只存 `LocalPath`，且 `ImagePartResolver` 只信任**本会话 ref 目录**内的路径」——但 `IToolOutputStore` 住在 `Seeing.Agent.Core`，能力包不能引用。

```csharp
namespace Seeing.Agent.Abstractions.Sessions;

/// <summary>
/// 会话资源目录端口：把工具产出的二进制写进该会话的资源目录，返回可被
/// <c>IImagePartResolver</c> 接受的本地路径。
/// <para>
/// <b>为什么需要它</b>：图片在会话里只存 <c>LocalPath</c>，而物化器只信任本会话资源目录内的路径。
/// 该目录的解析在 Core（随会话存储重定位），能力包只依赖 Abstractions，故需要这条端口。
/// </para>
/// </summary>
public interface ISessionArtifactStore
{
    /// <summary>
    /// 写入二进制产物并返回绝对路径。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="name">逻辑名（用于生成文件名，如 <c>shot</c>）；同名多次调用不覆盖。</param>
    /// <param name="content">内容。</param>
    /// <param name="extension">扩展名（如 <c>.png</c>；不合法回退 <c>.bin</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<string> SaveAsync(
        string sessionId,
        string name,
        ReadOnlyMemory<byte> content,
        string extension,
        CancellationToken ct = default);
}
```

**实现位置：**

| 层 | 实现 | 说明 |
|----|------|------|
| Core（进程级，兜底） | `SessionArtifactStore`（`src/spine/Seeing.Agent.Core/Output/`） | 委托 `IToolOutputStore.SaveBytesAsync(sessionId, name, content, ext, suffix: <epoch>)`；**这层是必需的**，否则 headless/CLI/Gateway 宿主没有实现 |
| WebUI（宿主） | **不要**再写一份 | Core 的实现在 WebUI 里同样可用（`IToolOutputStore` 已注册），宿主只 `MapSessionArtifactStore()` 不需要 |

**唯一生成规则（防覆盖）：** suffix 用单调递增的 epoch 毫秒（`DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`），保证同一 `callId` 多次截图不互相覆盖。

**为什么不用 `ToolResult.Parts.DataBase64` 直接内联：** 会话文件整文件重写，base64 会造成写放大（Phase 1 已确立的原则）。

---

## 6. 能力包 `Seeing.Agent.Tools.Browser`

模块 id **`browser`**，`DependsOn = ["io.local"]`（需要 `IFileSystem` 做可执行文件探测）。

### 6.1 配置节 `Browser`

配置键 `Browser`（`seeing.json`，`ConfigScope.Both`）：

```json
{
  "Browser": {
    "Headless": true,
    "ExecutablePath": null,
    "IdleSeconds": 600,
    "ViewportWidth": 1280,
    "ViewportHeight": 800,
    "AllowPrivateHosts": false,
    "DefaultTimeoutSeconds": 30
  }
}
```

| 字段 | 默认 | 说明 |
|------|------|------|
| `Headless` | `true` | 需要人工观察/登录时设 `false`（有头窗口，仍需 CDP 驱动） |
| `ExecutablePath` | `null` | 显式指定浏览器；`null` 时按 §3 顺序探测 |
| `IdleSeconds` | `600` | 会话页静默多久后回收（0 = 不回收，进程退出时统一关） |
| `ViewportWidth/Height` | `1280×800` | 快照与 screencast 共用同一视口 |
| `AllowPrivateHosts` | `false` | 是否允许导航到内网/回环地址（与 `webfetch` 的 SSRF 策略对齐，默认拒绝） |
| `DefaultTimeoutSeconds` | `30` | 单次操作超时上限 |

### 6.2 工具面：拆开还是单个

**拆开成 12 个工具**，每个工具一个动作。

原因不是「代码整洁」，而是**权限粒度**：`PermissionService` 的判定键是 `(PermissionKind.Tool, toolName)`，**不含参数**。若做成单个 `browser` 工具 + `action` 枚举，用户无法「允许点击、禁止执行 JS」——审批只能整包放行/整包拒绝。拆开后 `browser_eval`（任意 JS，等价于本机任意代码执行）可以与 `browser_snapshot`（只读）分开授权。

| 工具 id | 类别 | 只读 | 摘要 |
|---------|------|------|------|
| `browser_open` | Network | 否 | 打开/切换页面（可指定 URL、viewport、headed 覆盖） |
| `browser_snapshot` | Network | 是 | 输出可交互元素清单（`e1/e2…`）与页面标题/URL |
| `browser_click` | Network | 否 | 按 ref 或坐标点击 |
| `browser_type` | Network | 否 | 聚焦元素并输入文本（可清空、可逐字） |
| `browser_press` | Network | 否 | 按键（Enter/Tab/Escape/方向键/组合键） |
| `browser_scroll` | Network | 否 | 滚动（元素内或页面） |
| `browser_wait_for` | Network | 是 | 等待条件（元素出现/文本出现/网络空闲/固定时长） |
| `browser_screenshot` | Network | 是 | **截图并把 PNG 交给模型看**（Phase 1 链路） |
| `browser_eval` | Network | **高危** | 在页面上下文执行 JS 表达式并返回结果 |
| `browser_back` | Network | 否 | 后退 |
| `browser_forward` | Network | 否 | 前进 |
| `browser_close` | Network | 否 | 关闭该会话浏览器，释放资源 |

#### 6.2.1 ref 优先的元素定位

`browser_snapshot` 会给每个可交互元素打上 `data-seeing-ref="eN"` 并输出清单：

```
页面：Example Domain
URL：https://example.com/
可交互元素 3 个：
e1 link "More information..."
e2 input "搜索" (placeholder)
e3 button "提交"
```

`browser_click(ref="e2")` / `browser_type(ref="e2", text=…)` 用 ref 定位；**不要求**模型写 CSS 选择器（模型经常写错，且选择器随页面结构变化失效）。选择器作为可选后备参数保留（`selector`），用于 ref 已失效但用户明确知道结构的场景。

**ref 的生命周期**：ref 打在 DOM 属性上，**导航/重新渲染后即失效**。失效时工具返回自解释失败：「ref 'e2' 已失效（页面已变化），请重新调用 `browser_snapshot`」。这比静默点到错误元素安全。

#### 6.2.2 工具参数 Schema

所有工具遵循 `docs/architecture/04` §9（R1–R9）。核心几个：

```json
// browser_open
{ "url": "string (必需，http/https 或 about:blank)",
  "waitUntil": "enum[load, domcontentloaded, networkidle] (默认 domcontentloaded)",
  "timeout": "integer (秒，可选)" }

// browser_snapshot
{ "maxElements": "integer (默认 200，上限 500)",
  "includeText": "boolean (默认 true，是否附带可见文本摘要)" }

// browser_click
{ "ref": "string (e1 形式，与 x/y 二选一)",
  "selector": "string (CSS 选择器，ref 失效时的后备)",
  "x": "number (视口坐标)", "y": "number (视口坐标)",
  "button": "enum[left, right, middle] (默认 left)",
  "clickCount": "integer (默认 1)" }

// browser_type
{ "ref": "string (与 selector 二选一)",
  "selector": "string",
  "text": "string (必需)",
  "clear": "boolean (默认 true，先清空)",
  "submit": "boolean (默认 false，输入后按 Enter)",
  "delayMs": "integer (逐字延迟，默认 0)" }

// browser_press
{ "key": "string (必需，如 Enter / Tab / Escape / ArrowDown / Control+A)" }

// browser_scroll
{ "direction": "enum[up, down, left, right] (默认 down)",
  "amount": "integer (像素，默认 600)",
  "ref": "string (可选：在该元素内滚动)" }

// browser_wait_for
{ "kind": "enum[selector, ref, text, networkidle, timeout] (必需)",
  "value": "string (selector/text 时必需)",
  "timeout": "integer (秒，默认 30)" }

// browser_screenshot
{ "fullPage": "boolean (默认 false)",
  "ref": "string (可选：只截该元素)" }

// browser_eval
{ "expression": "string (必需，JS 表达式或 IIFE 函数体)"
  "awaitPromise": "boolean (默认 true)" }

// browser_back / browser_forward
{ "waitUntil": "enum[load, domcontentloaded, networkidle] (默认 domcontentloaded)" }

// browser_close
{ }   // 无参数
```

### 6.3 `BrowserEngine`（进程级）

```csharp
public sealed class BrowserEngine : IAsyncDisposable
{
    // 懒启动：第一次真正需要页面时才启动 Chromium（未使用浏览器的会话零成本）
    Task<IBrowser> GetBrowserAsync(CancellationToken ct);

    /// <summary>探测顺序：配置 → Program Files Edge → Program Files (x86) Edge → Chrome 两个位置 → 已下载 Chromium。</summary>
    string? ResolveExecutablePath();

    /// <summary>进程退出 / 模块 Deactivate 时统一关闭。</summary>
    ValueTask DisposeAsync();
}
```

| 决策 | 说明 |
|------|------|
| **懒启动** | `Browser` 不在模块 Activate 时启动。没人用浏览器就不该有 Chromium 进程 |
| **启动互斥** | `SemaphoreSlim(1,1)` 保护启动；失败缓存 10 秒（避免连续工具调用反复启动失败的重试风暴） |
| **启动参数** | `--no-sandbox --disable-dev-shm-usage --disable-gpu --no-first-run --disable-extensions --disable-background-networking --mute-audio` |
| **崩溃自愈** | 监听 `Browser.Disconnected`；进程意外退出时清空实例并让下一次调用重新启动 |

### 6.4 `BrowserSessionHost`（每会话一页）

```csharp
public interface IBrowserSessionHost
{
    /// <summary>取该会话的页面；不存在则创建（含 BrowserContext）。</summary>
    Task<BrowserPageHandle> GetPageAsync(string sessionId, CancellationToken ct);

    /// <summary>是否存在活动页面。</summary>
    bool HasPage(string sessionId);

    /// <summary>关闭该会话页面与 context。</summary>
    Task CloseAsync(string sessionId, CancellationToken ct);
}
```

**实现要点：**

| 要点 | 说明 |
|------|------|
| 隔离 | 每会话 `Browser.CreateBrowserContextAsync()`；`Page` 在该 context 内创建 → cookie / localStorage / 缓存互不串 |
| 视口 | 创建后立即 `SetViewportAsync(configured)`；screencast 与 snapshot 共用 |
| 状态 | 记 `Url` / `Title` / `LastActivityAt` / `RefEpoch`（每 navigations +1，用于 ref 失效判定） |
| 并发 | 同一会话的浏览器操作**串行**（`SemaphoreSlim(1,1)` per session）。理由：两个工具同时点同一页会互相打断，而 agent 一次只做一件事；串行还避免了 Playwright/Puppeteer 的 page 级竞态 |
| 回收 | `IdleSeconds > 0` 时注册 `ITimer`（Hosting 的 `TimeProvider` 抽象，测试可推进虚拟时间），静默超时后关闭页面与 context。**不关闭 browser 进程** |
| 会话删除 | 监听会话删除事件 → 主动关闭（避免孤儿 context 泄漏内存） |

**为什么每会话只一页（不做多标签）：** 本期 agent 的浏览器任务是线性的；多标签会把「ref 属于哪个标签」「哪个标签是激活的」变成必须进上下文的状态，收益不抵复杂度。列为后续演进。

### 6.5 `BrowserPageHandle`（单页能力）

```csharp
public sealed class BrowserPageHandle
{
    public string SessionId { get; }
    public int RefEpoch { get; }           // 每次导航 +1

    Task<NavigationResult> NavigateAsync(string url, WaitUntil waitUntil, TimeSpan timeout, CancellationToken ct);
    Task<BrowserSnapshot> SnapshotAsync(int maxElements, bool includeText, CancellationToken ct);
    Task ClickAsync(BrowserElementTarget target, MouseButton button, int clickCount, CancellationToken ct);
    Task TypeAsync(BrowserElementTarget target, string text, bool clear, bool submit, int delayMs, CancellationToken ct);
    Task PressKeyAsync(string key, CancellationToken ct);
    Task ScrollAsync(ScrollDirection direction, int amount, BrowserElementTarget? target, CancellationToken ct);
    Task<bool> WaitForAsync(WaitCondition condition, TimeSpan timeout, CancellationToken ct);
    Task<byte[]> ScreenshotAsync(bool fullPage, BrowserElementTarget? target, CancellationToken ct);
    Task<JsonElement?> EvaluateAsync(string expression, bool awaitPromise, CancellationToken ct);
    Task<NavigationResult> BackAsync(WaitUntil waitUntil, CancellationToken ct);
    Task<NavigationResult> ForwardAsync(WaitUntil waitUntil, CancellationToken ct);
}
```

`BrowserElementTarget` 是判别联合（`Ref` / `Selector` / `Coordinates`），解析失败统一返回可读原因：

```csharp
/// <summary>元素定位：ref 优先，selector 后备，坐标最后。</summary>
public readonly record struct BrowserElementTarget
{
    public string? Ref { get; init; }
    public string? Selector { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
}
```

### 6.6 快照生成（`BrowserSnapshotBuilder`）

走**注入 JS 遍历 DOM**，不是 CDP `Accessibility.getFullAXTree`：

| 方案 | 优点 | 缺点 | 结论 |
|------|------|------|------|
| CDP AX 树 | 语义最准（role/name/checked 全在） | `nodeId` 在导航与重新渲染后失效，且无法直接用于 `page.ClickAsync`；需要额外 `DOM.resolveNode` 往返，每元素一次 | 不采用 |
| DOM 遍历 + `data-seeing-ref` | 一次 JS 拿到全部元素；ref 直接可 `querySelector('[data-seeing-ref="eN"]')`；点击/输入零额外往返 | 语义弱于 AX（但 `role`/`aria-label` 已覆盖绝大多数需要） | **采用** |

采集规则（注入 JS 的唯一真相源，实现时按此写）：

```
选择器：a,button,input,select,textarea,[role],[onclick],[contenteditable="true"],summary,label[for]
跳过：不可见（offsetParent === null 且非 position:fixed；或 visibility:hidden/display:none）
      禁用（disabled）标注 (disabled) 但保留，因为模型需要知道「有但点不了」
      aria-hidden="true"
ref：按 DOM 顺序编号 e1..eN，写入 data-seeing-ref
行格式：{ref} {role} "{name}"{state}
  role  = role 属性 || 标签映射（a→link, button→button, input[type=checkbox]→checkbox, input→textbox, select→combobox, textarea→textbox）
  name  = aria-label || placeholder || 关联 label 文本 || value || innerText（trim，截 80 字）
  state = (disabled) / (checked) / (expanded) / (required) 等 aria 状态，空格分隔
上限：maxElements（默认 200），超出标注「还有 N 个元素未列出，可用 browser_eval 或缩小范围」
```

快照输出（工具 `Output`）：

```
页面：Example Domain
URL：https://example.com/
可交互元素 3 个：

e1 link "More information..."
e2 textbox "搜索" placeholder="搜索"
e3 button "提交"

可见文本摘要：
This domain is for use in illustrative examples...
```

---

## 7. 画面流与输入转发

### 7.1 为什么必须走 WebSocket

Blazor Server 的 circuit 走 SignalR，消息大小与频率都受限（默认 32KB 单帧、且与服务端渲染共用一条连接）。把 JPEG 帧塞进去会让整个 circuit 抖动，交互全面卡顿。因此**独立 WebSocket 端点**：

```
GET /api/browser/{sessionId}/stream   (WebSocket upgrade)
```

### 7.2 协议

**下行（服务端 → 浏览器）：**

```jsonc
// 帧
{ "type": "frame", "seq": 42, "mime": "image/jpeg", "data": "<base64>", "width": 1280, "height": 800 }
// 状态
{ "type": "state", "url": "https://…", "title": "…", "busy": false, "refEpoch": 3 }
// 错误
{ "type": "error", "message": "浏览器已关闭" }
```

**上行（浏览器 → 服务端）：**

```jsonc
{ "type": "mouse", "event": "down|up|move", "x": 100.5, "y": 200.0, "button": "left|right|middle" }
{ "type": "wheel", "x": 100, "y": 200, "deltaY": 120 }
{ "type": "key", "event": "down|up", "key": "a", "ctrl": false, "alt": false, "shift": false, "meta": false }
{ "type": "text", "text": "输入的文字" }          // 中文等 IME 输入走这条
{ "type": "navigate", "url": "https://…" }
{ "type": "reload" }
{ "type": "resize", "width": 900, "height": 600 }  // 面板尺寸变化 → 调 viewport
{ "type": "ping" }
```

### 7.3 帧推送（`BrowserScreencastHub`）

| 要点 | 说明 |
|------|------|
| 采集 | CDP `Page.startScreencast`（`format=jpeg, quality=60, maxWidth/maxHeight=viewport, everyNthFrame=1`） |
| **必须 ack** | 每收到一帧立刻 `Page.screencastFrameAck`，否则 Chromium 停止推帧（探针已实测：不 ack 只有 1 帧） |
| **必须有背压** | 只保留最新帧：若上一帧还没写给 WS 就丢掉新帧（丢帧不丢 ack）。否则慢客户端会把内存打满 |
| 空闲 | 无变化时 Chromium 不推帧；前端 `ping` 保持连接 |
| 多个观看者 | 同一会话可能同时有多个 circuit 打开（多标签页）。**每 viewer 一条 WS 连接**，Hub 内部维护 viewer 集合，帧广播；CDP screencast 只有一份 |
| 生命周期 | 第一个 viewer 连上时启动 screencast；最后一个断开后 **1 分钟**停止（避免刷新页面导致反复启停） |

### 7.4 输入转发与坐标换算

前端 `canvas` 尺寸与 viewport 不一定相等（面板可拖宽、窄屏覆盖）。**换算在 JS 侧完成**：

```
viewportX = (canvasX / canvasWidth) * viewportWidth
viewportY = (canvasY / canvasHeight) * viewportHeight
```

服务端收到坐标后直接 `page.Mouse.MoveAsync(x, y)` / `ClickAsync(x, y)`。

| 输入 | 服务端动作 |
|------|-----------|
| `mouse down/up` | `page.Mouse.DownAsync/MoveAsync/UpAsync(button)` |
| `wheel` | `page.Mouse.WheelAsync(0, deltaY)` |
| `key down/up` | `page.Keyboard.DownAsync(key)` / `UpAsync(key)`（含修饰键状态机） |
| `text` | `page.Keyboard.TypeAsync(text)`（IME 提交的整段文本） |
| `navigate` | `NavigateAsync(url, DOMContentLoaded)`，结果以 `state` 下行 |
| `resize` | `page.SetViewportAsync(w, h)` + 重启 screencast（新尺寸） |

**并发纪律**：输入事件与工具调用共用**同一把会话级互斥锁**。人在 canvas 上点击与 agent 调 `browser_click` 不能交错执行——否则鼠标状态机错乱（按下没抬起）。

**冲突提示**：agent 正在操作时（工具执行中）前端画布显示半透明遮罩 + 「正由智能体操作」，此时用户输入仍被转发但会排队；用户手动操作后，`state.refEpoch` 变化会通过 `state` 下行，agent 下次 `browser_snapshot` 自然看到新页面。

---

## 8. 权限与安全

| 面 | 决策 |
|----|------|
| 工具审批 | 12 个工具各自一条 `(PermissionKind.Tool, toolName)` 规则。建议默认规则：`browser_snapshot`/`browser_screenshot`/`browser_wait_for` 允许；`browser_open`/`browser_click`/`browser_type`/`browser_press`/`browser_scroll`/`browser_back`/`browser_forward`/`browser_close` 询问；`browser_eval` **必须询问**且不提供「本会话一直允许」以外的宽免 |
| 内网访问 | `browser_open` 默认拒绝回环/私网/链路本地地址（与 `webfetch` 的 SSRF 策略同源）；`Browser.AllowPrivateHosts=true` 才放行（本地开发需要） |
| 非 http(s) 协议 | 允许 `about:blank`；`file:` / `javascript:` / `data:` 默认拒绝（`data:` 允许作为「无网络页面」调试手段，但仅当 `url` 以 `data:text/html` 开头且长度 < 256KB） |
| 下载 | 关闭下载（`Page.SetRequestInterceptionAsync` 不启用，改为 `BrowserContext` 的下载行为设为 `Deny`），避免 agent 把文件写到未知位置 |
| 弹窗 | `page.Dialog` 一律 `Dismiss`（alert/confirm/prompt），并在工具输出里告知模型「弹窗被自动关闭」 |
| 新窗口 | `TargetCreated` → 关闭并提示「页面尝试打开新窗口，已被阻止」 |
| `browser_eval` 边界 | 在页面上下文执行，**不能**访问 Node/文件系统（headless Chromium 无 Node 绑定）；但仍可读页面数据、发起请求——因此保持高危标注 + 强制审批 |
| 凭据 | 不注入任何 cookie/token；用户需要登录时用 `Headless=false` 手动登录（cookie 留在该会话 context 内，随会话关闭销毁） |

---

## 9. 工作台视图

### 9.1 贡献声明（能力包 `DescribeUi()`）

```csharp
new WorkbenchContribution
{
    Point = WorkbenchPoints.DockId,
    Id = "browser",
    Title = "浏览器",
    ViewKey = WorkbenchPoints.ViewKeyFor("browser"),   // workbench.browser
    Icon = "globe",
    Requires = [Id],                                    // 模块停用即从目录消失
    Order = 20,                                         // 排在「文件」之后
    DefaultDock = WorkbenchDock.Right,
    Sizing = new WorkbenchSizing(DefaultPercent: 55, MinPx: 420),
    OpenPayloadSchema = /* { url?: string } */,
    ProvidedTools = [ "browser_open", "browser_snapshot", /* … */ ],
    ContributesContext = true                            // 「用户正在看哪个网页」对 agent 有用
}
```

### 9.2 视图（`BrowserWorkbench.razor`）

```
┌─────────────────────────────────────────────────────────┐
│ [◀][▶][⟳]  [ https://example.com/            ] [截图]  │  ← 工具栏（地址栏可输、回车导航）
├─────────────────────────────────────────────────────────┤
│                                                         │
│   <canvas>  ← WS 帧绘制（object-fit: contain）           │
│                                                         │
├─────────────────────────────────────────────────────────┤
│ Example Domain · 1280×800 · 智能体操作中…               │  ← 状态条
└─────────────────────────────────────────────────────────┘
```

| 行为 | 说明 |
|------|------|
| 打开载荷 | `OpenPayload.url` 存在 → 首连 WS 后自动导航 |
| 刷新恢复 | `ViewState["url"]` 存在 → 重连后导航回该地址（页面状态不恢复，只恢复地址） |
| 上下文上报 | URL/标题变化时 `Context.Report(new WorkbenchViewReport { ViewState = { url, title }, ContextSummary = $"正在浏览 {title}" })` |
| 未启动 | canvas 区域显示空态「浏览器尚未启动，智能体调用 `browser_open` 或在上方输入网址」 |
| 连接断开 | 自动重连（指数退避 1s→2s→4s，上限 15s），状态条显示「正在重连…」 |
| 高 DPI | canvas `width/height` 属性 = 帧尺寸；CSS 尺寸自适应；按 `devicePixelRatio` 不做额外放大（JPEG 已是最终像素） |

### 9.3 宿主登记

- `WebUiWorkbenchViews.RegisterBuiltIns` 加一行 `Register(registry, views, "browser", typeof(BrowserWorkbench))`
- WebUI `.csproj` 加 `ProjectReference` → `Seeing.Agent.WebUI.Browser`
- `WebUiHost` 加 `app.MapBrowserStreamEndpoints()`

---

## 10. 与 agent 的协作契约

### 10.1 上下文源（复用平台能力）

`ContributesContext = true` 让 `WorkbenchContextSource` 自动把「用户正在浏览哪个网页」纳入提示词（`docs/architecture/41` §八）。不需要 browser 包自己写上下文源。

### 10.2 典型 agent 流程

```
1. browser_open(url="https://example.com")
   → "已打开：Example Domain（https://example.com/）"
2. browser_snapshot()
   → e1 link "More information..." / e2 textbox "搜索" / e3 button "提交"
3. browser_type(ref="e2", text="puppeteer", submit=true)
   → "已在 e2 输入 9 个字符并按 Enter"
4. browser_wait_for(kind="networkidle")
5. browser_snapshot()            ← 拿到新页面的 ref
6. browser_screenshot()          ← 模型真的"看见"页面（Phase 1 链路）
7. browser_close()
```

**为什么要截图 + 快照两条路：** 快照是文本、精确、便宜，但会漏掉纯视觉信息（图表、布局错乱、验证码）；截图是像素真相但消耗视觉 token。两者并存，让模型自己选。

### 10.3 失败文案（自解释，不静默）

| 场景 | 返回 |
|------|------|
| 未打开页面就点 | 「该会话还没有打开页面，先调用 `browser_open`」 |
| ref 失效 | 「ref 'e2' 已失效（页面已变化），请重新调用 `browser_snapshot` 获取最新元素」 |
| ref 不存在 | 「页面中没有 ref 'e2'；当前快照共 3 个元素（e1–e3）」 |
| 元素不可见/被遮挡 | 「元素 e2 当前不可点击（不可见或被其他元素遮挡）」 |
| 超时 | 「等待超时（30 秒）：未出现 selector '.result'」 |
| 无浏览器可执行文件 | 「未找到可用的浏览器：请安装 Edge/Chrome，或在 seeing.json 的 Browser.ExecutablePath 指定路径」 |
| 私网地址被拒 | 「目标地址属于内网/回环，已被拒绝；如确需访问请在 seeing.json 设 Browser.AllowPrivateHosts=true」 |
| 未启用模块 | 工具的 schema 根本不存在（模块未启用 → 工具不挂载），无需文案 |

**实现期修正**：端点**不能**在「该会话还没有页面」时直接关连接——空态提示「在地址栏输入网址」，而连上即断会让它永远走不通。正确做法是接受连接、推一条 `running=false` 的 state，然后等 `navigate` 输入按需创建页面并补订阅。同一轮还修了地址栏 `onchange`（回车时读到旧值）与延迟停止在 `Dispose` 路径上 `await`（关闭标签页白等 1 分钟）。详见 `docs/architecture/42` §七。

---

## 11. 实测记录（本机探针）

`PuppeteerSharp 25.15.0` + `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`，.NET 10：

| 验证项 | 结果 |
|--------|------|
| `Puppeteer.LaunchAsync`（headless, 系统 Edge） | ✅ 启动成功 |
| `Page.SetViewportAsync(1280×800)` + `GoToAsync(data:text/html)` | ✅ |
| CDP `Accessibility.getFullAXTree` | ✅ 返回 3686 字节 JSON（含 RootWebArea / textbox / button） |
| CDP `Page.startScreencast`（jpeg, quality=60） | ✅ 启动成功 |
| `Page.screencastFrame` 事件 + `Page.screencastFrameAck` | ✅ 34ms 内收到 2 帧（首帧 payload 8026 字符）；**不 ack 则不再推帧**（已确认为必须） |
| `data-seeing-ref` 打标 + `querySelector('[data-seeing-ref="e2"]').click()` | ✅ 按钮文案变为 `clicked` |
| `Page.Mouse.ClickAsync(box.X+w/2, box.Y+h/2)` 坐标点击 | ✅ 生效 |
| `Page.FocusAsync` + `Page.Keyboard.TypeAsync("hello world")` | ✅ 输入框值正确 |
| `Page.ScreenshotDataAsync(jpeg)` | ✅ 8371 字节 |
| `GoToAsync(..., WaitUntilNavigation.DOMContentLoaded)` | ✅ 标题正确 |

---

## 12. 测试策略

沿用仓库纪律：**不引入 bUnit / 真浏览器依赖到常规测试**；浏览器逻辑分层，纯逻辑单测，真浏览器用例单列并可按环境跳过。

| 套件 | 覆盖 |
|------|------|
| `tests/capabilities/Seeing.Agent.Tools.Browser.Tests`（新） | ① `BrowserOptions` 默认值/校验/热重载绑定；② 可执行文件探测顺序（注入假 `IFileSystem`，钉住 5 个候选路径与回退 `null`）；③ URL 准入策略（http/https 通过、`file:`/`javascript:` 拒绝、`about:blank` 通过、私网按 `AllowPrivateHosts` 放行/拒绝、`data:text/html` 长度上限）；④ ref 解析与失效判定（`RefEpoch` 不符 → 明确的失效文案）；⑤ 快照行渲染（给定元素数组 → 稳定文本，含 disabled/checked 标注与截断标注）；⑥ `BrowserElementTarget` 判别（ref/selector/coordinates 三选一，缺参给可读错误）；⑦ 所有工具的描述与 Schema 契约（由 `Tools.Metadata.Tests` 的目录登记 + R1–R9 规则机械校验）。**全部不起真浏览器。** |
| `tests/capabilities/Seeing.Agent.Tools.Browser.IntegrationTests`（新，可选） | 真浏览器用例，用一个 `Fact(Skip="需要本机浏览器")` 或环境变量 `SEEING_BROWSER_TESTS=1` 开关：启动 → 打开 `data:text/html` 页面 → 快照 → 点 ref → 输入 → 截图字节非空 → screencast 收到 ≥1 帧 → 关闭。**默认不跑**（无头 CI 环境可能没有浏览器）。 |
| `tests/apps/Seeing.Agent.WebUI.Tests`（扩） | ① `WebUiWorkbenchViews` 能解析 `workbench.browser` 且组件声明了 `WorkbenchViewContext`；② `browser` 贡献在 `IUiContributionQuery` 可见集内（模块启用时）且 `Requires = ["browser"]`；③ `/api/browser/{sessionId}/stream` 对非 WebSocket 请求返回 400、对未知会话返回 404。 |
| `tests/apps/Seeing.Agent.HostComposition.Tests`（改） | `browser` 模块登记守门（在 `FullCapabilityComposition` 与 `BuiltInCapabilitySets.Full` 双处一致） |
| `tests/spine/Seeing.Agent.Tests`（扩） | `SessionArtifactStore`：写入落在 ref 目录、同名多次不覆盖、路径可被 `PathBoundary.IsWithin(refDir)` 接受、不合法扩展名回退 `.bin` |

**Invariants：** 未启用 `browser` 模块时，schema 中无任何 `browser_*` 工具，`workbench.dock` 可见集里没有 `browser`。

---

## 13. 分期

| 阶段 | 内容 | 判据 |
|------|------|------|
| **P1 脊柱接缝** | `ISessionArtifactStore` 契约 + Core 实现 + 单测（3 个文件，零风险先行） | 单测绿；不影响既有行为 |
| **P2 引擎与工具** | `BrowserEngine` / `BrowserSessionHost` / `BrowserPageHandle` / `BrowserSnapshotBuilder` + 12 个工具 + `BrowserModule` + 配置节 + 元数据守门 | 构建通过；工具单测绿；手工用真浏览器跑通「打开→快照→点→输入→截图」 |
| **P3 工作台视图** | `Seeing.Agent.WebUI.Browser` 类库（canvas / 工具栏 / WS 客户端 JS）+ 贡献声明 + 视图登记 + OpenPayload/ViewState | 手工验证：agent 打开页面后工作台实时显示，人可在 canvas 上点击输入 |
| **P4 画面流** | `BrowserScreencastHub` + `/api/browser/{sessionId}/stream` + 背压/多 viewer/生命周期 | 手工验证：帧率可用、拖宽面板不错位、多标签页互不干扰 |
| **P5 文档** | `docs/architecture/42-browser-workbench-release-notes.md`；`AGENTS.md` WHERE TO LOOK 加行；`05-extension-guide.md` 加「浏览器工具」小节 | 文档索引同步 |

P1 与 P2 可并行（P2 只在截图工具处依赖 P1）。

---

## 14. 破坏性变更清单

| 变更 | 影响 | 迁移 |
|------|------|------|
| 新增 `ISessionArtifactStore`（Abstractions） | 纯新增 | 无 |
| 新增能力包 `Seeing.Agent.Tools.Browser` + PuppeteerSharp 依赖 | 新增包 | CPM 加 `PuppeteerSharp 25.15.0` |
| `BuiltInCapabilitySets.Full` 加 `"browser"` | 全量能力集默认启用浏览器 | 不想用可 `Modules.Disabled: ["browser"]` |
| `FullCapabilityComposition` 加 `AddSeeingModule<BrowserModule>` | 四个宿主同步生效 | 同上 |
| `samples/Seeing.Agent.WebUI.Browser` 新项目 | 新 Razor 类库 | 加 `.slnx` |
| `WebUiHost` 加 `MapBrowserStreamEndpoints()` | 新增 HTTP 端点 | 无 |
| `WebUiWorkbenchViews` 加 `browser` 一行 | 工作台目录多一项 | 无 |

**没有破坏性 API 变更**：工作台平台、工具契约、LLM 契约一律不动。

---

## 15. 已知边界

| 项 | 说明 | 影响 |
|----|------|------|
| 每会话一个页面 | 不做多标签 | agent 无法并行处理两个网页；可通过开两个会话绕过 |
| 每会话一个 BrowserContext | 共享同一 Chromium 进程 | 一个页面崩溃（OOM）会带走同进程其他会话的页面；崩溃自愈会重建 |
| screencast 是 JPEG 有损 | quality=60 | 文字密集页面可能有压缩噪点；不影响 agent（agent 走 `browser_snapshot` 与 `browser_screenshot` 独立路径） |
| 无 WebSocket 鉴权签名 | 与 `api/sessions/.../assets` 同一基线（会话存在 + 本机/局域网） | 公网暴露需另行加固（列后续演进） |
| `browser_eval` 可读页面数据 | 页面里若有敏感信息（已登录后台），`eval` 可取出 | 靠审批拦截；建议不要在含凭据的会话里授予宽免 |
| 无 PDF 预览 / 无文件下载 | 下载被拒 | 需要文件用 `webfetch` 或人工 |
| 无网络录制 / 无 console 转发 | 本期不做 | 调试靠 `browser_eval` 自行读取 |
| headed 模式要求桌面会话 | Windows 服务/无桌面环境无法开窗 | 默认 headless，headed 仅用于人工登录 |
| 首帧延迟 | 冷启动 Chromium 约 200–400ms + 导航时间 | 可接受；预热列为后续演进 |

---

## 16. 后续演进

| 项 | 说明 |
|----|------|
| 多标签 | `BrowserSessionHost` 从单页改为页集合 + 激活页；`ref` 前缀带标签序号 |
| 网络/console 录制 | 订阅 `Page.Request/Response/Console`，作为工具（`browser_network` / `browser_console`）暴露 |
| PDF / 文件下载 | 下载目录限定在工作区临时目录 + `IWorkspacePathGate` 校验 |
| 会话登录态持久化 | 把 `BrowserContext` 的 storage state 存到 `~/.seeing/browser/<profile>`，跨会话复用登录 |
| 浏览器进程池 | 多会话共享一个 browser 已够；若页面数上到几十，改为按 CPU 核数分池 |
| WS 鉴权签名 | 与 assets 端点一起升级为带过期时间的 HMAC |

---

## 17. 附：关键代码引用

| 关注点 | 位置 |
|--------|------|
| 工作台贡献契约 | `src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/WorkbenchContribution.cs` |
| 工作台视图登记 | `samples/Seeing.Agent.WebUI/Shell/WebUiWorkbenchViews.cs` |
| 工作台视图上下文 | `samples/Seeing.Agent.WebUI.Workbench/Services/WorkbenchViewContext.cs` |
| 视图实现范式 | `samples/Seeing.Agent.WebUI.Files/Workbenches/FilesWorkbench.razor` |
| 工具基类 | `src/support/Seeing.Agent.Tools.Support/BuiltInToolBase.cs` |
| 工具产出图片契约 | `src/abstractions/Seeing.Agent.Abstractions/Tools/ToolOutputPart.cs`、`ToolResult.cs` |
| 图片物化器（只信 ref 目录） | `src/spine/Seeing.Agent.Core/Llm/ImagePartResolver.cs` |
| 二进制落盘（需包装成 `ISessionArtifactStore`） | `src/spine/Seeing.Agent.Core/Output/SessionToolOutputStore.cs` |
| 工具 schema 规范 R1–R9 | `docs/architecture/04-development-standards.md` §9 |
| 工具元数据守门 | `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/` |
| 能力模块注册点 | `samples/Seeing.Agent.HostComposition/FullCapabilityComposition.cs`、`src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs` |
| SSRF 校验先例 | `src/capabilities/Seeing.Agent.Tools.Web/WebFetchTool.cs`（`ValidateUrlSafetyAsync`） |
| WebSocket 端点先例 | `src/gateway/Seeing.Agent.Gateway/Endpoints/GatewayWebSocketHandler.cs` |
| 工作台页面挂载 | `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor` |
