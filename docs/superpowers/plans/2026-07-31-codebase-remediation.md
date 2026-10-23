# Seeing.Agent 全面审查修复计划（修订版 v3）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复审查发现的全部 29 项问题，含 5 项 P0 问题（装饰器链无效 + TimeoutToolContext 丢失字段 + 文档严重过时 + 物理残留 + 25 处同步阻塞）

**Architecture:** 7 个阶段，按依赖顺序执行：Phase 1 快速清理 → Phase 2 装饰器链接入（核心架构重构） → Phase 3 Session 包修复 → Phase 4 Agent 核心修复 → Phase 5 基础设施优化 → Phase 6 测试补充 → Phase 7 同步阻塞消缺 + CPM 一致性

**Tech Stack:** C# / .NET 10.0 / xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12

**风险控制：** 每 Phase 后执行 `dotnet build` + `dotnet test`，绝不破坏现有功能

---

## Phase 1: 快速清理（P0 问题）

### Task 1.1: 删除物理残留目录和孤立文件

**Files:**
- Delete: `src/Seeing.Agent.Plugins/` (整个目录 — 项目已删除但物理目录残留)
- Delete: `tests/Seeing.Agent.Plugins.Tests/` (整个目录 — 零测试文件)
- Delete: `src/Seeing.Agent/Rules/` (空目录 — 0 个文件，RuleEngine 已移除)
- Delete: `samples/Seeing.Agent.Host/Program.cs` (孤立文件 — 无 .csproj，不在 .slnx 中)

**确认依据：**
- `Seeing.Agent.Plugins.csproj` glob 搜索 0 结果
- `Seeing.Agent.slnx` 中无 Plugins 项目引用
- `src/Seeing.Agent/Rules/` 0 个文件（read 确认）
- `samples/Seeing.Agent.Host/` 仅 1 个文件，无 .csproj，slnx 无引用

- [ ] **Step 1: 删除四个残留项**

```powershell
Remove-Item -Recurse -Force "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Agent.Plugins"
Remove-Item -Recurse -Force "E:\Projects\CSharp\Seeing.Agent\tests\Seeing.Agent.Plugins.Tests"
Remove-Item -Recurse -Force "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Agent\Rules"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\samples\Seeing.Agent.Host\Program.cs"
```

- [ ] **Step 2: 验证构建不受影响**

```bash
dotnet build Seeing.Agent.slnx
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: 验证测试不受影响**

```bash
dotnet test tests/Seeing.Agent.Tests
```
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: 清理残留目录和孤立文件（Plugins / Rules / Host-Program.cs）"
```

---

### Task 1.2: 删除死代码 `SessionHookPoints.cs`

**Files:**
- Delete: `src/Seeing.Session/Hooks/SessionHookPoints.cs`

**确认依据：** grep 确认 `SessionHookPoints` 仅在自身定义文件（第 3 行）和 AGENTS.md 文档中引用。所有实际代码 14 处使用 `HookPoints` 类（`SessionManager.cs` 第 92/139/177/205/239... 行），而非 `SessionHookPoints`。

- [ ] **Step 1: 删除**

```powershell
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Hooks\SessionHookPoints.cs"
```

- [ ] **Step 2: 验证构建和测试**

```bash
dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests
```
Expected: Build + tests pass.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore: 删除 SessionHookPoints.cs 死代码（已被 HookPoints.cs 替代）"
```

---

### Task 1.3: 更新 AGENTS.md 文档

**Files:**
- Modify: `AGENTS.md` (根目录 — 204 行，全面重写)
- Modify: `src/Seeing.Session/AGENTS.md` (Session 子包文档)

**AGENTS.md 错误对照表：**

| 行号 | 错误引用 | 正确引用 |
|------|---------|---------|
| 27 | `Seeing.Agent.Plugins/`（11个内置 Agent） | 移除（已删除） |
| 33 | `Seeing.Agent.Plugins.Tests/` | 移除 |
| 39-40 | 不存在的 `Tui/`、`SpectreTui/` 示例 | 更新为 `Cli/`、`Gateway.*` 等实际示例 |
| 42-48 | docs/ 子目录 architecture/gateway/acp/development | 更新为 `docs/superpowers/plans/` + `specs/` |
| 64 | `src/Seeing.Agent/Rules/RuleEngine.cs` → `AddRule()` | `src/Seeing.Agent/Core/Permission/PermissionService.cs` |
| 73 | `src/Seeing.Agent.Plugins/Agents/*.cs` | `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` |
| 75-78 | 不存在的 `docs/README.md`、`docs/architecture/*.md` | 移除或更新为实际文档 |
| 93 | DI 表 `ToolInvoker`→应为 `ToolManager`；`RuleEngine`→不存在 | 更新为实际服务名 |
| 148 | P0 "OpenAI request.Model 未参与选模" | 已修复 → 标记为已修复 |
| 150 | P1 "ModelScope 缺少 provider 字段" | 经分析 ModelConfig 自带 Provider → 标记为已分析/非Bug |
| 155 | P3 "MCP 集成占位实现" | 已完整实现 → 标记为已修复 |
| 160-165 | TODO 标记表 | 补充新发现问题 |
| 185 | `Seeing.Agent.SpectreTui` 不存在 | 移除 |

- [ ] **Step 1: 重写根目录 AGENTS.md**

用以下完整内容覆盖（关键变更标注在注释中）：

```markdown
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
│   ├── Seeing.Agent/              # 主 NuGet 库（~170 个 C# 文件）
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
│   ├── Seeing.Session/            # 独立会话管理包（47 个文件）
│   └── Seeing.TokenEstimation/    # Token 估算
│
├── tests/
│   ├── Seeing.Agent.Tests/        # 单元测试（49 个文件，多模块覆盖）
│   ├── Seeing.Agent.Acp.Tests/    # ACP 测试
│   ├── Seeing.Agent.Memory.Tests/ # Memory 测试
│   ├── Seeing.Agent.Scheduler.Tests/ # Scheduler 测试
│   ├── Seeing.Agent.WebUI.Tests/  # WebUI 测试
│   ├── Seeing.Gateway.Tests/      # Gateway 测试
│   ├── Seeing.Gateway.Client.Tests/ # Gateway Client 测试
│   ├── Seeing.Gateway.QQ.Tests/   # QQ 通道测试
│   ├── Seeing.Gateway.WeCom.Tests/ # WeCom 通道测试
│   ├── Seeing.Session.Tests/      # Session 测试（15 个源文件）
│   └── Seeing.TokenEstimation.Tests/ # Token 估算测试
│
├── samples/
│   ├── Seeing.Agent.WebUI/        # Blazor Web 界面示例
│   ├── Seeing.Agent.Cli/          # 命令行管理工具
│   ├── Seeing.Gateway.Server/     # Gateway 服务端
│   └── Seeing.Gateway.ChannelHost/ # 通道宿主
│
├── docs/superpowers/
│   ├── plans/                     # 实施计划（15+ 个历史计划）
│   └── specs/                     # 设计规格（17 个设计文档）
│
├── CommandLineUtils/              # [外部] McMaster 命令行库
└── command-line-api/              # [外部] dotnet 命令行 API
```

## WHERE TO LOOK

| 任务 | 位置 | 说明 |
|------|------|------|
| 新增 Agent 实现 | `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | AgentDefinition 数据定义，或继承 AgentBase |
| 新增 Tool 工具 | `src/Seeing.Agent/Tools/Attributes/ToolAttributes.cs` | 使用 `[Tool]` 注解 |
| 子 Agent / Task | `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs` | Session-first：`task`/`task_status`；Child=`SessionKind.SubAgent` |
| 扩展生命周期钩子 | `src/Seeing.Agent/Core/Hooks/HookManager.cs` | 实现 `IHookHandler`，30+ 钩子点 |
| 配置权限规则 | `src/Seeing.Agent/Core/Permission/PermissionService.cs` | PermissionRuleEntry + PermissionService |
| 连接 MCP Server | `src/Seeing.Agent/MCP/McpClientManager.cs` | `ConnectAsync()`，支持 stdio/HTTP/SSE |
| DI 注册入口 | `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | `AddSeeingAgent()` |
| 会话管理 | `src/Seeing.Session/Management/SessionManager.cs` | 独立包，生命周期管理 |
| 扩展插件开发 | `src/Seeing.Agent/Extensions/ExtensionLoader.cs` | 实现 `IExtension` 接口 |
| 循环检测防护 | `src/Seeing.Agent/Core/Detection/LoopDetector.cs` | 防止 LLM 无限循环 |
| 文件系统安全 | `src/Seeing.Agent/Tools/BuiltIn/FileSystemHelper.cs` | 路径白名单、输出限制 |
| 配置深度合并 | `src/Seeing.Agent/Core/Configuration/MergeDeep.cs` | 递归合并算法 |
| 工具装饰器链 | `src/Seeing.Agent/Decorators/ToolDecoratorRegistry.cs` | 超时→重试→缓存（由 ToolManager 在 RegisterTool() 时通过 `IToolDecoratorRegistry.Apply()` 生效） |
| 内置 Agent | `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs` | build/plan/explore/general/title/summary |
| ACP 集成 | `src/Seeing.Agent.Acp/` | Passthrough 透传 + acp 工具委派，`IAgentExecutionRouter` 路由 |

## CONVENTIONS（仅非标准）

### 命名约定
- 接口前缀 `I`，抽象类后缀 `Base`，结果类后缀 `Result`
- Hook 点命名：`{领域}.{事件}` 格式（如 `tool.before_execute`）
- 异步方法统一 `Async` 后缀
- 私有字段：`_camelCase`（_ 前缀）
- 私有静态字段：`s_camelCase`（s_ 前缀）

### DI 生命周期
| 服务 | 生命周期 |
|------|----------|
| ToolManager, HookManager, PermissionService, SkillManager, McpClientManager, IToolDecoratorRegistry | Singleton |
| SessionManager, AgentExecutor | Scoped |
| Middleware (Logging, Permission, Retry) | Transient |

### 注解发现（非标准）
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
- 用户级配置：`~/.seeing/seeing.json`（Providers、DefaultModel 等跨项目通用）
- 项目级配置：`.seeing/seeing.json`（Gateway 服务行为、Agents、Acp 等项目专属，覆盖用户级）
- **默认 Agent 统一使用** `DefaultAgent`；ACP / Native 由 Agent 的 `Runtime` 自动分流，勿在 `Gateway` 节重复配置 Agent

### 内部 Helper 类（不对外暴露）
| 文件 | 用途 |
|------|------|
| `FileSystemHelper.cs` | 文件操作封装、MIME 类型、截断 |
| `OutputTruncator.cs` | 输出限制（行数/字节/行长度） |
| `BinaryFileDetector.cs` | 二进制检测（扩展名+内容采样） |
| `MergeDeep.cs` | 配置深度合并算法 |
| `TokenCounterHelper.cs` | Token 计数（Session.Compression 命名空间） |

### 文件系统限制常量
| 限制项 | 默认值 |
|-------|-------|
| 读取行数 | 2000 行 |
| 单行长度 | 2000 字符 |
| 输出字节 | 50KB |
| Grep 匹配 | 100 条 |
| Glob 文件 | 100 个 |

### 工具装饰器链
- **注册方式**: `IToolDecoratorRegistry` 在 DI 中注册为 Singleton，`ToolManager` 在 `RegisterTool()` 时自动 `Apply()` 装饰器
- **链顺序**: TimeoutToolDecorator（最外层）→ RetryToolDecorator（中间层）→ CachedToolDecorator（最内层，可选）
- **默认超时**: 30 秒 → 3 次重试（1s间隔指数退避）→ 5 分钟缓存
- **重试异常**: `TimeoutException`, `HttpRequestException`, `TaskCanceledException`, `IOException`
- Hook (`tool.before_execute`/`after_execute`/`on_error`) 在 ToolManager 层，不受装饰器链影响

## ANTI-PATTERNS（本项目）

| 禁止 | 原因 | 文件位置 |
|------|------|---------|
| **混用路径分隔符** | `\\` 和 `/` 混用破坏跨平台 | `Seeing.Agent.csproj` vs `WebUI.csproj` |
| **WebUI 项目禁用 CPM** | 破坏包版本一致性 | `samples/Seeing.Agent.WebUI.csproj` |
| **静默吞异常** | `Activator.CreateInstance` 失败需记录 | `Tools/Discovery/ReflectedTool.cs` |
| **工具 ID 冲突静默覆盖** | 最后注册 wins，无警告 | `ToolManager.RegisterTool()` |
| **Hook 点字符串硬编码** | 使用 `HookPoints.*` 常量 | 各处调用 |
| **Context 类添加业务逻辑** | Context 应为纯数据容器 | 设计约束 |
| **权限通道未配置** | 默认拒绝所有，需显式配置 | `ServiceCollectionExtensions.cs` |
| **同步包装阻塞 async** | `.GetAwaiter().GetResult()` 死锁风险 | `ToolManager.cs`/`McpClientManager.cs` 等 |

## 已知问题

| 优先级 | 问题 | 影响 | 状态 |
|--------|------|------|------|
| **P0** | 装饰器链未注册 DI → 完全不生效 | 超时/重试/缓存功能缺失 | 修复中（Phase 2） |
| **P0** | TimeoutToolContext 丢失 EmitAsync/Services/PermissionChannel | 接入装饰器后工具功能异常 | 修复中（Phase 2 Task 2.0） |
| **P1** | HookManager 缺少移除能力 | 无法动态卸载钩子 | 待修复 |
| **P1** | ProviderConfig 字段未消费（Timeout/Headers/MaxRetries/Options） | 配置无效 | Phase 5 |
| **P1** | ExecutionStateManager 缺少 IDisposable | CTS 资源泄漏 | Phase 3 |
| **P1** | 16 处 `.GetAwaiter().GetResult()` 同步阻塞 | ASP.NET 上下文死锁风险 | ToolManager 修复中(Phase 2), 其余后续 |
| **P2** | SessionForker.CloneMessage 浅拷贝 | Fork 子会话丢失多模态内容 | Phase 3 |
| **P2** | ISession/ISessionFactory/ISessionLifecycle 旧体系未清理 | 双轨维护负担 | Phase 3 |
| **P2** | CountTokens 4 处重复 | 维护散落 | Phase 3 |
| **P2** | ForkInlineAsync 与 SessionForker 80% 重复 | 代码冗余 | Phase 3 |
| **P3** | PermissionService HMAC 密钥不持久化 | 重启后缓存失效 | Phase 5 |
| **P3** | SessionArchiver.ListArchives 文件名解析 | sessionId 含下划线时错误 | Phase 4 |

## 命令

```bash
# 构建
dotnet build Seeing.Agent.slnx

# 测试
dotnet test tests/Seeing.Agent.Tests
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
- **装饰器链**: 超时（最外层）→ 重试 → 缓存（最内层）。由 `IToolDecoratorRegistry` 在 DI 中管理，`ToolManager.RegisterTool()` 时自动 Apply
- **循环检测**: SHA256 参数哈希，连续 3 次警告，5 次终止
- **解决方案格式**: `.slnx`（VS 2022 17.13+ 新格式）
- **测试命名**: `{方法}_{场景}_Should{预期结果}` 或 AAA 注释分区
- **内置 Agent**: build(默认主代理)/plan(计划.只读)/explore(代码探索)/general(通用.隐藏)/title(标题生成.隐藏)/summary(摘要生成.隐藏)
```

- [ ] **Step 2: 更新 Session 包 AGENTS.md**

`src/Seeing.Session/AGENTS.md` 修改两处：

```diff
- │   └── SessionHookPoints.cs # 钩子点常量
+ │   ├── HookPoints.cs         # 钩子点常量（实际使用）
+ │   └── SessionHookResult.cs  # 钩子结果

- SessionHookPoints.Created    // "session.created"
+ HookPoints.Created    // "session.created"
（其余 6 处同样替换）
```

- [ ] **Step 3: Commit**

```bash
git add AGENTS.md src/Seeing.Session/AGENTS.md
git commit -m "docs: 全面更新 AGENTS.md，修复所有过时引用和项目结构"
```

---

## Phase 2: 装饰器链接入（核心架构重构） 🔴

### 背景

**当前状态：**
- `ToolManager` 构造函数已接收 `IToolDecoratorRegistry?`（第 44 行）✓
- `RegisterTool()` 已调用 `_decoratorRegistry?.Apply(tool)`（第 407 行）✓
- `ServiceCollectionExtensions` 已传递 `sp.GetService<IToolDecoratorRegistry>()`（第 467 行）✓
- **但 `IToolDecoratorRegistry` 从未注册到 DI → 始终为 null → 装饰器链不生效** ❌
- `ToolManager.ExecuteAsync` 第 557-665 行内置重试是唯一生效的重试逻辑
- `RetryToolDecorator` 与 ToolManager 内置重试逻辑重复
- **`TimeoutToolContext` 丢失 3 个关键字段 → 接入后会导致工具功能异常** 🐛

**目标：** 修复 TimeoutToolContext → 注册装饰器链到 DI → 移除 ToolManager 内置重试 → 统一重试异常列表

---

### Task 2.0: 修复 `TimeoutToolContext` 丢失字段（前置条件）

**Files:**
- Modify: `src/Seeing.Agent/Decorators/TimeoutToolDecorator.cs`

**问题：** `TimeoutToolContext` 仅拷贝 7 个字段，丢失 `EmitAsync`（TaskTool/BashTool 事件流依赖）、`Services`（TaskTool DI 依赖）、`PermissionChannel`（工具权限请求依赖）。接入装饰器链后所有工具执行都会经过 TimeoutToolContext，导致这些字段为 null。

修复只需在第 79 行后补 3 行。

- [ ] **Step 1: 修改 TimeoutToolContext 构造函数**

`TimeoutToolDecorator.cs` 第 69-81 行：

```csharp
private class TimeoutToolContext : ToolContext
{
    public TimeoutToolContext(ToolContext original, CancellationToken timeoutToken)
    {
        SessionId = original.SessionId;
        MessageId = original.MessageId;
        CallId = original.CallId;
        Agent = original.Agent;
        CancellationToken = timeoutToken;
        SetMetadata = original.SetMetadata;
        AskPermission = original.AskPermission;
        EmitAsync = original.EmitAsync;                    // ← 补：事件流推送
        PermissionChannel = original.PermissionChannel;     // ← 补：权限请求通道
        Services = original.Services;                       // ← 补：DI 服务定位
    }
}
```

- [ ] **Step 2: 验证构建**

```bash
dotnet build src/Seeing.Agent
```
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Decorators/TimeoutToolDecorator.cs
git commit -m "fix(decorator): TimeoutToolContext 补全 EmitAsync/Services/PermissionChannel 字段拷贝"
```

---

### Task 2.1: 在 DI 中注册装饰器链 + 统一重试异常列表

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Seeing.Agent/Decorators/RetryToolDecorator.cs`

**注意：** 需要添加 `IMemoryCache` 依赖。ASP.NET Core 默认注册，但 Generic Host 需要手动 `services.AddMemoryCache()`。

- [ ] **Step 1: 统一重试异常列表**

`RetryToolDecorator.cs` 的 `DefaultIsRetryable` 方法（第 101-106 行）当前缺少 `IOException`：

```csharp
private static bool DefaultIsRetryable(Exception ex)
{
    return ex is TimeoutException
        || ex is HttpRequestException
        || ex is IOException                                          // ← 补
        || ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested;
}
```

ToolManager 的 `IsRetryableException`（将被移除）包含 `IOException`，统一到装饰器中。

- [ ] **Step 2: 注册 IToolDecoratorRegistry 到 DI**

在 `ServiceCollectionExtensions.cs` 中，找到 ToolManager 注册位置（约第 462 行），在其**之前**添加：

```csharp
// ========== 注册装饰器链（超时→重试→缓存）==========
services.AddSingleton<IToolDecoratorRegistry>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var cache = sp.GetService<IMemoryCache>();

    var registry = new ToolDecoratorRegistry(sp);

    // 最外层：超时装饰器（30 秒默认）
    registry.Register(tool => new TimeoutToolDecorator(
        tool,
        TimeSpan.FromSeconds(30),
        loggerFactory.CreateLogger<TimeoutToolDecorator>()));

    // 中间层：重试装饰器（3 次，1 秒间隔，指数退避）
    registry.Register(tool => new RetryToolDecorator(
        tool,
        maxRetries: 3,
        delay: TimeSpan.FromSeconds(1),
        logger: loggerFactory.CreateLogger<RetryToolDecorator>()));

    // 最内层：缓存装饰器（5 分钟过期）— 仅在 IMemoryCache 可用时
    if (cache != null)
    {
        registry.Register(tool => new CachedToolDecorator(
            tool,
            cache,
            TimeSpan.FromMinutes(5),
            loggerFactory.CreateLogger<CachedToolDecorator>()));
    }

    return registry;
});
```

需要添加 using：
```csharp
using Seeing.Agent.Decorators;
using Microsoft.Extensions.Caching.Memory;
```

- [ ] **Step 3: 验证构建**

```bash
dotnet build src/Seeing.Agent
```
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs src/Seeing.Agent/Decorators/RetryToolDecorator.cs
git commit -m "feat(decorator): DI 注册 IToolDecoratorRegistry + 统一重试异常列表（补 IOException）"
```

---

### Task 2.2: 移除 ToolManager 内置重试逻辑

**Files:**
- Modify: `src/Seeing.Agent/Tools/ToolManager.cs`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

**原因：** 重试现由 `RetryToolDecorator` 处理（Task 2.1 注册）。保留内置重试将导致**双重重试**（3×3=9 次）。

- [ ] **Step 1: 简化构造函数 — 移除 `maxRetries`/`retryDelay` 参数**

`ToolManager.cs` 第 32-58 行：

```csharp
public class ToolManager
{
    private readonly ILogger<ToolManager> _logger;
    private readonly Core.Hooks.IHookManager _hookManager;
    private readonly IRuleEvaluator? _ruleEvaluator;
    private readonly ConcurrentDictionary<string, ITool> _tools = new();
    private readonly IServiceProvider? _serviceProvider;
    private readonly IToolDecoratorRegistry? _decoratorRegistry;
    private readonly IWorkspaceProvider? _workspace;
    private readonly object _toolStateLock = new();
    private HashSet<string> _userDisabledTools = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _projectDisabledTools = new(StringComparer.OrdinalIgnoreCase);

    public ToolManager(
        ILogger<ToolManager> logger,
        Core.Hooks.IHookManager hookManager,
        IServiceProvider? serviceProvider = null,
        IToolDecoratorRegistry? decoratorRegistry = null,
        IRuleEvaluator? ruleEvaluator = null,
        IWorkspaceProvider? workspace = null)
    {
        _logger = logger;
        _hookManager = hookManager;
        _serviceProvider = serviceProvider;
        _decoratorRegistry = decoratorRegistry;
        _ruleEvaluator = ruleEvaluator;
        _workspace = workspace;
    }
```

- [ ] **Step 2: 替换内置重试循环为单次执行**

`ToolManager.cs` 第 553-665 行区域，替换为：

```csharp
            // 执行工具（装饰器链已在 RegisterTool 时 Apply，处理重试/超时/缓存）
            var startTime = DateTime.Now;

            try
            {
                var context = new ToolContext
                {
                    SessionId = sessionId,
                    CallId = toolCall.Id,
                    CancellationToken = cancellationToken,
                    EmitAsync = emitAsync,
                    PermissionChannel = permissionChannel,
                    Services = _serviceProvider
                };

                JsonElement args;
                if (argsMutable["args"] is JsonElement je)
                    args = je;
                else if (argsMutable["args"] != null)
                    args = JsonSerializer.SerializeToElement(argsMutable["args"]);
                else
                    args = toolCall.Arguments is JsonElement je2 ? je2 : new JsonElement();

                var toolResult = await tool.ExecuteAsync(args, context);

                // ========== Hook: tool.execute.after ==========
                _hookManager.TriggerFireAndForget(
                    HookRegistry.ToolExecuteAfter,
                    sessionId,
                    new Dictionary<string, object?>
                    {
                        ["toolId"] = toolId,
                        ["callId"] = toolCall.Id,
                        ["args"] = args
                    },
                    new Dictionary<string, object?>
                    {
                        ["success"] = toolResult.Success,
                        ["output"] = toolResult.Output,
                        ["error"] = toolResult.Error,
                        ["metadata"] = toolResult.Metadata,
                        ["duration"] = DateTime.Now - startTime
                    });

                toolResult.ToolCallId = toolCall.Id;
                toolResult.Duration = DateTime.Now - startTime;
                return toolResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工具执行失败: {ToolId}", toolId);

                // ========== Hook: tool.on_error ==========
                _hookManager.TriggerFireAndForget(
                    HookRegistry.ToolOnError,
                    sessionId,
                    new Dictionary<string, object?>
                    {
                        ["toolId"] = toolId,
                        ["callId"] = toolCall.Id,
                        ["error"] = ex
                    });

                throw;
            }
```

- [ ] **Step 3: 删除 `IsRetryableException` 方法**

删除原第 659-665 行的 `IsRetryableException` 私有方法（逻辑已移到 `RetryToolDecorator.DefaultIsRetryable`）。

- [ ] **Step 4: 更新 ServiceCollectionExtensions 中的构造调用**

`ServiceCollectionExtensions.cs` 第 470 行，移除已删除的 `maxRetries`/`retryDelay` 参数：

```csharp
var invoker = new ToolManager(logger, hookManager, sp, decoratorRegistry, workspace: workspace);
```

- [ ] **Step 5: 修复全部 4 个同步包装方法中的 `.GetAwaiter().GetResult()`**

`ToolManager.cs` 共有 4 个同步包装方法（第 100、128、344、359 行），全部改用 `Task.Run` 隔离：

```csharp
// 第 100 行
public List<FunctionToolSchema> GetToolSchemasForMode(AgentMode mode)
{
    return Task.Run(() => GetToolSchemasForModeAsync(mode)).GetAwaiter().GetResult();
}

// 第 128 行
public List<FunctionToolSchema> GetToolSchemasForAgent(AgentDefinition agent)
{
    return Task.Run(() => GetToolSchemasForAgentAsync(agent)).GetAwaiter().GetResult();
}

// 第 344 行
public List<FunctionToolSchema> GetToolSchemas()
{
    return Task.Run(() => GetToolSchemasAsync()).GetAwaiter().GetResult();
}

// 第 359 行
public void RegisterTool(ITool tool)
{
    if (tool == null || string.IsNullOrEmpty(tool.Id))
    {
        _logger.LogWarning("尝试注册无效工具，已跳过");
        return;
    }
    Task.Run(async () => await RegisterToolAsync(tool)).GetAwaiter().GetResult();
}
```

- [ ] **Step 6: 搜索并更新测试中对旧构造函数的调用**

```bash
rg "new ToolManager" tests/ --include="*.cs" -n
```

若测试创建 ToolManager 时传了 `maxRetries`/`retryDelay` 参数，移除这些参数。

- [ ] **Step 7: 验证构建**

```bash
dotnet build src/Seeing.Agent
```

- [ ] **Step 8: 验证测试**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "ToolInvoker|AgentModeFilter"
```
Expected: All tests pass。

- [ ] **Step 9: Commit**

```bash
git add src/Seeing.Agent/Tools/ToolManager.cs src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "refactor(tool): 装饰器链替代 ToolManager 内置重试；修复同步包装死锁风险"
```

---

### ⚠️ Phase 2 验证里程碑

完成 Phase 2 后必须验证：

```bash
# 1. 完整构建
dotnet build Seeing.Agent.slnx

# 2. 全部 Agent 测试
dotnet test tests/Seeing.Agent.Tests

# 3. 确认装饰器链生效：检查 DI 解析 IToolDecoratorRegistry 不为 null
#    可在 ToolManager.RegisterTool 中添加临时日志验证 _decoratorRegistry != null
```

---

## Phase 3: Session 包修复

### Task 3.1: 为 `ExecutionStateManager` 添加 `IDisposable`

**Files:**
- Modify: `src/Seeing.Session/Execution/ExecutionStateManager.cs`

- [ ] **Step 1: 实现 IDisposable**

```csharp
public class ExecutionStateManager : IExecutionState, IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // ... 现有字段 ...

    public Task StartExecutionAsync()
    {
        if (IsExecuting) return Task.CompletedTask;
        LastError = null;
        IsExecuting = true;
        IsPaused = false;
        _cts?.Dispose();  // 释放旧 CTS 再创建新的
        _cts = new CancellationTokenSource();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_cts != null)
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _cts = null;
        }
    }
}
```

- [ ] **Step 2: 验证**

```bash
dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests
```

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Session/Execution/ExecutionStateManager.cs
git commit -m "fix(session): ExecutionStateManager 实现 IDisposable 防止 CTS 资源泄漏"
```

---

### Task 3.2: 修复 `SessionForker.CloneMessage()` 浅拷贝

**Files:**
- Modify: `src/Seeing.Session/Management/SessionForker.cs`
- Modify: `src/Seeing.Session/Management/SessionManager.cs`

**SessionMessage.Clone() 已存在**（第 299-334 行，深拷贝 Parts/ToolCalls/TokenUsage/ReasoningContent 等全部字段），直接复用。Fork 时需要重新生成 Id（Guid）。

- [ ] **Step 1: 替换 SessionForker.CloneMessage**

`SessionForker.cs` 第 86-96 行：

```csharp
private static SessionMessage CloneMessage(SessionMessage msg)
{
    var clone = msg.Clone();
    clone.Id = Guid.NewGuid().ToString("N");
    return clone;
}
```

- [ ] **Step 2: 同步修复 SessionManager.ForkInlineAsync**

`SessionManager.cs` 第 459 行：

```csharp
// 修改前：
foreach (var msg in sourceSession.Messages)
    forkedSession.Messages.Add(msg);   // 直接引用，非克隆

// 修改后：
foreach (var msg in sourceSession.Messages)
    forkedSession.Messages.Add(msg.Clone());
```

- [ ] **Step 3: 验证**

```bash
dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests --filter "Fork|SessionChild"
```

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Session/Management/SessionForker.cs src/Seeing.Session/Management/SessionManager.cs
git commit -m "fix(session): Fork 使用 SessionMessage.Clone() 完整深拷贝"
```

---

### Task 3.3: 抽取共享 `CountTokens` + 修复 `SummarizingStrategy` 同步阻塞

**Files:**
- Create: `src/Seeing.Session/Compression/TokenCounterHelper.cs`
- Modify: `src/Seeing.Session/Compression/SessionCompressor.cs`（删除私有 CountTokens）
- Modify: `src/Seeing.Session/Compression/Strategies/SlidingWindowTokenStrategy.cs`（同上）
- Modify: `src/Seeing.Session/Compression/Strategies/SummarizingStrategy.cs`（同上 + 修复阻塞）
- Modify: `src/Seeing.Session/Compression/Strategies/HybridStrategy.cs`（同上）

- [ ] **Step 1: 创建 TokenCounterHelper**

```csharp
using Seeing.Session.Core;
using Seeing.TokenEstimation;

namespace Seeing.Session.Compression;

internal static class TokenCounterHelper
{
    public static int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter counter)
    {
        if (messages == null) return 0;
        var total = 0;
        foreach (var message in messages)
        {
            total += counter.Estimate(message.Content ?? string.Empty);
            if (!string.IsNullOrEmpty(message.ReasoningContent))
                total += counter.Estimate(message.ReasoningContent);
            if (message.ToolCalls != null)
                foreach (var tc in message.ToolCalls)
                {
                    total += counter.Estimate(tc.Name ?? string.Empty);
                    total += counter.Estimate(tc.Arguments ?? string.Empty);
                }
            if (message.Parts != null)
                foreach (var p in message.Parts)
                    if (!string.IsNullOrEmpty(p.Text))
                        total += counter.Estimate(p.Text);
        }
        return total;
    }
}
```

- [ ] **Step 2: 替换 4 个文件中的私有 CountTokens**

每个文件：
- 删除私有 `CountTokens` 方法
- 所有调用处改为 `TokenCounterHelper.CountTokens(messages, counter)`

- [ ] **Step 3: 修复 SummarizingStrategy 同步阻塞**

`SummarizingStrategy.cs` 第 115 行 `.GetAwaiter().GetResult()` → 使用 `Task.Run` 隔离：

```csharp
var summaryContent = Task.Run(() => GenerateSummaryAsync(toSummarize)).GetAwaiter().GetResult();
```

（`CompressByTokenBudget` 是同步接口方法，异步化需改接口签名，留后续迭代）

- [ ] **Step 4: 验证**

```bash
dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests --filter "Compression"
```

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Session/Compression/
git commit -m "refactor(session): 抽取共享 CountTokens + 修复 SummarizingStrategy 同步阻塞"
```

---

### Task 3.4: 消除 `ForkInlineAsync` 与 `SessionForker` 重复

**Files:**
- Modify: `src/Seeing.Session/Management/SessionManager.cs`

**问题:** `SessionManager` 的 `_logger` 是 `ILogger<SessionManager>?` 类型，而 `SessionForker` 构造函数需要 `ILogger<SessionForker>`。解决方法：向 SessionManager 构造函数添加 `ILoggerFactory?` 参数，或内部创建 SessionForker 时不传 logger。

- [ ] **Step 1: 检查 SessionManager 是否有 ILoggerFactory 注入**

```bash
rg "ILoggerFactory" src/Seeing.Session/Management/SessionManager.cs
```

若没有，添加可选的 `ILoggerFactory?` 参数。

- [ ] **Step 2: 替换 ForkInlineAsync**

`SessionManager.cs` 第 417-466 行：

```csharp
private async Task<SessionData> ForkInlineAsync(
    string sessionId, string? atMessageId, string? label, CancellationToken ct)
{
    var forkerLogger = _loggerFactory?.CreateLogger<SessionForker>();
    var forker = new SessionForker(forkerLogger, this);
    return await forker.ForkAsync(sessionId, atMessageId, label, ct);
}
```

若 `_loggerFactory` 不存在且不便于添加，备选方案：传 null logger：

```csharp
private async Task<SessionData> ForkInlineAsync(...)
{
    var forker = new SessionForker(null!, this);
    return await forker.ForkAsync(sessionId, atMessageId, label, ct);
}
```

`SessionForker` 构造函数中 logger 已标记为可选（参数无 `Required`），传 null 安全。

- [ ] **Step 3: 验证**

```bash
dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests
```

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Session/Management/SessionManager.cs
git commit -m "refactor(session): ForkInlineAsync 委托 SessionForker，消除 80% 重复代码"
```

---

### Task 3.5: 清理 Session 旧接口体系（含 ISessionLifecycle）

**Files:**
- Delete: `src/Seeing.Session/Core/ISession.cs`
- Delete: `src/Seeing.Session/Core/Session.cs`
- Delete: `src/Seeing.Session/Core/ISessionFactory.cs`
- Delete: `src/Seeing.Session/Core/SessionFactory.cs`
- Delete: `src/Seeing.Session/Core/ISessionLifecycle.cs`
- Delete: `src/Seeing.Session/Core/SessionLifecycle.cs`
- Delete: `tests/Seeing.Session.Tests/SessionFactoryTests.cs`
- Delete: `tests/Seeing.Session.Tests/SessionLifecycleTests.cs`
- Delete: `tests/Seeing.Session.Tests/ISessionFactoryTests.cs`
- Modify: `src/Seeing.Session/SessionServiceExtensions.cs`（移除旧接口注册）
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（移除 ISessionLifecycle 注册，第 388 行）
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`（如有 ISessionLifecycle 注册则移除）

**执行前验证（关键安全步骤）：**

- [ ] **Step 0: 全项目搜索引用确保零遗漏**

```bash
# 搜索 ISession 裸引用（排除 ISessionManager 等新体系）
rg "ISession[^A-Za-z]" src/ samples/ --include="*.cs" | rg -v "ISessionManager|ISessionStore|ISessionHook|ISessionEvent|ISessionLifecycle|ISessionFactory"

# 搜索 ISessionFactory
rg "ISessionFactory" src/ samples/ --include="*.cs"

# 搜索 SessionFactory
rg "SessionFactory" src/ samples/ --include="*.cs" | rg -v "ISessionFactory"

# 搜索 ISessionLifecycle（排除 ISessionLifecycleHook）
rg "ISessionLifecycle" src/ samples/ --include="*.cs" | rg -v "ISessionLifecycleHook|AcpSessionLifecycleHook"

# 搜索 SessionLifecycle（类引用，非 Acp 前缀版本）
rg "SessionLifecycle[^A-Za-z]" src/ samples/ --include="*.cs"
```

**预期结果：** 仅在被删除的文件自身和 DI 注册代码中有引用。若有其他引用，先记录下来再处理。

- [ ] **Step 1: 删除 8 个源文件和 3 个测试文件**

```powershell
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Core\ISession.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Core\Session.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Core\ISessionFactory.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Core\SessionFactory.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Core\ISessionLifecycle.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\src\Seeing.Session\Core\SessionLifecycle.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\tests\Seeing.Session.Tests\SessionFactoryTests.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\tests\Seeing.Session.Tests\SessionLifecycleTests.cs"
Remove-Item "E:\Projects\CSharp\Seeing.Agent\tests\Seeing.Session.Tests\ISessionFactoryTests.cs"
```

- [ ] **Step 2: 移除 ServiceCollectionExtensions 中的 ISessionLifecycle 注册**

`src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` 第 388 行，删除：

```csharp
services.AddSingleton<ISessionLifecycle, SessionLifecycle>();  // ← 删除此行
```

同时需要移除文件顶部的 using 引用（如果仅为此行）：`using Seeing.Session.Core;`

- [ ] **Step 3: 移除 WebUI 中的 ISessionLifecycle 注册**

搜索 `samples/Seeing.Agent.WebUI/Program.cs`：
```bash
rg "ISessionLifecycle|SessionLifecycle" samples/Seeing.Agent.WebUI/Program.cs
```
若有，移除对应注册行。

- [ ] **Step 4: 清理 SessionServiceExtensions 中的旧接口注册**

检查 `src/Seeing.Session/SessionServiceExtensions.cs` 的 `AddSessionManager()` 方法中是否有 ISessionFactory/ISessionLifecycle 注册：

```bash
rg "ISessionFactory|ISessionLifecycle|SessionFactory|SessionLifecycle" src/Seeing.Session/SessionServiceExtensions.cs
```
若有，移除。

- [ ] **Step 5: 验证全局构建**

```bash
dotnet build Seeing.Agent.slnx
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 6: 验证全量 Session 测试**

```bash
dotnet test tests/Seeing.Session.Tests
```
Expected: 剩余测试全部通过（删了 3 个旧体系测试文件）。

- [ ] **Step 7: 验证 Agent 测试**

```bash
dotnet test tests/Seeing.Agent.Tests
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor(session): 移除 ISession/ISessionFactory/ISessionLifecycle 旧接口体系"
```

---

## Phase 4: Agent 核心修复

### Task 4.1: 统一内置 Agent 定义字段

**Files:**
- Modify: `src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs`

| Agent | 当前缺失 | 补充值 | 理由 |
|-------|---------|--------|------|
| `title` | Tags, MaxSteps | `MaxSteps = 1`, `Tags = {"system","hidden","title-generation"}` | 单步执行，无工具调用 |
| `summary` | Tags, MaxSteps, Temperature | `MaxSteps = 1`, `Temperature = 0.5`, `Tags = {"system","hidden","summary-generation"}` | 同 title，补 Temperature 与 title 一致 |
| `explore` | MaxSteps, Temperature | `MaxSteps = 16`, `Temperature = 0.3` | 快速探索，低温度保精确 |
| `general` | MaxSteps, Temperature | `MaxSteps = 32`, `Temperature = 0.7` | 通用研究，适度创造 |

- [ ] **Step 1: 逐个修改 CreateXxxAgent() 方法**

- [ ] **Step 2: 验证**

```bash
dotnet build src/Seeing.Agent && dotnet test tests/Seeing.Agent.Tests --filter "AgentModeFilter|AgentDiscovery"
```

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Core/BuiltInAgents/BuiltInAgents.cs
git commit -m "fix(agent): 统一内置 Agent 的 Tags/MaxSteps/Temperature 字段"
```

---

### Task 4.2: 修复 `InMemorySessionStore` + `SessionArchiver`

**Files:**
- Modify: `src/Seeing.Session/Storage/InMemorySessionStore.cs`
- Modify: `src/Seeing.Session/Management/SessionArchiver.cs`

- [ ] **Step 1: InMemorySessionStore 添加 CancellationToken**

`InMemorySessionStore.cs` 找到 `EnumerateSessions` 方法（约第 79 行）：

```csharp
public async IAsyncEnumerable<SessionData> EnumerateSessions(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await Task.CompletedTask;
    foreach (var kvp in _sessions)
    {
        ct.ThrowIfCancellationRequested();
        yield return kvp.Value;
    }
}
```

- [ ] **Step 2: SessionArchiver 修复文件名解析**

`SessionArchiver.cs` 第 70-77 行：

```csharp
// 修改前（Split 会误拆 sessionId 中的下划线）：
var parts = fileName.Split('_');
if (parts.Length >= 2) {
    SessionId = parts[0],
    ArchivedAt = DateTimeOffset.TryParse(parts[1], out var dt) ? dt : ...

// 修改后（用 LastIndexOf 取最后一个下划线）：
var lastUnderscore = fileName.LastIndexOf('_');
if (lastUnderscore > 0)
{
    result.Add(new ArchiveInfo
    {
        SessionId = fileName.Substring(0, lastUnderscore),
        ArchivedAt = DateTimeOffset.TryParse(
            fileName.Substring(lastUnderscore + 1), out var dt) ? dt : DateTimeOffset.MinValue,
        FilePath = file,
        SizeBytes = new FileInfo(file).Length
    });
}
```

- [ ] **Step 3: 验证**

```bash
dotnet build src/Seeing.Session && dotnet test tests/Seeing.Session.Tests
```

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Session/Storage/InMemorySessionStore.cs src/Seeing.Session/Management/SessionArchiver.cs
git commit -m "fix(session): InMemorySessionStore 补充 CancellationToken；SessionArchiver 修复下划线解析"
```

---

## Phase 5: 基础设施优化

### Task 5.1: 消费 ProviderConfig.Headers + 持久化 HMAC 密钥

**Files:**
- Modify: `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs`
- Modify: `src/Seeing.Agent/Llm/Clients/AnthropicClient.cs`
- Modify: `src/Seeing.Agent/Core/Permission/PermissionService.cs`

- [ ] **Step 1: AnthropicClient 注入自定义 Headers**

在 `AnthropicClient` 构造 HttpClient 之后添加：

```csharp
if (config.Headers != null)
{
    foreach (var (key, value) in config.Headers)
    {
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
    }
}
```

- [ ] **Step 2: OpenAiClient 注入自定义 Headers**

根据 OpenAI SDK 的 `OpenAIClientOptions` API 或底层 `HttpClient` 注入 Headers。具体实现依赖 SDK 版本。

- [ ] **Step 3: PermissionService HMAC 密钥持久化**

`PermissionService.cs` 的 `LoadOrGenerateHmacKey()` 方法：

```csharp
private static byte[] LoadOrGenerateHmacKey()
{
    var keyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Seeing.Agent", "permission_hmac_key.bin");

    if (File.Exists(keyPath))
    {
        try { return File.ReadAllBytes(keyPath); }
        catch { }
    }

    var key = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(key);

    try
    {
        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(keyPath, key);
    }
    catch { }

    return key;
}
```

- [ ] **Step 4: 验证**

```bash
dotnet build src/Seeing.Agent && dotnet test tests/Seeing.Agent.Tests --filter "PermissionCache"
```

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Llm/Clients/ src/Seeing.Agent/Core/Permission/PermissionService.cs
git commit -m "feat: ProviderConfig.Headers 被 LLM 客户端消费 + HMAC 密钥持久化到文件"
```

---

## Phase 6: 测试补充与最终验证

### Task 6.1: 补充核心模块测试

**Files:**
- Create: `tests/Seeing.Agent.Tests/Permission/PermissionServiceTests.cs`
- Create: `tests/Seeing.Agent.Tests/Tools/FileSystemHelperTests.cs`
- Create: `tests/Seeing.Agent.Tests/Detection/BinaryFileDetectorTests.cs`
- Create: `tests/Seeing.Agent.Tests/Tools/OutputTruncatorTests.cs`
- Create: `tests/Seeing.Agent.Tests/Decorators/ToolDecoratorRegistryTests.cs`

**测试要点：**

**PermissionServiceTests:**
```csharp
[Fact] public void Evaluate_NoRules_ShouldApplyDefaultEffect()
[Fact] public void Evaluate_AllowRule_ShouldReturnAllow()
[Fact] public void Evaluate_DenyOverridesAllow_WhenHigherPriority()
[Fact] public void Evaluate_WildcardMatch_ShouldMatchAllTools()
```

**FileSystemHelperTests:**
```csharp
[Fact] public void IsPathWithinWorkspace_Allowed_ShouldReturnTrue()
[Fact] public void IsPathWithinWorkspace_Outside_ShouldReturnFalse()
```

**BinaryFileDetectorTests:**
```csharp
[Fact] public void IsBinary_PngExtension_ShouldReturnTrue()
[Fact] public void IsBinary_TxtFile_ShouldReturnFalse()
```

**OutputTruncatorTests:**
```csharp
[Fact] public void Truncate_WithinLimit_ShouldReturnUnchanged()
[Fact] public void Truncate_ExceedsLines_ShouldTruncate()
[Fact] public void Truncate_ExceedsBytes_ShouldTruncate()
```

**ToolDecoratorRegistryTests:**
```csharp
[Fact] public void Apply_NoDecorators_ShouldReturnOriginalTool()
[Fact] public void Apply_SingleDecorator_ShouldWrapTool()
[Fact] public void Apply_MultipleDecorators_ShouldChainInOrder()
```

- [ ] **Step 1-5: 创建并实现各测试文件**

- [ ] **Step 6: 运行全量测试**

```bash
dotnet test tests/Seeing.Agent.Tests
```

- [ ] **Step 7: Commit**

```bash
git add tests/Seeing.Agent.Tests/
git commit -m "test: 补充 Permission/FileSystem/Binary/Truncator/Decorator 测试覆盖"
```

---

### Task 6.2: Phase 6 局部验证

- [ ] **Step 1: 运行新测试和受影响测试**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "Permission|FileSystemHelper|BinaryFileDetector|OutputTruncator|ToolDecoratorRegistry"
```
Expected: 所有新建测试通过。

- [ ] **Step 2: Commit**

```bash
git add -A && git status
git commit -m "chore: Phase 6 — 新测试全部通过"

- [ ] **Step 1: 完整构建（零警告零错误）**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 2: 运行全部 12 个测试套件**

```bash
dotnet test tests/Seeing.Agent.Tests
dotnet test tests/Seeing.Session.Tests
dotnet test tests/Seeing.Agent.Memory.Tests
dotnet test tests/Seeing.Agent.Acp.Tests
dotnet test tests/Seeing.Agent.Scheduler.Tests
dotnet test tests/Seeing.Gateway.Tests
dotnet test tests/Seeing.Gateway.Client.Tests
dotnet test tests/Seeing.Gateway.WeCom.Tests
dotnet test tests/Seeing.Gateway.QQ.Tests
dotnet test tests/Seeing.TokenEstimation.Tests
dotnet test tests/Seeing.Agent.WebUI.Tests
```

- [ ] **Step 3: Commit**

```bash
git add -A && git status
git commit -m "chore: Phase 6-7 — 全量验证通过"
```

---

## Phase 7: 同步阻塞消缺 + CPM 一致性

> 共 25 处 `.GetAwaiter().GetResult()` + 3 个 CPM 不一致项目 + 1 个缺失 csproj。

### Task 7.1: McpClientManager 同步包装方法修复（4 处）

**Files:**
- Modify: `src/Seeing.Agent/MCP/McpClientManager.cs`

**位置：** `PauseServer(string)` 第 180 行, `ResumeServer(string)` 第 189 行, `PauseAllServers()` 第 197 行, `ResumeAllServers()` 第 208 行。

**修复：** 这些是公开的同步 API 包装。用 `Task.Run` 隔离避免死锁：

```csharp
// 第 180 行附近 PauseServer
public void PauseServer(string serverId)
{
    Task.Run(async () => await PauseServerAsync(serverId)).GetAwaiter().GetResult();
}

// 第 189 行附近 ResumeServer
public void ResumeServer(string serverId)
{
    Task.Run(async () => await ResumeServerAsync(serverId)).GetAwaiter().GetResult();
}

// 第 197 行附近 PauseAllServers
public void PauseAllServers()
{
    Task.Run(async () => await PauseAllServersAsync()).GetAwaiter().GetResult();
}

// 第 208 行附近 ResumeAllServers
public void ResumeAllServers()
{
    Task.Run(async () => await ResumeAllServersAsync()).GetAwaiter().GetResult();
}
```

- [ ] **Step 1: 修改 4 个方法并验证构建**

```bash
dotnet build src/Seeing.Agent
```

- [ ] **Step 2: 确认调用方无影响**

```bash
rg "\.PauseServer\(|\.ResumeServer\(|\.PauseAllServers\(|\.ResumeAllServers\(" src/ samples/ --include="*.cs"
```

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/MCP/McpClientManager.cs
git commit -m "fix(mcp): McpClientManager 同步包装改用 Task.Run 避免死锁"
```

---

### Task 7.2: McpConnectionCoordinator.Dispose + McpConfigLoader 修复（3 处）

**Files:**
- Modify: `src/Seeing.Agent/MCP/Management/McpConnectionCoordinator.cs`
- Modify: `src/Seeing.Agent/MCP/McpConfigLoader.cs`

- [ ] **Step 1: McpConnectionCoordinator.Dispose() 第 738 行**

Dispose 中同步调用 `DisconnectAsync` → 改用 `IAsyncDisposable`：

```csharp
// 添加 IAsyncDisposable 实现
public async ValueTask DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    if (_client != null)
    {
        try { await _client.DisconnectAsync(); } catch { }
    }
    
    _connectionLock?.Dispose();
    _stateLock?.Dispose();
}

// 原有的同步 Dispose 保留为兜底
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    
    _connectionLock?.Dispose();
    _stateLock?.Dispose();
}
```

检查 `McpClientManager.DisposeAsync` 中是否调用了 `_coordinator.Dispose()` → 改为 `await _coordinator.DisposeAsync()`。

- [ ] **Step 2: McpConfigLoader.LoadDefault 第 42/50 行**

```csharp
public static McpServerConfig? LoadDefault(IWorkspaceProvider workspace, ILogger? logger = null)
{
    // 第 42 行：用户级配置
    var userConfig = Task.Run(() => McpConfigPersistence.LoadAsync(ConfigLevel.User))
        .GetAwaiter().GetResult();
    // ... 合并逻辑 ...
    // 第 50 行：项目级配置
    var projectConfig = Task.Run(() => McpConfigPersistence.LoadAsync(ConfigLevel.Project))
        .GetAwaiter().GetResult();
    // ...
}
```

- [ ] **Step 3: 验证**

```bash
dotnet build src/Seeing.Agent && dotnet test tests/Seeing.Agent.Tests --filter "MCP"
```

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/MCP/Management/McpConnectionCoordinator.cs src/Seeing.Agent/MCP/McpConfigLoader.cs
git commit -m "fix(mcp): McpConnectionCoordinator 改用 IAsyncDisposable；McpConfigLoader 用 Task.Run 隔离"
```

---

### Task 7.3: AgentSelectionResolver + TaskTool 修复（2 处）

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentSelectionResolver.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs`

- [ ] **Step 1: AgentSelectionResolver.ResolveModelId 第 53 行**

`ResolveModelId` 是同步方法，内部调用 `IAgentRegistry.GetAgentAsync`：

```csharp
// 修改前：
var agent = _agentRegistry.GetAgentAsync(agentName).GetAwaiter().GetResult();

// 修改后：
var agent = Task.Run(() => _agentRegistry.GetAgentAsync(agentName)).GetAwaiter().GetResult();
```

- [ ] **Step 2: TaskTool.BuildDescription 第 449 行**

`BuildDescription` 内部调用 `IAgentRegistry.GetTaskableAgentsAsync`：

```csharp
// 修改前：
var agents = agentRegistry.GetTaskableAgentsAsync().GetAwaiter().GetResult();

// 修改后：
var agents = Task.Run(() => agentRegistry.GetTaskableAgentsAsync()).GetAwaiter().GetResult();
```

- [ ] **Step 3: 验证**

```bash
dotnet build src/Seeing.Agent
```

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Core/AgentSelectionResolver.cs src/Seeing.Agent/Tools/BuiltIn/Task/TaskTool.cs
git commit -m "fix: AgentSelectionResolver/TaskTool 同步调用改用 Task.Run 隔离"
```

---

### Task 7.4: 子项目同步阻塞修复（7 处）

**Files:**
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- Modify: `src/Seeing.Agent.Scheduler/Extensions/SchedulerServiceCollectionExtensions.cs`
- Modify: `src/Seeing.Agent.Gateway/Channels/GatewayChannelRegistry.cs`
- Modify: `src/Seeing.Agent.Acp/Session/AcpTaskStore.cs`
- Modify: `src/Seeing.Agent.Acp/Session/AcpSessionStore.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`

- [ ] **Step 1: ExecutionJobService.Cancel 第 197/204 行**

```csharp
public void Cancel(string executionId)
{
    // 第 197 行
    Task.Run(async () => await _sessionQueue.CancelAsync(executionId)).GetAwaiter().GetResult();
    // 第 204 行
    Task.Run(async () => await _bgTaskManager.CancelBySessionAsync(sessionId)).GetAwaiter().GetResult();
}
```

- [ ] **Step 2: SchedulerServiceCollectionExtensions 第 156 行**

```csharp
// 修改前：
QuartzSqliteInitializer.InitializeAsync(dbPath, logger).GetAwaiter().GetResult();

// 修改后：
Task.Run(() => QuartzSqliteInitializer.InitializeAsync(dbPath, logger)).GetAwaiter().GetResult();
```

- [ ] **Step 3: GatewayChannelRegistry.ResolvePluginPath 第 190 行**

```csharp
var resolved = Task.Run(() => _extensionLoader.ResolveTarget(spec)).GetAwaiter().GetResult();
```

- [ ] **Step 4: AcpTaskStore.Persist 第 74 行 + AcpSessionStore 第 44/133 行**

```csharp
// AcpTaskStore.cs 第 74 行
Task.Run(async () => await _sessionManager.SaveAsync(session.Id)).GetAwaiter().GetResult();

// AcpSessionStore.cs 第 44 行
Task.Run(async () => await _sessionManager.EnsureSessionAsync(seeingSessionId)).GetAwaiter().GetResult();

// AcpSessionStore.cs 第 133 行
Task.Run(async () => await _sessionManager.SaveAsync(session.Id)).GetAwaiter().GetResult();
```

- [ ] **Step 5: WebUI EventStreamHandler.ProcessEvent 第 1081 行**

```csharp
// 修改前：
ProcessEventAsync(evt).GetAwaiter().GetResult();

// 修改后：
Task.Run(async () => await ProcessEventAsync(evt)).GetAwaiter().GetResult();
```

- [ ] **Step 6: 验证全量构建**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 7: 验证相关测试**

```bash
dotnet test tests/Seeing.Agent.Acp.Tests
dotnet test tests/Seeing.Agent.Scheduler.Tests
dotnet test tests/Seeing.Gateway.Tests
```

- [ ] **Step 8: Commit**

```bash
git add src/Seeing.Agent.App/ src/Seeing.Agent.Scheduler/ src/Seeing.Agent.Gateway/ src/Seeing.Agent.Acp/ samples/
git commit -m "fix: 子项目全部 .GetAwaiter().GetResult() 改用 Task.Run 隔离避免死锁"
```

---

### Task 7.5: ServiceCollectionExtensions DI 初始化修复（3 处）

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

**位置：** 第 68 行（`UnifiedConfigManager.LoadAsync`）、第 262 行（同上）、第 338 行（`AgentDiscovery.DiscoverAgentsAsync`）。

**说明：** 这三处在 DI 初始化（应用启动）时运行，不在 ASP.NET 请求上下文中，死锁风险低。但仍应规范化处理。

- [ ] **Step 1: 修改 3 处同步阻塞**

```csharp
// 第 68 行附近
Task.Run(async () => await UnifiedConfigManager.LoadAsync(...)).GetAwaiter().GetResult();

// 第 262 行附近  
Task.Run(async () => await UnifiedConfigManager.LoadAsync(...)).GetAwaiter().GetResult();

// 第 338 行附近
Task.Run(async () => await AgentDiscovery.DiscoverAgentsAsync(...)).GetAwaiter().GetResult();
```

- [ ] **Step 2: 验证构建**

```bash
dotnet build src/Seeing.Agent
```

- [ ] **Step 3: 验证 Agent 测试（确保 DI 初始化正常）**

```bash
dotnet test tests/Seeing.Agent.Tests
```

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "fix(di): ServiceCollectionExtensions 同步加载改用 Task.Run 隔离"
```

---

### Task 7.6: CPM 一致性修复 + TokenBudget.Tests 补充

**Files:**
- Modify: `tests/Seeing.Session.Tests/Seeing.Session.Tests.csproj` — 重新启用 CPM
- Check: `tests/Seeing.TokenBudget.Tests/` — 缺失 .csproj

- [ ] **Step 1: 分析 Seeing.Session.Tests 为什么禁用 CPM**

查看 csproj 中显式指定了 Version 的包，对比 `Directory.Packages.props` 中的版本是否一致。

```bash
rg "Version=" tests/Seeing.Session.Tests/Seeing.Session.Tests.csproj
```

对比：
```bash
rg "PackageVersion" Directory.Packages.props
```

若版本一致，移除 `<EnableCentralPackageVersions>false</EnableCentralPackageVersions>` 和所有显式 Version 属性。

- [ ] **Step 2: 重新启用 Seeing.Session.Tests 的 CPM**

```xml
<!-- 删除此属性 -->
- <EnableCentralPackageVersions>false</EnableCentralPackageVersions>

<!-- 所有 PackageReference 移除 Version 属性 -->
- <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />
+ <PackageReference Include="Microsoft.NET.Test.Sdk" />
```

- [ ] **Step 3: 处理 TokenBudget.Tests 缺失 csproj**

`tests/Seeing.TokenBudget.Tests/` 仅含 `obj/` 目录，无 `.csproj` 文件。选项：

**方案 A（推荐）：** 删除目录。若 slnx 中无引用且无测试文件，属于残留物。
```powershell
Remove-Item -Recurse -Force "tests/Seeing.TokenBudget.Tests"
```

**方案 B：** 创建标准测试项目。如果 TokenBudget 确实需要独立测试套件，则补完 csproj。

**Step 3 具体执行：** 先检查 slnx 中是否引用：
```bash
rg "TokenBudget.Tests" Seeing.Agent.slnx
```
若无引用 → 方案 A，直接删除。

- [ ] **Step 4: 验证**

```bash
dotnet build Seeing.Agent.slnx
dotnet test tests/Seeing.Session.Tests
```

- [ ] **Step 5: Commit**

```bash
git add tests/Seeing.Session.Tests/
git commit -m "fix: Seeing.Session.Tests 重新启用 CPM；清理 TokenBudget.Tests 残留"
```

---

### ⚠️ Phase 7 验证里程碑

```bash
# 完整构建
dotnet build Seeing.Agent.slnx

# 受影响的测试套件
dotnet test tests/Seeing.Agent.Tests
dotnet test tests/Seeing.Session.Tests
dotnet test tests/Seeing.Agent.Acp.Tests
dotnet test tests/Seeing.Agent.Scheduler.Tests
dotnet test tests/Seeing.Gateway.Tests
```

---

### Task 7.7: 全量最终验证

- [ ] **Step 1: 全量构建（零错误零警告）**

```bash
dotnet build Seeing.Agent.slnx
```

- [ ] **Step 2: 运行全部 12 个测试套件**

```bash
dotnet test tests/Seeing.Agent.Tests
dotnet test tests/Seeing.Session.Tests
dotnet test tests/Seeing.Agent.Memory.Tests
dotnet test tests/Seeing.Agent.Acp.Tests
dotnet test tests/Seeing.Agent.Scheduler.Tests
dotnet test tests/Seeing.Gateway.Tests
dotnet test tests/Seeing.Gateway.Client.Tests
dotnet test tests/Seeing.Gateway.WeCom.Tests
dotnet test tests/Seeing.Gateway.QQ.Tests
dotnet test tests/Seeing.TokenEstimation.Tests
dotnet test tests/Seeing.Agent.WebUI.Tests
```

- [ ] **Step 3: 确认 25 处 `.GetAwaiter().GetResult()` 已全部修复**

```bash
rg "\.GetAwaiter\(\)\.GetResult\(\)" src/ samples/ --include="*.cs" -c
```
Expected: 0 matches（或仅剩设计上必需的，如 `Main()` 中的入口点调用）。

- [ ] **Step 4: 最终 Commit**

```bash
git add -A && git status
git commit -m "chore: 全量验证通过 — 29 项问题全部修复"
```

---

## 修复状态追踪

| 审查问题 | 优先级 | 对应 Task | 状态 |
|---------|--------|---------|------|
| 装饰器链完全不生效 | **P0** | Task 2.1+2.2 | pending |
| TimeoutToolContext 丢失字段 | **P0** | Task 2.0 | pending |
| AGENTS.md 严重过时 | **P0** | Task 1.3 | pending |
| Plugins/Rules 物理残留 | **P0** | Task 1.1 | pending |
| Seeing.Agent.Host 孤立文件 | **P0** | Task 1.1 | pending |
| SessionHookPoints 死代码 | P1 | Task 1.2 | pending |
| ExecutionStateManager 缺 IDisposable | P1 | Task 3.1 | pending |
| SessionForker.CloneMessage 浅拷贝 | P1 | Task 3.2 | pending |
| CountTokens 4 处重复 | P1 | Task 3.3 | pending |
| SummarizingStrategy 同步阻塞 | P1 | Task 3.3 | pending |
| ForkInlineAsync 80% 重复 | P1 | Task 3.4 | pending |
| ISession/ISessionFactory/ISessionLifecycle 旧体系 | P1 | Task 3.5 | pending |
| 内置 Agent 字段不一致 | P1 | Task 4.1 | pending |
| ProviderConfig.Headers 未消费 | P1 | Task 5.1 | pending |
| InMemorySessionStore 缺 CancellationToken | P2 | Task 4.2 | pending |
| SessionArchiver 文件名解析 | P2 | Task 4.2 | pending |
| HMAC 密钥不持久化 | P3 | Task 5.1 | pending |
| 核心模块缺测试 | P2 | Task 6.1 | pending |
| RetryToolDecorator 缺少 IOException | P1 | Task 2.1(Step1) | pending |
| McpClientManager 旧版包装器 | P2 | 验证后确认非死代码 | resolved |
| McpClientManager 同步包装（4处） | P1 | Task 7.1 | pending |
| McpConnectionCoordinator/McpConfigLoader（3处） | P1 | Task 7.2 | pending |
| AgentSelectionResolver/TaskTool（2处） | P1 | Task 7.3 | pending |
| 子项目同步阻塞（ExecutionJobService/Scheduler/Gateway/Acp/WebUI 7处） | P1 | Task 7.4 | pending |
| ServiceCollectionExtensions DI 初始化（3处） | P1 | Task 7.5 | pending |
| Seeing.Session.Tests CPM 不一致 | P2 | Task 7.6 | pending |
| TokenBudget.Tests 缺失 csproj | P2 | Task 7.6 | pending |
| WebUI CPM 禁用（设计如此） | P2 | 已知限制（Blazor） | wontfix |
| WebUI.Tests CPM 禁用（依赖 WebUI） | P2 | 已知限制 | wontfix |

---

## 风险评估

| 风险 | 可能性 | 影响 | 缓解措施 |
|------|--------|------|---------|
| 装饰器链导致工具执行行为变化 | 中 | 高 | Task 2.0 先修 TimeoutToolContext；Phase 2 后全量测试验证 |
| ToolManager 签名变更破坏测试 | 中 | 中 | Task 2.2 Step6 全局搜索测试中的构造调用并更新 |
| ISessionLifecycle 移除影响 ACP | 低 | 中 | Task 3.5 Step0 先 grep 确认引用范围；ACP 走钩子系统不直接依赖 |
| Phase 间文件冲突（ServiceCollectionExtensions 被多 Phase 修改） | 低 | 低 | 按 Phase 顺序执行，每次完整重写该文件的相关区域 |
| IMemoryCache 在非 ASP.NET 环境不可用 | 低 | 低 | CachedToolDecorator 仅在 cache != null 时注册 |
| McpConnectionCoordinator 改用 IAsyncDisposable 影响调用方 | 低 | 中 | Task 7.2 同步修改 McpClientManager.DisposeAsync 调用链 |
| Seeing.Session.Tests 重新启用 CPM 后版本冲突 | 低 | 低 | Task 7.6 先对比版本再修改 |
| 子项目 `.GetAwaiter()` 修改影响异步行为 | 低 | 低 | 都用 `Task.Run` 隔离，行为不变 |
