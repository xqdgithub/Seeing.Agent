# Seeing.Agent TUI 入口设计（seeing-tui）

**日期**：2026-09-21
**分支**：feature/modular-architecture
**状态**：设计定稿 v2.2（架构终审 **PASS**；技术终审 PASS WITH ISSUES 的 2 项已修订，待最终确认）

> v2.2 增量修订：① §6.1 补「进行中 step 半途回放」规则（`stream.start` 被环形缓冲挤出时，未见 start 的进行中 step 跳过 delta 累加，由 `stream.complete` 整体覆盖），消除长流式切回双写；② §4.3 修正 `IHost` 非 `IAsyncDisposable`，改 `using var host`。

**v2.1 修订摘要（对应复审剩余项）**：① §6.1 回放去重改为「按 key 幂等 upsert + 进行中 LoopId 保留」（修正「切回运行中会话」失效）；② §5.1.1 `CarriableModuleTypes` 逐字沿用 `WebHostShape` 约定；③ §4.3 交叉引用与 Host 释放；④ §5.1.2 措辞；⑤ §6.3 补「阻塞读为刻意设计」「重启前剔除已 Commit 内容」「`Measure` 需 `RenderOptions(capabilities,size)`」；⑥ §6.4 Markdig pipeline 配置；⑦ §7.3 退出屏障改由 root Singleton 取用；⑧ §12.2 病句清理；⑨ §0.3 `Status`/`Progress` 获取方式；⑩ §13/§15 补「切回运行中会话续渲」测试与 P0 验证项。

**v2 修订摘要（对应审查 Issues）**
- 引擎下沉到 sample，`Hosting.Tui` 薄化为「镜像 Hosting.Web」的纯接线包（解 M2/M3，Spectre 不再进脊柱层）。
- 修正启动时序：补 `await host.StartAsync()` / `StopAsync()`（阻断 B1）。
- 修正 Spectre `Live` API：0.49.1 与 0.57.2 均**只有委托式 `Start/StartAsync`，无 `Stop()`**；改为「委托 + 专用渲染线程 + 每轮新建 LiveDisplay」模型（阻断 H-tech1）。
- 依赖升级到最新稳定版：`Spectre.Console 0.57.2`、`Markdig 1.4.0`；补充 `Spectre.Console.Ansi` 传递依赖说明（用户要求）。
- 修正 TokenBudget 接线：`AddTokenBudgetIntegration(registry, builder.Configuration)` + `AddTokenBudgetHooks()` + `IBudgetStatusNotifier` + `UseTokenBudgetHooks()`（阻断 H-tech2）。
- 修正 `SurfaceRegistry`：权限与问答是**两个独立实例**，须分别注册/注销（高 H1/H-tech4）。
- 修正退出屏障：`IPersistenceFlusher.FlushAllAsync`（非 `FlushAsync`）且须经 `ISessionStore`/`ISessionGroupStore` 转型取用（中 M1/H-tech3）。
- 统一事件回放/去重口径，消除 §5.2 与 §6.1 矛盾（中 tech6）。
- 输入层改为 raw stdin 读取（`Console.ReadKey` 无法承载 bracketed paste / CSI-u），补 `TreatControlCAsInput` 与可取消输入线程（中 tech8）。
- 移除 v1 的 `Console.SetOut` 代理设计（`AnsiConsoleOutput.IsTerminal` 语义不确定），降级为 Phase 6 可选 spike（中 tech9）。
- 修正会话存储路径：`<workspace>/.seeing/sessions`（非 `~/.seeing/sessions`）（低 tech12）。
- 补 `--boot`、`InitializeAcpCommands`、`ITuiEventPump` 抽象、空集确认期、`CarriableModuleTypes` 对齐。

**依据**
- `docs/architecture/01-overview.md`、`02-modules.md`、`03-configuration-lifecycle.md`、`04-development-standards.md`、`05-extension-guide.md`、`06-compliance-audit.md`
- `docs/architecture/08-permission-authorization-release-notes.md`
- `docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md`
- `docs/superpowers/specs/2026-09-20-question-tool-and-interaction-infrastructure-design.md`
- `docs/superpowers/specs/2026-09-21-llm-stream-turn-retry-and-error-persistence-design.md`
- 代码基线：`samples/Seeing.Agent.WebUI/Program.cs`、`samples/Seeing.Agent.WebUI/Services/{SessionEventStreamRouter,EventStreamHandler,TaskCardAggregator,TaskSessionResolver,PermissionInbox,WebUiSurfacePresenter}.cs`、`samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`、`src/hosting/Seeing.Agent.Hosting/{ChatOrchestrator.cs,HostingServiceCollectionExtensions.cs,Execution/{ExecutionJobService.cs,ChatEventTracker.cs}}`、`src/hosting/Seeing.Agent.Hosting.Headless/*`、`src/hosting/Seeing.Agent.Hosting.Web/*`、`src/spine/Seeing.Agent.Core/{Extensions/ServiceCollectionExtensions.cs,Extensions/PermissionServiceExtensions.cs,Core/Questions/QuestionServiceExtensions.cs,Core/Interactions/SurfaceRegistry.cs,Execution/ExecutionEventPublisher.cs,Configuration/WorkspaceProvider.cs}`、`src/abstractions/Seeing.Agent.Abstractions/{Chat,Events,Interactions,Questions,Permissions,Llm}/**`、`src/primitives/Seeing.Session/{Core,Persistence,Storage}/**`、`src/capabilities/Seeing.Agent.TokenBudget/Extensions/TokenBudgetServiceExtensions.cs`

---

## 0. 依赖与版本策略

### 0.1 目标依赖

| 包 | 版本 | 现状 | 处置 |
|----|------|------|------|
| `Spectre.Console` | **0.57.2**（最新稳定） | `Directory.Packages.props` 现为 `0.49.1`（注释「TUI 相关」，无人使用） | **升级到 0.57.2** |
| `Spectre.Console.Cli` | 不使用 | 现为 `0.49.1` | 本设计不使用（其最新稳定仅 `0.55.0`，滞后于主包；参数面极小，改用 `System.CommandLine`）。保留 CPM 条目不动，或后续单独清理 |
| `Markdig` | **1.4.0**（最新稳定） | CPM 现为 `0.41.0`，**当前无任何 CPM 项目引用**（WebUI 走非 CPM 显式 `1.3.2`） | 升级到 1.4.0，不影响 WebUI |
| `System.CommandLine` | 2.0.11（CPM 现值） | CLI sample 已用 | 直接复用；最新稳定 2.0.12 可作低风险同步项 |

### 0.2 版本策略（用户要求：用最新稳定版，避免后续升级问题）

1. **只用最新稳定版**（无 `-preview`/`-alpha`），并在 `Directory.Packages.props` 单点维护。
2. **P0 spike 以编译 + 反射显式校验**所有用到的 Spectre API，不依赖未文档化行为；发现问题立即在本设计内改为后备实现，而不是降级到旧版本。
3. **不锁定旧版本**：若升级引入破坏性 API 变更，修调用点，不改版本回退。
4. 记录已验证的**关键 API 事实**（见 §0.3），实施时以本表为准。

### 0.3 已核验的 Spectre.Console 0.57.2 事实（实施硬约束）

| 事实 | 证据 | 设计含义 |
|------|------|----------|
| `LiveDisplay` **只有** `Start(Action<LiveDisplayContext>)` / `Start<T>(Func<...>)` / `StartAsync(...)`；**无 `Stop()`、无无参 `Start()`** | 对 0.57.2 `lib/net8.0/Spectre.Console.dll` 反射枚举 `LiveDisplay` 成员 | §6.3 固化模型必须用「委托返回 → Write → 重启」 |
| `LiveDisplayContext` 只有 `UpdateTarget(IRenderable)` / `Refresh()` | 同上 | 活动区原地更新即用此二者 |
| `LiveDisplay.AutoClear` / `Overflow` / `Cropping` 可读写；`VerticalOverflow` = `Crop`/`Ellipsis`/`Visible` | 同上 | 用 `AutoClear(false)` 保留末帧（**实现说明**：最终改用 `AutoClear(true)` + `Overflow(Crop)` + `Cropping(Top)` + `TailClipRenderable`，理由见 §6.3） |
| `AnsiConsoleOutput` 仅有 `AnsiConsoleOutput(TextWriter)` 构造；`IsTerminal` 只读 | 同上 | 代理 writer 方案存在 `IsTerminal` 误判风险 → v2 不采用 |
| **无内置 Markdown 渲染**（`Spectre.Console` 公共类型中无任何 Markdown 类型）；NuGet 无 `Spectre.Console.Markdown` 包（404） | 公共类型枚举 + NuGet flat container | Markdown 必须用 Markdig 自行映射到 Spectre 原语 |
| 可用原语：`Table`/`Panel`/`Rule`/`Grid`/`Rows`/`Columns`/`Layout`/`Tree`/`Markup`/`Text`/`Paragraph`/`SelectionPrompt<T>`/`MultiSelectionPrompt<T>`/`TextPrompt<T>`/`ConfirmationPrompt`/`Spinner`；`Status`/`Progress` 需经 `AnsiConsole.Status()` / `AnsiConsole.Progress()` 获取（构造依赖 `IAnsiConsole`） | 公共类型枚举 + 反射 | 渲染与提示全部可满足 |
| `net10.0` 目标下 `Spectre.Console` 传递依赖 `Spectre.Console.Ansi 0.57.2`（0.50+ 拆分） | 0.57.2 `Spectre.Console.nuspec` | 无需显式声明；`dotnet restore` 自动解析 |

---

## 1. 问题与动机

### 1.1 需求

提供一个**独立的 TUI 入口**，能力对齐 WebUI 的「会话聊天」，核心是聊天窗口，交互风格参照主流 Coding Agent（Claude Code / Codex CLI / Gemini CLI）：

- 内联流式终端界面（非全屏面板、非 Web）；
- 完整会话能力：新建、继续、列表、重命名、删除、分支；
- 流式文本 + 推理渲染、工具调用卡片、Markdown 终端渲染；
- 权限审批内联就地弹出、Question 问答内联作答；
- 子 Agent / Task 进度卡片、Todo 与 Token 预算状态；
- Agent / 模型 / 思考档 / 场景切换、斜杠命令、附件/图片输入；
- 取消/中断执行。

**明确不做**：不复制 WebUI 的设置页、模型管理页、网关管理页、多 Agent 会议大屏、会话组关系抽屉等配置/管理界面。TUI 只读现有配置（`.seeing/seeing.json`、`~/.seeing/seeing.json`）解析默认 Agent / 模型。

### 1.2 现状盘点（WebUI 聊天链路）

| 环节 | WebUI 位置 | 与 TUI 的关系 |
|------|-----------|--------------|
| 统一读写入口 | `IChatOrchestrator` | **直接复用**（提交、取消、会话 CRUD、事件订阅） |
| 执行与事件流 | `ExecutionJobService` + `IExecutionEventPublisher` | **直接复用**（schema 单点、事件缓冲、断线补历史） |
| 服务端消息投影 | `ChatEventTracker`（独立类，`Hosting/Execution/ChatEventTracker.cs`，`ExecutionJobService` 调用） | **直接复用**（消息历史已由服务端投影落盘，UI 只读） |
| 事件解释 | `WebUI/Services/EventStreamHandler.cs` | **不复用**，TUI 自研轻量 `TuiEventInterpreter` |
| 多订阅/补历史路由 | `SessionEventStreamRouter` | **不复用**（Blazor circuit 维度耦合），TUI 单活跃会话自研 `TuiEventPump` |
| 子代理进度聚合 | `TaskCardAggregator` + `TaskSessionResolver` | **模式复用**，TUI 自研 `TuiTaskTracker`（写入 TUI 视图态，不写 `SessionData`） |
| 权限在途投影 | `PermissionInbox` | **模式复用**，TUI 自研 `TuiPermissionQueue` |
| 呈现可呈现性 | `WebUiSurfacePresenter` | **契约复用**，TUI 自研 `TuiSurfaceProvider` |
| 渲染管线 | `Rendering/MessageRenderPipeline`（`RenderFragment`） | **完全重建**（Spectre/ANSI） |
| UI 组件 | `Components/**`（AntDesign） | **完全重建** |
| JS 互操作 | `wwwroot/js/app.js` | **不存在**（终端原生） |
| 会话存储 | `FileSessionStore` → `<workspace>/.seeing/sessions/*.json` | **直接复用**（同一 workspace 下 TUI 与 WebUI 共享会话数据） |

### 1.3 关键事实（决定设计边界的硬约束）

1. **消息历史不由 UI 写入**：`ExecutionJobService` 已在服务端把事件投影进 `SessionData` 并落盘（`ChatEventTracker`）。UI 只需「加载快照 + 消费增量事件用于实时渲染」。
2. **schema 单点**：TUI 绝不自行计算工具 schema，一律经 `IChatOrchestrator.SubmitAsync` 走 `ExecutionJobService`。
3. **审批可呈现性是硬门槛**：仅实现 `IPermissionChannel` 不够，**必须注册 `ISurfaceProvider`**；`PermissionService` 在 `CanSurface(sessionId)==false` 时立即 `Deny(NoChannel)`（`PermissionService.cs`），且**不创建在途请求**（因此 `PendingChanged` 也补不回来）。
4. **文档漂移**：`AGENTS.md` / `docs/architecture` 中的 `IPermissionPresenter` / `IPermissionPresentationStore` / `PermissionPresentationStore` **在当前代码中已不存在**；权威契约为 `src/abstractions/Seeing.Agent.Abstractions/Interactions/{ISurfaceProvider,ISurfaceRegistry,IPendingRequestManager}.cs` 与 `src/spine/Seeing.Agent.Core/Core/Interactions/SurfaceRegistry.cs`。
5. **会话存储路径是 `<workspace>/.seeing/sessions`**：`AddSeeingCore` 用 `Path.Combine(workspace.ProjectSeeingDirectory, "sessions")`（`ProjectSeeingDirectory` = `<workspace>/.seeing`）。`~/.seeing/sessions` 只是 `FileSessionStore` 无显式目录时的兜底。**因此「与 WebUI 共享会话」的前提是同一 workspace。**
6. **无现成交互式终端对话代码**：`samples/Seeing.Agent.Cli` 是只读管理命令（Headless 宿主）；`samples/Seeing.Gateway.Console.Demo` 是最小「提交 + 订阅事件流」范式（跨进程客户端）。

---

## 2. 目标与非目标

### 2.1 目标

1. 新增薄 Host Shape 包 `Seeing.Agent.Hosting.Tui`：仅宿主接线（镜像 `Hosting.Web` 的薄度）。
2. 新增可执行入口 `samples/Seeing.Agent.Tui`（程序集名 `seeing-tui`）：组合根 + TUI 引擎（镜像 WebUI「UI 全在 sample」）。
3. 实现内联流式 TUI：进入即就绪，新建/继续会话，流式渲染，随时取消。
4. 实现完整聊天渲染：文本、推理、工具卡、Markdown、代码块、Todo、Token 预算。
5. 实现权限审批与 Question 的内联就地交互。
6. 实现子 Agent / Task 进度展示与子会话查看。
7. 实现会话管理与 Agent/模型/思考档/场景切换的 TUI 本地命令。
8. 复用现有会话存储与配置，与 WebUI 数据互通。
9. 落地可测试架构：终端 I/O 与纯逻辑分离，纯逻辑有单元测试。

### 2.2 非目标

- **不做任何配置编辑 UI**：无设置页、无模型管理页、无网关/ACP 管理页、无原始 JSON 编辑器。
- 不做多 Agent 会议大屏、会话组关系树抽屉、双栏摘要卡。
- 不做多活跃会话并行渲染（仅单活跃会话；后台执行不中断，切回补齐）。
- 不做 WebUI `TaskCardAggregator` 的「回写父会话 `TaskSteps` + 防抖落盘」（TUI 只在本地视图展示进度）。
- 不做 Gateway / ACP 的宿主呈现端（不做外发通道绑定 UI）。
- 不实现 `--print` / 管道一次性问答模式（v1 要求可交互终端；非 TTY 直接拒绝启动）。
- 不为工具新建专用展示卡（edit/read/grep 等按通用卡渲染）。
- 不改动 WebUI 任何代码。

---

## 3. 术语

| 术语 | 含义 |
|------|------|
| TUI 引擎 | sample `Seeing.Agent.Tui` 中与具体入口无关的终端聊天实现（事件泵/视图态/渲染/输入/提示） |
| 活动区（Live 区） | 终端底部由 Spectre `Live` 原地重绘的区块，含「进行中的回合 + 输入行 + 状态栏」 |
| 固化（Commit） | 把已完成内容从活动区移出、作为普通输出追加入终端滚动历史 |
| 渲染线程 | `SpectreTerminalSurface` 持有的**唯一**控制台写入线程 |
| 会话快照 | `SessionData`；消息历史的权威投影，由服务端 `ChatEventTracker` 维护 |
| 回合（Loop） | 一次 assistant 循环（`loop.start` → `loop.complete`），可含多个 step |
| 在途请求 | 已发起、尚未决议的权限请求 / Question |
| 可呈现性（Presence） | `ISurfaceRegistry.CanSurface(sessionId)`：是否存在能展示该会话交互的宿主 |
| 活跃会话 | TUI 当前渲染与输入的会话（同一时刻恰好一个） |
| 会话组 | `ISessionGroupManager` 中的分组，含锚点 Root 与其 Child/Fork 成员 |

---

## 4. 总体架构

### 4.1 项目落位

```
src/hosting/Seeing.Agent.Hosting.Tui/           # 新增：薄 Host Shape（镜像 Hosting.Web）
├── TuiHostShape.cs                             # HostShapeDescriptor（Id="tui"）+ CarriableModuleTypes
├── TuiHostingServiceCollectionExtensions.cs    # AddSeeingHostingTui()
└── Permissions/
    └── TuiPermissionChannel.cs                 # IPermissionChannel（事件流承载，no-op）

samples/Seeing.Agent.Tui/                       # 新增：可执行入口 + TUI 引擎（镜像 WebUI 把 UI 放 sample）
├── Seeing.Agent.Tui.csproj                     # AssemblyName=seeing-tui；InternalsVisibleTo 测试项目
├── Program.cs                                  # 组合根
├── CliOptions.cs                               # 启动参数（System.CommandLine）
├── Services/
│   ├── TuiChatEngine.cs                        # 主循环（生产者；提示串行化）
│   ├── TuiSessionController.cs                 # 活跃会话生命周期 + 提交/取消/切换/会话 CRUD
│   ├── ITuiEventPump.cs / TuiEventPump.cs      # 单会话事件订阅与频道泵入（抽象便于测试）
│   ├── TuiEventInterpreter.cs                  # IMessageEvent → 视图态变更（纯逻辑）
│   ├── TuiTaskTracker.cs                       # task 子会话进度聚合
│   ├── TuiPermissionQueue.cs                   # 权限在途投影
│   ├── TuiQuestionQueue.cs                     # 问答在途投影
│   ├── TuiSurfaceProvider.cs                   # ISurfaceProvider（呈现可呈现性）
│   ├── TuiViewState.cs                         # 纯数据视图态
│   └── TuiCommandRouter.cs                     # 本地命令 vs 服务端命令分流（纯逻辑）
├── Input/
│   ├── RawInputReader.cs                       # 原始 stdin 字节读取（raw 模式）
│   ├── InputKeyDecoder.cs                      # 字节/序列 → 语义动作（纯函数）
│   └── TuiInputEditor.cs                       # 多行编辑态（只产出编辑态，不写终端）
└── Rendering/
    ├── ITerminalSurface.cs                     # 终端写出口抽象（便于测试）
    ├── SpectreTerminalSurface.cs               # 渲染线程 + Live 委托模型 + 固化
    ├── TextRenderer.cs                         # 流式文本 + 推理
    ├── MarkdownTerminalRenderer.cs             # Markdig → Spectre（纯函数）
    ├── ToolCardRenderer.cs                     # 工具卡（含 bash/todowrite 特化）
    ├── TaskCardRenderer.cs                     # 子代理进度卡
    ├── SessionListView.cs / StatusBarView.cs
    └── Prompts/                                # 权限/问答/选择提示封装

tests/apps/Seeing.Agent.Tui.Tests/              # 新增：引擎与纯逻辑单测（镜像 tests/apps/Seeing.Agent.WebUI.Tests）
```

**为什么引擎放 sample 而非 Host Shape 包**：既有 Host Shape 包均为薄宿主胶水（`Hosting.Web` 只有 Circuit 胶水 + Channel + Descriptor，重 UI 全在 WebUI sample）；把整个 TUI 引擎放 `src/hosting/**` 会同时造成①偏离薄惯例②第三方 UI NuGet（Spectre）进入脊柱层③「可复用」缺乏第二个消费者（YAGNI）。本设计与 `Hosting.Web` vs WebUI 的分工完全同构。

### 4.2 依赖方向与合规

```
Seeing.Agent.Hosting            ─┐
Seeing.Agent.Core               ─┼──▶  Seeing.Agent.Hosting.Tui        # 脊柱层，薄，零第三方 UI 依赖
Seeing.Agent.Abstractions       ─┘            │
                                              └──▶  samples/Seeing.Agent.Tui ──▶ 能力包 + Spectre.Console + Markdig
                                                          （可执行组合根 / UI 层）
```

- `Hosting.Tui` 属**脊柱层 Hosting**，允许引用 Core / Abstractions / Hosting；**不引用 Spectre / Blazor / Markdig**。
- **禁止**能力包（`Tools.*` / `Skills` / `Mcp` / `Llm.*` 等）反向引用 `Hosting.Tui` 或 sample。
- **禁止**在 Core 中新增任何 TUI 类型或能力。
- 能力包选择只发生在 sample（参照 `samples/Seeing.Agent.Cli` / `Seeing.Agent.WebUI`）。
- 不新增 schema 计算点；不绕开 `ExecutionJobService` 直接触碰 executor。

### 4.3 启动时序（修正 B1：必须启动 Host）

```csharp
// samples/Seeing.Agent.Tui/Program.cs（伪代码，非逐行可编译）
var cli = CliOptions.Parse(args);                          // workspace/--continue/--resume/--agent/--model/--boot
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();                          // 日志只写文件（§12.2）
builder.Logging.AddProvider(new TuiFileLoggerProvider(cli.LogDirectory));

if (cli.Workspace is not null)
    Environment.SetEnvironmentVariable("SEEING_WORKSPACE_ROOT", cli.Workspace);   // WorkspaceProvider 最高优先级

var registry = new ConfigSectionRegistry();
builder.Services.AddSingleton<IConfigSectionRegistry>(registry);

// 1) 能力模块（与 WebUI 对齐；不含 Gateway 服务端）
AddSeeingModule<LocalExecutionWorldModule / FileSystemModule / WebModule / ShellModule / BasicModule /
    GitModule / SessionToolsModule / SkillsModule / McpModule / OpenAiLlmModule / AnthropicLlmModule /
    ModelCapabilitiesModule / BuiltinCatalogModule / DeepSeekLlmModule / OpenCodeZenLlmModule /
    AgentsBuiltInModule / QuestionToolsModule>(registry);
AddSeeingAcp(registry);
AddSeeingScheduler(registry);
AddTokenBudgetIntegration(registry, builder.Configuration);   // 【修正】必须传 IConfiguration
AddMemoryServices(registry);

// 2) 脊柱
builder.Services.AddSeeingCore(registry);

// 3) Tui Host Shape（描述符 + ProcessSettlementOptions + IPermissionChannel + ChatOrchestrator + ExecutionEngine）
builder.Services.AddSeeingHostingTui(o =>
{
    o.MaxConcurrentExecutions = -1;
    o.EventBufferSize = 100;
    o.ExecutionHistoryLimit = 100;
    o.SessionIdleTimeout = TimeSpan.FromMinutes(30);
    o.CleanupInterval = TimeSpan.FromMinutes(5);
});

// 4) sample 侧引擎与 UI 服务
AddTuiUi(builder.Services);                    // sample 内扩展：生成器/渲染线程/Surface/Queues/Prompts
AddTokenBudgetHooks();                         // 【修正】预算 Hook 注册
builder.Services.AddSingleton<IBudgetStatusNotifier, TuiBudgetStatusNotifier>();
BootOverrideSource.ApplyToServices(builder.Services, args);   // 【修正】--boot / SEEING_BOOT

using var host = builder.Build();   // 注意：IHost 不实现 IAsyncDisposable，不能用 await using

// 5) 必须在 Host.Start 之前
using (var scope = host.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    await sp.InitializeSeeingAsync();
    sp.InitializeCommands();
    sp.InitializeAcpCommands();                // 【修正】ACP 命令发现
    sp.UseTokenBudgetHooks();                  // 【修正】预算 Hook 接线
}

// 6) 启动 Host：必须！否则 HostedService 不运行（会话组事件桥/idle resume/热重载/Agent 加载 全部失效）
await host.StartAsync();

try
{
    return await host.Services.GetRequiredService<TuiChatEngine>().RunAsync(cli);
}
finally
{
    await host.StopAsync();
}
```

**关键点**

- `AddSeeingHostingTui` 内部完成 `AddChatOrchestrator()` + `AddExecutionEngine(configure)`（对齐 Headless），sample 不重复调用。
- `ProcessSettlementOptions`：`HostDefaultScenario = "full"`、`HostDefaultSeams["executionWorld"]="io.local"`、`BootOverride = BootOverrideSource.ResolveFromEnvironment()`。
- `InitializeSeeingAsync` 内部已含 `WorkspaceProvider.InitializeAsync` → `IComponentManager.LoadAllAsync` → `ToolManager.LoadToolStateAsync`，无需额外调用。
- **必须 `host.StartAsync()`**：`AddSeeingCore` / `AddExecutionEngine` 注册了 `AgentManager`、`AgentInitializationService`、`SessionGroupEventBridge`、`ReloadOrchestratorStarter`、`AgentLoopSchedulerHostedService` 等 `IHostedService`。其中 `SessionGroupEventBridge` 是 `ISessionGroupManager.Changed → ISessionGroupEventBus.Publish` 的唯一桥；不启动则 §5.2 依赖的组变更订阅永不触发，子代理可呈现性退化。
- `using var host`（`IHost` **不实现** `IAsyncDisposable`，不可 `await using`）确保退出时释放 root provider；`StopAsync` 只停止 HostedService，不释放容器。

---

## 5. 组件设计

### 5.1 Host Shape 接线（薄包）

#### 5.1.1 `TuiHostShape`

```csharp
namespace Seeing.Agent.Hosting.Tui;

public sealed class HostShapeDescriptor      // 与 Web/Headless 同构（各自命名空间内同名类）
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> CarriableModuleTypes { get; init; }
    public string? DefaultScenario { get; init; }
}

public static class TuiHostShape
{
    public const string Id = "tui";

    // 沿用 WebHostShape 的既有约定（tools.* / provider.* / token-budget），去掉 TUI 不承载的 gateway 与 ui
    public static readonly IReadOnlyList<string> CarriableModuleTypes =
    [
        "tools.basic", "tools.filesystem", "tools.shell", "tools.web", "tools.git",
        "llm.openai", "llm.anthropic", "provider.deepseek", "provider.opencodezen",
        "skills", "mcp", "memory", "scheduler", "acp", "token-budget",
    ];

    public static HostShapeDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        CarriableModuleTypes = CarriableModuleTypes,
        DefaultScenario = "full",
    };
}
```

> `CarriableModuleTypes` 目前全仓无运行时读取方（仅各 Shape 自行定义），保留仅为形态自描述；本列表**逐字沿用 `WebHostShape` 的既有字符串约定**（含 `tools.*` 前缀与 `provider.*`），仅去掉 `gateway` 与 `ui`，避免自造 id 失真。实施时以 sample 实际注册集为准。

#### 5.1.2 `AddSeeingHostingTui`

```csharp
public static IServiceCollection AddSeeingHostingTui(
    this IServiceCollection services,
    Action<ExecutionOptions>? configureExecution = null)
{
    services.AddSingleton(TuiHostShape.Descriptor);
    services.AddSingleton(new ProcessSettlementOptions
    {
        HostDefaultScenario = TuiHostShape.Descriptor.DefaultScenario,
        HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["executionWorld"] = "io.local",
        },
        BootOverride = BootOverrideSource.ResolveFromEnvironment(),
    });

    // 事件流承载通道（语义同 Hosting.Web 的 EventStreamPermissionChannel）
    services.AddSingleton<IPermissionChannel, TuiPermissionChannel>();

    services.AddChatOrchestrator();
    services.AddExecutionEngine(configureExecution);

    return services;
}
```

**本包不注册的内容**（由 sample 负责，与 WebUI 分工一致）：

- 向两个**已由 Core（`AddSeeingCore`）注册**的 `SurfaceRegistry` 实例注册 `TuiSurfaceProvider` 的动作（事件流 + 呈现端模式；registry 实例不由本包创建）；
- 引擎、渲染、输入、提示、队列；
- 能力包。

#### 5.1.3 `TuiPermissionChannel`

```csharp
public sealed class TuiPermissionChannel : IPermissionChannel
{
    public PermissionEffect? TryAutoApprove(PermissionRequest request) => null;
    public ValueTask PresentAsync(PermissionRequest request, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask DismissAsync(PermissionResolution resolution, CancellationToken ct = default) => ValueTask.CompletedTask;
}
```

与 `EventStreamPermissionChannel` 完全一致：请求呈现与收敛由 `PermissionRequestManager` 经执行事件流广播，TUI 从 `IPermissionRequestManager.GetAllPending()` / `PendingChanged` 取在途请求，经 `TryResolve` 回传裁决。不复用 `Hosting.Web` 的实现，避免 `Hosting.Tui → Hosting.Web` 的反向耦合。

### 5.2 `TuiSurfaceProvider`（sample 侧，关键）

**契约**：`SurfaceSessionIds` 必须返回**已确认的稳定快照**，不得实时读取易变状态。

**快照集合定义**（单活跃会话模型）：

```
SurfaceSessionIds =
      { ActiveSessionId }
    ∪ { ActiveSessionId 所属会话组全部成员（锚点 + Child/Fork，递归） }
    ∪ { 任何存在在途 Permission/Question 请求的会话 Id }
```

**为什么必须包含子会话**：`task` 子代理以 Child 会话身份发起权限/问答请求。若快照只含活跃会话，`CanSurface(childId)=false` → `PermissionService` **立即 Deny(NoChannel) 且不创建在途请求**（§1.3.3），子任务静默失败，`PendingChanged` 也补不回来。

**维护方式**（存快照，不实时读）：

| 触发 | 动作 |
|------|------|
| 活跃会话切换（`TuiSessionController` 通知） | 重算组快照（`ISessionGroupManager.GetGroupForSessionAsync` / 递归 `ListChildrenAsync`） |
| `ISessionGroupEventBus`（活跃组）变更 | 重算成员集（**依赖 `host.StartAsync()`，见 §4.3**） |
| `IPermissionRequestManager.PendingChanged` | 新出现的会话并入快照 |
| `IQuestionRequestManager.PendingChanged` | 同上 |
| 请求决议 / 会话删除 | 重算（移除） |

**发布节流**：变更后经 **50ms 去抖** 发布 `SurfacedChanged`。

**空集确认期**（修正 L5）：若一次重算结果为**空集**，延迟 **1s** 再提交（期间若恢复非空则取消提交），避免「新子会话刚创建、组事件未到、快照瞬时为空」导致 fail-closed。1s 内组事件到达即补入。

**注册时机与生命周期**：`TuiChatEngine.RunAsync` 在「解析/创建活跃会话 → 刷新快照」之后、进入主循环之前，分别 `Resolve` 两个 registry 并各 `Register(provider)` 一次；退出时各 `Unregister` 一次。

> **两个 registry 是独立实例**（修正 H1）：权限侧由 `PermissionServiceExtensions` 注册 `new SurfaceRegistry()`；问答侧由 `QuestionServiceExtensions` 注册**另一个** `new SurfaceRegistry()`。`SurfaceRegistry` 类本身同时实现两个接口，但 DI 生成**两个不同实例**。**必须分别 Resolve、分别 Register、分别 Unregister**；只操作一个会导致另一侧 `CanSurface` 恒 false。

### 5.3 TUI 引擎组件（sample 侧）

| 组件 | 生命周期 | 职责 | 依赖 |
|------|----------|------|------|
| `TuiChatEngine` | Singleton | 主循环；提示串行化；调度渲染与命令 | 下述全部 |
| `TuiSessionController` | Singleton | 活跃会话解析/创建/切换；提交、取消、会话 CRUD；通知 `TuiSurfaceProvider` | `IChatOrchestrator`、`IExecutionSubmitter`、`ISessionManager`、`ISessionGroupManager` |
| `ITuiEventPump` / `TuiEventPump` | 每会话实例 | `SubscribeEvents` 订阅 → `Channel<IMessageEvent>`；补历史靠订阅自回放（§6.1） | `IChatOrchestrator` |
| `TuiEventInterpreter` | 每会话实例 | `IMessageEvent` → `TuiViewState` 变更（纯逻辑） | `TuiViewState` |
| `TuiTaskTracker` | 每父会话实例 | 检测 `task` 工具调用 → 订阅子会话流 → 聚合进度到视图态 | `ITuiEventPump`（子流）、`ISessionGroupManager` |
| `TuiPermissionQueue` | Singleton | `PendingChanged` → 在途快照；`TryResolve` 回传 | `IPermissionRequestManager` |
| `TuiQuestionQueue` | Singleton | `PendingChanged` → 在途快照；`TryResolve` 回传 | `IQuestionRequestManager` |
| `TuiSurfaceProvider` | Singleton | 呈现可呈现性快照 | 两个 `ISurfaceRegistry`、`ISessionGroupManager`、`ISessionGroupEventBus`、两个 PendingManager |
| `TuiViewState` | 每会话实例 | 纯数据：消息时间线、当前回合、执行态、Todo、预算、待批计数 | 无 |
| `ITerminalSurface` / `SpectreTerminalSurface` | Singleton | 终端写出口（专用渲染线程） | Spectre |
| `RawInputReader` | Singleton | 原始 stdin 读取 | 无 |
| `TuiInputEditor` | Singleton | 编辑态：文本、光标、历史、补全候选、粘贴缓冲。**只被主循环读/改，绝不写终端** | 无 |
| `InputKeyDecoder` | 静态 | 字节/序列 → 语义动作 | 无 |
| `TuiCommandRouter` | Singleton | 命令分类（本地拦截 / 服务端转发） | `ICommandRegistry` |

**单写者原则（修正 tech10）**：只有 `SpectreTerminalSurface` 的**渲染线程**写控制台。主循环、输入读取线程、事件泵后台任务都不直接写终端；输入线程只向 `Channel<InputAction>` 投递按键/粘贴，事件泵只向 `Channel<IMessageEvent>` 投递事件，主循环消费后调用 surface。`TuiInputEditor` 是**纯编辑态**，不持有 `ITerminalSurface`。

### 5.4 引擎内部数据流

```
RawInputReader ──InputAction──▶ ┐
                                ├─▶ TuiChatEngine（主循环，单线程决策）
ITuiEventPump ──IMessageEvent──▶ ┘        │
                                          ├─▶ TuiEventInterpreter ─▶ TuiViewState（纯数据）
                                          ├─▶ TuiTaskTracker / TuiPermissionQueue / TuiQuestionQueue
                                          └─▶ ITerminalSurface.Update / Commit / Prompt
                                                     │
                                          SpectreTerminalSurface 渲染线程（唯一写者）
```

---

## 6. 事件 → 渲染管线

### 6.1 订阅与补历史（修正 tech6）

```
活跃会话确定
  → pump = ITuiEventPump.Start(sessionId)
      ├─ 1) session = await orchestrator.GetSessionAsync(sessionId)    // 快照
      │     → ViewState.ResetFromSession(session)
      └─ 2) await foreach (e in orchestrator.SubscribeEvents(sessionId, ct))
            → channel.WriteAsync(e)                                     // 主循环消费
```

- `ExecutionEventPublisher.SubscribeAsync` 会**先灌入整个环形缓冲**（最多 `EventBufferSize=100`，`buffer.GetAll()`），其中可能包含**快照之前**的旧事件。因此「快照之后」的增量语义**必须靠去重**，而不是靠不调用 `GetBufferedEvents`。
- **去重与幂等口径（v2.1 修正，替代 v2 的「已完成 LoopId 集合」口径）**：
  - **关键事实**：**进行中的回合也会被 `ChatEventTracker` 写入 `SessionData.Messages`**（`EnsureAssistantMessage` 在首个 `stream.delta` / `tool.call.*` 到达时按 `{loopId}_step{n}` 落消息）。因此「从 Messages 推导已完成 LoopId 集合」**不成立**——它会把进行中的 LoopId 误判为已完成，导致「切回后台运行中的会话」时丢弃该回合的回放与实时事件，破坏 §7.1 的「切回补齐」。
  - **正确口径 = 按 key 幂等 upsert，而非按事件丢弃**：
    - assistant 消息/渲染块以 `{loopId}_step{step}` 为键（与服务端 `ChatEventTracker` 的 Id 规则一致）**upsert**，重复回放只覆盖同一键，不产生重复节点；
    - `ToolCallEvent` 按 `CallId` upsert；`StreamDeltaEvent` 按 `(LoopId, Step)` 累积；`TodoUpdateEvent` 整体替换。
  - **进行中回合的判定与保留**：`GetOverview(sessionId)` 的 `HasActiveExecution` / `CurrentExecution` 非空时，最新未闭合回合即**进行中**，其 `LoopId` **必须保留**（不得跳过）；`LoopComplete` / `ExecutionComplete` 到达后才标记为已终态。
  - **进行中 step 的「半途回放」规则（修正长流式双写）**：`StreamDeltaEvent` 是**累加**语义且**不带 offset/序号**，而 `SubscribeAsync` 只回放环形缓冲（默认 100 条）。一次 LLM 流式动辄 >100 个 delta，切回时该 step 的 `stream.start` 很可能已被挤出缓冲 → 回放会从半途 delta 开始，在快照 seed 的部分内容上重复累加（`stream.complete` 前短暂双写）。
    - **规则**：`ViewState` 维护「本次 pump 已见 `stream.start` 的 `(LoopId,Step)` 集合」`R`。对快照中已存在、但 `(LoopId,Step) ∉ R` 的进行中 step：**保留快照 seed 内容、跳过 `stream.delta` 累加**，直到 `stream.complete` 用完整 `ChatMessage` **整体覆盖**该 step。
    - 效果：长流式切回不会双写；该 step 在此期间显示为快照的持久化部分内容，`stream.complete` 到达后自愈为完整内容。
    - 若需更高保真（把半途缺失的 delta 也补齐），Phase 6 可用 `GetBufferedEvents` 做一次基线对账（此场景下 `GetBufferedEvents` 是正解而非仅诊断）。
  - **非回合类事件**（`session.*` / `todo.update` / `budget.*` / `mode.update` / `permission.*` / `question.*`）始终处理，不参与回合去重。
- **会话切换**：新开 `ITuiEventPump`，停止并 dispose 旧 pump（其订阅随 `CancellationToken` 结束）；**不取消后台执行**（对齐 WebUI「刷新不打断执行」）。
- `GetBufferedEvents` 仅作诊断（Phase 6 可选对账），正常路径不用。

### 6.2 事件分类与处理

| 事件 `Type` | 视图态副作用 | 渲染 |
|---|---|---|
| `loop.start`（`ExecutionStartedEvent`） | 记录 `ActiveExecutionId`；新建当前回合 | 活动区新建「回合」；状态栏 → 运行中 |
| `loop.start`（`LoopStartEvent`） | 新建 loop 分组 | 活动区新建 loop 块 |
| `stream.start` | 重置当前 step 缓冲（重发=重置） | — |
| `stream.delta` | 累积文本/推理；工具参数增量 | 活动区节流重绘（§6.3） |
| `stream.complete` | 定稿当前 step 消息 | 活动区重绘 |
| `tool.call.pending/running/complete` | 按 `CallId` upsert（状态/参数/结果/标题/错误/metadata） | 工具卡 |
| `permission.request` / `permission.resolved` | 由 `TuiPermissionQueue` 承接 | 权限提示（§8） |
| `question.request` / `question.resolved` | 由 `TuiQuestionQueue` 承接 | 问答提示（§8） |
| `loop.complete`（`ExecutionCompleteEvent`） | `ExecutionId` 匹配才清执行态；回合定稿 | 固化回合；状态栏 → 空闲 |
| `loop.complete`（`LoopCompleteEvent`） | 关闭 loop 分组 | 固化 loop |
| `loop.cancelled` | 清执行态；标记取消 | 固化并标注「已取消」 |
| `error` | 记录 `LastError` | 错误块（红） |
| `llm.retry` | 重试倒计时 | 状态栏/行内「重试中 (n/m)」 |
| `command.result` | `ShouldContinue=false` 短路；`NeedsRefresh` → 重载快照 | 命令输出 |
| `todo.update` | 替换 Todo 列表 | 状态栏摘要；`/todo` 展开 |
| `budget.status` / `budget.warning` | 预算状态（**记录类型位于 `Seeing.Agent.TokenBudget` 能力包**，sample 可引用） | 状态栏/警告 |
| `mode.update` | ACP 模式 | 状态栏 |
| `session.title.changed` / `session.updated` | 标题/元数据 | 标题、状态栏 |
| `skill.content` | — | 折叠提示（`/skill` 展开） |
| `compaction.started/delta/completed/failed` | 压缩进度 | 活动区进度块 → 固化 |
| `schema.snapshot` | — | 不渲染（内部） |
| `navigate` | — | TUI 忽略；Debug 日志 |

> `ExecutionStartedEvent` / `ExecutionCompleteEvent` 复用 `loop.start` / `loop.complete` 的 `Type`，必须按 `ExecutionId` 与 `LoopId` 配对区分。

### 6.3 活动区与固化（修正 H-tech1：委托式 Live）

**布局**：活动区是一个 `Rows` 渲染体：`[进行中的 step 视图] / [输入行（最多 6 行）] / [状态栏]`。

**`SpectreTerminalSurface` 渲染线程模型**（`LiveDisplay` 仅委托式 `Start`，无 `Stop()`）：

```
渲染线程循环（唯一写控制台）：
  while (!ct.IsCancellationRequested)
  {
      TuiRenderCommand? commit = null; PromptRequest? prompt = null;
      // 每轮新建 LiveDisplay 实例（不依赖其可重用性）
      var live = AnsiConsole.Live(_currentView).AutoClear(false)
                            .Overflow(VerticalOverflow.Ellipsis);
      live.Start(ctx =>
      {
          while (true)
          {
              var cmd = _commands.Reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
              switch (cmd)
              {
                  case Update u:  _currentView = u.View; ctx.UpdateTarget(_currentView); ctx.Refresh(); break;
                  case Commit c:  commit = c; return;       // 退出委托 → 允许向下 Write
                  case Prompt p:  prompt = p; return;       // 退出委托 → 允许交互提示
                  case Stop:      return;
              }
              if (ct.IsCancellationRequested) return;
          }
      });
      // 委托返回：活动区最后一帧由 AutoClear(false) 保留
      if (commit is not null) AnsiConsole.Write(commit.Renderable);       // 固化到滚动历史
      if (prompt is not null) RunPromptOnThisThread(prompt);              // Spectre Prompt，写真实控制台
  }
```

- **固化 = 退出委托 → `AnsiConsole.Write` → 下一轮新建 Live 重启**；**重启前必须先把刚 `Commit` 的内容从 `_currentView` 中剔除**，否则活动区会重复显示。
- **渲染线程内的同步阻塞读是刻意设计**（`ReadAsync(...).GetAwaiter().GetResult()`）：该线程是专用线程、无 `SynchronizationContext`，且必须与 `live.Start` 的阻塞委托配对；**不属**仓库反模式「同步包装阻塞 async」，实现时勿改回 async 以免破坏 Live 委托模型（如需更稳妥，可用 `WaitHandle`/`SemaphoreSlim.Wait` 的同步等待替代）。
- **提示**（权限/问答/选择）也在渲染线程执行，天然满足单写者，并与活动区串行互斥；放在「委托返回之后」执行，可规避 Spectre `DefaultExclusivityMode` 的互斥死锁。
- `ITerminalSurface` 对外只暴露：`Update(IRenderable)`、`Commit(IRenderable)`、`PromptAsync<T>(Func<IAnsiConsole, Task<T>>)`、`Stop()`；主循环是生产者。
- **溢出保护**：`Update` 前用 `IRenderable.Measure(new RenderOptions(console.Profile.Capabilities, new Size(width, height)))` 估算活动区高度（`RenderOptions` 无无参构造，需显式传 capabilities 与尺寸）；若 ≥ `Profile.Height - 4`，把「最旧的稳定内容」先 `Commit` 出去，直到回落阈值内。单条超长（如工具输出）直接截断为「前 N 行 + …（共 M 行，/expand <callId> 查看）」并固化摘要。
- **安全提交边界**：`loop.complete`/`loop.cancelled`（整回合）、空行或闭合代码围栏、累计 ≥ 24 行、工具进入终态、`command.result`/`error`/`compaction.completed`。
- **节流**：字符阈值（代码块 20 / 普通 8）或 60ms 定时，取先到者。

**实现说明（活动区溢出策略，有意改进偏离本设计初稿）**：最终实现不是上文的 `AutoClear(false)` + 「溢出即固化整帧」，而是
`AutoClear(true)` + `Overflow(VerticalOverflow.Crop)` + `Cropping(VerticalOverflowCropping.Top)`，
并新增 `TailClipRenderable` 把活动区硬限制为「终端高度 − 1 行」（裁尾保头，输入行 + 状态栏恒在末尾）。
理由：`AutoClear(false)` 在活动区高度超过终端时会触发 Spectre 的 `Completed` 语义重写整帧，导致内容重复；改为「Live 帧退出即擦除 + 裁剪尾部」后，
溢出不再是固化触发条件，固化改由 `TuiChatEngine` 按「稳定前缀 / 终态块 / 安全提交边界」主动提交到滚动历史（`CommitAsync` 后补写换行，避免与下一帧 Live 粘连）。

**后备实现（R1 触发条件）**：若 P0 spike 发现「委托返回 + 重启 Live + `AutoClear(false)`」在目标终端有闪烁/残留，则把 `SpectreTerminalSurface` 换为**自研 ANSI 活动区**（`\x1b[nA` 上移 + `\x1b[J` 清屏 + 行数由 `IRenderable.Measure` 得出），Spectre 仅用于「渲染体 → ANSI 字符串」（经固定宽度的 `IAnsiConsole` 输出到 `StringWriter`）。`ITerminalSurface` 保证替换零影响主循环。

### 6.4 渲染细节

- **流式文本**：累加缓冲经 `MarkdownTerminalRenderer` 容忍式渲染（未闭合围栏按代码块处理）。
- **推理**：默认 dim 折叠（一行「`*` 思考（N 行，/reasoning 展开）」头 + 尾部 **3** 行）；`/reasoning` 切换。**实现说明**：推理只在块正文尚未固化任何前缀（`offset == 0`）时渲染于活动区，并随首个固化片一次性写入滚动历史，避免「思考插入到回复中间」。
- **Markdown（Markdig 1.4.0 → Spectre 原语）**：解析用 `new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build()`（**管道表格必须启用扩展**：`UseAdvancedExtensions()` 或显式 `UsePipeTables()`）；映射：标题→加粗/下划线样式；列表→`•`/缩进；引用→`│`；表格→`Table`；行内代码→底色；代码块→`Panel` +（可选）最简关键字着色；链接→下划线 + URL 后缀。`MarkdownTerminalRenderer` 必须**全量 try/catch 降级为纯文本**，绝不抛出。
- **工具卡**：单行头（状态图标 + 工具名 + 摘要 + spinner）；完成体（结果前 12 行 + 截断提示）；`bash` 特化（命令/退出码/等宽输出）；`todowrite` 特化（Todo 卡）；`/expand <callId>` 全量展开。
- **子代理进度卡**：`task` 标签 + 描述 + 状态 + 子会话 Id；展开体为 `TuiTaskTracker` 聚合的步骤子树；`/open <taskId>` 切换会话。
- **实现说明（字形安全集）**：图标与分隔符统一取自 `TuiGlyphs`，全部码点均可被系统默认代码页 936（GBK）编码往返，避免经编码器 BestFit 回退成字面 `?`。实际使用：`>`（输入提示）`*`（推理/压缩）`●`（工具与待定）`|`（spinner 帧）`√`（成功）`×`（失败）`!`（拒绝）`-`（取消）`▌`（输入光标）`─`（分隔线）`·`（无序列表）`→`（箭头）。**禁用**不可编码字形（会回退 `?`）：`✻ ❯ ⏺ ⠋ ✓ ✗ ⛔ ⊘ •`。

---

## 7. 会话与状态管理

### 7.1 单活跃会话模型

- 任一时刻只有一个活跃会话参与渲染；`TuiChatEngine` 持有单一 `TuiViewState`。
- **用户消息回显（实现说明）**：用户提交的文本由引擎**本地一次性写入滚动历史**（`CommitAsync`，终态块，`Key="user:local"`），**不进入 `TuiViewState`**——服务端历史仅在会话激活时加载，实时回合不渲染用户侧；若不本地回显则用户会误以为未发送，而写入视图态又会与服务端历史键重复。
- **后台执行不中断**：切换会话只停止旧 pump 的渲染消费，不取消执行。
- **切回补齐**：切回时从 `GetSessionAsync` 快照重建，再靠 `SubscribeEvents` 自回放补增量（§6.1 去重口径）。
- **并发执行**：后端允许跨会话并发（`MaxConcurrentExecutions=-1`）；TUI 只渲染活跃会话，其余在状态栏提示「N 个会话后台执行中」。

### 7.2 会话生命周期（TUI 本地命令 → `IChatOrchestrator`）

| 操作 | 调用 | 备注 |
|------|------|------|
| 新建 | `CreateSessionAsync(title, agentId, workspace)` | 成功即切换活跃会话 |
| 列表 | `ListSessionsAsync()`（仅返回每组锚点） | `SessionListView` 按 `UpdatedAt` 倒序 |
| 继续最近 | `ListSessionsAsync()` 取 `UpdatedAt` 最大 | `--continue` |
| 指定恢复 | `GetSessionAsync(id)` | `--resume <id>`；不存在则退出码 3 |
| 重命名 | `RenameSessionAsync(id, title)` | `TextPrompt` |
| 删除 | `DeleteSessionAsync(id)` | `ConfirmationPrompt`；删活跃会话后切最近（无则新建） |
| 分支 | `BranchSessionAsync(id, title)` | `/fork` |
| 清空 | 服务端 `/clear`（转发） | 收 `command.result(NeedsRefresh)` 后重载快照 |
| 取消当前 | `IExecutionSubmitter.CancelAsync(executionId)` | **禁止** `IChatOrchestrator.Cancel`（同步阻塞，§12.4） |
| 级联取消 | `CancelBySessionAsync(sessionId)` | `/cancel all` |

### 7.3 持久化与退出屏障（修正 M1/H-tech3）

- 复用 `ISessionStore`（`WriteBehindSessionStore(FileSessionStore)`）→ **`<workspace>/.seeing/sessions/{id}.json`**；同一 workspace 下与 WebUI 共享。
- 配置切换（Agent/Model/Thinking/Scenario/AutoApprove）写回 `SessionData` 后 `SessionManager.SaveAsync(sessionId)`。
- **退出屏障**（由 `TuiChatEngine` 经构造注入的 **root Singleton** 取用；**勿复用启动时 `using` 的 scope**，那个 scope 在 `InitializeSeeingAsync` 之后即释放）：
  ```csharp
  // TuiChatEngine 构造注入：ISessionStore / ISessionGroupStore（均为 Singleton）
  if (_sessionStore is IPersistenceFlusher f1) await f1.FlushAllAsync(ct);
  if (_groupStore   is IPersistenceFlusher f2) await f2.FlushAllAsync(ct);
  ```
  `IPersistenceFlusher` **未注册为 DI 服务**（只有 `WriteBehind*Store` 实现它），必须对已注册的 `ISessionStore` / `ISessionGroupStore` 做 `is` 转型（参照 `SessionReloadHandler` 的既有用法）。方法名是 `FlushAllAsync`，**不存在无参 `FlushAsync`**。

---

## 8. 权限与问答

### 8.1 可呈现性

见 §5.2。硬要求：快照含活跃会话组全部成员 + 在途请求所属会话；两个 registry 分别注册；空集 1s 确认期。

### 8.2 内联提示 UX

**权限提示**（活动区暂停，串行处理）：

```
┌ 权限请求 ────────────────────────────────────────────┐
│ 工具   bash                                          │
│ 归属   build · 主会话   （或：子代理 · explore / 后台会话标题）
│ 命令   rm -rf ./obj                                  │
│ 风险   high                                          │
└──────────────────────────────────────────────────────┘
? 请选择
  ❯ 本次允许
    始终允许（本会话）
    允许此目录（会话目录）
    本次拒绝
    始终拒绝
```

- 选项按 `PermissionRequest.AllowedScopes` 过滤。
- 提交：`IPermissionRequestManager.TryResolve(requestId, decision, scope, PermissionResolvedBy.User, reason, expectedSessionId: request.SessionId)`。
- `Esc` 取消提示 = **不决策**（保持挂起），不得误 Deny。

**问答提示**：按 `QuestionRequest` 题型渲染——单选 `SelectionPrompt`、多选 `MultiSelectionPrompt`、文本 `TextPrompt`（默认值/必填/单答案截断 2000 字符）；「其他」追加自由输入项。提交：`IQuestionRequestManager.TryResolve(requestId, result, expectedSessionId)`。

**队列与串行**：主循环在「无未决提示」时从队列取一条呈现；状态栏显示 `待批 N`。

### 8.3 非交互与降级

- **非 TTY**（`Console.IsInputRedirected || Console.IsOutputRedirected`）→ 打印错误并 `return 2`，不启动。
- 运行中失去可呈现性（不应发生）→ 依赖 fail-closed `Deny(NoChannel)`。

### 8.4 自动批准

- `SessionData.AutoApprove` 三态由 `/auto-approve` 切换并 `SaveAsync`。
- 全局 `SeeingAgentOptions.Permission.AutoApproveAll` 与 `SessionData.AutoApprove` 的合流由**既有授权链 / `PermissionService`** 判定。
- `TuiPermissionChannel.TryAutoApprove` 恒 `null`，TUI **不自实现** `TryAutoApprove`，避免语义分叉。
- 效果：被判定为「自动允许」的请求不进入 `TuiPermissionQueue`（不弹提示）；只有 `Ask` 结果才需用户决策。

---

## 9. 子 Agent / Task

- **检测**：`TuiEventInterpreter` 遇 `tool.call.*` 且工具名为 `task`（或 `TaskId` 非空）→ 通知 `TuiTaskTracker`。
- **解析子会话 Id**：移植 WebUI `TaskSessionResolver` 纯逻辑（`origin_tool_call_id` 匹配 + `ISessionGroupManager` 冷兜底），落点 `TuiTaskTracker` 私有方法（不改 WebUI；DRY 备注见 §16）。
- **订阅**：`TuiTaskTracker` 为子会话开轻量 `TuiEventPump`，把子会话 `tool.call.*` 聚合为步骤列表（工具名/参数摘要/状态），写父视图态。
- **不落盘**：不回写父 `SessionData.Messages[].ToolCalls[].TaskSteps`。
- **展示与打开**：进度卡（§6.4）；`/open <taskId>` 切换活跃会话（子会话为组内成员，天然可呈现）。
- **终态**：子会话 `loop.complete` / `loop.cancelled` / `error` → 停止订阅并固化最终卡。

---

## 10. 斜杠命令

**分工**：TUI 本地命令「拦截，不提交」；服务端命令「作为消息提交」，由 `ExecutionJobService.ProcessCommandAsync` 处理并回 `command.result`；**未知 `/xxx` 会被服务端当作普通输入透传**（不是命令）。

**本地命令（`TuiCommandRouter` 拦截）**：

| 命令 | 行为 |
|------|------|
| `/help` `/h` `/?` | TUI 帮助（本地命令 + `ICommandRegistry` 元数据合并） |
| `/new [title]` | `CreateSessionAsync` 并切换（覆盖服务端 `/new` 的 WebUI 导航语义） |
| `/sessions` | 会话列表选择器 |
| `/resume <id>` | 切换会话 |
| `/rename [title]` | 重命名活跃会话 |
| `/delete` | 删除活跃会话（二次确认） |
| `/fork` | `BranchSessionAsync` 并切换 |
| `/open <sessionId或taskId>` | 切换子会话/指定会话 |
| `/agent [name]` | 交互式选择主 Agent（`IAgentRegistry.GetPrimaryAgentsAsync`） |
| `/model [id]` | 交互式选择文本模型（`IModelConfigManager.GetModelsByType(ModelType.Text)`） |
| `/thinking <level>` | 思考档（`ILlmService.GetModelConfig(modelId)` 的 `Options.Thinking.Levels`） |
| `/scenario [name]` | 场景切换（`IScenarioCatalog.ListAll()`） |
| `/auto-approve <follow\|on\|off>` | 三态切换 + 落盘 |
| `/reasoning [on\|off]` | 推理显示开关（本地 UI 偏好） |
| `/cancel [all]` | 取消当前/级联执行 |
| `/expand <callId>` | 展开工具完整输出 |
| `/todo` | 展开 Todo 面板 |
| `/exit` `/quit` `/q` | 退出（覆盖服务端 `/exit`） |

**服务端转发**：`/clear`、`/compact`、`/tools`、`/mcp`、skill 命令、以及任何未命中本地的 `/xxx`（按普通输入下发）。

**覆盖声明**：本地 `/new` `/help` `/agent` `/model` `/fork` `/exit` 有意覆盖服务端同名命令（服务端实现携带 WebUI 导航语义，如 `/new` 返回 `/session/new?title=...`）。覆盖方式为「按名拦截后不下发」；`ICommandRegistry` 仍用于 `/help` 展示与 skill 命令发现。

**补全**：输入以 `/` 开头且无空格时 Tab 触发；唯一前缀直接补全，多候选打印候选表（Phase 5 升级方向键下拉）。

---

## 11. 附件 / 图片

1. `/attach <path> [mime]` —— 读取文件 → Base64 → `ChatAttachment { Base64Data, MimeType, FileName }`；
2. `@<path>` 前缀（如 `@./docs/a.png 看看这个`）——首个路径 token 转附件，其余为文本；**含空格路径须引号**（`@"C:\My Files\a.png" 说明`）或改用 `/attach`。

- 图片 mime 由扩展名 + `BinaryFileDetector` 判定；非文本大文件给体积警告并二次确认。
- **bracketed paste 的粘贴路径文本不自动转附件**，须显式 `@`/`/attach`，避免误读。
- 待发送附件在输入行上方显示 `[1] a.png (image/png, 42KB)`；`/detach <n>` 移除。

---

## 12. 错误处理与边界

### 12.1 输入层（修正 tech8：raw stdin）

**`Console.ReadKey` 不能承载 bracketed paste / CSI-u**（它返回 `ConsoleKeyInfo`，不暴露原始 VT 字节序列；`ESC[200~` 不会被整体交付）。因此：

- `RawInputReader` 使用 `Console.OpenStandardInput()` 的原始字节流读取，自行解析 VT 序列：
  - Windows 需 `ENABLE_VIRTUAL_TERMINAL_INPUT`（经 `Console` 模式设置或 P/Invoke `SetConsoleMode`）；
  - Unix 需 termios raw 模式（`ICANON`/`ECHO` 关闭）。
- 启动发 `ESC[?2004h` 启用 bracketed paste，退出发 `ESC[?2004l`；`ESC[200~`…`ESC[201~` 之间内容整体插入，内部换行不触发提交。
- **键语义**：`\r`=`\x0d` → 提交；`Ctrl+J`=`\x0a` → 插入换行；Shift+Enter 仅在终端发送可区分序列（Kitty/CSI-u）时生效，否则回落 `Ctrl+J`；↑/↓ 历史召回；Tab 补全。
- **`Console.TreatControlCAsInput = true`**：否则默认 Ctrl+C 触发 `CancelKeyPress` 终止流程，输入层收不到该键。
- **输入线程可取消**：阻塞读无法被取消，退出时通过「发送哨兵/关闭标准输入 + `CancellationToken`」停止读取线程；`InputKeyDecoder` 为纯函数便于单测。
- `Ctrl+C`：输入非空 → 清空；输入空且执行中 → 取消执行；输入空且空闲 → 二次确认退出。
- `Esc`：执行中 → 取消执行；提示中 → 取消提示（权限=不决策）。

### 12.2 日志与标准输出隔离

- `builder.Logging.ClearProviders()`，日志写文件 `~/.seeing/logs/seeing-tui-{yyyyMMdd}.log`（新增极简 `TuiFileLoggerProvider`，仅写文件）。
- **实现说明（日志路径）**：`Program.cs` 的 `ResolveLogDirectory()` 固定返回用户级 `~/.seeing/logs/`（**与 workspace 无关**，目录不存在自动创建），文件名 `seeing-tui-{yyyyMMdd}.log`；与 §12.7 一致。
- **实现说明（控制台编码）**：启动第一步（任何 `Console`/`AnsiConsole` 访问之前）执行 `TuiConsoleEncoding.EnsureUtf8()`，把标准输入/输出强制为无 BOM UTF-8（代码页 65001）；退出时 `TuiConsoleEncoding.Restore()` 还原原编码，避免残留 65001。无控制台/重定向时 setter 抛异常被吞掉，静默跳过。
- **v2 不采用 `Console.SetOut` 代理**：`AnsiConsoleOutput` 仅 `AnsiConsoleOutput(TextWriter)` 构造且 `IsTerminal` 只读，代理 writer 可能导致 `IsTerminal=false`、Spectre 退化为非 ANSI 后端或对真实句柄做游标操作（已知在重定向下 `LiveDisplay.Start` 会抛 `IOException`）。
- 散落输出防护降级为 **Phase 6 可选 spike**：验证「`Console.SetOut(proxy)` + 代理正确透传终端能力」后再启用。可行做法是让代理**实现 `IAnsiConsoleOutput`**（显式提供 `Writer` / `IsTerminal` / `Width` / `Height` + `SetEncoding`），而非仅包 `TextWriter`——否则 `IsTerminal` 会误判、Spectre 可能退化后端或对真实句柄做游标操作。
- v1 风险缓解：宿主组合由我们掌控、日志已隔离、工具子进程输出由工具捕获，直接写 `Console` 的组件极少。

### 12.3 渲染边界

- 宽度以 `console.Profile.Width` 为准；Spectre 处理 CJK 宽字符。
- 活动区溢出回退（§6.3）。
- 窗口尺寸变化：每帧重读 `Profile`；`Console.CancelKeyPress` 参与取消语义。
- Markdown 极端输入不得抛出（`MarkdownTerminalRenderer` 全量降级）。

### 12.4 取消语义

- **禁止** `IChatOrchestrator.Cancel(string)`（内部 `Task.Run(...).GetAwaiter().GetResult()`，同步阻塞，会卡主循环）。
- 统一 `IExecutionSubmitter.CancelAsync(executionId)` 与 `CancelBySessionAsync(sessionId)`。

### 12.5 退出

顺序：停止输入读取线程 → 停止事件泵 → （若有执行中）提示并 `CancelAllInFlightAsync` → 两个 `SurfaceRegistry.Unregister(provider)` → 退出屏障 `FlushAllAsync`（§7.3）→ 恢复终端（`ESC[?2004l`、显示光标 `ESC[?25h`）→ `return 0`。

### 12.6 其他

- Agent/模型不可用：提交前校验 `IModelConfigManager.GetModelsByType(ModelType.Text)`；不可用则拒绝提交并保留输入。
- ACP 运行时：透传 Mode/Model；状态栏显示 ACP 模式（`mode.update`）。
- `--resume` 无效 Id → 退出码 3。

### 12.7 参数与默认值

| 参数 | 默认值 | 说明 |
|------|--------|------|
| 工具结果预览行数 | 12 | 超出截断并提示 `/expand <callId>` |
| 流式重绘字符阈值 | 代码块 20 / 普通 8 | 另加 60ms 定时上限 |
| 活动区高度阈值 | `Profile.Height - 4` | 超出触发固化回退 |
| 安全提交行数 | 24 | 累计行数触发固化 |
| 输入历史条数 | 100 | 仅内存 |
| 输入行最大显示行数 | 6 | 超出内部滚动 |
| 快照发布去抖 | 50ms | `TuiSurfaceProvider` |
| 空集确认期 | 1s | 同上 |
| 渲染命令通道容量 | 1（合并 Update） | 避免渲染积压 |
| 日志目录 | `~/.seeing/logs/` | 文件名 `seeing-tui-{yyyyMMdd}.log` |
| 默认场景 | `full` | `TuiHostShape.Descriptor.DefaultScenario` |
| 非 TTY 退出码 | 2 | 未启动 |
| `--resume` 无效 Id 退出码 | 3 | 未启动 |
| 推理显示 | 折叠 | `/reasoning` 切换 |

---

## 13. 测试策略（修正 tech11）

| 层 | 测试对象 | 方式 |
|----|----------|------|
| 纯逻辑 | `InputKeyDecoder`（键序列 → 动作：提交/换行/历史/补全/取消/粘贴边界） | 表驱动 xUnit |
| 纯逻辑 | `TuiCommandRouter`（本地拦截 vs 转发、别名、未知命令、覆盖声明） | xUnit + fake `ICommandRegistry` |
| 纯逻辑 | `TuiEventInterpreter`（各事件 → 视图态；`ExecutionStartedEvent`/`LoopStartEvent` 配对；按 key upsert 的幂等回放；**切回后台运行中会话时进行中 LoopId 被保留、续渲不重复不回退**；**含 `EventBufferSize` 溢出——`stream.start` 不在回放缓冲——的长流式切回不得双写**） | xUnit + 快照 `SessionData` |
| 纯逻辑 | `MarkdownTerminalRenderer`（标题/列表/表格/围栏/超长/未闭合 → 不抛，降级） | xUnit（渲染为文本断言） |
| 纯逻辑 | `TuiSurfaceProvider`（组快照、在途并入、去抖、空集确认、注册/注销） | xUnit + fake 管理器/registry |
| 纯逻辑 | `TuiTaskTracker`（taskId 解析、子流聚合、终态收敛） | xUnit + **fake `ITuiEventPump`** |
| 集成（DI） | `AddSeeingHostingTui` 解析全部服务、`IPermissionChannel` 存在、`host.StartAsync()` 后组事件桥工作 | `ServiceCollection` + 最小 Host |
| 渲染 | `TuiViewState` 驱动 `ITerminalSurface` 的 fake 记录 `Update`/`Commit` 序列 | xUnit |

- **`ITuiEventPump` 抽象**：使 `TuiTaskTracker` 与主循环可在无真实 `IChatOrchestrator` 下单测。
- **不新增测试依赖**：以 `ITerminalSurface` / `ITuiEventPump` 抽象替代真实终端与事件源；如需 Spectre 官方测试基建可评估 `Spectre.Console.Testing`（0.57.2 存在），但**非必需**。
- 测试项目落位 `tests/apps/Seeing.Agent.Tui.Tests`（镜像 `tests/apps/Seeing.Agent.WebUI.Tests`），加入 `.slnx` 的 `/tests/apps/`；sample 的 `.csproj` 增加 `InternalsVisibleTo`。
- 运行：`dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll`（遵循 AGENTS.md 的 VSTest 约定）。

---

## 14. 合规与反模式核对

| 反模式 | 本设计的对策 |
|--------|--------------|
| 在 Core 加工具/能力包引用 | 不触碰 Core；能力包选择仅在 sample |
| 使用已删除的 `AddSeeingAgent` | 使用 `AddSeeingCore(registry)` + 显式模块 |
| 能力包引用 Core 具体类型 | `Hosting.Tui` 属脊柱层；能力包不引用 `Hosting.Tui` 或 sample |
| `ConfigureServices` 永久挂死工具且无法 Deactivate | 能力挂载仍走既有 `Activate` 机制 |
| schema 在 executor/多处重复计算 | TUI 不算 schema，只经 `SubmitAsync` |
| SeeingAgent 配置写进 appsettings.json | TUI 只读 `.seeing/seeing.json` / `~/.seeing/seeing.json` |
| 混用路径分隔符 | 使用 `Path.Combine`；路径由既有 `WorkspaceProvider` 解析 |
| 静默吞异常 | 渲染降级与提示失败均记日志 |
| Hook 点字符串硬编码 | 不使用 Hook 字符串 |
| Context 类添加业务逻辑 | `TuiViewState` 纯数据容器 |
| 无交互宿主却期望审批 | 显式注册两个 `SurfaceRegistry` 的 provider；非 TTY 拒绝启动 |
| 同步包装阻塞 async | 禁用 `IChatOrchestrator.Cancel`，只用 `CancelAsync` |
| WebUI 项目禁用 CPM | TUI 启用 CPM；`Spectre.Console` 升 `0.57.2`、`Markdig` 升 `1.4.0` |
| 宿主形态含重 UI | `Hosting.Tui` 仅接线，UI/引擎全在 sample |

**文档漂移处置**：不依赖已删除的 `IPermissionPresenter` / `IPermissionPresentationStore`；实现以 `Interactions` 契约与 `SurfaceRegistry` 为准。建议实现落地时同步修订 `AGENTS.md` 与 `docs/architecture/08` 的失效类型名（独立小改动，需用户确认后单独提交）。

---

## 15. 分期实施

| 阶段 | 内容 | 出口标准 |
|------|------|----------|
| **P0 依赖升级 + 技术验证** | 升级 `Spectre.Console 0.57.2` / `Markdig 1.4.0`；spike 验证：①`Live` 委托返回 + `AutoClear(false)` + 重启的固化行为（含重启前剔除已 Commit 内容不重复）；②raw stdin + bracketed paste 在 Windows Terminal / pwsh 与 Linux 的表现，含**输入线程取消路径**（Windows `ReadFile` 阻塞的取消手段）与旧终端无 bracketed paste 时的降级（粘贴内换行不得误触发提交）；③`IRenderable.Measure(new RenderOptions(Profile.Capabilities, size))` 高度估算；④**切回后台运行中会话的流式续渲**不回退，含 `EventBufferSize` 溢出的长流式切回不双写 | 最小程序：活动区 + 固化 + 多行输入 + 粘贴，无撕裂；四项验证通过；否则启用 R1 后备（自研 ANSI） |
| **P1 宿主与最小闭环** | 薄 `Hosting.Tui` + sample 组合根（含 `host.StartAsync`）+ `TuiChatEngine`/`ITuiEventPump`/`TuiInputEditor`/`ITerminalSurface`；新建/继续会话；提交；流式文本；取消；退出 | 与真实模型完成一轮对话并流式显示，Esc 可取消 |
| **P2 渲染完备** | Markdown、推理折叠、工具卡（bash/todowrite）、截断与 `/expand`、`CommandResultEvent` | 工具调用可视、结果可读 |
| **P3 交互完备** | `TuiSurfaceProvider`（两 registry、组快照、空集确认）+ 权限提示 + 问答提示 + 状态栏待批 | 需要审批的 bash/写文件可在终端授权；**含子代理审批专项用例** |
| **P4 子代理与状态** | `TuiTaskTracker` + 进度卡 + `/open` + Todo 面板 + Token 预算状态 | 子任务进度可见、可进入子会话 |
| **P5 会话与命令完备** | `/sessions` `/resume` `/rename` `/delete` `/fork` `/agent` `/model` `/thinking` `/scenario` `/auto-approve` `/reasoning` `/help`、补全、附件 | 全功能对齐（除 §2.2 非目标） |
| **P6 健壮性与测试** | 溢出回退、resize、退出屏障、日志隔离、`Console.SetOut` 代理 spike（可选）、单测补齐、README | 测试全绿、界面无撕裂、退出干净 |

---

## 16. 风险与开放问题

| # | 风险 | 影响 | 应对 |
|---|------|------|------|
| R1 | `Live` 委托 + `AutoClear(false)` + 重启在部分终端闪烁/残留 | 观感/稳定 | P0 spike；`ITerminalSurface` 隔离，必要时换自研 ANSI 活动区（§6.3 后备） |
| R2 | Shift+Enter 在多数终端不可区分 | 输入体验 | 以 `Ctrl+J` 为主，文档说明；Kitty/CSI-u 探测后启用 |
| R3 | 活动区高度溢出渲染错乱 | 稳定 | §6.3 溢出回退 + 安全提交边界，P0 验证 |
| R4 | 子代理权限请求因可呈现性缺失被静默 Deny | 功能正确性 | §5.2 组快照 + 空集确认 + `host.StartAsync`；P3 专项集成用例 |
| R5 | 宿主内其他组件写 `Console` 撕裂界面 | 稳定 | §12.2 日志隔离；`Console.SetOut` 代理列为 Phase 6 可选 spike |
| R6 | 长会话重绘性能 | 性能 | 仅固化追加、活动区只留尾部；渲染通道容量 1 合并 Update |
| R7 | ~~Spectre 引入脊柱层~~ | — | **已解决**：引擎在 sample，`Hosting.Tui` 零第三方 UI 依赖 |
| R8 | 会话组快照频繁重算 | 性能 | 50ms 去抖 + 仅重算活跃组 |
| R9 | ACP 运行时（Cursor）与 Native 行为差异 | 兼容 | 复用 WebUI 相同分流（`AgentDefinition.Runtime`）；P5 验证 |
| R10 | 与 Gateway/后台会话共享 `<workspace>/.seeing/sessions` 的写入竞争 | 数据 | 既有 `WriteBehindSessionStore` 去抖 + 原子替换；TUI 不新增写路径 |
| R11 | Spectre 0.50+ 破坏性变更（如 ANSI 拆分、Live API） | 升级/实施 | §0.2/§0.3 策略：用最新稳定 + P0 编译与反射校验 + 记录 API 事实 |

**开放问题（待用户/审查确认）**

1. 默认 `DefaultScenario="full"`（对齐 WebUI）——确认。
2. 是否在 P5 一并提供 `--print` 一次性问答（非交互）模式——当前列为非目标。
3. 推理默认折叠——默认折叠，`/reasoning` 切换。
4. `Spectre.Console.Cli` 是否从 CPM 移除——本设计不用；建议后续单独清理。
5. `System.CommandLine` 是否同步到 2.0.12——低风险可选。
6. `TaskSessionResolver` 逻辑移植的 DRY 问题——本设计接受重复（WebUI 不改）；未来可上提为共享纯函数（单独立项）。
