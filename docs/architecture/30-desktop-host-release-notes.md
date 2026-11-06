# 桌面宿主（Seeing.Agent.Desktop）

把 WebUI 打包成「双击即启动」的原生窗口应用，**不改动任何 WebUI 页面、CSS、JS 或 circuit 服务**。

## 为什么不是 MAUI / Blazor Hybrid

WebUI 是 Blazor **Server**：`AddServerSideBlazor()` + `MapBlazorHub()` + SignalR circuit，
页面里依赖 `CircuitContext`、`ICircuitResourceCleanup`、Scoped circuit 服务与 `_Host` 承载页。

MAUI 的 `BlazorWebView` 是 **Hybrid 渲染**模型（进程内直接渲染组件树，没有 HTTP/circuit），
迁移意味着重写承载页、生命周期与全部 circuit 作用域服务的语义 —— 那是重写，不是打包。

桌面宿主走的是另一条路：**同进程起 Kestrel，原生 WebView 指向它**。
对 WebUI 而言一切照旧（就是一个浏览器在访问 `127.0.0.1`），
对外则是一个带图标、无地址栏的桌面窗口。

## 结构

```
Seeing.Agent.Desktop (本目录)
├── Program.cs                 窗口 + 宿主生命周期 + 外链拦截 + 端口分配
├── Assets/seeing.ico|png      应用图标（由 tools/generate-icon.ps1 生成）
└── tools/generate-icon.ps1    从 WebUI 的 wwwroot/seeing-mark.png 光栅化

Seeing.Agent.WebUI
└── Hosting/WebUiHost.cs       宿主组装（单一真相源）
    ├── WebUI/Program.cs       三行：BuildAsync → RunAsync（浏览器宿主）
    └── Desktop/Program.cs     BuildAsync → StartAsync → 开窗（桌面宿主）
```

**单一真相源**是这套结构的核心：DI 注册表只存在 `WebUiHost.BuildAsync` 一处。
否则桌面版会随 WebUI 演进慢慢漂移，直到某个能力在桌面端莫名失效。

## 启动流程

1. **解析工作区**（`DesktopWorkspace.Apply`，在宿主组装之前）。
2. 分配回环端口（从 7658 起，逐个探测 IPv4/IPv6 通配与回环）。
3. 后台线程 `WebUiHost.BuildAsync` → `StartAsync`（不阻塞 UI 线程）。
4. 就绪后开原生窗口，`Load("http://127.0.0.1:<port>")`。
5. 窗口关闭 → `StopAsync`，Host 正常停止（circuit、后台执行、文件句柄按 .NET Host 规则释放）。

## 工作区解析（桌面版特有）

`WorkspaceProvider` 的兜底是「进程当前工作目录」。桌面应用从资源管理器/快捷方式启动时，
cwd 是 **exe 所在目录**（Windows 下常为 `Program Files`，不可写）。
若把它当工作区，会话库、记忆与文件写入会落到不可写目录，表现为启动后功能莫名失败。

因此桌面宿主在组装前只做一件事：**把进程起始位置摆到合理的地方**。

| 顺序 | 来源 | 说明 |
|------|------|------|
| 1 | `SEEING_WORKSPACE_ROOT` 环境变量 | 用户显式指定；目录不存在则跳过 |
| 2 | 进程当前目录 | **当且仅当它不是 exe 目录**；从终端/IDE 启动时保留用户显式意图 |
| 3 | `%LOCALAPPDATA%\Seeing` | 应用默认：稳定、可写、不污染文档目录，且不被 OneDrive 同步（Linux/macOS 对应 `~/.local/share/Seeing`） |

第 2 步的「不是 exe 目录」判定区分大小写不敏感、忽略尾随分隔符。
第 3 步的目录名是 **`Seeing`**（产品名），不是 `Seeing.Agent`。

### 桌面壳不决定最终工作区

以上只是「起始位置」。**最终工作区仍由 Core 的 `WorkspaceProvider.InitializeAsync` 裁决**：

```
SEEING_WORKSPACE_ROOT → 项目级 Workspace.CustomPath
  → Workspace.UseGlobal 为真时的用户级 GlobalWorkspaceRoot
  → 启动目录（即上表选定的位置）
```

桌面壳**不读**用户级 `GlobalWorkspaceRoot`。Core 的语义是「配置了全局目录也要显式 `Use` 才生效」，
壳若自己读就会绕过 `Workspace.UseGlobal` 开关，使「配了全局目录但项目里选择不使用」的配置失效。

`Workspace` 节必须嵌套在 `SeeingAgent` 之下（`WorkspaceProvider` 按
`seeing.json` → `SeeingAgent.Workspace` 读取）；写在顶层会被静默忽略。

解析必须在 `WebUiHost.BuildAsync` **之前**执行：`WorkspaceProvider` 构造时就把 cwd
记成 `StartupDirectory`，之后再改 cwd 不会更新它。

## 关键约束（改动前必读）

| 约束 | 原因 |
|------|------|
| `OutputType` 必须是 `WinExe` | `Exe` 生成控制台子系统程序，双击会额外弹出 cmd 窗口（黑框 + 应用窗口两个窗口） |
| 日志必须落盘 | `WinExe` 没有 stdout/stderr；用 `DesktopFileLoggerProvider` 写 `~/.seeing/logs/seeing-desktop-yyyyMMdd.log` |
| 启动失败须用窗口提示 | 没有控制台，「双击没反应」无从排查；用同一个 WebView 显示错误页（不用 WinForms，那会把 TFM 绑到 `net10.0-windows`） |
| 工作区解析必须在 `BuildAsync` 之前 | `WorkspaceProvider` 构造时把 cwd 记为 `StartupDirectory`，之后改 cwd 不生效 |
| 桌面壳不得自行读取 `GlobalWorkspaceRoot` | 会绕过 `Workspace.UseGlobal` 开关；工作区只由 Core 裁决 |
| 内容根必须是 `AppContext.BaseDirectory` | 否则 `WebApplication.CreateBuilder` 以宿主进程工作目录为准，静态资源清单与 wwwroot 定位失败 |
| `AddApplicationPart(WebUI 程序集)` | 桌面宿主是「外部入口程序集」；不注册的话 `_Host.cshtml` 不会被发现，整窗口 404 |
| `StaticWebAssetBasePath=/`（见 csproj） | 被引用项目的静态资源默认挂到 `_content/<包名>/`，会让 `css/*.css`、`js/app.js` 全部 404 |
| `--urls` 与 `--DisableHttpsRedirection=true` 走命令行 | **不能**用 `Environment.SetEnvironmentVariable`：WebUI 派生的 ChannelHost/Gateway 子进程会继承环境变量，导致监听地址被污染 |
| 只绑 `127.0.0.1` | 桌面形态下不应把本地 Agent 暴露到局域网 |

## 发布

**推荐用脚本**（先清理 obj/bin，规避 RID 交叉污染）：

```powershell
pwsh -File samples/Seeing.Agent.Desktop/tools/publish.ps1 -Runtime win-x64
```

产物约 56 MB，目标机需预装 .NET 10 运行时。等价的直接命令：

```bash
# Windows / macOS(arm64) / Linux 分别把 -r 换成 win-x64 / osx-arm64 / linux-x64
dotnet publish samples/Seeing.Agent.Desktop -c Release -r win-x64 -o artifacts/seeing-win-x64
```

产物是文件夹形态（非单文件）。

### 已知陷阱：CS1501「方法没有采用 N 个参数的重载」

改了 WebUI 的公共方法签名（例如 `WebUiHost.BuildAsync` 增参）后，桌面宿主
却仍按旧签名编译。这是**增量构建缓存失效**，不是代码问题：

`samples/Seeing.Agent.WebUI/obj/<Config>/net10.0/` 下并存 `win-x64` /
`linux-x64` / `osx-arm64` / `ref` / `refint` 多个 RID 分支，共用同一组
`.cache` 文件。用不同 `-r` 交替构建后，MSBuild 增量判断读到交叉污染的时间戳，
误判为无需重编译。**`publish.ps1` 的清理步骤就是为此而设，不要跳过。**

判断方法：比较 `WebUiHost.cs` 与
`obj/Release/net10.0/win-x64/ref/Seeing.Agent.WebUI.dll` 的修改时间，
后者更早即中招。

### 为什么不是自包含

WebUI 的 `OutputType` 是 `Exe`（CLI 的 `seeing web` 要独立启动 `Seeing.Agent.WebUI.dll`），
而 SDK 规则 **NETSDK1150** 禁止「自包含的可执行文件引用非自包含的可执行文件」。

命令行 `-p:SelfContained=true` 之所以不报错，是因为该属性作为**全局属性**广播到所有项目；
Visual Studio 的发布配置（`FolderProfile.pubxml`）只把它写在入口项目的属性组里，
不参与项目引用的属性传递，于是稳定失败。

为消除这种「入口不同、结果不同」的差异，`Seeing.Agent.Desktop.csproj` 显式声明
`<SelfContained>false</SelfContained>`，发布配置也不再设置该属性。

若确实需要自包含（给未装 .NET 的机器），仍可用命令行显式指定 ——
全局属性会覆盖项目级声明：

```bash
dotnet publish samples/Seeing.Agent.Desktop -c Release -r win-x64 -p:SelfContained=true -o artifacts/seeing-win-x64-sc
```

但**不要**把这个开关写进 `.pubxml` 或在 VS 发布界面选「独立」部署模式，那会重新触发 NETSDK1150。

**为什么不用 `PublishSingleFile=true`**：PhotinoX 依赖原生库，单文件需解包到临时目录，
首次启动慢，且部分 Linux 发行版 `/tmp` 为 `noexec` 会直接失败。

### 平台依赖

| 平台 | WebView 引擎 | 额外要求 |
|------|-------------|---------|
| 所有平台 | — | **.NET 10 运行时**（产物为框架依赖，不自带运行时） |
| Windows 10/11 | WebView2 (Chromium) | Win11 自带；Win10 需 WebView2 Runtime |
| macOS 12+ | WKWebView | 系统自带；分发给他人需签名 + 公证，否则 Gatekeeper 拦截 |
| Linux x64 | WebKitGTK 4.1 | Ubuntu 24.04+ 默认有；老发行版需装 `libwebkit2gtk-4.1-0` |

## 图标

`Assets/` 下的文件由 `tools/generate-icon.ps1` 从
`samples/Seeing.Agent.WebUI/wwwroot/seeing-mark.png`（品牌首字母 S，透明底、高 1024）生成，
多尺寸 PNG + 合成 ICO（16/24/32/48/64/128/256，内嵌 PNG 条目）。

**为什么用首字母 S 而不是完整字标**：完整 `SEEING` 字标宽高比 ≈4.56，压进 16×16 / 32×32
的正方形会糊成一条不可读的横线（实测 16px 下纯色像素为 0）。S 的近方形比例能撑住小尺寸。

改了品牌主图后重新生成：

```powershell
pwsh -File samples/Seeing.Agent.Desktop/tools/generate-icon.ps1
```

> 主图 `seeing-mark.png` 由源图裁剪得来（白底转透明）。
> 换了源图就从主图这一步重做，不要手工逐个改 `Assets/` 里的 PNG —— 多尺寸之间会漂移。

## 已知边界

- **无单实例控制**：重复双击会开第二个窗口并占用下一个端口。后续如需「唤回已有窗口」，
  需加跨平台单实例锁（Windows 命名 Mutex / Unix 文件锁）+ 端口记录。
- **`SetStatusBarEnabled` 在 macOS/Linux 无原生效果**（PhotinoX 明确说明），Windows 上有效。
- **外链一律外开**：`RegisterNavigationStartingHandler` 拦截任何非本机地址交给系统浏览器，
  避免应用窗口被外部站点带走且无法返回。
- **窗口尺寸/位置不持久化**：每次启动都是 1440×900 居中。
- **工作区判定依赖「不是 exe 目录」这一条**：若用户把 exe 直接放到自己的项目目录里运行，
  cwd 会等于 exe 目录而被跳过，回退到全局默认/文档目录。此时可用
  `SEEING_WORKSPACE_ROOT` 显式指定。
