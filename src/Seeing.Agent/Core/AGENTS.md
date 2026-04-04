# Core Layer - Interfaces & Abstractions

核心接口定义与抽象基类，构成 Seeing.Agent 框架的契约层。

## STRUCTURE

```
Core/
├── Interfaces/           # 6 个核心接口
│   ├── IAgent.cs         # Agent 契约 + AgentMode/AgentContext/AgentResult
│   ├── ITool.cs          # Tool 契约 + ToolContext/ToolResult/FileAttachment
│   ├── ISkill.cs         # Skill 契约 + SkillContext/SkillResult/SkillInfo
│   ├── IHook.cs          # Hook 契约 + HookContext/HookResult/HookPoints
│   ├── IRuleEngine.cs    # 权限引擎契约
│   └── IExtension.cs     # 扩展插件契约
├── Abstractions/         # 3 个抽象基类
│   ├── AgentBase.cs      # Agent 基类（日志辅助方法）
│   ├── SkillBase.cs      # Skill 基类（参数获取/结果构造）
│   └── ToolBase.cs       # Tool 基类（JSON 解析/Success/Failure）
└── Models/               # 2 个数据模型
    ├── ChatMessage.cs    # 对话消息（Role/Content/Attachments）
    └── ConfigurationModels.cs  # 配置模型（LlmConfig/AgentOptions）
```

## WHERE TO LOOK

| 扩展点 | 文件 | 关键成员 |
|--------|------|----------|
| 实现 Agent | `Abstractions/AgentBase.cs` | `ExecuteAsync()`, `SystemPrompt`, `Model` |
| 实现 Tool | `Interfaces/ITool.cs` | `Id`, `ParametersSchema`, `ExecuteAsync()` |
| 实现 Skill | `Interfaces/ISkill.cs` | `Name`, `Location`, `ExecuteAsync()` |
| 实现 Hook | `Interfaces/IHook.cs` | `HookPoint`, `Priority`, `HookPoints.*` |
| 权限规则 | `Interfaces/IRuleEngine.cs` | `Evaluate()`, `AddRule()` |
| 扩展插件 | `Interfaces/IExtension.cs` | `InitializeAsync()`, `ConfigureServices()` |

## CONVENTIONS（核心层特定）

### 接口命名
- 上下文类：`{领域}Context`（如 `ToolContext`, `SkillContext`）
- 结果类：`{领域}Result`（如 `ToolResult`, `SkillResult`）
- 信息类：`{领域}Info`（如 `SkillInfo`）

### 上下文结构
所有 Context 类统一包含：
```csharp
public string SessionId { get; set; }
public string MessageId { get; set; }
public CancellationToken CancellationToken { get; set; }
```

### Hook 点常量
```csharp
// HookPoints 静态类定义 20+ 常量
public static readonly string ToolBeforeExecute = "tool.before_execute";
public static readonly string ChatBeforeStart = "chat.before_start";
// 格式: {领域}.{事件}
```

### 结果构造（基类提供）
```csharp
// ToolBase
protected ToolResult Success(string title, string output);
protected ToolResult Failure(Exception error);

// SkillBase
protected SkillResult Success(string output);
protected SkillResult Failure(string message);
```

## ANTI-PATTERNS（核心层）

| 禁止 | 原因 |
|------|------|
| **Context 类添加业务逻辑** | Context 应为纯数据容器 |
| **接口添加默认实现** | 保持接口纯净，实现放基类 |
| **Result 类继承层次** | 每个领域独立 Result，无通用基类 |
| **Hook 点字符串硬编码** | 使用 `HookPoints.*` 常量 |
| **AgentMode 扩展新值** | 固定三种：Primary/SubAgent/All |

## 扩展指南

**新增领域接口：**
1. 定义 `I{领域}` 接口继承核心能力
2. 定义 `{领域}Context` 包含 SessionId/MessageId
3. 定义 `{领域}Result` 包含 Success/Error
4. 可选：定义 `{领域}Base` 抽象基类提供辅助方法