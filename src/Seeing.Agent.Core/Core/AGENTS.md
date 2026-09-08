# Core Layer - Interfaces & Abstractions

核心接口定义与抽象基类，构成 Seeing.Agent 框架的契约层。

## STRUCTURE

```
Core/
├── Interfaces/           # 核心接口（15+ 文件）
│   ├── IAgent.cs         # Agent 契约 + AgentMode/AgentContext/AgentResult
│   ├── ITool.cs          # Tool 契约 + ToolContext/ToolResult/FileAttachment
│   ├── ISkill.cs         # Skill 契约 + SkillContext/SkillResult/SkillInfo
│   ├── IHook.cs          # Hook 契约 + HookContext/HookResult/HookPoints
│   ├── IRuleEngine.cs    # 权限引擎契约
│   ├── IExtension.cs     # 扩展插件契约
│   ├── IAgentRegistry.cs # Agent 注册表
│   ├── IComponentManager.cs # 组件管理器
│   └── IPermissionChannel.cs # 权限通道
│
├── Abstractions/         # 抽象基类
│   ├── AgentBase.cs      # Agent 基类（配置驱动/代码驱动双模式）
│   ├── SkillBase.cs      # Skill 基类（参数获取/结果构造）
│   └── ToolBase.cs       # Tool 基类（JSON 解析/Success/Failure）
│
├── Models/               # 数据模型
│   ├── AgentDefinition.cs # Agent 定义模型
│   ├── ConfigurationModels.cs # 配置模型
│   └── ConcurrentMetadataStore.cs # 元数据存储
│
├── Detection/            # 检测服务
│   └── LoopDetector.cs   # 循环调用检测（SHA256 哈希）
│
├── Configuration/        # 配置工具
│   └── MergeDeep.cs      # 深度合并算法
│
├── Permission/           # 权限系统
│   └ PermissionCache.cs  # 权限缓存层
│
├── Sessions/             # 会话集成
├── Prompts/              # 动态提示构建
├── Questions/            # 问题管理
├── Todo/                 # Todo 管理
├── Snapshot/             # 文件快照
├── Background/           # 后台任务
└── Events/               # 消息事件类型
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
| Agent 注册 | `Interfaces/IAgentRegistry.cs` | `GetAgentAsync()`, `RegisterAgent()` |
| 组件加载 | `Interfaces/IComponentManager.cs` | `LoadAllAsync()` |
| 循环检测 | `Detection/LoopDetector.cs` | `DetectLoop()` |
| 权限缓存 | `Permission/PermissionCache.cs` | `GetOrAdd()` |
| 配置合并 | `Configuration/MergeDeep.cs` | `Merge()` |

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
| **绕过 LoopDetector** | 可能导致 LLM 无限循环 |

## NOTES

- **接口数量**: 15+ 核心接口
- **AgentBase 双模式**: 配置驱动（`.md` 文件）+ 代码驱动（继承）
- **LoopDetector**: SHA256 参数哈希，3 次警告，5 次终止
- **MergeDeep**: 数组不合并（替换），对象递归合并