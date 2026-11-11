# 浏览器工作台 Release Notes

**日期:** 2026-11-11
**范围:** 新增契约 `Abstractions/Sessions/ISessionArtifactStore`、新增能力包 `Seeing.Agent.Tools.Browser`、新增 Razor 类库 `Seeing.Agent.WebUI.Browser`、`samples/Seeing.Agent.WebUI/Endpoints/BrowserStreamEndpoints.cs`、`BuiltInCapabilitySets.Full`、`FullCapabilityComposition`、`WebUiWorkbenchViews`、`WebUiHost`
**前置:** [`2026-11-11-tool-image-parts-design.md`](../superpowers/specs/2026-11-11-tool-image-parts-design.md)（工具产出图片 → 会话只存引用，框架出站前物化）、[`41`](41-workbench-plugin-platform-release-notes.md)（会话工作台平台）
**设计:** [`2026-11-11-browser-workbench-design.md`](../superpowers/specs/2026-11-11-browser-workbench-design.md)

## 一句话

会话页多了一个「浏览器」工作台：服务端跑真实 Chromium（PuppeteerSharp + 系统 Edge），画面经 WebSocket 推到 `<canvas>`，鼠标键盘反向转发；agent 用 12 个独立的 `browser_*` 工具驱动同一个页面，并能用 `browser_screenshot` 把截图交给多模态模型看。

---

## 一、为什么不嵌 iframe

| 方案 | 致命问题 |
|------|---------|
| iframe | 绝大多数站点发 `X-Frame-Options: DENY` / CSP `frame-ancestors`，页面加载不出来；跨源 iframe 受同源策略保护，服务端**无法**读 DOM、无法派发真实点击 |
| 反向代理页面再注入 | 要重写全部 HTML/CSS/JS、处理 CORS/CSP/WebSocket/证书/登录态，等于自研浏览器 |
| WebView2 | 只能跑 Windows 桌面宿主，WebUI（浏览器访问）与 Gateway.Server（无头）全部不可用 |

选定：**服务端真浏览器 + 画面流 + 输入转发**（与 browser-use / Playwright MCP / Cursor 浏览器工具同一条路径）。

## 二、分层

| 层 | 位置 | 内容 |
|----|------|------|
| 契约 | `src/abstractions/Seeing.Agent.Abstractions/Sessions/ISessionArtifactStore.cs` | 会话资源目录端口（工具产出的二进制落盘） |
| 能力包 | `src/capabilities/Seeing.Agent.Tools.Browser/`（模块 id `browser`） | `BrowserEngine` / `BrowserSessionHost` / `BrowserPageHandle` / `BrowserSnapshotBuilder` / `BrowserScreencastHub` / `BrowserLifetimeService` + 12 个工具 + 配置节 |
| UI 类库 | `samples/Seeing.Agent.WebUI.Browser/` | `BrowserWorkbench.razor` + `browser-workbench.js`（WS 客户端 / 帧绘制 / 输入采集）+ CSS |
| 宿主 | `samples/Seeing.Agent.WebUI/` | `BrowserStreamEndpoints`（`/api/browser/{sessionId}/stream`）、视图登记、`UseWebSockets()` |

**依赖方向**：能力包只依赖 Abstractions + Tools.Support + `PuppeteerSharp`；UI 类库只依赖 Abstractions + WebUI.Workbench；组件类型只在宿主登记。

---

## 三、`ISessionArtifactStore`：为什么必须新增这条契约

`browser_screenshot` 要把 PNG 落盘并交给模型。Phase 1 确立的规则是「图片只存 `LocalPath`，且物化器只信任**本会话 ref 目录**内的路径」——但解析该目录的 `IToolOutputStore` 住在 `Seeing.Agent.Core`，能力包不能引用。

于是新增契约端口，实现留在 Core（`SessionArtifactStore`，委托 `SessionToolOutputStore`）：

```csharp
Task<string> SaveAsync(string sessionId, string name, ReadOnlyMemory<byte> content,
                       string extension, CancellationToken ct = default);
```

### 3.1 一个真实的坑：唯一性不能放在 `suffix`

首版把唯一后缀放在 `SaveBytesAsync(suffix: "-<epochMs>")`。实测同一毫秒内两次截图得到**同一个文件名**，后写覆盖先写（图片静默丢失）。

根因：`SessionToolOutputStore.SanitizeExtension` 把 suffix 截断到 **8 个 ASCII 字符**，十进制毫秒时间戳有 13 位，截断后恰好丢掉最低位。

**修法**：唯一性改放在 `name` 里（`name` 只做非法字符替换，不截断），后缀改用 base36 递增序号。测试断言的是「每条内容都能读回自己的值」这个不变量，而不是「文件名不同」这个实现细节。

---

## 四、工具面：为什么拆成 12 个

| 工具 id | 读/写 | 说明 |
|---------|-------|------|
| `browser_open` | 写 | 打开/导航 |
| `browser_snapshot` | 读 | 可交互元素清单（`e1`、`e2`…） |
| `browser_click` | 写 | ref / selector / 坐标 |
| `browser_type` | 写 | 输入（可清空、可回车） |
| `browser_press` | 写 | 按键与组合键 |
| `browser_scroll` | 写 | 页面或元素内滚动 |
| `browser_wait_for` | 读 | selector / ref / text / networkidle / timeout |
| `browser_screenshot` | 读 | **截图交给模型看**（Phase 1 链路） |
| `browser_eval` | **高危** | 页面上下文执行 JS |
| `browser_back` / `browser_forward` | 写 | 历史 |
| `browser_close` | 写 | 释放页面与上下文 |

**拆开的唯一理由是权限粒度**：`PermissionService` 的判定键是 `(PermissionKind.Tool, toolName)`，**不含参数**。合成一个 `browser` + `action` 枚举后，用户无法「允许截图、禁止执行任意 JS」——审批只能整包放行。拆开后 `browser_eval` 可以单独设为必须审批。

`Browser.EnableEval=false` 可让该工具**根本不出现在 schema 里**（与权限是两件事：一个是不提供能力，一个是不批准）。

## 五、ref 优先的元素定位

`browser_snapshot` 给每个可交互元素写 `data-seeing-ref="eN"` 并输出：

```
e1 link "More information..."
e2 textbox "搜索" (required)
e3 button "提交" (disabled)
```

`browser_click(ref="e2")` 直接 `querySelector('[data-seeing-ref="e2"]')`。

| 方案 | 结论 |
|------|------|
| CDP `Accessibility.getFullAXTree` | 语义最准，但 `nodeId` 在导航/重渲染后失效，且不能直接 `ClickAsync`，每元素还要一次 `DOM.resolveNode` 往返 |
| DOM 遍历 + `data-seeing-ref` | 一次 JS 拿全量，ref 直接可用，零额外往返 | **采用** |

**ref 的生命周期**：打在 DOM 上，导航即失效。失效时工具返回「ref 'e2' 已失效（页面已变化），请重新调用 `browser_snapshot`」，而不是静默点到错误元素。

## 六、画面流

| 关注点 | 实现 |
|--------|------|
| 传输 | 独立 WebSocket `/api/browser/{sessionId}/stream`（不塞 Blazor circuit：JPEG 帧会让服务端渲染全面卡顿） |
| 采集 | CDP `Page.startScreencast`（jpeg, quality=60, everyNthFrame=1） |
| **必须 ack** | 每帧立即 `Page.screencastFrameAck`。**不 ack 则只有第一帧**（探针已实测） |
| 背压 | 只保留最新帧：上一帧没写完就丢新帧（丢帧不丢 ack）。慢客户端不会把内存打满 |
| 多观看者 | 每 viewer 一条 WS；Hub 内一个 CDP screencast 广播（CDP screencast 是页级单例，重复 start 会互相顶掉） |
| 生命周期 | 首个 viewer 连上启动；最后一个离开后**延迟 1 分钟**停止（避免刷新导致反复启停） |
| 坐标 | 前端按 canvas 显示比例换算到视口像素，服务端直接 `Mouse.*` |
| 并发 | 人工输入与 agent 工具调用共用 `BrowserPageHandle` 的**会话级互斥**，鼠标状态机不会错乱 |

## 七、实测发现并修掉的六个缺陷

实现期间用真浏览器跑出来，都不是编译器能报的：

| # | 缺陷 | 症状 | 修法 |
|---|------|------|------|
| 1 | 端点在「无页面」时直接关连接 | 空态提示「在地址栏输入网址」，但照做永远没反应（连接已断） | 接受连接后保持；`navigate` 输入按需创建页面再补订阅 |
| 2 | 地址栏用 `onchange` | 用户「输入新网址 + Enter」时读到的是**旧值**，表现为「输入了新地址却打开上一个页面」 | 改 `@oninput` 实时同步；失焦时恢复成实际地址 |
| 3 | 延迟停止被 `await` | 订阅 Dispose 白等 1 分钟（一次端到端用例实测 62 秒） | 延迟停止改为后台任务，`Dispose` 立即返回 |
| 4 | `DeactivateAsync` 直接 `Dispose` DI 单例 | 模块停用后重新启用（热重载）→ 内部信号量已释放 → 浏览器永远起不来 | 改为 `ShutdownAsync`：关资源但不释放信号量，实例保持可用 |
| 5 | `browser_close` / 空闲回收不释放画面流 | 同会话再次 `browser_open` 复用指向已关闭页面的 CDP 会话 → 画面永远停在最后一帧 | `BrowserSessionHost.PageClosed` 事件 + `BrowserLifetimeService` 接线到 `Hub.DropSessionAsync` |
| 6 | `resize` 后 screencast 尺寸不跟随 | 面板拖宽后画布被拉伸（文字发虚） | `Hub.RestartAsync`：CDP 的 `maxWidth/maxHeight` 只在启动时生效，视口变了必须重启 |

第 3 条的教训写进了注释：**任何「宽限期」都不该在 Dispose 路径上 await**。
第 4 条的一般化结论：**DI 单例的生命周期归容器管，模块停用只能「关资源」，不能 Dispose 实例**——
否则热重载会让该单例永久不可用（这个仓库里模块停用/启用是常态操作）。

## 七之二、独立架构评审后修掉的接缝问题

首版通过全部单测与手工验收，但**评审发现四个「包内自洽、跨包断开」的阻塞项**——原因很一致：
单测全部在包内用 `new` 构造，没有一条穿过 DI 或 HTTP。逐条如下（都已修 + 已补守门测试）：

| # | 缺陷 | 症状 | 修法 |
|---|------|------|------|
| 7 | **配置节从未生效**：只写了 `AddOptions<BrowserOptions>()`，没有 `ConfigSectionOptionsMonitor` 桥接 | `Headless`/`ExecutablePath`/`AllowPrivateHosts`/`IdleSeconds` 全部读不到，`CurrentValue` 永远是 `new BrowserOptions()` 的默认值。**且内网永远被拒、有头模式永远打不开** | 照 `SkillsModule` 补三件套（`ConfigSectionOptionsMonitor` + `IOptionsMonitor` + `IOptions`）；新增 DI 级守门测试 `BrowserOptionsBindingTests` |
| 8 | **模块门控被 HTTP 面绕过**：`/api/browser/{id}/stream` 无条件映射 | `Modules.Disabled: ["browser"]` 时工具与工作台都消失了，但端点仍能连上并**按需拉起 Chromium** | 端点内查 `IModuleCatalog.IsEnabled("browser")`，停用返回 404；抽出 `IsModuleEnabled` 供单测直接验证 |
| 9 | **锁内同步阻塞 async**：`GetPageAsync` 在 `lock` 内调 `DisposeQuietly()`（内部 `GetAwaiter().GetResult()`） | 违反仓库「同步包装阻塞 async」反模式；旧 context 的 `CloseAsync` 需要线程池线程，可能卡住全进程新建页面 | 改为「锁内只做字典交换，锁外 `await DisposeAsync()`」 |
| 10 | **`DependsOn = ["io.local"]` 是假依赖**：注释说「需要 `IFileSystem` 探测」，全包零 `IFileSystem` 引用（实际用静态 `File.Exists`） | 假依赖让最小宿主的激活顺序与失败模式失真；原守门测试还把假依赖固化了 | 删除依赖，改注释说明理由；守门测试改为断言「必须为空」 |
| 11 | **`ConfigScope.Both` 让 `ExecutablePath` 可被项目级配置覆盖** | 项目级配置随仓库分发、内容由第三方控制 → `git clone` + `browser_open` = 任意程序执行 / SSRF | 整节改 `ConfigScope.UserOnly`（与 `Providers`/`ModelOverrides` 一致） |
| 12 | **页面上下文泄漏**：`CreateBrowserContextAsync` 成功后若建页/首次导航失败，context 未回收 | context 永久留在浏览器进程里，不报错、只在内存里堆积 | 加 `catch` 回收 + `DisposeContextQuietlyAsync`；首次导航失败主动 `CloseAsync` |
| 13 | **全局锁内做首次导航**：一个 30 秒不响应的站点会堵住其它会话新建页面 | 「同会话串行」被实现成「全进程页面创建串行」 | 首次导航移到全局锁之外（页级互斥已保证安全） |
| 14 | **`Busy` 是死状态**：端点里四处硬编码 `false`，遮罩永远不出现 | 设计承诺的「智能体正在操作」遮罩从未生效 | `BrowserPageHandle.RunAsync` 进出时置 `IsBusy`，Hub 在操作前后广播 `state` |
| 15 | **下载拦截没实现**：只有 `SetRequestInterceptionAsync(false)`（等于什么都没做） | 文档承诺「下载关闭」，实际会落到 Puppeteer 默认下载目录 | 创建上下文时 `BrowserContextOptions.DownloadBehavior = Deny`（注意：该选项**只能**在建 context 时传，没有事后等价 API） |
| 16 | **弹窗提示只在部分工具出现**：只有 6 个工具调 `SuccessWithDialogHint`，`snapshot`/`scroll`/`wait_for` 从不 drain | 那几条路径上模型永远不知道页面弹过窗 → 「点了没反应」时反复重试 | 收口到基类 `Success()` 覆写，12 个工具自动获得该行为；`browser_open` 显式 `BindSession` |
| 17 | **ref 未校验形状**：ref 直接拼进 CSS 属性选择器 | 模型可注入选择器片段命中快照外元素，「ref 只能命中快照打标元素」的安全叙事不成立 | `HasValidRefShape()` 白名单（`e` + 纯数字） |
| 18 | **`Hub` 在 `Deactivate` 走 `DisposeAsync`**（与 Engine/Host 的 `ShutdownAsync` 不对称） | 当前靠「恰好没释放信号量」才正确；谁按 IDisposable 直觉补一行 `_gate.Dispose()` 就复现「停用再启用后永久不可用」 | 拆出 `ShutdownAsync()`，`DisposeAsync` 只委托它且不释放信号量 |
| 19 | **`BrowserLifetimeService` 注释含事实错误**，且 `IModuleHostedService` 未被注册（`ModuleId`/`IsRunning` 是死代码） | 注释说「`IsRunning` 恒 false」，实际有维护；下一个人会照错误前提决策 | 改为实现 `IHostedService` + `AddModuleHostedService<T>`（与 `FileWatcherHostedService` 同形），删掉错误注释 |
| 20 | **`browser_eval` 可能不挂载**（`EnableEval` 配置） | `ProvidedTools` 声称 12 个、实际注册 11 个，破坏「ProvidedTools ⇔ 实际挂载」不变量 | 删除 `EnableEval`；要禁止任意 JS 用权限系统（`(PermissionKind.Tool, "browser_eval")` deny）或停用整模块——不为「可选工具」新增一种机制 |
| 21 | **端点下转到实现类取配置**（`host as BrowserSessionHost`） | 能力包刻意只暴露 `IBrowserSessionHost`；下转依赖实现细节，类型不匹配时静默退回默认值 | 端口新增 `AdmitUrl(url)`，地址准入（`BrowserUrlPolicy` + `AllowPrivateHosts`）真正成为唯一判定点 |
| 22 | **端点注入了未使用的 `IWorkspacePathGate`** | 「声明了却没用的端口」会让后来者以为已做路径校验 | 删除 |

**最重要的结论**是第 7 条：这类缺陷**任何包内单测都抓不到**，必须有一条穿过 DI 的测试。
新增的 `BrowserOptionsBindingTests`（4 条）钉住「`seeing.json` 里写的值能读到」，这是本次评审最有价值的产出。

## 七之三、第二轮（并发 / 生命周期）评审后修掉的问题

第二轮专门审并发、资源生命周期、协议正确性。它确认了最担心的**死锁路径不存在**
（page 锁 ↔ stream 锁无顺序反转、`FramePump` 无丢失唤醒、CDP ack 在正常路径上无条件发出），
但找出 2 个阻塞项与若干「声称已修但实际无效」的问题：

| # | 缺陷 | 症状 | 修法 |
|---|------|------|------|
| 23 | **`browser_close` → `browser_open` 后画面永久冻结**：端点的 `EnsureSubscribedAsync` 只看「订阅非空」就永久跳过；而页面关闭会清掉 Hub 里的流 | 工作台停在最后一帧、状态条仍显示「已连接」，且收不到 `running=false`（页面关闭不推状态），只能关掉面板重开 | 订阅改为「按页面实例判定」：实例变了就撤旧订阅重订；页面消失时推 `running=false`；每次输入前自愈式重订 |
| 24 | **`PageClosed` 只带 sessionId，close/reopen 交错会误杀健康流** | 一次慢速关闭的完成事件在新页面已订阅之后才到 → 新页面的流被删，「刚打开就黑屏、再无帧」 | 事件签名改为 `Func<string, BrowserPageHandle, Task>`；`DropSessionAsync(sessionId, page)` 用 `ReferenceEquals` 只清「指向该实例」的那条 |
| 25 | **`IsPrivateHost` 被尾点绕过**（`localhost.` / `127.0.0.1.` / `foo.localhost.`） | `Uri.Host` 保留末尾点，DNS 与浏览器按绝对 FQDN 解析到回环 → `AllowPrivateHosts=false` 时仍能访问本机任意端口 | `TrimEnd('.')` + 空名按私有处理；补 6 条尾点用例 |
| 26 | **`resize` 重启 screencast 读的是配置视口**（#6 的修复实际无效） | 面板拖宽到 > 配置宽（1280）时 CDP 按 1280 等比缩小 → 画布降采样、文字发虚。窄面板反而正常，所以只在宽屏暴露 | `BrowserPageHandle` 记录实际 `ViewportWidth/Height`（`ResizeAsync` 更新），`StartCoreAsync` 用页面实际视口 |
| 27 | **`DrainDialogs` 无锁**（`OnDialog` 有锁，读取没有） | `List` 并发 `ToArray`/`Clear` 可能抛异常，且是从 `Success()` 里抛——一次成功的点击变成失败。本轮把 drain 收口到 12 个工具后，窗口从 6 处放大到每次成功 | 整段进 `lock (_dialogs)` |
| 28 | **`BrowserPageHandle._gate.Dispose()`** | 排队在 `WaitAsync` 上的工具/人工输入会拿到 `ObjectDisposedException`，且它在 `try` 之外，逸出成「点击元素失败：Cannot access a disposed object.」（文案误导、竞态偶发） | 不 Dispose 信号量（每会话一个托管对象，随句柄 GC） |
| 29 | **Shift 符号键全部失效**（`!` `?` `:` `@` `%`） | `NormalizeKey` 小写化无助，这些字符在 Puppeteer 键定义表里**没有条目** → 抛 Unknown key，被 WS 循环吞成 Debug 日志。用户侧表现为「密码里少了几个字符」 | 单字符可打印键改走 `SendCharacterAsync`（不依赖布局表）；对应 `KeyUp` 变 no-op |
| 30 | **`Busy` 推送时机错了**（#14 实际仍是死代码） | `IsBusy` 在 `RunAsync` 内部置位/复位，调用方「操作前/后各推一次」读到的都是 false → 遮罩从未亮过；且 agent 工具路径完全没有推送 | `BrowserPageHandle.BusyChanged` 事件在持锁层触发；端点订阅后转 `hub.PublishState` |
| 31 | **`AddViewerAsync` 先入列后起流** | `StartCoreAsync` 失败（页面刚关、CDP 断）时 sink 永久留在 `_sinks` → 该会话 screencast 永不因「无观众」停止，每帧都对断开的 WS 调 SendAsync | 先起流、后入列 |
| 32 | **grace 停止不停检观众** | 两个标签页看同一会话、其中一个刷新时命中窗口 → 60 秒后 screencast 被停，但剩余 WS 仍 Open（不触发 onclose 重连）→ 画布永久冻结 | 停流前复检 `_sinks.Count`；`_stopGraceCts` 改用独立小锁保护（不能复用 `_gate`，会自锁） |
| 33 | **WS 上行不分片**：固定 64KB 缓冲、不看 `EndOfMessage` | 粘贴超过 64KB 的文本被截断 → JSON 解析失败被吞成 Debug → **用户粘贴的内容静默丢失**；分片切断多字节字符还会产生 U+FFFD | 累积到 `EndOfMessage`，超限显式 `MessageTooBig` 拒绝；上限提到 1MB |
| 34 | **端点无兜底 catch** | CDP 创建失败/浏览器崩溃时异常逸出 WS 处理函数 → 前端只看到「连接出错」并重连，每次重连踩同一个异常 | 加 `catch (Exception)`：记录 + 推 `OnError` + 正常关闭 |
| 35 | **截图绕过统一出口** | 截图路径不做 `DrainDialogs`（与其余 11 个工具不一致）；且磁盘落盘超时被译成「页面在限定时间内没有响应」，模型会去重试导航 | 基类加 `Success(output, metadata, parts)` 重载；截图单独 catch `TimeoutException` 说明「写盘超时」 |
| 36 | **`wait_for(kind=ref)` 绕过 ref 白名单** | 该路径直接把 value 拼进属性选择器，「ref 只能是快照编号」的边界在此不成立 | ref 形状校验下沉到 `ResolveSelector()` 唯一构造点（抛 `BrowserElementValidationException` → 可读失败） |
| 37 | **`RestartAsync` 失败留下死态** | `_cdp` 已 null 而 `_sinks` 非空 → 有观众、无采集，画布冻结且无提示 | 失败时回滚并 `PublishError` + 推 `running=false` |
| 38 | **前端帧绘制乱序** | 解码慢的旧帧后完成覆盖新帧；页面静止后不再出新帧 → 画布长期停在旧画面 | 用 `seq` 丢弃过期帧（解码前后各判一次） |
| 39 | **地址栏被状态推送覆盖** | 用户敲到一半时页面推状态（导航完成、busy 变化都推）→ 输入框被重置成当前 URL，看起来像「吃字」 | 加 `_addressEditing` 标志（focus 置位 / blur 复位），编辑中不覆盖 |
| 40 | **`ResizeObserver` 无去抖** | 拖分隔条每秒触发数十次 resize，每次都 `SetViewport` + 重建 CDP 会话（四个往返）→ 画面闪、CPU 飙、与 agent 抢锁 | 前端 150ms 去抖 + 尺寸未变不发；服务端再挡一次 |

## 七之四、线上故障：画布黑屏、工具操作「没反应」

**现象**：打开浏览器工作台后画布一直黑屏显示「浏览器尚未启动」；让 agent 调 `browser_open`，
聊天里能看到工具执行成功（`browser_snapshot` 也返回了元素），但画布毫无变化。

### 根因（三层，逐层被证据推翻后才定位）

| 层 | 假设 | 证据 | 结论 |
|----|------|------|------|
| 1 | 静止页面不推帧 | 实测：`startScreencast` 后**4ms 内**就收到首帧 | ❌ 推翻 |
| 2 | 缺 `PageCreated` 通知 | 页面由 agent 工具创建，端点只在「连接建立 / 收到输入」时检查页面 —— agent 那条路径两个时机都不触发 | ✅ 真缺陷，补事件后仍未出画面 |
| 3 | **回调里自死锁** | 页面创建回调先取 `ensureSubscribedGate`，再调 `EnsureSubscribedAsync`（它自己也取同一信号量）→ 永久阻塞。日志表现：通知发出、但 `AddViewerAsync` 从未被调用 | ✅ **真正根因** |

### 修复

1. **`BrowserSessionHost.PageCreated` 事件**（与 `PageClosed` 对称）：页面建好后通知订阅方。
2. **`BrowserPageNotifier`**（`IModuleHostedService`）：按会话分组的回调登记 + 广播，
   把「页面创建」转达给当前所有 WS 连接（端点是 HTTP handler，无法在连接前订阅宿主事件）。
3. **端点订阅该总线**：agent 一建页面，画布立刻建立画面流。
4. **消除自死锁**：回调不再外层重复取 `ensureSubscribedGate`（`EnsureSubscribedAsync` 自己会取）。

### 验证

真浏览器实测：agent 调 `browser_open` → 日志 `页面已创建，通知 1 个画面流订阅者` →
`添加画面流观看者` → `已启动画面流（1280×800）` → JS 推 resize 后重启为 `554×346` →
**画布从默认 300×150 变为 554×346 并显示真实页面内容**。

### 教训

- **「工具能用」不等于「画面能看」**：两条链路完全独立（工具走 `IToolManager`，画面走 WS）。
  任何「UI 没反应」的报障，先分别确认两条链路，不要假设一条通另一条也通。
- **加监听/订阅类修复必须警惕锁重入**：`EnsureSubscribedAsync` 名字像幂等函数，
  但它内部取锁；在持有同一把锁的回调里调用它就会自死锁，而且症状是「什么都没发生」
  ——没有异常、没有日志，极难定位。
- **诊断日志级别要够**：本轮排查中大量关键路径用的是 `LogDebug`，默认配置下看不到，
  一度误判为「方法没被调用」。关键链路的状态迁移应在 `Information`，
  只有高频路径才用 `Debug`。

## 七之五、第二个报障：`browser_open` 不会打开工作台

**现象**：`browser_open` 执行成功（后续工具也能操作页面），但界面上没有出现浏览器面板。

### 两个根因

| # | 缺陷 | 影响 |
|---|------|------|
| 1 | **`browser_open` 从不请求打开工作台**：它只建页面 + 导航，与工作台零交互 | 页面在服务端建好了，但用户看不到任何东西 —— 能力不可见 |
| 2 | **`HostRequestDispatcher` 只查旧的 `HostRequestActivePanelRegistry`**，从不查工作台的 `WorkbenchRequestHandlerRegistry` | `workbench.registry.open` 永远找不到 handler，被判 `Unavailable`。**连 `open_workbench` 工具本身在真实 UI 里也打不开工作台** —— 这是平台级缺陷，不只是浏览器的问题 |

### 为什么之前没被发现

- 单测只覆盖「工具产出什么」，没覆盖「工具是否驱动了 UI」。
- `open_workbench` 的五道门闸（扩展点 / id / 模块 / 载荷 / 界面）都只做**前置校验**，
  校验通过后发请求——而请求落地那一步（dispatcher 路由）从来没被测过。
- 两套路由表并存是 `41` 里记录的已知边界（「旧 `HostRequestActivePanelRegistry` 仍在」），
  但当时认为「编辑器 `editor.*` 走旧表、工作台走新表」互不影响，
  实际是**dispatcher 只走了旧表**。

### 修复

1. **`browser_open` 尽力打开工作台**：页创建并导航成功后，发
   `WorkbenchRequestKinds.RegistryOpen`（`workbenchId = browser`）。
   失败**不改变工具结果**——headless/Gateway/CLI 没有工作台扩展点，页面照建、工具照用，
   只在输出里附一句说明（「界面未就绪」等）。
2. **`HostRequestDispatcher` 改为两张表都查**：先查 `WorkbenchRequestHandlerRegistry`
   （按 kind 前缀，含 `workbench.registry.*`），未命中再回落到
   `HostRequestActivePanelRegistry`（编辑器 `editor.*`）。
   `HasAny` 也要两张表都问，否则「他 circuit 持有」的跳过判定会误判为 `Unavailable`。
3. **`BrowserModule.IdValue` 常量化**：模块 id 同时是工作台 id，三处消费点
   （模块声明 / `browser_open` 请求 / 端点门控）必须同源。

### 验证

真浏览器实测：**不预先打开任何面板**，直接让 agent 调 `browser_open` →
工作台标签「浏览器」自动出现 → 状态「已连接」→ 画布收到真实帧（554×346 并显示页面内容）。

### 教训

- **「工具成功」不等于「用户看见了」**：`browser_open` 语义是「打开浏览器」，
  对用户而言「打开」必然包含「能看见」。工具只建后端页面是半成品。
- **两套路由表并存是定时炸弹**：`41` 把「旧表仍在」记为已知边界，但真正的风险不是
  「两套表都有人用」，而是**派发器只认其中一套**。这类缺陷的测试必须覆盖
  「注册 → 派发 → 落地」全链，只测注册表本身的 `TryResolve` 不够。
- **报障要区分「没打开」与「打开了但没画面」**：两者根因完全不同
  （前者是请求路由，后者是画面流），混在一起排查会浪费大量时间。：两条链路完全独立（工具走 `IToolManager`，画面走 WS）。
  任何「UI 没反应」的报障，先分别确认两条链路，不要假设一条通另一条也通。
- **加监听/订阅类修复必须警惕锁重入**：`EnsureSubscribedAsync` 名字像幂等函数，
  但它内部取锁；在持有同一把锁的回调里调用它就会自死锁，而且症状是「什么都没发生」
  ——没有异常、没有日志，极难定位。
- **诊断日志级别要够**：本轮排查中大量关键路径用的是 `LogDebug`，默认配置下看不到，
  一度误判为「方法没被调用」。关键链路的状态迁移应在 `Information`，
  只有高频路径才用 `Debug`。

## 七之六、第三个报障：HTTPS 下画面流永远连不上（前端「正在重连」）

**现象**（VS 启动，`https://localhost:53065`）：工作台面板能打开，但画布始终是「浏览器尚未启动」，
右下角显示「正在重连（15 秒后）」并逐步退避到最大值。

### 排查过程（四个错误假设被逐一推翻）

| # | 假设 | 证据 | 结论 |
|---|------|------|------|
| 1 | 缺少 `PageCreated` 通知 | 已在七之四修掉 | ❌ 不足以解释 |
| 2 | `UseWebSockets()` 顺序错（在 `UseRouting` 前） | 调整顺序后现象不变 | ❌ 推翻 |
| 3 | HTTPS 开发证书未被信任 | **同一页面里 SignalR 的 `wss://.../_blazor` 连接正常** —— 证书若不受信任它也该失败 | ❌ 推翻（关键反证） |
| 4 | `UseHttpsRedirection` 对 WS 返回 307/400 | 设 `DisableHttpsRedirection=true` 后现象不变 | ❌ 推翻 |
| 5 | **Kestrel 在 HTTPS 下协商到 HTTP/2，浏览器用 extended CONNECT 做 WS 升级，ASP.NET Core 默认不支持** | 在管道最前置诊断中间件打印：`Method=CONNECT`、`Upgrade=` 为空、`IsWebSocketRequest=False`；HTTP 端口下则是 `GET + Upgrade=websocket` | ✅ **根因** |

### 修复

`WebUiHost` 里把 Kestrel 限制为 HTTP/1.1：

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listen => listen.Protocols = HttpProtocols.Http1);
});
```

修复后同一路径的请求变成 `GET + Upgrade=websocket`，`wss` 握手成功（收到 `OPEN` + `state`）。

### 对远程部署的影响（重要）

**不需要额外证书**，也不影响生产部署：

| 场景 | 是否需要证书 | 是否可用 |
|------|-------------|---------|
| 本机 VS 调试（`https://localhost`） | 自签开发证书（HTTP/1.1 限制后即可用） | ✅ |
| 远程部署 + 真实证书（Let's Encrypt / 公司 CA） | 需要，且**本就受信任** | ✅ |
| 远程部署 + 反向代理（nginx / Caddy / Traefik） | 证书在代理层，应用侧是 ws 明文 | ✅（最常见做法） |
| 内网 / 纯 HTTP | 不需要 | ✅ |

「wss 必须有证书」是 TLS 协议的固有要求，不是本项目的额外负担 ——
生产环境本来就有受信任证书，所以这个 bug 只影响**本机开发**。

### 代价与取舍

强制 HTTP/1.1 放弃了 h2 的多路复用。本应用是 Blazor Server（本身靠一条 SignalR 长连接），
h2 收益有限；WebSocket 画面流是硬需求。若将来必须用 h2，需要改用 WebTransport 或把 WS 独立端口。

### 教训

- **「同一页面里另一个 wss 能连」是最有价值的反证**：它一次性排除了证书、网络、代理三类猜测。
  遇到「服务端 WS 连不上」时，先找一个**同源、已知可用**的 WS 端点做对照。
- **WebSocket 的 close code 1006 不携带任何原因**，服务端也常常没有日志（请求没到端点）。
  这时唯一有效的办法是在**管道最前置**加临时中间件打印 `Method` / `Upgrade` / `IsWebSocketRequest`
  —— 我为此绕了三轮，成本全在「没有在最早的位置取证」。
- **不要写猜测性的用户提示**：我曾在前端写「HTTPS 开发证书可能未被信任」，
  而那是错的。错误归因比没有归因更糟 —— 会把用户引向完全无关的排查方向。
  最终前端只陈述事实（「无法建立画面流连接」），不做归因。
### 评审确认「没有问题」的点（记录以免后续重复排查）
- **page 锁 ↔ stream 锁无顺序反转**：`resize` 路径先释放 `page._gate` 再取 `stream._gate`，两者不嵌套；`SubscribeAsync` 全程只取 stream 锁。
- **`FramePump` 无丢失唤醒**：`_pending`/`_flushing` 全在同一把 `_lock` 内读写。
- **CDP ack 在正常路径无条件发出**（先 ack、后判空、后解析），base64 解码失败也在 ack 之后。
- **`browser_eval` 不是宿主编织注入**：表达式作为**参数**传给 `EvaluateFunctionAsync`，不是 C# 插值拼接。
- **`SessionArtifactStore` → `ImagePartResolver` 链路必然通过**：路径落在 ref 目录内，文件名不越界。
- **私网判定的主流编码形式都正确**：`0x7f.1`、`2130706433`、`127.1`、`[::1]`、`::ffff:127.0.0.1`、各类 CIDR 均命中（唯一缺口是尾点，已修）。

## 八、安全

| 面 | 决策 |
|----|------|
| 地址准入 | `BrowserUrlPolicy` 唯一判定点（工具与地址栏共用）：只 http/https/about:blank/data:text/html；默认拒绝回环/私网/链路本地（含云元数据 `169.254.169.254`），`Browser.AllowPrivateHosts=true` 才放行 |
| 弹窗 | 一律 `Dismiss`，并在工具输出里告知模型「弹窗已被自动关闭」（否则模型以为点了没反应） |
| 下载 | 关闭：agent 不该把文件写到未知位置 |
| `browser_eval` | 单独工具 + 高危标注；执行环境无 Node/文件系统访问，受同源策略约束 |
| 凭据 | 不注入任何 cookie/token；登录态由**持久 profile** 承载（见 §八之二）：在专用 profile 里登录一次，之后所有会话复用 |
| WS 鉴权 | 与 `api/sessions/.../assets` 同一基线（会话存在 + 本机/局域网）。公网暴露需另行加固（已知边界） |

**边界说明（重要）**：`BrowserUrlPolicy` 按**字面 IP 与 localhost 名**判定，不做 DNS 解析。解析后再校验存在 TOCTOU（校验后域名重新解析到内网），而浏览器导航无法固定连接目标。真正的强隔离应由宿主网络策略承担——这一点写在类型注释里，避免后来者误以为它是完整 SSRF 防护。

## 八之二、持久登录态（`PersistLogin`）

### 需求与它撞上的三条硬墙

需求很朴素：「用我自己的登录状态帮我办事」。为此先把「复用用户已有 Edge 登录态」的三条路全部实测排除了：

| 路径 | 实测结果 | 原因 |
|------|---------|------|
| 直接指向真实 profile | ❌ `ProcessException: The browser is already running for ...` | Edge 运行时对 profile 加独占锁 |
| 对真实 profile 开远程调试接管 | ❌ 端口不可达 | Chromium 136+ 禁止对默认 profile 开 `--remote-debugging-port`（防凭据窃取） |
| 复制 profile 到别处 | ❌ `Win32 32: 被另一进程占用`（五种共享模式全失败）/ SQLite `Error 14` | Cookie 库被独占；且 `Local State` 含 `app_bound_encrypted_key`，密钥绑定 app 身份，换进程解不开 |

**结论**：只能走第四条 —— 专用 profile 里登录一次，之后永久复用。

### 隔离上下文不落盘（决定了实现形态）

| 上下文 | 种 cookie | 关浏览器后重启 |
|--------|----------|--------------|
| `CreateBrowserContextAsync()`（原实现） | ✅ 1 个 | ❌ **0 个** |
| `DefaultContext` | ✅ 1 个 | ✅ 仍在 |

隔离上下文等同无痕窗口。所以**要持久登录态就必须放弃上下文隔离**，这是二选一，不存在两全方案。

### 实现

```csharp
// BrowserEngine.LaunchAsync
_launchedProfileDirectory = options.PersistLogin ? ResolveProfileDirectory(options) : null;
if (_launchedProfileDirectory is { } dir) { Directory.CreateDirectory(dir); launchOptions.UserDataDir = dir; }

// BrowserSessionHost.GetPageAsync —— 上下文策略分叉
if (options.PersistLogin)
    page = await browser.NewPageAsync();                          // DefaultContext（cookie 写盘）
else
    context = await browser.CreateBrowserContextAsync(...);        // 隔离（cookie 不写盘）
```

四个配套决定：

| 问题 | 决定 | 理由 |
|------|------|------|
| 下载禁止怎么设 | 从 `BrowserContextOptions.DownloadBehavior` 移到 CDP `Browser.setDownloadBehavior(deny)` | 默认上下文拿不到 `BrowserContextOptions`；实测浏览器级 CDP 可用 |
| profile 目录位置 | 默认 `{用户目录}/.seeing/browser-profile`，可配 `ProfileDirectory` | 允许覆盖是为了测试隔离与多实例；同目录被两个进程打开会直接拒绝启动 |
| 配置热重载 | 比对 `_launchedProfileDirectory` 与期望值，不同则重启浏览器 | 否则「换了 profile 目录但登录态还是旧的」，看不出是没重启 |
| profile 目录不可用 | 退回临时 profile + `LogWarning` | 「能用但每次重新登录」远好过「完全用不了」 |

### 默认上下文实测可用的能力

| 能力 | 结果 |
|------|------|
| 多页并存、关一页不影响其它页 | ✅ |
| 页面数降到 0 时浏览器仍存活 | ✅ `IsConnected` 仍 True |
| 各页 cookie 共享 | ✅（这正是登录态复用的前提） |
| `Browser.setDownloadBehavior("deny")` | ✅ |

### 语义变化（唯一一条）

`PersistLogin=true`（默认）时 **cookie 跨会话共享**。两个会话同时操作同一个站点账号会互相顶掉登录态。要回退到「每会话隔离」：`Browser.PersistLogin=false`（登录态不再持久）。

### 首次登录流程

面板 canvas 已支持真人鼠标键盘（与工具共用同一把会话锁），所以不需要新代码：

```
agent 调 browser_open("https://github.com/login")
    ↓ 面板显示登录页
用户在面板里输入账号密码（或 Headless=false 在有头窗口里登录，能过验证码/扫码）
    ↓ cookie 写入 ~/.seeing/browser-profile
之后所有会话直接是登录身份
```


## 九、配置节 `Browser`

```json
{
  "Browser": {
    "Headless": true,
    "ExecutablePath": null,
    "IdleSeconds": 600,
    "ViewportWidth": 1280,
    "ViewportHeight": 800,
    "AllowPrivateHosts": false,
    "DefaultTimeoutSeconds": 30,
    "PersistLogin": true,
    "ProfileDirectory": null
  }
}
```

| 项 | 说明 |
|----|------|
| `Headless` | 默认 true（无桌面环境也能跑）；需人工登录/观察时设 false |
| `ExecutablePath` | null 时按平台探测，见 §九之二 |
| `IdleSeconds` | 页面静默回收（只关页面与 context，不关进程）；0 = 不回收 |
| `PersistLogin` | 默认 true：复用持久 profile 保留登录态，代价是 cookie 跨会话共享（见 §八之二） |
| `ProfileDirectory` | null 时用 `{用户目录}/.seeing/browser-profile`；仅在 `PersistLogin=true` 时生效 |
| 视口/超时 | 有钳制（宽 320–4096、高 240–4096、超时 1–300 秒）：配置写坏不得让页面变 1px |

## 九之二、跨平台可执行文件探测（Linux / macOS）

### 事故

Ubuntu 服务器上装了 Chromium，工具却始终报「未找到 Edge / Chrome」，且**装完之后一直探测不到**。原因有两层：

1. `Candidates()` 只列了 Windows 路径（`%ProgramFiles%\Microsoft\Edge\Application\msedge.exe` 等）。
   `Environment.SpecialFolder.ProgramFiles` 在 Linux 上返回空串，于是候选集合为空 ——
   不是「探测失败」，而是**一个候选都没有**，所以无论装什么都命中不了。
2. 失败文案写死「请安装 Microsoft Edge 或 Google Chrome」——Ubuntu 上这两个包
   都不在默认 apt 源里，文案没给出任何可执行动作。

### 修复

**① 候选清单按平台分支**（`BrowserEngine.Candidates(BrowserPlatform)`，平台做成显式参数以便在任意平台上钉住三种清单）：

| 平台 | 顺序 |
|------|------|
| Windows | Edge（ProgramFiles(x86) → ProgramFiles）→ Chrome → 用户级 Chrome / Edge / Chromium（`%LOCALAPPDATA%`，本次补上用户级 Edge） |
| macOS | `/Applications` 下的 Chrome → Chromium → Edge 应用包 |
| Linux | `/usr/bin/google-chrome-stable` → `google-chrome` → `/opt/google/chrome/chrome` → `/usr/bin/chromium` → `/usr/bin/chromium-browser`（Debian 旧包名）→ `/snap/bin/chromium`（Ubuntu apt 装 chromium 常落 snap）→ Edge → **PATH 扫描** → **Puppeteer 下载缓存** |

PATH 扫描与缓存扫描的理由：

- PATH：手动解压、`~/.local/bin`、conda 环境不在任何标准目录，但一定在 PATH 上。只读环境变量 + `File.Exists`，不启动 `which` 子进程。
- 缓存：用户可能已按 Puppeteer 文档跑过 `BrowserFetcher.DownloadAsync()`，浏览器在 `~/.cache/puppeteer`（新版 `chrome-linux64/chrome`）或 `~/.local-chromium`（旧版 `chrome-linux/chrome`）。目录按版本名降序，刚下载的优先。

**② 「找不到可执行文件」不再吃失败冷却**

`FailureCooldown = 10s` 原本对所有失败一视同仁。但这两种失败对重试的期待完全相反：

| 失败形态 | 用户预期 | 处理 |
|---------|---------|------|
| 磁盘上找不到可执行文件（还没装） | 装完**立刻**可用 | 跳过冷却（`BrowserExecutableMissingException` 标记） |
| 进程起不来（profile 锁、崩溃） | 频繁重试只会更慢 | 保留 10 秒冷却 |

「服务先起来、运行中才装浏览器」是服务器部署的正常顺序，让用户为此等冷却、
或误以为「装了也没用」，代价远大于一次 `File.Exists`（探测本身不起进程）。
用异常类型而非匹配错误文案来区分 —— 文案会改。

**③ root 运行时自动补 `--no-sandbox`**

Chromium 以 root 运行且未加该参数时**硬拒绝启动**
（`Running as root without --no-sandbox is not supported`）。
反检测档原本刻意不加它（它会削弱指纹），于是在 Ubuntu 服务器（通常 root / sudo）上
「反检测开着 = 浏览器完全起不来」，且只报「启动失败」。

`BrowserEngine.MustDisableSandbox()` 在非 Windows 且用户名为 `root` 时返回 true，
反检测档据此补上该参数。误判代价可控：非 root 的 `root` 用户多一个参数，
指纹稍弱但能跑 —— 远好过反向的「完全起不来」。

**④ 失败文案与设置页按平台给指引**

`BrowserEngine.InstallHint()` 在 Linux 上直接给出 `sudo apt install chromium-browser`；
设置页的 `ExecutablePath` 占位文案也按平台变化，并新增「重新探测」按钮
（装好浏览器后无需重启服务，按钮只刷新页面显示）。

**⑤ 探测每次读磁盘，不做进程级缓存**

`ResolveExecutable` 每次调用都重新探测。缓存会让「启动时没装、运行中才装」永远命中不了，
而探测成本是若干次 `File.Exists`，远低于一次失败的进程启动。

## 九之三、启动失败的可读解释（Linux 服务器专项）

Ubuntu 上「可执行文件找到了但起不来」有三个高频原因，原始异常全都是
`ProcessException: Failed to launch browser!` 加一段夹着 stderr 的文本 —— 用户看不出该做什么。

`BrowserEngine.TryExplainLaunchFailure(string, out string?)` 把这些模式翻成可执行指引：

| stderr 特征 | 真实原因 | 给出的指引 |
|-------------|---------|-----------|
| `error while loading shared libraries: libnss3.so` | 精简镜像 / 最小化服务器缺 Chromium 运行时依赖 | 完整 `sudo apt install -y libnss3 libatk1.0-0 … libgbm1 libasound2`，并点名缺失的那个库 |
| `Missing X server or $DISPLAY` | 无图形会话却开有头 | 改无头模式，或 `xvfb-run -a` 启动服务 |
| `No usable sandbox` / `namespace sandbox` | Docker / 受限容器沙箱不可用 | 在「追加启动参数」里加 `--no-sandbox --disable-setuid-sandbox` |
| 其他 | — | 返回 `false`，原样抛出（不吞信息、不给错指引） |

配套的**前置拦截**：`MissingDisplay()` 在有头模式 + 无 `DISPLAY`/`WAYLAND_DISPLAY` 时直接抛出
说明性错误，而不是等 Chromium 自己失败。刻意**不**静默回落无头 ——
用户开有头就是为了反检测，悄悄换掉会变成「设置里明明关了无头，行为却没变」这种更难查的问题。

`IsProfileLocked`（`The browser is already running`）有独立的重试路径，
明确排除在上述解释之外：否则用户会去 `apt install` 一个根本不缺的包。

## 十、破坏性变更

| 变更 | 影响 |
|------|------|
| 新增 `ISessionArtifactStore`（Abstractions） | 纯新增 |
| 新增能力包 + `PuppeteerSharp 25.15.0`（CPM） | 新增依赖 |
| `BuiltInCapabilitySets.Full` / `FullCapabilityComposition` 加 `browser` | Full 档默认启用；不想用可 `Modules.Disabled: ["browser"]` |
| `WebUiHost` 加 `UseWebSockets()` + `MapBrowserStreamEndpoints()` | 新增中间件与端点 |
| `WebUiWorkbenchViews` 加 `browser` 一行 | 工作台目录多一项 |
| `.slnx` 加 3 个项目 | — |

**没有破坏性 API 变更**：工作台平台、工具契约、LLM 契约一律未动。

## 十一、测试

| 套件 | 数量 | 钉住什么 |
|------|------|---------|
| `tests/capabilities/Seeing.Agent.Tools.Browser.Tests`（新） | 100 | 地址准入（私网/协议/data URL 长度/相对路径/**尾点绕过**）；快照渲染字节稳定与截断标注；元素定位判别（ref/selector/坐标三选一 + ref 形状白名单含唯一构造点校验）；可执行文件探测顺序；配置钳制；12 个工具的 id/描述/分类/Schema 自描述、无页面时的指引文案、参数冲突文案；**生命周期**（停用→启用→再停用不抛 ObjectDisposedException、`ShutdownAsync` 后宿主仍可用、接线服务幂等）；**配置桥接**（`UserOnly` 作用域、`seeing.json` 值能经 `IOptionsMonitor` 读到、`AdmitUrl` 遵守配置） |
| 同上 `BrowserPersistLoginTests`（新） | 7 | `PersistLogin` 默认 true；profile 目录默认位置（`{用户目录}/.seeing/browser-profile`）且必须是绝对路径；配置优先级；相对路径展开；`null` 参数抛 `ArgumentNullException`；换目录必须解析出不同路径（否则热重载不重启浏览器） |
| 同上 `BrowserPersistLoginEndToEndTests`（新，真浏览器） | 2 | **cookie 跨浏览器重启存活**（登录 → 彻底关停 → 同 profile 重启 → 服务端仍收到 `SEEING_AUTH`）；`PersistLogin=false` 时相邻会话**看不到**对方的 cookie（隔离生效） |
| 同上 `BrowserEndToEndTests` | 4 | **真浏览器**（`SEEING_BROWSER_TESTS=1` 开关）：打开→快照→按 ref 输入→按 ref 点击→`eval` 验证 DOM 变化→截图落盘→关闭；screencast 收到真实帧；关闭后重开画面仍能推帧；**关闭后立即重开不误杀新页面的流**（`PageClosed` 带页面实例 + `DropSessionAsync` 实例比对） |
| `tests/spine/Seeing.Agent.Tests`（扩） | 7 | `SessionArtifactStore`：落在资源目录内、同名多次内容各自独立、并发不互相覆盖、扩展名净化后仍在界内 |
| `tests/apps/Seeing.Agent.WebUI.Tests`（扩） | 8 | `workbench.browser` 视图键可解析、信封校验零诊断、贡献归属与 `Requires`、模块门控、`DependsOn` 必须为空、工具数与声明一致；**端点门控**（模块停用即不可用、模块 id 与能力包一致） |
| `tests/capabilities/Seeing.Agent.Tools.Metadata.Tests`（改） | 349 | 工具 id 集合从 56 → 68；浏览器同族工具双向路由点名；枚举参数描述必须列出全部取值 |
| `tests/apps/Seeing.Agent.HostComposition.Tests` | 16 | Full 能力集与组合根一致性（含 `browser`） |

**全量**：56 个测试程序集、**6086+ 项测试、0 失败**
（`Seeing.Agent.Scheduler.Tests` 有一项在并行跑全量时偶发失败，单独连跑三次均绿；
根因是该套件用共享 SQLite 临时文件、未隔离并发，与本次改动无关）。

## 十二、手工验收（已完成）

用 `playwright-cli` + Chromium(headless) 驱动 WebUI：

1. 启动日志出现 `Activating module browser`，无 UI 贡献诊断。
2. 会话页「工作台」下拉出现「▦ 浏览器」。
3. 打开后状态条显示「已连接」，空态提示可见（**连接不再被立即断开**）。
4. 地址栏输入 `data:text/html,...` 回车 → 服务端日志 `浏览器已启动（无头）` + `已为会话 … 创建浏览器页面` → 画布出现真实页面（canvas 尺寸从默认 300×150 变为 554×346）。
5. 在画布上点击 → 内部页面背景变红、文字变为 `CLICKED`（**人工输入链路完整**）。
6. 地址栏再输入新页面回车 → 标题切换为新页面（**旧值缺陷已修**）。

## 十三、已知边界

| 项 | 说明 |
|----|------|
| 每会话一个页面 | 不做多标签；agent 无法并行处理两个网页（可开两个会话绕过） |
| 每会话一个 BrowserContext 共享进程 | 一个页面 OOM 会带走同进程其它会话的页面；有崩溃自愈重建 |
| screencast 是 JPEG 有损 | 不影响 agent（agent 走 snapshot / screenshot 独立路径） |
| WS 无签名 | 与 assets 端点同基线；公网需加固 |
| 无网络/console 录制、无 PDF、无下载 | 需要时用 `browser_eval` 或 `webfetch` |
| 冷启动 200–400ms | 未做预热 |
| `browser_eval` 可读页面数据 | 已登录后台的敏感信息可被取出，靠审批拦截 |
| **cookie 跨会话共享**（`PersistLogin=true`） | 两个会话同时操作同一站点账号会互相顶掉登录态；要隔离就设 `PersistLogin=false` |
| **无内建凭据加密** | profile 目录是 Chromium 原生格式（DPAPI/ABE 加密的 cookie）。能读该目录的进程等价于能拿到登录态，靠 OS 文件权限 |
| 持久 profile 体积随使用增长 | `~/.seeing/browser-profile` 会缓存站点数据；未做自动清理 |

## 十三之二、两处实测修复（画面比例 / 底色 / profile 残留）

### 13-2.1 画面被拉伸铺满（用户报「缩放不对、变形」）

**症状**：浏览器画面填满整个工作台面板，内容被横向或纵向拉长。

**根因**：`drawImage(bitmap, 0, 0)` + CSS `width/height: 100%`。位图按帧尺寸，显示尺寸却由 CSS 强制铺满，
一旦帧的宽高比与面板不一致（视口被钳制在 `[320,2560]×[240,1600]`、或 resize 尚未生效），就被非等比拉伸。

**修复**：位图保持 1:1（帧像素，不做重采样），显示尺寸改由 `applyCanvasFit` 按 contain 计算：

```js
const scale = Math.min(rect.width / bmpW, rect.height / bmpH);
canvas.style.width  = `${Math.round(bmpW * scale)}px`;
canvas.style.height = `${Math.round(bmpH * scale)}px`;
```

三个配套细节：

| 细节 | 原因 |
|---|---|
| 不取 `Math.min(1, scale)` | 面板比网页大时应适度放大，否则小视口页面在中间缩成一小块 |
| resize 时立即 `applyCanvasFit` | 面板拖动后可用空间变了，不重算则画面尺寸停留在旧值 |
| `detach` 清掉行内尺寸 | 否则同一 DOM 节点重新 attach 时会先用旧比例渲染一帧 |

**验证**（真实 WebUI + 真实浏览器，宽页面 1700px 内容）：

```
canvasCss 675×422, canvasBitmap 1280×800
cssRatio 1.599, bmpRatio 1.600, distortionPercent 0.06
```

### 13-2.2 底色是黑色（用户报「希望变白或灰」）

`stage` / `canvas` / 空态 `overlay` 三处原先都是深色（`#141414`、`#1e1e1e`、`rgba(20,20,20,0.92)`）。
白色网页 + 深色留白看起来像「加载失败」，也会让 contain 的留白非常刺眼。

改为中性浅灰 `#f0f0f0`（overlay `rgba(240,240,240,0.92)`，文字改深色）。
实测采样：画面像素 `(255,255,255)`、stage 背景 `rgb(240, 240, 240)`，画布垂直居中（上下各留 211px）。

### 13-2.3 残留进程导致浏览器永久起不来（`PersistLogin` 引入的新失败模式）

**症状**：日志 `ProcessException: The browser is already running for ...browser-profile`，
之后每次启动都失败，界面上只有「画面不动」，没有任何指向根因的提示。

**根因**：持久 profile 是进程级的独占资源。上一个浏览器进程没退干净（崩溃、强杀、测试中断、
WebUI 被 `Stop-Process`）时，它的 profile 主锁仍在，Chromium 拒绝启动。
因为是**持久**目录，这把锁不会自己消失 —— 与临时 profile 的「每次全新」不同，
这是持久化换来的固有代价。

**修复**：启动时若撞到 `already running`，清理残留进程后重试（条件轮询，不猜固定时长）。

```csharp
catch (ProcessException ex) when (profileDirectory is not null && IsProfileLocked(ex))
{
    var killed = KillLeftoverProcess(profileDirectory);
    if (killed == 0) throw;
    browser = await RetryLaunchAfterCleanupAsync(launchOptions, profileDirectory, ex);
}
```

**关键安全约束**：只杀 PID 文件里记录的那个进程，**绝不按进程名批量清理**。
用户自己的 Edge 用同一个可执行文件，按名杀会连带关掉他正在用的浏览器（未提交的表单、播放中的视频都是真实损失）。

PID 记录落在 profile 目录内的 `.seeing-engine.pid`（两行：PID + 可执行文件路径），
路径校验用来防 PID 复用误杀；干净关停时删除该文件。不用 WMI 读命令行：
那需要额外的 `System.Management` 包，且非 Windows 环境不可用。

**验证**（故意的残留进程场景）：

```
残留进程 PID 11088 仍在运行
=== BrowserEngine 启动 ===
✅ 启动成功: Edg/157.0.4341.42
残留进程已退出: True
干净关停后 PID 文件存在: False
用户 Edge 进程总数: 266（未受影响，说明没有误杀）
```


## 十四、后续演进

多标签（`BrowserSessionHost` 改页集合 + ref 前缀带标签号）；网络/console 录制为工具；下载限定到工作区临时目录 + `IWorkspacePathGate`；**每会话独立持久 profile**（当前是全局共享一份，若要按会话隔离需按 sessionId 派生目录，代价是每个会话各登录一次）；浏览器进程池；WS 签名；profile 体积清理。

## 十五、人工接管（takeover）

### 15.1 问题

人与 agent 同时操作同一页面会产生不可预测的结果：

- agent 正按 `ref` 点击，用户同时滚动 → 元素位移、ref 失效、点错元素
- agent 的 `mousedown` 与用户的 `mousedown` 各自 `up` 配对 → 鼠标状态机错乱
- 用户自己导航后，agent 手里那批 ref 全部作废，但它并不知道

之前「智能体正在操作」只是一个右上角小提示条，**不拦截输入**。于是用户看到的是：
提示条在闪（鼠标移动也算 busy，已在前一轮修掉），同时自己的点击和 agent 的操作互相打架。

### 15.2 所有权模型

明确划分：**agent 操作期间页面归它，用户要操作必须先接管。**

| 状态 | 遮罩 | 用户输入 | 显示 |
|------|------|---------|------|
| `_busy && !_takenOver` | 全屏覆盖，`pointer-events: auto` | **全部拦截** | 黄色「智能体正在操作 · 点击接管」 |
| `_takenOver` | 无 | 放行 | 绿色「已接管，智能体暂停」 |
| 其它 | 无 | 放行 | — |

遮罩底色 `rgba(0,0,0,0.06)` 是刻意选的：用户必须看清 agent 在做什么（这是工作台存在的理由），
只需要感知到「我现在点不了」。拖动验证码也必须在接管之后——这是用户明确确认的语义。

### 15.3 接管的三步

```csharp
// BrowserStreamEndpoints.HandleTakeoverAsync
1. runtime.InterruptAsync(sessionId)                    // 硬停当前轮
2. runtime.DeliverAsync(Inject + System, "已被接管")     // 告知，不唤醒
3. sink.OnTakeoverResult(interrupted, actionCount)      // 前端解除遮罩
```

**为什么用 `Inject` 而不是 `Wake`**：`Wake` 会立即激活一轮新执行——用户刚说「我要自己操作」，
结果立刻又跑一轮 LLM 调用（还可能再去动浏览器），既浪费 token 也违背接管意图。
`Inject` 只落盘，下次用户主动发话时自然进入上下文。

**为什么前端要等服务端确认**：点了接管就立刻解除遮罩的话，中断失败时用户会以为已获得控制权，
而 agent 还在操作——那种假象比不响应更糟。所以 `OnTakeoverResult` 才置 `_takenOver`。

### 15.4 一个必须诚实说明的物理限制

**中断无法撤销已经发出去的 CDP 指令。** 若接管时那个工具正在执行，它的点击/输入**可能仍然生效**。
这不是缺陷，是能力边界。确认卡片上写明了这一点：

> 接管后，当前这一轮任务会被**立即中断**。
> 正在执行的这一步可能已经生效（已发出的操作无法撤销）。

另一个延迟：若 agent 正在跑工具 A，工具 B 在排队；B 要等拿到锁、检查 `CancellationToken`
才会发现被取消。所以**中断到生效最多有一个工具的延迟**。

### 15.5 人工操作记录

`BrowserPageHandle` 记录人工输入到环形缓冲（上限 50 条）+ 结构化日志：

```csharp
public sealed record BrowserHumanAction
{
    public required DateTimeOffset At { get; init; }
    public required string Kind { get; init; }    // click / type / key / scroll / navigate
    public required string Target { get; init; }
    public string? Value { get; init; }           // 密码字段为 ***
    public string Url { get; init; } = "";
}
```

| 决定 | 理由 |
|------|------|
| 只在人工输入路径记录 | agent 工具调用不记。否则告知文本会把自己干的事说成用户干的 |
| `click` 在 `mouseup` 记录 | 一次完整点击 = down + up；只在 down 记会让「按下未抬起」的误触也算点击 |
| `scroll` 按主方向概括 | 逐条记 `deltaY` 会让摘要被滚动条目淹没 |
| 密码字段脱敏为 `***` | 输入内容会进日志（常被长期保留与转发），明文密码是不可逆泄露。读 `activeElement.type` 判断，读失败时**默认脱敏** |
| 上限 50 条 + `ClearHumanActions()` | 无限累积会让摘要淹没重点；接管处理后清空避免重复告知 |

**不做可回放**（用户确认）：坐标点击在页面结构变化后必然失效，`ref` 只在同一 `RefEpoch` 内有效。
回放工具会给出「看起来能用、实际常常打偏」的假能力。

### 15.6 告知文本

```
[浏览器接管] 用户于 20:35:12 手动接管了浏览器页面，上一轮执行已被中断。
请停止操作浏览器，等待用户明确指示。

用户此前的操作（供你理解页面为何已变化）：
- 点击 (120, 340)
- 在 当前焦点元素 输入 “搜索词”
- 滚动 向下
```

用户选了「最小信息量」档：只说被接管 + 用户做了什么，不附页面状态（agent 可自己 `browser_snapshot` 获取）。

### 15.7 实测

真实 WebUI 端到端（agent 导航到 8 秒延迟的本地站，制造持续 busy）：

```
遮罩出现（agent 操作中）                      第 3.2 秒 ✅
遮罩拦截点击（elementFromPoint 命中遮罩）      isMaskOnTop: true ✅
确认卡片出现                                  ✅
点「接管并停止智能体」                          ✅
接管后状态：已接管（中断了智能体，记录 0 条人工操作）✅
```

`interrupted: true` 证明真中断了执行（不是「当时没在跑」的假成功）。

新增 4 个测试（`BrowserHumanActionTests`，真浏览器）：

| 用例 | 钉住什么 |
|------|---------|
| `HumanClicksAndTyping_ShouldBeRecorded` | 点击/滚动/输入都被记录；普通字段不脱敏 |
| `PasswordTyping_ShouldBeMasked` | 密码框输入记为 `***` |
| `ClearHumanActions_ShouldEmptyTheList` | 清空接口有效 |
| `AgentToolCalls_ShouldNotBeRecordedAsHumanActions` | agent 工具调用不混入人工记录 |

## 十六、取消会话不中止等待（修复）

### 现象

取消会话后，执行中的 `browser_wait_for` 仍是「执行中」，要等满超时才结束。

### 根因（两层）

**① PuppeteerSharp 的 `WaitFor*` 只接受超时，没有 CancellationToken**

```csharp
// 修复前：取消令牌根本没传下去
await _page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Timeout = timeoutMs });
```

`RunAsync` 拿到了 `cancellationToken`，但只用于自己的 `_gate.WaitAsync`，没有影响内部的等待。

**② `kind=timeout` 分支每片都 return true**

第一次尝试分片实现时，`default` 分支在 `Task.Delay(sliceMs, ct)` 之后 `return true`，
于是第一片（200ms）就「成功」了 —— 30 秒等待变成 200ms，取消也无从生效。

### 修复

把等待切成 200ms 分片，片间检查取消令牌：

```csharp
while (true)
{
    cancellationToken.ThrowIfCancellationRequested();
    var remainingMs = ...;
    var sliceMs = Math.Min(remainingMs, SliceMs);

    try
    {
        if (await TryWaitOnceAsync(condition, sliceMs, ct)) return true;
        if (isLastSlice) { /* timeout 类型算成功，其它类型抛 TimeoutException */ }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;   // ← 必须排在超时判定之前
    }
    catch (Exception ex) when (IsWaitTimeout(ex)) { /* 继续下一片 */ }
}
```

**三个关键细节**：

| 细节 | 原因 |
|------|------|
| `OperationCanceledException` 分支必须排在超时判定**之前** | `Task.Delay(slice, ct)` 取消时抛的也是 `OperationCanceledException`；先按超时吞掉就永远取消不了 |
| `IsWaitTimeout` 要匹配类型名 | PuppeteerSharp 抛的是 `WaitTaskTimeoutException`，**不是** BCL `TimeoutException` 的子类 |
| `kind=timeout` 在预算用尽时返回 `true` | 它的语义是「等够时长即成功」，不是「等不到条件则失败」 |

### 实测

```
kind=timeout，30 秒等待，1.5 秒后取消  → 取消后 2ms 脱离      ✅
kind=selector（不存在元素），1.5 秒后取消 → 取消后 152ms 脱离  ✅
正常完成（条件立刻满足）                → 仍正确             ✅
真正超时（1 秒）                       → 1008ms 抛 TimeoutException ✅
```

## 十七、浏览器设置页（配置卡）

### 为什么需要

`Headless` 是「能否过反爬」与「是否弹窗」的决定性开关，两种需求都存在；
`AntiDetection` 涉及「去沙箱 / 要真 GPU」的兼容性取舍。
写死任何一侧都会让另一类用户无法工作，而让用户手改 JSON 既容易写错、
又看不到「当前生效值」与「默认路径」。

### 位置与形态

```
设置页 → 「浏览器」页签（order: 50，Requires: ["browser"]）
```

四张卡：运行模式 / 登录态 / 视口与超时 / 安全边界。

每个开关都带**代价说明**，例如：

> 复用持久 profile：开启后所有会话共用同一个 profile……**代价是 cookie 跨会话共享**——
> 两个会话同时操作同一站点账号会互相顶掉登录态。

### 关键实现约束

| 约束 | 原因 |
|------|------|
| 必须写 **用户级** 配置 | `Browser` 节注册为 `UserOnly`（`ExecutablePath` 决定启动哪个可执行文件、`AllowPrivateHosts` 决定能否访问内网）。写项目级会被 `ValidateScope` 抛 `ConfigScopeException` —— 这是有意保留的硬边界 |
| 派生只读属性必须 `[JsonIgnore]` | 保存时序列化整个 `BrowserOptions`。不标注就会把 `EffectiveWidth` / `IdleTimeout` / `OperationTimeout` 写进 seeing.json —— 它们没有 setter、读不回来，纯粹是噪声，还会让用户误以为可手改 |
| 注入属性不能与类型同名 | `@inject IOptionsMonitor<BrowserOptions> BrowserOptions` 会在 Razor 里遮蔽类型名，导致 `BrowserOptions.SectionName` 解析到实例上而编译失败 |
| `ResolveProfileDirectory` 改为 public | 设置页要展示实际生效路径。宿主自己拼路径会让「默认位置」出现第二份实现，漂移时症状是「设置页显示与实际用的不同」 |

### 分层守门（`BrowserLayeringTests`）

用户特别强调「不能依赖 Core」，因此用反射钉住：

| 用例 | 钉住什么 |
|------|---------|
| `CapabilityAssembly_ShouldNotReferenceCore` | 程序集引用列表里不能出现 `Seeing.Agent.Core*` / `Seeing.Agent.Hosting*` |
| `CapabilityAssembly_ShouldReferenceExpectedDownstreamLayers` | 必须只依赖 Abstractions + Tools.Support |
| `BrowserOptions_ShouldLiveInCapabilityNamespace` | 配置类型留在能力包（模块自己声明自己的配置） |
| `ResolveProfileDirectory_ShouldBePublicForHostDisplay` | 该方法是宿主展示用的公开入口 |
| `BrowserOptions_DerivedProperties_ShouldNotBeSerialized` | 派生属性不出现在序列化结果里 |

**为什么用反射而不只看 csproj**：间接引用（A→B→Core）同样造成类型泄漏，csproj 看不出。

### 实测

```
设置页签列表: 常规 / 启动能力 / 工作模式 / 调度器 / 记忆 / 浏览器 / 模型能力 / 插件
点击「浏览器」→ 5 个开关 + 输入框 + 说明文案全部渲染  ✅
改 IdleSeconds=999 → 保存 → 落盘 SeeingAgent.Browser  ✅
出现「尚未生效，请重启」提示                          ✅
保存后只含 8 个可写属性，无派生属性                    ✅
```

## 十八、热重载的真实边界（修复一个静默失效）

### 问题

在设置页关掉「无头模式」并保存后，配置确实写入了、`IOptionsMonitor` 也返回了新值，
**但浏览器进程仍是旧的**——开关形同虚设，且没有任何报错。

根因：`ProfileChanged()` 只比对 profile 目录。

```csharp
// 修复前
private bool ProfileChanged()
{
    var desired = options.PersistLogin ? ResolveProfileDirectory(options) : null;
    return !string.Equals(desired, _launchedProfileDirectory, ...);
    // ↑ Headless / AntiDetection / ExecutablePath / ExtraArgs 全被忽略
}
```

### 修复：进程级签名

```csharp
public static string ComputeSignature(BrowserOptions options) => string.Join('\u001e',
    options.Headless,
    options.AntiDetection,
    options.ExecutablePath ?? string.Empty,
    options.UserAgent ?? string.Empty,
    extraArgsJoined,
    profileOrTemp);
```

**为什么不逐项比较**：漏项是迟早的事（新增一个启动参数就容易忘）。拼成签名后，
任何进入签名的配置变化都会触发重启，增删项只需改一处。

### 哪些项进签名（实测确认的边界）

| 配置项 | 进签名 | 原因 |
|--------|--------|------|
| `Headless` | ✅ | 启动参数 |
| `AntiDetection` | ✅ | 启动参数 + 页面注入 |
| `ExecutablePath` | ✅ | 启动哪个可执行文件 |
| `UserAgent` | ✅ | 页面级设置，需重新应用 |
| `ExtraArgs` | ✅ | 启动参数（**顺序也进签名**——后写覆盖前写） |
| `PersistLogin` / `ProfileDirectory` | ✅ | profile 锁 |
| `IdleSeconds` | ❌ | 每次回收扫描实时读 |
| `ViewportWidth/Height` | ❌ | 建页时读（影响新页面，不需重启） |
| `DefaultTimeoutSeconds` | ❌ | 每次操作实时读 |
| `AllowPrivateHosts` | ❌ | 每次导航实时读 |

### 设置页的提示也据此修正

之前无条件提示「请重启」，会让用户白重启（改个空闲超时也要重启？），
也让提示失去可信度。现在只在签名真的变化时才提示。

### 实测

```
Headless true → false  → 签名变化 → 浏览器重启 → 新页面 UA 不含 Headless  ✅
只改 IdleSeconds        → 签名不变 → 不重启（运行期配置）                 ✅
ExtraArgs 顺序对调      → 签名变化（正确，顺序影响行为）                  ✅
```

新增 15 个测试（`BrowserRestartSignatureTests`）：

| 用例 | 钉住什么 |
|------|---------|
| `ProcessLevelSetting_ShouldChangeSignature`（参数化 × 7） | 7 个进程级项改动必须改变签名 |
| `RuntimeSetting_ShouldNotChangeSignature`（参数化 × 5） | 5 个运行期项改动不得改变签名 |
| `Signature_ShouldBeStableForEquivalentConfigs` | 仅运行期项不同 → 签名相同 |
| `Signature_ShouldDistinguishExtraArgsOrdering` | 参数顺序进签名 |
| `Signature_NullOptions_ShouldThrow` | 空参数防御 |




