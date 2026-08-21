# Seeing.Agent 项目知识库

**生成时间:** 2026-07-31
**Branch:** master
**目标框架:** .NET 10.0
**语言:** C#

---

## 概述

完整的 AI Agent 框架，支持 Skill/Tool/Hook/Permission/MCP 集成。主库为 NuGet 包 (`Seeing.Agent`)，提供 Agent 编排、工具发现、权限控制等核心能力。**独立会话管理包** (`Seeing.Session`) 可单独使用。

## 项目结构

```
Seeing.Agent/
├── Seeing.Agent.slnx              # 解决方案（VS 2022 17.13+ 新格式）
├── global.json                    # SDK 版本锁定：10.0.102
├── Directory.Build.props          # 启用中央包管理 (CPM)
├── Directory.Packages.props       # 包版本集中定义
│
├── src/
│   ├── Seeing.Agent/              # 主 NuGet 库（**260** 个 C# 文件）
│   ├── Seeing.Agent.Abstractions/ # 零实现契约包（接口/DTO/注解/常量/事件声明，仅引用原语层 Seeing.Session）
│   ├── Seeing.Agent.Acp/          # ACP 集成
│   ├── Seeing.Agent.App/          # 应用层 / Chat 编排器
│   ├── Seeing.Agent.Gateway/      # Gateway 集成
│   ├── Seeing.Agent.Memory/       # 记忆系统
│   ├── Seeing.Agent.Scheduler/    # 调度器
│   ├── Seeing.Agent.TokenBudget/  # Token 预算管理
│   ├── Seeing.Gateway/            # Gateway 核心协议
│   ├── Seeing.Gateway.Client/     # Gateway 客户端
│   ├── Seeing.Gateway.QQ/         # QQ Gateway 通道
│   ├── Seeing.Gateway.WeCom/      # 企业微信 Gateway 通道
│   ├── Seeing.Session/            # 独立会话管理包（**46** 个文件）
│   └── Seeing.TokenEstimation/    # Token 估算
│
├── tests/
│   ├── Seeing.Agent.Tests/        # 单元测试（**65** 个文件）
│   ├── Seeing.Agent.Acp.Tests/    # ACP 测试
│   ├── Seeing.Agent.Memory.Tests/ # Memory 测试
│   ├── Seeing.Agent.Scheduler.Tests/ # Scheduler 测试
│   ├── Seeing.Agent.WebUI.Tests/  # WebUI 测试
│   ├── Seeing.Gateway.Tests/      # Gateway 测试
│   ├── Seeing.Gateway.Client.Tests/ # Gateway Client 测试
│   ├── Seeing.Gateway.QQ.Tests/   # QQ 通道测试
│   ├── Seeing.Gateway.WeCom.Tests/ # WeCom 通道测试
│   ├── Seeing.Session.Tests/      # Session 测试
│   └── Seeing.TokenEstimation.Tests/ # Token 估算测试
│
├── samples/
│   ├── Seeing.Agent.WebUI/        # Blazor Web 界面示例
│   ├── Seeing.Agent.Cli/          # 命令行管理工具
│   ├── Seeing.Gateway.Server/     # Gateway 服务端
│   ├── Seeing.Gateway.ChannelHost/ # 通道宿主
│   ├── Seeing.Gateway.Console.Demo/ # 控制台演示
│   └── Seeing.Gateway.WeCom.Demo/ # 企业微信演示
│
├── docs/superpowers/
│   ├── plans/                     # 实施计划
│   └── specs/                     # 设计规格
│
├── CommandLineUtils/              # [外部] McMaster 命令行库
└── command-line-api/              # [外部] dotnet 命令行 API
```

## 依赖方向规范（三层）

| 层 | 项目 | 说明 |
|----|------|------|
| 原语层 | `Seeing.Session`、`Seeing.TokenEstimation` | 零依赖或仅 BCL |
| 主库层 | `Seeing.Agent` | 依赖原语层 + Abstractions |
| 扩展层 | `Seeing.Agent.*`、`Seeing.Gateway*`、`plugs/providers/*` | 只引用 Abstractions 接口与契约 + 原语层 |

- **依赖只能向下**：禁止反向引用（扩展层 → 主库层 → Abstractions → 原语层）
- **Abstractions 零实现纪律**：只放接口/DTO/注解/常量/事件声明，禁止实现逻辑；唯一豁免是允许引用原语层 `Seeing.Session`（`AgentDefinition.BudgetConfig` 依赖 `TokenBudgetConfig`）
- **扩展包禁止引用主库具体类**，只能引用 Abstractions 接口与契约
- **协议层独立**：`Seeing.Gateway`（核心协议包）禁止引用主库，仅依赖 Abstractions；`Seeing.Agent.Gateway`（集成包）允许引用主库

## WHERE TO LOOK

| 任务 | 位置 | 说明 |
|------|------|------|
| 新增 Agent 实现 | `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | AgentDefinition 纯数据定义（无基类继承；自定义行为通过 Hook/工具/子代理组合表达） |
| Agent 执行入口 | `src/Seeing.Agent.Abstractions/Agents/IAgentExecutor.cs` | 定义 + 消息入参 → 流式事件；默认实现 `NativeAgentExecutor`，ACP 走 `AcpAgentExecutor` |
| Agent 注册 / 运行时 | `src/Seeing.Agent.Abstractions/Agents/IAgentRegistry.cs`、`IAgentRuntimeManager.cs` | 注册/查询/权限筛选 + 默认 Agent / 模型绑定（拆分自 IAgentManager） |
| Todo 存取 | `src/Seeing.Agent.Abstractions/Todo/ITodoStore.cs` + `src/Seeing.Agent/Todo/SessionContextTodoStore.cs` | 端口-适配器，替代 Session 魔法键直写 |
| 扩展插件生命周期 | `src/Seeing.Agent.Abstractions/Extensions/IExtensionManager.cs` | 插件加载/激活/停用 |
| 契约类型总览 | `src/Seeing.Agent.Abstractions/` | 接口/DTO/注解/常量/事件声明（Events/Hooks/Tools/Agents/Extensions/Llm/Mcp/Permissions/Skills/Commands/Configuration/Todo/Components） |
| 新增 Tool 工具 | `src/Seeing.Agent/Tools/Attributes/ToolAttributes.cs` | 使用 `[Tool]` 注解 |
| 子 Agent / Task | `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` | Session-first：`task`/`task_status`；Child=`SessionKind.SubAgent` |
| 扩展生命周期钩子 | `src/Seeing.Agent/Core/Hooks/HookManager.cs` | 实现 `IHookHandler`，30+ 钩子点 |
| 配置权限规则 | `src/Seeing.Agent/Core/Permission/PermissionService.cs` | PermissionRuleEntry + PermissionService |
| 连接 MCP Server | `src/Seeing.Agent/MCP/McpClientManager.cs` | `ConnectAsync()`，支持 stdio/HTTP/SSE |
| DI 注册入口 | `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | `AddSeeingAgent()` |
| 会话管理 | `src/Seeing.Session/Management/SessionManager.cs` | 独立包，生命周期管理 |
| 扩展插件开发 | `src/Seeing.Agent/Extensions/ExtensionLoader.cs` | 实现 `IExtension`/`IToolExtension` 等拆分接口（Abstractions.Extensions） |
| 循环检测防护 | `src/Seeing.Agent/Core/Detection/LoopDetector.cs` | 防止 LLM 无限循环 |
| 文件系统安全 | `src/Seeing.Agent/Tools/BuiltIn/FileSystemHelper.cs` | 路径白名单、输出限制 |
| 配置深度合并 | `src/Seeing.Agent/Core/Configuration/MergeDeep.cs` | 递归合并算法 |
| 工具装饰器链 | `src/Seeing.Agent/Decorators/ToolDecoratorRegistry.cs` | 超时→重试→缓存（由 ToolManager 在 RegisterTool() 时通过 `IToolDecoratorRegistry.Apply()` 生效） |
| 内置 Agent | `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | build/plan/explore/general/title/summary |
| ACP 集成 | `src/Seeing.Agent.Acp/` | Passthrough 透传 + acp 工具委派，`IAgentExecutor` 执行器 |

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
- **链顺序**: TimeoutToolDecorator（最外层）→ RetryToolDecorator（中间层）→ CachedToolDecorator（最内层，可选）
- **默认**: 30 秒超时 → 3 次重试（1s 间隔指数退避）→ 5 分钟缓存
- **重试异常**: `TimeoutException`, `HttpRequestException`, `TaskCanceledException`, `IOException`

## ANTI-PATTERNS

| 禁止 | 原因 |
|------|------|
| **混用路径分隔符** | `\\` 和 `/` 混用破坏跨平台 |
| **WebUI 项目禁用 CPM** | 破坏包版本一致性 |
| **静默吞异常** | `Activator.CreateInstance` 失败需记录 |
| **工具 ID 冲突静默覆盖** | 最后注册 wins，无警告 |
| **Hook 点字符串硬编码** | 使用 `HookPoints.*` 常量 |
| **Context 类添加业务逻辑** | Context 应为纯数据容器 |
| **权限通道未配置** | 默认拒绝所有，需显式配置 |
| **同步包装阻塞 async** | `.GetAwaiter().GetResult()` 死锁风险 |

## 已知问题

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

## 命令

```bash
# 构建
dotnet build Seeing.Agent.slnx

# 测试
# 注意：测试项目启用了 UseMicrosoftTestingPlatformRunner（MTP），但 MTP 在此环境下
# 无法发现测试（"Zero tests ran"）。一律改用 VSTest 直接运行已构建的程序集：
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
# 指定测试类（VSTest 过滤语法）：
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~AgentModeFilterTests"
dotnet test tests/Seeing.Session.Tests

# 打包 NuGet
dotnet pack src/Seeing.Agent -c Release
dotnet pack src/Seeing.Session -c Release

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
- **装饰器链**: 超时（最外层）→ 重试 → 缓存（最内层）
- **循环检测**: SHA256 参数哈希，连续 3 次警告，5 次终止
- **解决方案格式**: `.slnx`（VS 2022 17.13+ 新格式）
- **测试命名**: `{方法}_{场景}_Should{预期结果}` 或 AAA 注释分区
- **内置 Agent**: build(默认)/plan(计划)/explore(探索)/general(通用)/title(标题)/summary(摘要)
