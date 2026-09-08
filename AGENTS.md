# Seeing.Agent 项目知识库

**生成时间:** 2026-07-31（架构文档增量深化：2026-09-08）
**Branch:** feature/modular-architecture（以当前分支为准）
**目标框架:** .NET 10.0
**语言:** C#

---

## 架构与开发规范（必读）

正式模块化文档在 **`docs/architecture/`**：

- [文档索引](docs/architecture/README.md)
- [系统架构](docs/architecture/01-overview.md)
- [模块地图](docs/architecture/02-modules.md)
- [配置·结算·热重载](docs/architecture/03-configuration-lifecycle.md)
- [开发规范与反模式](docs/architecture/04-development-standards.md)
- [扩展指南](docs/architecture/05-extension-guide.md)
- [合规审查 / 现状债](docs/architecture/06-compliance-audit.md)

设计规格：`docs/superpowers/specs/2026-09-08-modular-architecture-design.md`。

**硬规则摘要：** Core 瘦脊柱（零工具）；能力经 `AddSeeingModule`；`InitializeSeeingAsync` 在 Host.Start 前；schema 只在 ExecutionJobService 计算；能力包目标只依赖 Abstractions。

## 概述

完整的 AI Agent 框架，支持 Skill/Tool/Hook/Permission/MCP 集成。主库为 NuGet 包 (`Seeing.Agent.Core`)，提供脊柱核心（配置、执行、权限、钩子）；能力包通过 `AddSeeingModule*` 按需组合。**独立会话管理包** (`Seeing.Session`) 可单独使用。

## 项目结构

```
Seeing.Agent/
├── Seeing.Agent.slnx
├── docs/architecture/             # 架构与开发规范（日常必读）
├── docs/superpowers/specs/        # 模块化设计规格
├── src/
│   ├── primitives/                # Session, TokenEstimation, ConfigSchema
│   ├── abstractions/              # Seeing.Agent.Abstractions
│   ├── spine/                     # Seeing.Agent.Core
│   ├── hosting/                   # Hosting + Web/Headless/Embed/Gateway
│   ├── capabilities/              # Tools.*, Skills, Mcp, Llm.*, Agents, Scheduler, Memory, Acp, TokenBudget, IO.Local
│   └── gateway/                   # Seeing.Gateway*, Seeing.Agent.Gateway
├── tests/                         # 与 src 同名分类镜像（另含 apps/、plugs/）
├── samples/  Seeing.Agent.WebUI, Gateway.Server, Cli, …
└── plugs/providers/
```

> 程序集名未改；仅磁盘路径分类。WHERE TO LOOK 中路径请按上表映射（如 Core → `src/spine/Seeing.Agent.Core/`）。

## 依赖方向规范（四层）

| 层 | 项目 | 说明 |
|----|------|------|
| 原语层 | `Seeing.Session`、`Seeing.TokenEstimation`、`Seeing.ConfigSchema` | 零依赖或仅 BCL |
| 契约层 | `Seeing.Agent.Abstractions` | 接口/DTO/注解/常量；允许引用原语层 |
| 脊柱层 | `Seeing.Agent.Core`、`Seeing.Agent.Hosting*` | Core 瘦脊柱；Hosting 编排；Host Shape 不拉可选能力 |
| 能力/扩展层 | `Seeing.Agent.Tools.*`、`Skills`/`Mcp`/`Llm.*`、`Scheduler`/`Memory`/`Acp`、`Seeing.Gateway*` | **只**引用 Abstractions + 原语（+ Tools.Support）；复扫见 `docs/architecture/06-compliance-audit.md` |

- **依赖只能向下**：禁止能力包反向依赖 Core 具体类型
- **Abstractions 零实现纪律**：只放接口/DTO/注解/常量/事件声明
- **协议层独立**：`Seeing.Gateway` 仅依赖 Abstractions；`Seeing.Agent.Gateway` 为集成包
- **日常必遵细节**（热重载 / schema 单点 / Activate 挂载）：以 `docs/architecture/03–05` 为准

## WHERE TO LOOK

| 任务 | 位置 | 说明 |
|------|------|------|
| 架构 / 规范 / 扩展 | `docs/architecture/` | 日常开发必读；合规债见 06 |
| 新增内置 Agent | `src/capabilities/Seeing.Agent.Agents.BuiltIn/` | AgentDefinition 纯数据；经模块 Activate |
| Agent 执行入口 | `src/abstractions/Seeing.Agent.Abstractions/Agents/IAgentExecutor.cs` | Native / ACP 分流 |
| 编排 / schema 单点 | `src/hosting/Seeing.Agent.Hosting/`（`ExecutionJobService`） | 工具 schema 只在此计算 |
| Todo 存取 | `src/abstractions/.../Todo/` + Hosting 适配器 | 端口-适配器 |
| 模块生命周期 | `src/abstractions/.../Modules/` + `src/spine/.../SettlementEngine` | 结算 / Activate / Deactivate |
| 契约类型总览 | `src/abstractions/Seeing.Agent.Abstractions/` | Events/Hooks/Tools/Agents/… |
| 新增 Tool | `src/capabilities/Seeing.Agent.Tools.*` + `[Tool]` | 经 `AddSeeingModule`；Activate 挂载 |
| 子 Agent / Task | `src/hosting/Seeing.Agent.Hosting/` Task 工具 | Session-first |
| 多流 / Task 卡片 UI | `samples/Seeing.Agent.WebUI/Services/` | SessionEventStreamRouter、TaskCardAggregator |
| Hook | `src/spine/Seeing.Agent.Core/` HookManager | `HookPoints.*` 常量 |
| 权限 | `src/spine/Seeing.Agent.Core/` PermissionService | Allow/Deny/Ask |
| MCP | `src/capabilities/Seeing.Agent.Mcp/` | 能力模块，非 Core |
| DI 注册入口 | Core `AddSeeingCore` + 各包 `AddSeeingModule*` | `InitializeSeeingAsync` 在 Host.Start 前 |
| 会话管理 | `src/primitives/Seeing.Session/` | 独立包 |
| ACP | `src/capabilities/Seeing.Agent.Acp/` | 能力模块 |

## CONVENTIONS（仅非标准）

### 命名约定
- 接口前缀 `I`，抽象类后缀 `Base`，结果类后缀 `Result`
- Hook 点命名：`{领域}.{事件}` 格式（如 `tool.before_execute`）
- 异步方法统一 `Async` 后缀
- 私有字段：`_camelCase`（_ 前缀）
- 私有静态字段：`s_camelCase`（s_ 前缀）

### 接口后缀即职责
| 后缀 | 职责 |
|------|------|
| Store | 纯数据存取（无业务规则、无生命周期） |
| Registry | 集合管理（注册/查询/注销条目，不含执行与生命周期） |
| Manager | 生命周期+编排（可组合 Store/Registry/Service） |
| Service | 业务能力入口（请求-响应式操作） |
| Provider | 能力适配器（可插拔实现） |
| Executor | 执行引擎（定义+上下文 → 事件/结果流） |
| Channel | 通信通道（请求/审批通道） |
| Loader | 加载器（从源加载组件） |
| Sink | 单向出口（执行器向工具提供的只写能力出口，不可反向调用） |

层级：`Store < Registry < Manager`；接口名 = 实现类名去 I。

### DI 生命周期
| 服务 | 生命周期 |
|------|----------|
| ToolManager, HookManager, PermissionService, SkillManager, McpClientManager, IToolDecoratorRegistry | Singleton |
| SessionManager, AgentExecutor | Singleton |
| Middleware (Logging, Permission, Retry) | Transient |

### 注解发现
```csharp
[Tool("获取天气信息", Name = "可选自定义ID")]
public static async Task<string> GetWeather(
    [ToolParam("城市名")] string city,
    [Required] DateTime date) { }
```
**禁止**：async void、out/ref 参数、泛型方法、重载工具名

### 配置文件命名
- 选项类后缀 `Options`（如 `SeeingAgentOptions`）
- 配置节名称 `SeeingAgent`
- **Agent/Gateway/ACP 配置仅写在** `.seeing/seeing.json`（不使用 `appsettings.json` 的 `SeeingAgent` 节）
- 用户级配置：`~/.seeing/seeing.json`
- 项目级配置：`.seeing/seeing.json`
- **默认 Agent 统一使用** `DefaultAgent`；ACP / Native 由 Agent 的 `Runtime` 自动分流

### 内部 Helper 类
| 文件 | 用途 |
|------|------|
| `FileSystemHelper.cs` | 文件操作封装、MIME 类型、截断 |
| `OutputTruncator.cs` | 输出限制（行数/字节/行长度） |
| `BinaryFileDetector.cs` | 二进制检测（扩展名+内容采样） |
| `MergeDeep.cs` | 配置深度合并算法 |
| `TokenCounterHelper.cs` | Token 计数（Session.Compression 命名空间） |

### 文件系统限制
| 限制项 | 默认值 |
|-------|-------|
| 读取行数 | 2000 行 |
| 单行长度 | 2000 字符 |
| 输出字节 | 50KB |
| Grep 匹配 | 100 条 |
| Glob 文件 | 100 个 |

### 工具装饰器链
- **注册**: `IToolDecoratorRegistry` 在 DI 中注册为 Singleton，`ToolManager` 在 `RegisterTool()` 时自动 `Apply()` 装饰器
- **链顺序**: RetryToolDecorator（最外层）→ ToolTimeoutDecorator → CachedToolDecorator（最内层，可选）
- **默认**: 3 次重试（1s 间隔指数退避）→ 超时（能力感知，兜底全局 `ToolExecutionTimeout`）→ 缓存默认关闭（内置工具均不声明缓存，因读取磁盘/仓库即时状态易产生脏数据）
- **重试异常**: `TimeoutException`, `HttpRequestException`, `TaskCanceledException`, `IOException`
- **超时职责**: `ToolTimeoutDecorator` 在工具执行漏斗内施加超时——读取工具能力 `timeout.skip=true`（豁免）或 `timeout.budget`（自身上限），未声明时回落到 `SeeingAgentOptions.ToolExecutionTimeout`（IOptionsMonitor 实时读取，支持热重载；默认 null 关闭）。超时返回 `Failure` + `Title="执行超时"` + `Metadata["timeout"]=true`，由上层统一渲染"执行超时"。

### 工具能力元数据（Tool Capabilities）
- **机制**: 工具通过 `IToolCapabilities.Capabilities`（`IReadOnlyDictionary<string,string>`）声明静态能力元数据。`ITool` 继承该接口，默认从类级 `[ToolCapability(key,value)]` 属性读取；`ToolBase` 子类可覆盖属性；`ToolDecorator` 透传内层能力。
- **预定义键**（`ToolCapabilityKeys`）：`timeout.skip`（豁免全局兜底超时）、`timeout.budget`（按工具超时上限，毫秒）、`cache.enabled`（允许缓存，默认 false）、`cache.ttl`（缓存过期毫秒）、`cache.scope`（`session`/`global`，键含 SessionId 与否）。
- **消费端**: `ToolTimeoutDecorator` 读 `timeout.skip`/`timeout.budget`；`CachedToolDecorator` 读 `cache.*`。
- **扩展**: 新能力只需新增预定义键 + 消费端，现有工具零改动；新工具声明能力只需一个属性或 Attribute。

### 工具取消契约
- 工具须观察 `context.CancellationToken`；取消后不得再写父会话事件。
- `BackgroundTaskManager.WaitAsync` 接受 `CancellationToken`，取消时抛 `OperationCanceledException`（而非等到超时）。
- 取消路径下不响应取消的工具可能被 `ToolDrainTimeout`（10s）兜底跳过终态事件，其任务/进程泄漏为**已知边界**。

## ANTI-PATTERNS

完整清单与评审检查表见 [`docs/architecture/04-development-standards.md`](docs/architecture/04-development-standards.md)。

| 禁止 | 原因 |
|------|------|
| **在 Core 加工具 / 能力包引用** | 破坏瘦脊柱；用 `AddSeeingModule` |
| **使用已删除的 `AddSeeingAgent`** | 改用 `AddSeeingCore(registry)` + 显式模块 |
| **能力包引用 Core 具体类型** | 只依赖 Abstractions（复扫见 `docs/architecture/06`） |
| **在 `ConfigureServices` 永久挂死工具且无法 Deactivate** | enabled 与 DI/schema 不一致；目标 Activate 挂载 |
| **schema 在 executor / 多处重复计算** | 只在 `ExecutionJobService` 算一次 |
| **SeeingAgent 配置写进 appsettings.json** | 只用 `~/.seeing` / `.seeing/seeing.json` |
| **混用路径分隔符** | `\\` 和 `/` 混用破坏跨平台 |
| **WebUI 项目禁用 CPM** | 破坏包版本一致性 |
| **静默吞异常** | `Activator.CreateInstance` 失败需记录 |
| **工具 ID 冲突静默覆盖** | 最后注册 wins，无警告 |
| **Hook 点字符串硬编码** | 使用 `HookPoints.*` 常量 |
| **Context 类添加业务逻辑** | Context 应为纯数据容器 |
| **权限通道未配置** | 默认拒绝所有，需显式配置 |
| **同步包装阻塞 async** | `.GetAwaiter().GetResult()` 死锁风险 |

## 已知问题 / 架构债

模块化合规债以 **[`docs/architecture/06-compliance-audit.md`](docs/architecture/06-compliance-audit.md)** 为准（P0：扩展包去 Core 引用、工具 Activate 挂载、Hosting→Skills 硬引用）。

以下为历史条目摘要（部分已关闭）：
| 优先级 | 问题 | 状态 |
|--------|------|------|
| **P0** | 装饰器链未注册 DI | 修复中（Phase 2） |
| **P0** | TimeoutToolContext 丢失字段 | 修复中（Phase 2） |
| **P1** | HookManager 缺少移除能力 | 待修复 |
| **P1** | ProviderConfig 字段未消费 | 修复中（Phase 5） |
| **P1** | ExecutionStateManager 缺 IDisposable | 修复中（Phase 3） |
| **P2** | SessionForker.CloneMessage 浅拷贝 | 修复中（Phase 3） |
| **P2** | ISession 旧体系未清理 | 修复中（Phase 3） |
| **P2** | CountTokens 4 处重复 | 修复中（Phase 3） |
| **P3** | HMAC 密钥不持久化 | 修复中（Phase 5） |
| **P1** | IAgent/AgentBase/IAgentManager 死代码体系未清理 | **已完成**（2026-08-18 解耦重构：IAgentExecutor 统一执行入口 + Registry 拆分） |
| **P1** | Todo 魔法键后门（TodoManager/TodoReadTool 孤儿） | **已完成**（ITodoStore 端口-适配器化，SessionContextTodoStore） |
| **P1** | Seeing.Gateway 反向依赖主库 | **已完成**（协议层独立，仅依赖 Abstractions） |
| **P1** | IExtension 巨型接口 + ConfigureServices 死契约 | **已完成**（按组件类型拆分 7 接口） |
| **P2** | 子代理 Task 卡片执行中无进度（旧设计无 UI 层聚合） | **已完成**（2026-08-27：SessionEventStreamRouter + TaskCardAggregator UI 层聚合，spec `2026-08-27-task-card-ui-aggregation-design.md`） |
| **P3** | 刷新瞬时流尾弱化（skipSet 丢 buffer 流尾 delta，下一事件才渲染） | 已知边界（待集成测试确认） |
| **P3** | 同会话排队 exec1+exec2 闪断（exec1 Complete 清态、exec2 Started 恢复） | 已知边界（本期容忍） |
| **P3** | 页面 Dispose 级联取消含子会话（刷新即取消） | **已完成**（2026-08-27：Dispose 移除 `CancelBySessionAsync`/标记取消，仅清理 UI 订阅；主动取消与程序关闭仍取消） |
| **P3** | `LoadChildrenFromStorageAsync` 未命中即全量 `ListAsync` 扫描 | 已知边界（建议加缓存 TTL） |

## 命令

```bash
# 构建
dotnet build Seeing.Agent.slnx

# 测试
# 注意：测试项目启用了 UseMicrosoftTestingPlatformRunner（MTP），但 MTP 在此环境下
# 无法发现测试（"Zero tests ran"）。一律改用 VSTest 直接运行已构建的程序集：
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
# 指定测试类（VSTest 过滤语法）：
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentModeFilterTests"
dotnet test tests/primitives/Seeing.Session.Tests

# 打包 NuGet
dotnet pack src/spine/Seeing.Agent.Core -c Release
dotnet pack src/primitives/Seeing.Session -c Release

# 运行示例
dotnet run --project samples/Seeing.Agent.WebUI
dotnet run --project samples/Seeing.Agent.Cli
```

## NOTES

- **测试框架**: xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12
- **SDK 版本**: 10.0.102，rollForward: minor
- **中央包管理**: 启用
- **外部子仓库**: `CommandLineUtils/`、`command-line-api/` 非本项目代码
- **日志规范**: 结构化日志 `{PropertyName}` 格式
- **装饰器链**: 重试（最外层）→ 缓存（最内层）；超时由工具自身 + AgentExecutor 全局兜底负责
- **循环检测**: SHA256 参数哈希，连续 3 次警告，5 次终止
- **解决方案格式**: `.slnx`（VS 2022 17.13+ 新格式）
- **测试命名**: `{方法}_{场景}_Should{预期结果}` 或 AAA 注释分区
- **内置 Agent**: build(默认)/plan(计划)/explore(探索)/general(通用)/title(标题)/summary(摘要)
