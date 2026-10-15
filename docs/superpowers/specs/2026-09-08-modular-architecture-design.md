# Seeing.Agent 模块化架构设计

**日期:** 2026-09-08
**状态:** 草案（重设计后）
**范围:** 框架分层、能力 seam、模块生命周期、宿主组合。不含具体工具算法或 UI 改版。

本文是目标架构，不是过渡方案。开发阶段允许一次性重构，不保留兼容入口。先搬家，再改项目名；禁止把 `Seeing.Agent` 改名为 Core 却仍装着全部工具。禁止在旧形状上打补丁。

---

## 1. 问题

当前文档写的是「原语 → 主库 → 扩展」，后半段已部分落地（Scheduler / Memory / ACP / TokenBudget / Gateway）。主库本身仍是产品默认包：

- `AddSeeingAgent()` 一次注册 FS / Shell / Web / Todo / Time / Task / Skill / 整棵 MCP / OpenAI+Anthropic 客户端。
- `Seeing.Agent.csproj` 编译依赖 `ModelContextProtocol.Core`。
- 扩展包违反「只引用 Abstractions」，直接引用主库具体类。
- `IExtension` 与 `AddXxx()` 双轨；`ComponentManager` 在 Core 里写死 Skill/Mcp/Plugin 加载器。
- 工具直打 `File.*` / `Process.Start`。没有 `IFileSystem`，`IShellService` 只选方言和杀进程树，不是执行缝。
- `ToolManager` 写死 `PrimaryTools` / `SubAgentTools`，schema 与真实能力不一致。
- `SeeingAgentOptions` 认识 Gateway / ACP / TokenBudget / Plugins 等所有子系统；`UnifiedConfigManager` 在 `BuildSectionRegistry` 里写死全部节。
- `PromptBuilder` 直接依赖 `SkillManager` 具体类。
- `ExecutionJobService` 是具体类，`TaskTool` 用服务定位器在运行期解析它。

目标：可扩展、可定义。进程里有什么由宿主引用决定；这次启用什么由配置决定；换沙箱只换执行世界，不改工具。

---

## 2. 决策摘要

| 主题 | 决定 |
|------|------|
| 组合 | A 编译期引用 = 进程目录；B `seeing.json` = 启用/激活子集。不做运行期热插卸程序集 |
| 作者 | 宿主登记目录；用户选子集。目录外 id 告警并忽略 |
| 拆包 | 按能力拆工具包；配置可禁用单个工具。不按单个工具拆 csproj |
| 提供方 | `IFileSystem` + `ISubprocess` 成对绑定为 `IExecutionWorld`。工具只吃接口 |
| 扩展模型 | 单一 `ISeeingModule`。删除 `IExtension` 与文件插件扫描 |
| 产品形态 | 拆成正交两层：Host Shape（部署形态，宿主代码选）+ Scenario（场景预设，配置选）。禁止全能 `AddSeeingAgent()` |
| 场景粒度 | 进程级 Scenario + per-session Scenario 覆盖。模块进程级加载，可见性按会话场景收窄 |
| 循环 | 留在 Core。不拆成多个 NuGet，不抄 Cordis / 会话事件溯源 |
| 可见性 | 可见 = 可向模型请求。Ask 可出现在 schema。Deny/未启用/禁用不得进 schema |
| 子代理 | Hosting 级，不是 FS 那种可选能力包 |
| 实施顺序 | 先抽类型与引用，再改项目名 |

明确不做：Cordis、行级 patch、isolate realm、按工具拆包、`seeing.json` 选择 Profile、磁盘热加载 DLL、保留 `AddSeeingAgent()` 全开、用 `Hosting.Web` 冒充脊柱。

---

## 3. 分层与包

```
原语          Session / TokenEstimation / ConfigSchema
契约          Seeing.Agent.Abstractions
脊柱          Seeing.Agent.Core
执行世界      Seeing.IO.Local          （日后 Seeing.IO.Sandbox）
宿主运行时    Seeing.Agent.Hosting     队列、Orchestrator、Task、命令发现
能力          Tools.* / Mcp / Skills / Llm.* / 已有扩展包
组合          Hosting.Minimal | Web | Headless | Gateway
```

依赖只向下。Core **禁止**引用任何能力包、IO 提供方、Hosting。能力包只引用 Abstractions（需要调度口时只依赖接口）。Hosting 可引用 Core + Abstractions；**禁止**引用 ACP。组合包可以引用能力包，这是产品闭包，不是脂肪核。

当前 `src/Seeing.Agent` 在类型搬空后重命名为 `Seeing.Agent.Core`。当前 `Seeing.Agent.App` 并入 `Seeing.Agent.Hosting`，并切断对 `Seeing.Agent.Acp` 的项目引用（斜杠命令不得 `new AcpDynamicSkillCommand`）。

### 3.1 Core 里有什么

循环（`IAgentExecutor` 的 Native 实现）、空的 `IToolManager`（含 schema API）、Hook、权限引擎、配置脊柱（`IConfigSectionStore`，不拥有各子系统 Options 形状）、装饰器链、提示词**装配**（`PromptBuilder` + `IPromptSectionContributor`）、模块目录与启用结算、压缩**入口**（依赖 `ISummarizer`，默认无操作）、`ITodoStore` 默认实现。

### 3.2 Core 里没有什么

任何 `ITool` 实现、MCP SDK、具体 LLM HTTP 客户端、Git/FS/Web 实现、内置 Agent 人格正文、Gateway/TokenBudget/ACP 的 Options 校验器、万能 `ComponentManager`、文件插件扫描。Skill/MCP 的发现留在各自模块内。

### 3.3 包地图

| 包 | 职责 |
|----|------|
| Abstractions | 接口、DTO、`ISeeingModule`、`IModuleCatalog`、`IFileSystem`、`ISubprocess`、`IExecutionWorld`、补全后的 `IToolManager`、`IPromptSectionContributor`、`IExecutionSubmitter` |
| Core | 见 3.1 |
| IO.Local | 本地盘 + 本地进程，成对注册为一个 `IExecutionWorld` |
| Hosting | `ExecutionJobService`、会话队列、idle 调度、ChatOrchestrator、`TaskTool` / `TaskStatusTool`、`todo_write`、斜杠命令注册表（空） |
| Tools.FileSystem | read/write/edit/glob/grep/delete/workspace，只经 `IFileSystem` / `ISubprocess` |
| Tools.Shell | bash 方言执行，spawn 只经 `ISubprocess` |
| Tools.Web | fetch / search / code-search |
| Tools.Git | git 服务与工具，spawn 只经 `ISubprocess` |
| Tools.Basic | `current_time`（以及若需独立于循环提醒的轻量工具）。不单独为 Time 再拆包 |
| Skills | SkillManager + `skill` 工具 + 路径发现 |
| Mcp | 现有 MCP 栈整包迁出 |
| Llm.OpenAI / Llm.Anthropic | 各注册一个 `ILlmClientFactory`，Core 只聚合 |
| Agents.BuiltIn | build/plan/explore/general/title/summary 的**人格**，不含工具说明书 |
| 已有 | Scheduler / Memory / ACP / TokenBudget / Gateway* 改为 `ISeeingModule`，去掉「先全开核心再补丁」 |

Todo：`ITodoStore` 在 Core（数据口，不是工具）。`todo_write` 由 **Hosting** 登记，与 Task 同属循环配套，**不**做成独立 NuGet。循环提醒只通过 Hook 发出，禁止执行器写死工具名。Core 程序集不含任何 `ITool` 实现。

### 3.4 部署形态（Host Shape）与场景（Scenario）正交

当前 spec 的 Profile 把「怎么跑」和「跑什么能力」焊死了。拆成正交两层：

- **Host Shape**（部署形态）：`Web` / `Headless` / `Gateway` / `Embed`。宿主代码选，编译期。只决定 UI 表面、服务端点、宿主 HostedService。**不**决定能力集。
- **Scenario**（场景预设）：`Code` / `Work` / `Research` / `Minimal` / 自定义。`seeing.json` 选或宿主代码提供，可热重载。决定启用模块集 + 默认 Agent + seams 绑定 + 工具禁用。

二者正交：

| | Web | Headless | Gateway | Embed |
|---|---|---|---|---|
| Code | ✓ | ✓ | — | ✓ |
| Work | ✓ | ✓ | ✓ | — |
| Research | ✓ | — | — | — |

Host Shape 包（`Seeing.Agent.Hosting.Web` 等）**不 ProjectReference 能力包**，只做：UI 表面 + 宿主 HostedService + 声明本形态可承载哪些模块类型。能力包由宿主 sample（`samples/Seeing.Agent.WebUI` 等）自己引用。

Scenario 是**纯数据预设**，不是包。内置 Scenario 定义在 Core（或 `Seeing.Agent.Scenarios` 数据包），用户可在 `seeing.json` 的 `scenarios` 节自定义。

**内置 Scenario 在 Core 中以纯字符串 id 数组表达，Core 不引用也不解析 `Tools.Git`/`MemoryModule` 等类型；id 与模块的绑定只发生在宿主 `IModuleCatalog` 结算时（`available` 集合）。** Scenario 引用了未引用的模块 id → 结算时该 id 不在 `available`，告警并忽略，不拒启。

| 内置 Scenario | 启用模块 | 默认 Agent |
|---------------|----------|-----------|
| `minimal` | Core + Hosting + IO.Local + 一个 LLM + Basic | general |
| `code` | minimal + FileSystem + Shell + Git + Subagent | build |
| `work` | minimal + FileSystem + Web + Memory + Scheduler | general |
| `research` | minimal + Web + Memory | explore |
| `full` | 全部已引用模块 | build |

嵌入方可以不引用任何 Hosting.*，只 `AddSeeingCore()` + 自己点名的包 + 自己的 Scenario。无 Scenario 且配置未写 `modules.enabled` 时，启用集为空。

禁止再提供 `AddSeeingAgent()` 暗装工具。

---

## 4. 能力 Seam

一个 seam 必须同时有：定义（Abstractions）、提供方（可替换包）、消费方（通常是工具）。只拆工具项目、提供方仍写在工具里，不算模块化。

### 4.1 执行世界（必须成对）

| 定义 | 默认提供方 | 消费方 |
|------|------------|--------|
| `IFileSystem` | IO.Local | Tools.FileSystem、权限路径解析、输出落盘若触盘 |
| `ISubprocess` | IO.Local | Tools.Shell、Tools.Git、MCP stdio、ACP 子进程、经进程的 Grep/Glob |

二者必须属于同一个 `IExecutionWorld`。Profile 的 `seams.executionWorld` 一次换掉整对。禁止只换盘不换进程（或相反），避免工具搜本地、写远程。

`IShellService` 降为选 bash/pwsh 的辅助，**禁止**再作为 spawn 缝。`BashTool` 与 `GitService` 不得 `new ProcessStartInfo`。

Grep/Glob：经 `ISubprocess` 跑搜索，以便跟随执行世界。禁止默认在本机 ripgrep 远程盘。

路径截断、MIME、行号渲染可留在工具包；路径解析、读、写、list、spawn 必须进提供方。

**三层归属（避免 IO.Local 反向依赖 Core）：**

- `ISubprocess`（IO.Local）只做纯 spawn：`StartAsync(SubprocessSpec)` → `ISubprocess`。`SubprocessSpec` 含 command/workdir/env/encoding/timeout/cancel。提供方内部 `new ProcessStartInfo` + 实现进程树 kill。IO.Local 不反向依赖 Core。
- `shell.env` Hook 由 **BashTool** 在调 `ISubprocess.StartAsync` 前触发，拿 env dict 塞进 `SubprocessSpec.Environment`。
- 危险命令拦截留 BashTool（工具层安全策略），`ShellOptions` 随 5.4 拆节迁出 `SeeingAgentOptions`，BashTool 通过 `IOptionsMonitor<ShellOptions>` 直接拿。
- PowerShell UTF-8 注入归 `IShellService.PrepareCommand(shellName, command)`（方言辅助），BashTool 调它拿注入后命令串再传 `ISubprocess`。

**`IWorkspaceProvider` 只保留「配置目录在哪」**（`.seeing` 放哪）：只留 `UserSeeingDirectory`/`ProjectSeeingDirectory`，**删 `WorkspaceRoot`**。工具运行时的 cwd 必须来自 `IExecutionWorld`。`IExecutionWorld`（Abstractions）暴露 `Cwd` + `IFileSystem` + `ISubprocess`。BashTool/GrepTool/GlobTool/ExecutionJobService 注入 `IExecutionWorld`，cwd fallback 改 `_world.Cwd`。`ToolManager` 保留 `IWorkspaceProvider`（只用于 tool-state.json 路径，属配置目录用途）。`IExecutionWorld` 由 `seams.executionWorld` 在结算时绑定，DI 单例。

### 4.2 其它 seam

| Seam | 规则 |
|------|------|
| `ILlmClientFactory` | 多实现并列。`ProviderConfig.Type` 由 `ProviderType` 枚举改为 `string`（`"openai"`/`"anthropic"`/`"ollama"`）；`ILlmClientFactory.SupportedTypes` 改为 `IReadOnlySet<string>`，`SupportsType(string)` 做**大小写不敏感**比较。Core 聚合 `IEnumerable<ILlmClientFactory>`，按 `config.Type` 取首个 `SupportsType==true` 的工厂，多工厂支持同 type 时**先注册 wins**。删除 Core 内 `DefaultLlmClientFactory` 的 OpenAI/Anthropic switch。旧 `ProviderType` 退为内置常量字符串，不作路由键。**存量 `providers.json` 迁移：** 反序列化时把旧 PascalCase 值归一化为小写（`OpenAI→openai`、`Anthropic→anthropic`），读时容错、写时新格式；首次保存时自动迁移，不要求用户手改 |
| `IMcpManager` | 仅 Mcp 包实现。不作为新模块的接口样板（保持瘦：登记工具 / 提供服务 / 贡献提示词） |
| `ISkillManager` | 仅 Skills 包 |
| `IAgentExecutor` | **一个门面**。Abstractions 新增 `IAgentExecutorImplementation`（Native + ACP 各实现）。Core 注册 `AgentExecutorRouter` 为 `IAgentExecutor`，内部聚合 `IEnumerable<IAgentExecutorImplementation>`，按 `AgentRuntime` 分派。ACP 模块 `ConfigureServices` 注册 `AcpAgentExecutor` 为 `IAgentExecutorImplementation`（追加，非 Replace）。删除 `AcpServiceCollectionExtensions.ReplaceExecutionRouter`。**`AcpAgentExecutor` 改为 implementation 后删除对 `NativeAgentExecutor` 的构造器依赖与 `Runtime != AcpPassthrough` 的透传分支；分派由 router 负责，implementation 不得再持有兄弟 implementation** |
| `ISummarizer` | Core 默认无操作；LLM 摘要实现跟 LLM 或 TokenBudget，不绑死 OpenAI |
| `IPermissionChannel` | Core 默认；WebUI / DenyAll 覆盖。可通过 `seams.permissionChannel` 绑定已登记实现 |
| `IExecutionSubmitter` | 抽到 Abstractions，含 `SubmitAsync` + `Cancel(executionId)` + `CancelBySessionAsync` + `WaitForExecutionAsync`，供 `TaskTool` 与 `TaskStatusTool` 使用，打破 `ToolManager → TaskTool → ExecutionJobService → IAgentExecutor → ToolManager` 构造环。`IExecutionStatusProvider`（`GetOverview` + `GetExecution`）一并搬到 Abstractions。事件订阅不进这两个接口，走 `IExecutionEventPublisher`（已在 Abstractions）。`TaskTool`/`TaskStatusTool` 构造期注入 `IExecutionSubmitter` + `IExecutionStatusProvider` + `IExecutionEventPublisher`，删 `context.Services` 服务定位器 |

同一 seam 的提供方（执行世界、权限通道）在目录里只能有一个绑定。多个 LLM 工厂不是替换。

### 4.3 提示词分节

Core 只装配。能力包实现 `IPromptSectionContributor`（稳定 section 名与顺序）。`Agents.BuiltIn` 只写人格，不写「用 read 不要 cat」。未激活的模块零贡献。

**删除 `PromptBuilder` 对 `SkillManager` 具体类的直接依赖。** 技能列表由 Skills 模块通过 `IPromptSectionContributor` 贡献，Core 只消费接口。

**改「占位符替换」为「section 注入」：** `PromptBuilder.BuildAsync` 先跑 base prompt，再按 contributor `Order` 把 section 文本 append 到固定锚点（如 `## Tools`/`## Skills` 标题下），**不依赖 Agent prompt 写占位符**。`IPromptSectionContributor` 暴露 `SectionName` + `Order` + `BuildAsync(PromptContext)`。`BuildToolSection` 仍由 Core 算（吃 `context.Tools`），包装成 Core 内置 contributor；`SkillManager` 改实现 `ISkillsSectionContributor`。spec 明确 section 注入锚点约定（标题名），内置 Agent prompt 需改写以含锚点标题。

---

## 5. 模块生命周期

只保留一条管线。

```
宿主登记目录（A）
  → 读 seeing.json 结算启用集（B）
  → 目录内已引用模块 ConfigureServices（惰性）
  → Build
  → Activate 启用集
  → 工具级开关
```

### 5.1 契约

`ISeeingModule`：

- `Id`：稳定 id（`filesystem`、`mcp`、`llm.openai`、`io.local`）
- `ProvidedTools` / `ProvidedSeams` / `DependsOn`（模块 id）
- `ConfigureServices`：只登记 DI。禁止在此无条件 `AddHostedService` 并启动连接
- `ActivateAsync` / `DeactivateAsync`：挂摘工具、连断外部、登记/撤销 ReloadHandler、贡献/撤销提示词分节

同一 `Id` 重复登记且未 `ReplaceModule` → **启动失败**。`ReplaceModule` 后目录与 seam 均为新提供方。

`AddSeeingMcp()` 等是 `AddSeeingModule<McpModule>()` 的薄封装，方便不走 Profile 的嵌入方。

### 5.2 诚实加载

| 说法 | 含义 |
|------|------|
| 不需要的能力不进进程 | 宿主不引用该包（Minimal / 自定义组合） |
| 用户关掉 MCP | 程序集仍在（若 Profile 引用了它）；不 Activate、HostedService 见开关后空转结束、工具不注册 |
| 换沙箱 | 宿主引用 `IO.Sandbox` 并 `seams.executionWorld = sandbox`，或 `ReplaceModule` |

`Activate`/`Deactivate` **不承诺**从 MS.DI 卸单例、卸 `IHostedService`、卸程序集。

**HostedService 与连接类资源的可停面：**

- 每个 HostedService `StartAsync` 第一行查 `IModuleCatalog.IsEnabled(moduleId)`，未启用即 return（Scheduler 已是样板，ACP/Memory 三个 HostedService 全部补这道门）。
- 长循环 `BackgroundService.ExecuteAsync` 改双条件 `while (!stoppingToken.IsCancellationRequested && moduleEnabled)`，`Deactivate` 翻 `moduleEnabled=false` + 写哨兵消息唤醒 Channel 读取。
- 连接类资源（`SqliteConnection`、`AcpConnectionManager`）**不进 DI singleton**，由模块 `Activate/Deactivate` 自管生命周期；DI 只注册工厂，每次 Activate 取新实例。

热重载：目录内已 `ConfigureServices` 的模块可再 Activate/Deactivate；不能启用从未引用的包。

### 5.3 结算

结算分两级：**进程级**（启动时 + 热重载）和 **会话级**（每次执行前）。

**进程级结算**（决定哪些模块 Activate）：

```
available = 宿主目录（Host Shape 引用的包）
scenario  = seeing.json.scenario ?? Host Shape 默认 scenario
base     = scenario.modules ∩ available          （无 Scenario 则为空）
enabled  = (用户 modules.enabled ?? base) ∩ available − 用户 modules.disabled
```

**会话级结算**（决定本次执行的工具可见集，不改变模块 Activate 状态）：

```
sessionScenario = session.Scenario ?? 进程级 scenario
sessionBase     = sessionScenario.modules ∩ 进程级 enabled
sessionEnabled  = (session.modules.enabled ?? sessionBase) ∩ 进程级 enabled − session.tools.disabled − 用户 tools.disabled
```

会话级只能**收窄**进程级已启用的模块，不能启用进程级没有的。会话级 Scenario 覆盖进程级 Scenario 时，取交集。

**在途轮次边界：**

- 进程级 Scenario 热重载触发 Activate/Deactivate。若检测到有在途执行，**Deactivate 推迟到在途执行结束**（不截断进行中工作）；强制切换时取消在途并报错，二选一由 `ReloadOrchestrator` 配置决定，默认等待。
- 会话级 `session.Scenario` 切换只对**下一次 `SubmitAsync`** 生效。当前轮次用其 `BuildExecutionContextAsync` 时快照的 scenario 跑完，中途切换不影响在途轮次。
- 「立即变化」定义在「下一次执行」，非「当前执行」。

**schema 唯一计算点（避免双算）：** `ExecutionJobService` 是 schema 的**唯一计算点**。新 `IToolManager` API 接受「层 1∩层 2 已结算的 tool id 集」+ `agent`，返回层 3（Agent Allowed/Denied）过滤后的 schema。`ExecutionJobService` 在调 executor 前算好 schema，结果经 `AgentContext.ToolSchemas` 传入 executor。`NativeAgentExecutor.BuildRequestAsync` **删除自算 schema 的 `GetToolSchemas(agent)` 调用**，改读 `context.ToolSchemas`。`SchemaSnapshotEvent` 与 `ChatRequest.Tools` 同源，不变量才成立。

未知 id → 告警、忽略。启用集中的模块 `DependsOn` 未启用 → **拒绝启动**（硬依赖）。Agent 提示词里提到的可选工具未启用 → 不拒启。

文件插件 / `ExtensionLoader` / `seeing.json` 的 Plugins 扫描：**删除**。第三方只通过编译期包进入目录。

### 5.4 配置形状

两级文件仍是 `~/.seeing/seeing.json` 与 `./.seeing/seeing.json`，项目覆盖用户。数组按现有 `MergeDeep` 整段替换。

```json
{
  "host": "web",
  "scenario": "code",
  "scenarios": {
    "code": {
      "modules": ["io.local", "filesystem", "shell", "git", "subagent", "agents.builtin", "llm.openai"],
      "defaultAgent": "build",
      "seams": { "executionWorld": "local" },
      "tools": { "disabled": [] }
    },
    "work": {
      "modules": ["io.local", "filesystem", "web", "memory", "scheduler", "agents.builtin", "llm.openai"],
      "defaultAgent": "general"
    },
    "custom-research": {
      "modules": ["io.local", "web", "memory", "agents.builtin", "llm.openai"],
      "defaultAgent": "explore",
      "tools": { "disabled": ["bash"] }
    }
  },
  "modules": {
    "enabled": null,
    "disabled": ["git"],
    "tools": { "disabled": ["delete"] }
  },
  "seams": {
    "executionWorld": "local",
    "permissionChannel": "default"
  }
}
```

- `host`：部署形态，由宿主代码选，**不**进 `seeing.json`（此处仅文档示意）。实际由 `AddSeeingWebHost()` / `AddSeeingHeadlessHost()` 决定。
- `scenario`：当前启用的场景名，指向 `scenarios` 里的一个预设。用户可切换。
- `scenarios`：场景预设字典。每个预设含 `modules`（启用模块集）+ `defaultAgent` + `seams` + `tools.disabled`。用户可自定义新 key。
- `modules.*` 与 `seams.*`：在 Scenario 之上的覆盖/裁剪。`modules.enabled=null` 表示用 scenario 的 modules。
- `modules.*`、`seams.*`、`scenarios.*` 由 Core 拥有。
- `mcp` / `skills` / `scheduler` / `gateway` 等节由对应模块拥有。Core 不校验、不注册这些 Options。
- `SeeingAgentOptions` 拆节，禁止神对象。每节配独立的 `<TOptions>` 类 + 独立 `IOptions<T>`/`IOptionsMonitor<T>`，各模块 `AddOptions<XAOptions>().Bind(configSection)` + 监听 `ConfigChanged` 中本节名。

**`UnifiedConfigManager` 的 `BuildSectionRegistry` 不再写死全部节。** Core 只注册 `modules` / `seams` / `agents` / `permission` / `models` / `workspace` 等脊柱节。能力包在 `ConfigureServices` 里通过 `IConfigSectionRegistry` 注册自己的节（`mcp`、`skills`、`scheduler` 等）。`UnifiedConfigManager` 只消费注册表，不拥有节列表。`GetSection<T>` 直接从 `_cache` 反序列化，不再 switch 到 `SeeingAgentOptions`。

**`IConfigSectionRegistry` 时序（不能经 DI 取）：** `IConfigSectionRegistry` 是 `AddSeeingCore` 之前由宿主 `new` 的可变实例，`services.AddSingleton<IConfigSectionRegistry>(instance)`，`AddSeeingModule<T>(registry)` 共享同一实例。`UnifiedConfigManager.LoadAsync` **不在工厂内同步调**，推迟到 `InitializeSeeingAsync` 显式触发（此时所有模块 `ConfigureServices` 已跑完）。

**公共 API 形状（一次性破坏性改动）：** `AddSeeingCore(IConfigSectionRegistry registry)` 与 `AddSeeingModule<T>(IConfigSectionRegistry registry)` 共用同一参数签名。宿主 `Program.cs` 新模板：

```csharp
var registry = new ConfigSectionRegistry();
services.AddSingleton<IConfigSectionRegistry>(registry);
services.AddSeeingAcp(registry);        // 薄封装：AddSeeingModule<AcpModule>(registry)
services.AddSeeingScheduler(registry);
services.AddMemoryServices(registry);
services.AddSeeingCore(registry);       // 注册脊柱节到同一 registry
// ... build
await host.Services.InitializeSeeingAsync();  // pre-host，触发 LoadAsync + 结算 + Activate
```

`AddSeeingAcp`/`AddSeeingScheduler` 等薄封装签名变更是一次性破坏性改动。顺序约束：`AddSeeingModule` 必须在 `AddSeeingCore` 之前调用；`InitializeSeeingAsync` 是加载的唯一入口，必须在 `Host.StartAsync` 之前显式调用。

### 5.5 初始化

`InitializeSeeingAgentAsync` 改为 `InitializeSeeingAsync`：工作区 → 结算 → Activate。Skill/MCP 加载只发生在对应模块的 `ActivateAsync` 里。删除 Core 万能 `ComponentManager`。

**内置 Agent 人格出 Core 后的两阶段注册：**

- 删 `ServiceCollectionExtensions` 里的 `GetBuiltInAgents()` + `DiscoverAgentsAsync()` 调用。
- `AgentManager` 构造器删 `IEnumerable<AgentDefinition>` 参数，只持 `IAgentStore`，启动时 store 为空。
- `Agents.BuiltIn` 包定义 `AgentsBuiltInModule : ISeeingModule`，`ActivateAsync` 里把人格通过 `IAgentStore.RegisterAsync` 注册。
- 结算引擎保证 `Agents.BuiltIn` 在 `AgentManager.StartAsync` 之前 Activate。
- `InitializeSeeingAsync` 必须在 `Host.StartAsync` 之前显式调用（pre-host），确保模块 Activate 先于 HostedService 启动。

**SessionData 增加 `Scenario` 字段：** `SessionData` 新增 `string? Scenario`（会话级场景名，null = 用进程级）和 `SessionScenarioOverride`（可选的会话级模块/工具裁剪）。**存量会话兼容：** 反序列化缺失 `Scenario` 时为 null，结算时 null 回退进程级 scenario（不报错、不强制写值）。`SessionManager.CreateAsync` 新建会话时 `scenario` 参数可选，null = 进程级。`SessionManager.CreateChildAsync` 接受 scenario 参数；**`TaskTool` 创建子会话时显式传 `parent.Scenario`**（spec 点名 `TaskTool` 改造点）。会话级结算由 `ExecutionJobService` 在执行前读 `session.Scenario` + `SessionScenarioOverride` 计算。

---

## 6. 可见性、权限与替换

### 6.1 三层过滤（同时生效，只能收窄）

```
1. 进程目录 + 进程级模块启用 + 进程级 tools.disabled + 删除 PrimaryTools/SubAgentTools 硬编码
2. 会话级 Scenario 覆盖（sessionScenario.modules ∩ 进程级 enabled − session.tools.disabled）
3. AgentDefinition.AllowedTools / DeniedTools / AllowedMcpServers
4. PermissionRules + IPermissionChannel（Allow / Deny / Ask）
```

不设 `AllowedModules`（与层 1、层 2、层 3 重复）。

**可见 = 可请求：**

- 未启用模块、用户禁用、会话场景排除、Agent Deny、默认不可用的工具：**不得**进入提示词 schema。
- Ask 可以进入 schema；执行仍走权限通道。
- schema 中的每个工具必须能进入执行漏斗（随后可能 Ask 或按路径 Deny）。禁止「广告了却默认不可请求」。

Agent 不能启用进程里没有的工具。会话级 Scenario 只能收窄进程级已启用的，不能新增。用户启用了 `filesystem` 而会话场景排除它 → 该会话看不到 FS 工具，其它会话不受影响。

**删除 `ToolManager` 的 `PrimaryTools` / `SubAgentTools` / `SubAgentExcludedTools` 硬编码。** SubAgent 的「禁止 task」由 Agent 定义的 `DeniedTools` 表达（内置 Agent 数据里写死），不是由 `ToolManager` 按 mode 写死。`AgentMode` 只用于「是否允许被 Task 调用」这类元数据，不再决定工具可见性。

**框架兜底防递归：** `AgentRegistry` 注册时校验 `Mode == SubAgent` 且 `DeniedTools` 不含 `task` → **自动注入** `task` 到 `DeniedTools` 并告警。内置 explore/general 显式设 `DeniedTools = ["task"]`。`plan` Agent 改用 `AllowedTools` 白名单收窄 schema（而非 PermissionRules Allow），Deny 规则只做资源级。`build` Agent 不设 `AllowedTools`/`DeniedTools`（全开）。`ToolManager.GetToolSchemasForModeAsync` 删三个 HashSet，改为 `GetToolSchemasForAgentAsync(agent)` 走层 2。

### 6.2 替换与并列

| 场景 | 规则 |
|------|------|
| 两个 `filesystem` 或两个 `io.local` | 必须 Replace，否则拒启 |
| `llm.openai` 与 `llm.anthropic` | 并列 |
| Native / ACP 执行 | 一个门面，内部并列，不是双注册 `IAgentExecutor` |
| 配置写目录外 id | 告警、忽略 |
| 配置不能单独完成「换成沙箱包」 | 包必须已被宿主引用；`seams.executionWorld` 只在已登记世界中选择 |

**`seams.permissionChannel` 的值是模块 id**（或模块提供的命名绑定）。Core 在结算时按 id 找已登记模块，由该模块在 `ConfigureServices` 里注册 `IPermissionChannel`。禁止 Core 里写死 `blazor`/`deny-all` 的 switch。

### 6.3 启动失败 vs 运行期降级

| 失败 | 行为 |
|------|------|
| 重复 id、启用集硬依赖缺失、seam 未绑定却有消费方 Activate | 拒绝启动 |
| MCP 服务器挂了、Skill 目录不存在 | 进程继续；模块 unhealthy；其工具不出现 |
| 未启用任何 `llm.*` | 进程可启动；真正聊天失败并说明没有提供方 |
| 模型指向未启用的 Provider | 该次请求失败 |
| `Deactivate` | 必须对称卸工具、停连接、撤 Handler，禁止工具残留 |

权限通道、规则引擎留在 Core。`IWorkspaceWhitelist` 留在 Core，由 FileSystem 激活后写入。工具不得在内部另搞放行。

### 6.4 可观察

`IModuleCatalog` 只读：`available / enabled / healthy`、贡献工具、失败原因。宿主与 CLI 只读此对象，不猜测装配结果。

---

## 7. 一轮对话

```
AddSeeingCore + Host Shape + 能力包引用
        → 进程级结算 → Activate
用户消息（携带 session.Scenario）→ Hosting 队列 / ChatOrchestrator
        → 会话级结算（sessionScenario ∩ 进程级 enabled）
        → IAgentExecutor 门面
            1. 解析 Agent
            2. IToolManager 当前可请求工具（层 1∩层 2∩层 3）
            3. PromptBuilder：上述 schema + 已激活分节
            4. ILlmService → 已启用工厂
            5. 工具调用 → Hook → 权限（层 4）→ 装饰器 → 模块实现（经 ExecutionWorld）
            6. 事件回 Session / UI
```

Core 不 `new` 能力工具，不直连 OpenAI/Anthropic。停用模块后，进行中的该模块调用走取消/失败，不得再写父会话。斜杠命令依赖的模块未启用 → 发现不到或明确报「模块未启用」。

---

## 7.5 WebUI 场景化装配

WebUI 是 **Web Host Shape** 的壳：布局、Circuit、`BlazorPermissionChannel`、会话窗。它**不**按场景拆成多个前端应用，而是和后台同一套「模块贡献 + Scenario 过滤」。

### 7.5.1 UI 贡献模型

能力模块可选实现 `IUiContribution`（Abstractions，仅 Web Host Shape 消费）。Headless / Gateway 不加载 UI 贡献；同一模块在 Web 上贡献页面，在 Gateway 上零 UI。

| 贡献类型 | 内容 | 现有落点 |
|----------|------|----------|
| `Nav` | 侧栏项（路由 + 图标 + 标题 + `requires` 模块集 + 可选 `scenarios`） | 替换 `AppSidebar` 硬编码菜单 |
| `SettingsCard` | 设置页卡片（路由 + 渲染片段 + `requires`） | 替换 `Settings.razor` / 各 `*Page` 硬挂载 |
| `Slot` | 命名插槽贡献（`session_prompt` / `message_toolbar` / 自定义） | 复用现有 `SlotHost` + toolbar 插槽 |
| `MessageRenderer` | 消息类型渲染器（已有 `MessageRenderPipeline`） | 复用现有管道 |

`requires` 是模块 id 列表；`scenarios` 省略 = 凡模块启用即显示，列出 = 仅这些场景显示。可见性由 `IModuleCatalog` + 进程级 Scenario 结算结果决定，**页面禁止自己猜「有没有 Memory」**。

### 7.5.2 两级可见性（与后台对齐）

| 层 | 范围 | 决定 |
|----|------|------|
| 进程级 Scenario | 侧栏 / 设置 / 管理页可见集 | 切进程场景 = 整站导航变 |
| 会话级 Scenario | 会话壳差异（默认 Agent、场景徽标、可选工具栏插槽、提示词分节） | 同进程多会话可并行不同场景；侧栏稳定不跳 |

侧栏只跟进程级。会话级 Scenario **只改会话壳**：`SessionToolbar` 显示当前场景徽标 + 切换器，默认 Agent 取 `sessionScenario.defaultAgent`，会话内工具栏/输入区插槽按会话场景过滤。多会话并行时侧栏不会随焦点会话跳变。

### 7.5.3 路由与未启用页

- **Blazor 路由机制改造：** `App.razor` 的默认 `<Router>` 改为自定义 `ModuleRouter`，路由表来源 = `IUiContributionRegistry.Routes`（运行期可变），不再靠 `AppAssembly` 反射扫 `@page`。现有 `*Page.razor` 删除 `@page` 指令，改为暴露 `RenderFragment`/组件类型由模块 `IUiContribution.Nav` 携带。未命中或命中但模块未启用 → `ModuleRouter` 渲染 `NotEnabledFragment`（友好「当前场景未启用 X」+ 切换场景入口）。路由变更触发 `ModuleRouter` 刷新（`StateHasChanged`）。
- 贡献的路由由模块登记，`AppSidebar` 从 `IUiContributionRegistry` 渲染，删除硬编码 `MenuItem`。
- 直链访问未启用模块的页面 → 友好「当前场景未启用 X」提示 + 切换场景入口，**不**渲染空白或报错。
- 会话级场景排除某模块 → 该会话内该模块的插槽/渲染器不出现，但进程级页面（如 `/cron-jobs`）仍可访问（属进程级可见性）。

### 7.5.4 现有插槽保留

`SlotHost`（`session_prompt`、`message_toolbar`）和 `SessionToolbar` 的 `LeftActions`/`RightActions` 插槽保留。升级为：插槽贡献由 `IUiContribution` 登记，可见性读 `IModuleCatalog` + 会话 Scenario。`AppSidebar` 与 `Settings.razor` 的硬编码改为从注册表渲染。

---

## 8. 测试不变量

Core 测试禁止引用 Tools.* / Mcp / Llm.* / IO.Local。用测试替身（内存 FS、假 LLM、假 Subprocess）覆盖循环与结算。删除「`AddSeeingAgent` 后必有 ReadTool」类测试。

| 不变量 | 断言 |
|--------|------|
| Core 零工具 | 只 `AddSeeingCore()` 时 `IToolManager` 为空，schema 为空 |
| 目录外无效 | `enabled: ["not-in-catalog"]` 不启用、不加载新类型 |
| 默认启用 | Minimal/Web 不写 enabled → 用 DefaultEnabled；无 Profile 不写 → 空 |
| 可见即可请求 | 任意步进：schema 工具 id == 层 1∩层 2 的集合；Ask 可在其中 |
| 执行世界成对 | 解析到的 `IFileSystem` 与 `ISubprocess` 属于同一 `IExecutionWorld` |
| 工具不触 OS | FileSystem/Shell/Git 测试用假世界即可完成，程序集无对 `File.WriteAllText` / `Process.Start` 的生产路径（测试替身除外） |
| 停用对称 | Deactivate 后工具与分节消失；再 Activate 恢复 |
| 硬依赖 | A 依赖 B 只启用 A → 拒启 |
| Replace | 同 id 未替换 → 拒启；替换后旧类型不可解析 |
| LLM 并列 | 关 openai 不影响已启用的 anthropic |
| 门面执行器 | 容器中只有一个 `IAgentExecutor`（router）；`IAgentExecutorImplementation` 可多个，按 Runtime 分派；无 Replace |
| 运行期降级 | MCP 连接失败 → 宿主仍启动，模块 unhealthy |
| 扩展包入口 | Scheduler/Memory/ACP 只经 `ISeeingModule` / `AddXxx`，不依赖全开核心 |
| Options 拆节 | Core 程序集不包含 Gateway/Acp Options 类型 |
| 配置节注册 | `UnifiedConfigManager` 的 `BuildSectionRegistry` 只含脊柱节；能力包节由模块注册；`LoadAsync` 推迟到 `InitializeSeeingAsync` |
| 提示词解耦 | `PromptBuilder` 不引用 `SkillManager` 具体类；技能列表由 `IPromptSectionContributor` 贡献；section 注入走锚点标题，不依赖 Agent prompt 写占位符 |
| 子执行解耦 | `TaskTool` 与 `TaskStatusTool` 只依赖 `IExecutionSubmitter` + `IExecutionStatusProvider` + `IExecutionEventPublisher`（均 Abstractions），不解析 `ExecutionJobService` 具体类，不用 `context.Services` 服务定位器 |
| 模型可见即可重建 | 新增 `SchemaSnapshotEvent : IMessageEvent`（`ToolIds` + `SectionIds`）；`ExecutionJobService` 在调 executor 前发事件；`ChatEventTracker` 写入 `SessionMessage.Metadata["schema_snapshot"]`；`SessionForker` 深拷贝。**同模块集下**重放可重建；跨进程模块集变化时取交集并告警，不拒启 |
| 会话级场景收窄 | 会话 A 用 `code`、会话 B 用 `work`，同进程同时运行；A 看不到 memory 工具，B 看不到 git 工具；会话级只收窄进程级已启用，不能新增 |
| 场景切换 | 切换 `session.Scenario` 后下一次执行 schema 立即变化；进程级模块 Activate 状态不变 |
| Scenario 纯数据 | 内置 Scenario 是数据预设，不是包；用户 `seeing.json` 可定义新 scenario key；`scenario` 字段可热重载 |
| SubAgent 防递归 | `AgentRegistry` 注册时 `Mode==SubAgent` 且 `DeniedTools` 不含 `task` → 自动注入并告警；自定义 SubAgent 漏写不会导致 task 可见 |
| HostedService 可停 | 每个模块 HostedService `StartAsync` 第一行查 `IModuleCatalog.IsEnabled`，未启用 return；长循环双条件 + 哨兵唤醒；连接类资源由模块自管不进 DI singleton |
| LLM 类型可扩展 | `ProviderConfig.Type` 为 string；第三方工厂经 `SupportedTypes` 声明，不改 Abstractions 枚举 |
| UI 贡献跟模块 | 侧栏/设置/路由由 `IUiContributionRegistry` 渲染；`AppSidebar` 无硬编码 `MenuItem`；未启用模块页面友好提示 |
| 侧栏只跟进程级 | 切进程场景侧栏变；切会话场景侧栏不变；多会话并行不同场景侧栏稳定 |
| 会话壳跟会话级 | 会话 Scenario 切换后 `SessionToolbar` 徽标/默认 Agent/插槽立即变化；进程级模块 Activate 状态不变 |
| UI 真相源唯一 | 页面可见性只读 `IModuleCatalog` + Scenario，不自行检查「有没有 X 模块」 |
| schema 唯一计算 | `ExecutionJobService` 是 schema 唯一计算点；executor 读 `context.ToolSchemas`，不自算；`SchemaSnapshotEvent` 与 `ChatRequest.Tools` 同源 |
| 存量 providers 迁移 | 读旧 `providers.json`（PascalCase `Type`）归一化为小写，不报错；首次保存写新格式 |
| 存量会话兼容 | `SessionData.Scenario` 缺失为 null，回退进程级，不报错不强制写 |
| 在途轮次边界 | 进程级热重载 Deactivate 推迟到在途结束（默认）；会话级切换只对下一次 Submit 生效 |
| Scenario 纯字符串 | Core 内置 Scenario 是字符串 id 数组，不引用能力包类型；未引用 id 结算时告警忽略 |

---

## 9. 实施顺序（搬家，不是改名）

1. Abstractions：补 `IFileSystem` / `ISubprocess` / `IExecutionWorld`、`IToolManager` schema、`IPromptSectionContributor`、`IExecutionSubmitter` + `IExecutionStatusProvider`（搬到 Abstractions）、`IAgentExecutorImplementation`、`ISeeingModule`、`IConfigSectionRegistry`、`SchemaSnapshotEvent`、`IUiContribution` / `IUiContributionRegistry`。`ProviderConfig.Type` 改 string。
2. 抽出 `Seeing.IO.Local`；改 Read/Bash/Git 等走接口（此时可仍在主库目录，但依赖已反向）。BashTool 三层归属按 4.1 拆分。
3. 抽出 Tools.* / Mcp / Llm.* / Skills / Agents.BuiltIn；删除 Core 内工厂 switch 与 MCP 包引用。`ProviderManager` 改聚合 `IEnumerable<ILlmClientFactory>`。
4. 抽出 Hosting（Execution + Task + Orchestrator）；切断 App→ACP。`AgentExecutorRouter` 替换 `ReplaceExecutionRouter`。
5. `SeeingAgentOptions` 拆节；删除 `IExtension`、PluginLoader、`AddSeeingAgent`、`PrimaryTools` 硬编码；`UnifiedConfigManager` 改为消费 `IConfigSectionRegistry`，`LoadAsync` 推迟到 `InitializeSeeingAsync`。`AgentManager` 改两阶段注册。
6. 主库改名为 Core；落地 Host Shape（Web/Headless/Gateway/Embed）+ 内置 Scenario（minimal/code/work/research/full）；改 sample 启动。`InitializeSeeingAsync` 在 `Host.StartAsync` 之前显式调用。
7. 各模块 HostedService 补 `IsEnabled` 门控；连接类资源改模块自管。`SessionData` 加 `Scenario` 字段；`ExecutionJobService` 加会话级结算。
8. WebUI：`IUiContributionRegistry` + `AppSidebar`/`Settings` 改注册表渲染；`SessionToolbar` 加场景徽标 + 切换器；未启用页友好提示。
9. 按第 8 节补不变量测试。

每一步以「引用方向 + 不变量测试」为完成定义，不以改名为完成定义。

---

## 10. 与 deepseek-harness 的取舍

借：seam 三角色、fs+subprocess 成对换世界、提示词跟能力走、Hosting 当组合、可见漏斗唯一。

不借：Cordis 与可逆 effect、行级 patch、isolate / per-session 子树、过细拆包、Session 事件溯源。
