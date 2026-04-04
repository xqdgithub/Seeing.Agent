# Seeing.Agent 项目知识库

**生成时间:** 2026-04-04  
**目标框架:** .NET 10.0  
**语言:** C#

---

## 概述

完整的 AI Agent 框架，支持 Skill/Tool/Hook/Rules/MCP 集成。主库为 NuGet 包 (`Seeing.Agent`)，提供 Agent 编排、工具发现、权限控制等核心能力。

---

## 项目结构

```
Seeing.Agent/
├── Seeing.Agent.slnx              # 解决方案（XML 格式，VS 2022 17.13+）
├── src/Seeing.Agent/              # 主 NuGet 库（24 个源文件）
│   ├── Core/                      # 接口 + 抽象 + 模型
│   ├── Tools/                     # 工具系统（注解发现 + 调用器）
│   ├── MCP/                       # MCP Server 集成
│   ├── Hooks/                     # 生命周期钩子
│   ├── Rules/                     # 权限规则引擎
│   ├── Skills/                    # 技能管理
│   ├── Sessions/                  # 会话管理
│   └── Extensions/                # DI 扩展入口
├── tests/Seeing.Agent.Tests/      # 单元测试（30 个用例）
├── samples/Seeing.Agent.Sample/   # 示例程序（ACP 协议演示）
└── docs/                          # 架构文档
```

---

## WHERE TO LOOK

| 任务 | 位置 | 说明 |
|------|------|------|
| 新增 Agent 实现 | `src/Seeing.Agent/Core/Abstractions/AgentBase.cs` | 继承基类 |
| 新增 Tool 工具 | `src/Seeing.Agent/Tools/Attributes/ToolAttributes.cs` | 使用 `[Tool]` 注解 |
| 扩展生命周期钩子 | `src/Seeing.Agent/Hooks/HookManager.cs` | 实现 `IHookHandler` |
| 配置权限规则 | `src/Seeing.Agent/Rules/RuleEngine.cs` | `AddRule()` 方法 |
| 连接 MCP Server | `src/Seeing.Agent/MCP/McpClientManager.cs` | `ConnectAsync()` |
| DI 注册入口 | `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | `AddSeeingAgent()` |
| 理解架构设计 | `docs/ARCHITECTURE.md` | 完整设计文档 |
| 查看代码问题 | `docs/REVIEW.md` | 评审报告 |

---

## CONVENTIONS（仅非标准）

### 命名约定
- 接口前缀 `I`，抽象类后缀 `Base`，结果类后缀 `Result`
- Hook 点命名：`{领域}.{事件}` 格式（如 `tool.before_execute`）
- 异步方法统一 `Async` 后缀

### DI 生命周期
| 服务 | 生命周期 |
|------|----------|
| HookManager, RuleEngine, SkillManager, ToolInvoker, McpClientManager | Singleton |
| SessionManager | Scoped |

### 注解发现（非标准）
```csharp
[Tool("获取天气信息")]
public static async Task<string> GetWeather(
    [ToolParam("城市名")] string city,
    [Required] DateTime date) { }
```

### 配置文件命名
- 选项类后缀 `Options`（如 `SeeingAgentOptions`）
- 配置节名称 `SeeingAgent`

---

## ANTI-PATTERNS（本项目）

| 禁止 | 原因 |
|------|------|
| **混用 Microsoft.Extensions 包版本** | 当前 v9/v10 混用，建议统一 |
| **在 ReflectedTool 中静默吞异常** | `Activator.CreateInstance` 失败时需记录 |
| **使用 `.slnx` 解决方案格式** | VS 2022 17.13+ 新格式，兼容性有限 |
| **缺失 `global.json`** | 无 SDK 版本锁定，构建可能不一致 |
| **缺失 `Directory.Packages.props`** | 未使用中央包管理 |

---

## 命令

```bash
# 构建
dotnet build Seeing.Agent.slnx

# 测试
dotnet test tests/Seeing.Agent.Tests

# 打包 NuGet
dotnet pack src/Seeing.Agent -c Release

# 运行示例
dotnet run --project samples/Seeing.Agent.Sample
```

---

## NOTES

- **测试框架**: xUnit + Moq + FluentAssertions
- **依赖风险**: `ModelContextProtocol.Core` 1.2.0 MCP 集成待完善
- **ACP 集成**: 示例程序包含 ACP 协议支持（`Acp.NetCore`）
- **已知问题**: ToolInvoker 与 ToolRegistry 职责重叠（见 `docs/REVIEW.md`）
- **日志规范**: 结构化日志 `{PropertyName}` 格式，级别 Info(Debug)