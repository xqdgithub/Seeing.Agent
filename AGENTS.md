# Seeing.Agent 项目知识库

**生成时间:** 2026-04-22  
**Commit:** f5e87f4  
**Branch:** master  
**目标框架:** .NET 10.0  
**语言:** C#

---

## 概述

完整的 AI Agent 框架，支持 Skill/Tool/Hook/Rules/MCP 集成。主库为 NuGet 包 (`Seeing.Agent`)，提供 Agent 编排、工具发现、权限控制等核心能力。**独立会话管理包** (`Seeing.Session`) 可单独使用。

---

## 项目结构

```
Seeing.Agent/
├── Seeing.Agent.slnx              # 解决方案（XML 格式，VS 2022 17.13+ 新格式）
├── global.json                    # SDK 版本锁定：10.0.102
├── Directory.Build.props          # 启用中央包管理 (CPM)
├── Directory.Packages.props       # 包版本集中定义（29 个包）
│
├── src/
│   ├── Seeing.Agent/              # 主 NuGet 库（121 个 C# 文件）
│   ├── Seeing.Agent.Plugins/      # Agent 插件实现（11 个内置 Agent）
│   └── Seeing.Session/            # 独立会话管理包（28 个文件）
│
├── tests/
│   ├── Seeing.Agent.Tests/        # 单元测试（多模块覆盖）
│   ├── Seeing.Agent.Plugins.Tests/ # 插件测试
│   └── Seeing.Session.Tests/      # Session 测试
│
├── samples/
│   ├── Seeing.Agent.WebUI/        # Blazor Web 界面示例
│   ├── Seeing.Agent.Tui/          # 终端 UI 示例
│   ├── Seeing.Agent.SpectreTui/   # Spectre.Console TUI 示例
│   └── Seeing.Agent.Host/         # Generic Host 示例
│
├── docs/                          # 集中文档（架构、Gateway、ACP、开发评审）
│   ├── README.md                  # 文档索引
│   ├── architecture/              # 框架设计与 Extension
│   ├── gateway/                   # Gateway 集成
│   ├── acp/                       # ACP 集成
│   ├── development/               # 架构/Provider 评审
│   └── plans/                     # 历史实施计划（维护者参考）
│
└── CommandLineUtils/              # [外部] McMaster 命令行库
└── command-line-api/              # [外部] dotnet 命令行 API
```

---

## WHERE TO LOOK

| 任务 | 位置 | 说明 |
|------|------|------|
| 新增 Agent 实现 | `src/Seeing.Agent/Core/Abstractions/AgentBase.cs` | 继承基类，支持配置驱动/代码驱动 |
| 新增 Tool 工具 | `src/Seeing.Agent/Tools/Attributes/ToolAttributes.cs` | 使用 `[Tool]` 注解 |
| 扩展生命周期钩子 | `src/Seeing.Agent/Hooks/HookManager.cs` | 实现 `IHookHandler`，20+ 钩子点 |
| 配置权限规则 | `src/Seeing.Agent/Rules/RuleEngine.cs` | `AddRule()` 方法 |
| 连接 MCP Server | `src/Seeing.Agent/MCP/McpClientManager.cs` | `ConnectAsync()`，支持 stdio/HTTP/SSE |
| DI 注册入口 | `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | `AddSeeingAgent()` |
| 会话管理 | `src/Seeing.Session/Management/SessionManager.cs` | 独立包，生命周期管理 |
| 扩展插件开发 | `src/Seeing.Agent/Extensions/ExtensionLoader.cs` | 实现 `IExtension` 接口 |
| 循环检测防护 | `src/Seeing.Agent/Core/Detection/LoopDetector.cs` | 防止 LLM 无限循环 |
| 文件系统安全 | `src/Seeing.Agent/Tools/BuiltIn/FileSystemHelper.cs` | 路径白名单、输出限制 |
| 配置深度合并 | `src/Seeing.Agent/Core/Configuration/MergeDeep.cs` | 递归合并算法 |
| 装饰器链模式 | `src/Seeing.Agent/Decorators/ToolDecoratorRegistry.cs` | 超时→重试→缓存 |
| Agent 插件实现 | `src/Seeing.Agent.Plugins/Agents/*.cs` | Oracle/Metis/Momus/Sisyphus 等 |
| ACP 集成 | `src/Seeing.Agent.Acp/` + `docs/acp/integration.md` | Passthrough 透传 + acp 工具委派，`IAgentExecutionRouter` 路由 |
| 项目文档索引 | `docs/README.md` | 架构、Gateway、ACP、开发评审与历史计划 |
| 框架架构 | `docs/architecture/ARCHITECTURE.md` | 分层设计、配置结构 |
| Extension 开发 | `docs/architecture/EXTENSION.md` | 插件加载与配置层级 |

---

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
| ToolInvoker, HookManager, RuleEngine, SkillManager, McpClientManager, AgentRegistry | Singleton |
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

### 文件系统限制常量
| 限制项 | 默认值 |
|-------|-------|
| 读取行数 | 2000 行 |
| 单行长度 | 2000 字符 |
| 输出字节 | 50KB |
| Grep 匹配 | 100 条 |
| Glob 文件 | 100 个 |

---

## ANTI-PATTERNS（本项目）

| 禁止 | 原因 | 文件位置 |
|------|------|---------|
| **混用路径分隔符** | `\\` 和 `/` 混用破坏跨平台 | `Seeing.Agent.csproj` vs `WebUI.csproj` |
| **WebUI 项目禁用 CPM** | 破坏包版本一致性 | `samples/Seeing.Agent.WebUI.csproj` |
| **解决方案未包含所有项目** | 缺失 4 个项目引用 | `Seeing.Agent.slnx` |
| **静默吞异常** | `Activator.CreateInstance` 失败需记录 | `Tools/Discovery/ReflectedTool.cs` |
| **工具 ID 冲突静默覆盖** | 最后注册 wins，无警告 | `ToolInvoker.RegisterTool()` |
| **Hook 点字符串硬编码** | 使用 `HookPoints.*` 常量 | 各处调用 |
| **Context 类添加业务逻辑** | Context 应为纯数据容器 | 设计约束 |
| **权限通道未配置** | 默认拒绝所有，需显式配置 | `ServiceCollectionExtensions.cs` |

---

## 已知问题（`docs/development/REVIEW.md` + `docs/development/PROVIDER_FLOW_REVIEW.md`）

| 优先级 | 问题 | 影响 |
|--------|------|------|
| **P0** | OpenAI `request.Model` 未参与实际选模 | 多模型路由失效 |
| **P1** | `ModelScope` 缺少 `provider` 字段 | `GetClient` 失败 |
| **P1** | HookManager 缺少移除能力 | 无法动态卸载钩子 |
| **P2** | ProviderConfig 字段未消费 | Timeout/Headers 未使用 |
| **P3** | MCP 集成占位实现 | `ModelContextProtocol.Core` 集成待完善 |

---

## TODO 标记位置

| 文件 | 内容 |
|------|------|
| `Extensions/ExtensionLoader.cs` | NuGet 下载功能未实现 |
| `Core/ComponentManager.cs` | Markdown 规则解析待完善 |

---

## 命令

```bash
# 构建（使用中央包管理）
dotnet build Seeing.Agent.slnx

# 测试（xUnit + Moq + FluentAssertions）
dotnet test tests/Seeing.Agent.Tests
dotnet test tests/Seeing.Session.Tests

# 打包 NuGet
dotnet pack src/Seeing.Agent -c Release
dotnet pack src/Seeing.Session -c Release

# 运行示例
dotnet run --project samples/Seeing.Agent.WebUI
dotnet run --project samples/Seeing.Agent.SpectreTui

# 代码格式化（CommandLineUtils 子项目）
pwsh -File CommandLineUtils/build.ps1 -ci
```

---

## NOTES

- **测试框架**: xUnit 2.9 + Moq 4.20 + FluentAssertions 6.12
- **SDK 版本**: 10.0.102，rollForward: minor
- **中央包管理**: 启用，29 个包版本集中管理
- **依赖风险**: `ModelContextProtocol.Core` 1.0.0 MCP 集成待完善
- **外部子仓库**: `CommandLineUtils/`、`command-line-api/` 非本项目代码
- **日志规范**: 结构化日志 `{PropertyName}` 格式，级别 Info(Debug)
- **装饰器链**: 超时（最外层）→ 重试 → 缓存（最内层）
- **循环检测**: SHA256 参数哈希，连续 3 次警告，5 次终止
- **解决方案格式**: `.slnx` 是 VS 2022 17.13+ 新格式，兼容性有限
- **测试命名**: `{方法}_{场景}_Should{预期结果}` 或 AAA 注释分区