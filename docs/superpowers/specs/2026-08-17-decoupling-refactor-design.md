# Seeing.Agent 解耦重构设计：Abstractions 契约包 + Agent 纯数据化 + Todo 端口-适配器

**日期:** 2026-08-17
**状态:** 已实施（2026-08-18 完成解耦重构 + 2026-08-27 完成会话归属/执行引擎下沉/Task 事件清理）

---

## 1. 背景与动机

当前架构存在三类问题：

### 1.1 协议层反向依赖领域层
`Seeing.Gateway`（通道协议包）反向引用 `Seeing.Agent` 主库，使用主库的 `IMessageEvent`/`MessageEventType`/`TokenUsage`/`ChatRole`/`ChatMessage` 及全部具体事件类型（`GatewayEventMapper.cs:1-2,150,158`、`GatewayTokenUsage.cs:1`、`GatewayEvent.cs:45`）。

### 1.2 扩展包依赖主库具体类
`Seeing.Agent.Memory` / `Acp` / `Gateway` / `Scheduler` 等扩展包直接引用主库具体类（`ExtensionContext` 持有 `HookManager`/`ToolManager`/`McpClientManager` 具体类），无法独立编译、测试、发布。

### 1.3 Agent 体系名不副实 + 双重间接层
- `IAgent`（17 个可变属性）与 `AgentDefinition` 完全重复，且承载运行时状态（Status/Disabled）
- `AgentBase`（289 行双模式基类）在 src 内**零子类**（tests 有 3 个测试桩子类 + 9 处 `Mock<IAgent>`，Phase 2 需同步改造）——"代码驱动模式"是死代码
- 执行入口挂在 `IAgent` 实例上，真正干活的是 847 行的 `AgentExecutor`（无接口）
- `IAgentExecutionRouter` 名为"路由"实为执行入口，语义误导

### 1.4 Todo 双轨 + Session 封装破坏
- `TodoManager`（281 行文件持久化）**零消费方**（孤儿代码）
- `TodoWriteTool` / `AgentExecutor` 用魔法字符串键 `"todos"` 直接调用 `ISessionManager.Get/SetContext`（Session 键值后门）读写会话内部状态
- `TodoReadTool` 同样用魔法键读（TodoReadTool.cs:266），且全库无注册、无消费方（孤儿代码）
- `AcpTool.cs:236-238` 同样用三个魔法键做透传
- `AgentExecutor` 对 `ISessionManager` 的使用：读 Todo（AgentExecutor.cs:823-833）+ 读 SubAgent 会话权限快照（AgentExecutor.cs:733，类型化属性 `PermissionSnapshot`，非魔法键）

### 1.5 接口命名混乱
30+ 公共接口的后缀（Manager/Store/Registry/Service/Provider）无统一语义，存在 `IProviderManager` vs `IProviderRegistry`、三个 Config/Model Manager 并存、`IAgentManager` 26 方法违反 ISP 等问题。

### 1.6 Providers 配置存储位置混乱
`Providers` 节虽声明为 UserOnly，但物理上仍存在 `seeing.json` 中，依赖 `EnsureProvidersUserOnlyAsync` 加载时收敛 + `SaveSectionAsync` 写后清理项目级残留（双保险防"删除复活"），且历史上误写的 `ProviderModels` 节需额外吸收迁移（已移除解析，仅保留清理）。Provider 连接与模型目录本应是与项目无关的用户级私有配置，混在 `seeing.json` 造成：
- 用户级/项目级 `seeing.json` 中 `Providers`/`ProviderModels` 键的残留与清理逻辑（`RemoveSeeingAgentKeysAsync`）持续维护成本
- `DefaultProvider` 节点冗余——默认模型（`DefaultModel`）的模型配置已含 `Provider` 引用，该节点无独立价值

---

## 2. 目标与非目标

### 2.1 目标
1. 建立三层依赖规范：**原语层 → 主库层 → 扩展层**，依赖只能向下
2. 新建 `Seeing.Agent.Abstractions` 契约包（零实现、零依赖），迁移全部公共表面
3. Agent 体系纯数据化：删除 `IAgent`/`AgentBase`，`AgentDefinition` 纯数据，`IAgentExecutor` 统一执行入口
4. Context 类纯数据化与瘦身
5. Todo 体系：删除 `TodoManager` 孤儿代码，引入 `ITodoStore` 端口-适配器
6. 消除 `AgentExecutor`/`TodoWriteTool`/`AcpTool` 对 Session 内部存储细节（Get/SetContext）的依赖
7. 确立命名规范（后缀即职责）并修正违规接口
8. 拔除 `Seeing.Gateway` 对主库的 ProjectReference
9. Providers 配置独立化：迁移到用户级 `providers.json`，删除 `seeing.json` 中的收敛/清理逻辑与 `DefaultProvider` 节点

### 2.2 非目标（本次不做）
| 项 | 原因 |
|----|------|
| Hook 5 模式增强（Serial/Waterfall） | 涉及 HookManager 执行引擎改造 + 全部调用点，独立第二步 |
| `IPermissionService` 6 个 Evaluate 方法合并 | 显式方法对调用方友好，列为后续可选优化 |
| 主库大领域拆包（Llm/MCP 独立成包） | 成本极高，先通过 Abstractions 划定边界，后续按需拆 |
| NuGet 插件包下载实现 | 独立功能开发，非本次解耦范围 |
| 插件运行时动态管理（热更） | 依赖可逆注册，后续阶段 |

**注**：§3 的"扩展层绝不依赖主库具体类"为**最终目标态**；本次实施范围以 §8 为准（扩展包→主库引用分阶段移除，本次只强制移除 Seeing.Gateway 一处）。

---

## 3. 依赖方向规范

```
原语层  Seeing.Session / Seeing.ConfigSchema / Seeing.Gateway / Seeing.Agent.Abstractions
          ↑  零 Agent 依赖，只含接口 + DTO + 事件声明 + 注解 + 常量
主库层  Seeing.Agent
          ↑  实现 Abstractions，只依赖原语层
扩展层  Seeing.Agent.Memory / Acp / Gateway / Scheduler / TokenBudget
          ↑  只依赖 Abstractions（+ 原语层），绝不依赖主库具体类
宿主层  samples/Seeing.Agent.WebUI / Cli / Server   组装一切
```

**强制性规则**（写入 AGENTS.md）：
1. 依赖只能向下，禁止反向引用
2. Abstractions 包禁止添加实现逻辑（只放接口/DTO/注解/常量/事件声明）
3. 扩展包禁止引用主库具体类，只能引用 Abstractions 接口

---

## 4. Seeing.Agent.Abstractions 包

### 4.1 包定位
- 路径：`src/Seeing.Agent.Abstractions/`
- 命名空间：`Seeing.Agent.Abstractions.*`（全部迁移类型换新命名空间）
- csproj：**零 ProjectReference（豁免：允许引用原语层 `Seeing.Session`）**——`AgentDefinition.BudgetConfig`（AgentDefinition.cs:126）依赖 Session 包的 `TokenBudgetConfig`（Session/Core/Budget/TokenBudgetConfig.cs:6），按 §3 层级规范原语层同级可引用；允许依赖 `System.Text.Json`（BCL）、`Microsoft.Extensions.Logging.Abstractions`（纯接口包）
- NuGet 包名：`Seeing.Agent.Abstractions`

### 4.2 目录结构与迁移清单

```
src/Seeing.Agent.Abstractions/
├── Events/
│   ├── IMessageEvent.cs          # ← Core/Events/MessageEventTypes.cs:109
│   ├── MessageEvents.cs          # ← 具体事件类型（StreamCompleteEvent/StreamDeltaEvent/
│   │                             #    LoopCancelledEvent/TodoUpdateEvent 等）
│   ├── MessageEventType.cs       # ← MessageEventTypes.cs:8 枚举
│   ├── LoopPhase.cs              # ← MessageEventTypes.cs:91
│   └── TokenUsage.cs             # ← Llm/LlmModels.cs:178（class → record）
├── Hooks/
│   ├── IHookManager.cs           # ← Core/Hooks/IHookManager.cs（原样）
│   ├── IHookHandler.cs           # ← Core/Hooks/IHookHandler.cs
│   ├── HookPayload.cs / HookResult.cs / HookSpec.cs / HookPolicy.cs
│   ├── HookDataContract.cs / DataField.cs
│   └── HookRegistry.cs           # ← 224 行纯静态常量，整个迁移
├── Tools/
│   ├── ITool.cs / ToolCategory.cs / ToolContext.cs
│   ├── IToolManager.cs           # 新建（注册/执行/查询/禁用/反注册）
│   ├── ToolAttributes.cs         # [Tool]/[ToolParam]/[Required]/[ToolParamType]
│   ├── ToolResult.cs / FileAttachment.cs
│   └── IToolEventSink.cs / IToolMetadataSink.cs   # 新建（替代委托字段）
├── Agents/
│   ├── AgentDefinition.cs        # 纯数据化（删除 AgentFactory/FromAgent）
│   ├── AgentContext.cs           # 纯数据化（见 §6.2）
│   ├── AgentMode.cs / AgentStatus.cs（删除 RequiresFactory）
│   ├── IAgentExecutor.cs         # 新建（统一执行入口）
│   ├── IAgentRegistry.cs         # 拆分后（注册/查询/注销/权限筛选）
│   ├── IAgentRuntimeManager.cs   # ← 保留（默认 Agent/模型绑定/会话级覆盖，见 §6.1）
│   ├── IAgentStore.cs            # ← 保留（纯存取，Registry 底层存储）
│   └── ModelReference.cs         # ← AgentModels.cs:139
├── Extensions/
│   ├── IExtension.cs             # 重新设计（见 §7.1）
│   ├── IAgentExtension.cs / IToolExtension.cs / IHookExtension.cs
│   ├── IProviderExtension.cs / IMcpExtension.cs / ISkillPathExtension.cs / ICommandExtension.cs
│   ├── ExtensionContext.cs       # 服务改接口引用
│   ├── ExtensionMeta.cs          # State → enum ExtensionLoadState
│   └── IExtensionManager.cs      # 新建（对齐 ExtensionManager 具体类）
├── Llm/
│   ├── ChatMessage.cs / ChatRole.cs      # ← LlmModels.cs:21,10
│   │                                     #   依赖链一：ChatMessage → ToolCall → FunctionCall
│   │                                     #   依赖链二：TodoUpdateEvent → TodoItem（独立链）
│   ├── ToolCall.cs / FunctionCall.cs     # ← LlmModels.cs:83,132
│   ├── ChatContentPart.cs        # ← Llm/ChatContentPart.cs:9
│   ├── ILlmProvider.cs / IProviderRegistry.cs / IConfigurableLlmProvider.cs
│   ├── ProviderConfig.cs / ProviderInfo.cs / ModelType.cs / ModelConfig.cs
│   │                              # 注：ModelType 有两处定义（Llm/ModelType.cs:6 与
│   │                              #     Core/Models/ConfigurationModels.cs:45），两处均迁移
│   ├── ModelRef.cs               # ← 留在主库！static class 含 Format/Parse/IsKnownProvider
│   │                             #    实现逻辑（Llm/ModelRef.cs:7-60），违反零实现约束
│   └── ProvidersChangedEventArgs.cs
├── Mcp/
│   ├── IMcpManager.cs + IMcpStatusProvider.cs / IMcpController.cs / IMcpConfigManager.cs
│   └── McpServerConfig.cs 等 DTO
├── Permissions/
│   ├── IPermissionService.cs / IPermissionChannel.cs / IRuleEvaluator.cs
│   ├── PermissionRuleEntry.cs / PermissionContext.cs（**仅 DTO**——GenerateHmacKey/ComputeIntegrityHash/
│   │   FromAgentContext/CreateSubAgentContext/VerifyIntegrity/PermissionDelegationException 留主库
│   │   PermissionIntegrity.cs，见 Phase 0/实施记录）/ PermissionResult.cs
│   ├── PermissionKind.cs            # ← Core/Permission/PermissionKind.cs（同文件含 PermissionKind/
│   │                                #    PermissionEffect/ConditionLogic/FileOperation 4 枚举）
│   └── AgentPermissionPolicy.cs
├── Skills/
│   ├── ISkill.cs / SkillContext.cs / SkillResult.cs / SkillInfo.cs
│   └── ISkillManager.cs          # 新建（现有 SkillManager 无接口）
├── Commands/
│   └── ICommandRegistry.cs + ICommand.cs 等 DTO
├── Configuration/
│   ├── PluginSpec.cs             # DTO 部分（JSON 转换器留主库或随迁）
│   └── ConfigLevel.cs            # ← Core/Configuration/WorkspaceProvider.cs:102（IAgentRuntimeManager 依赖）
├── Todo/
│   ├── ITodoStore.cs             # 新建（LoadAsync/SaveAsync）
│   └── TodoItem.cs               # ← Core/Todo/TodoItem.cs（同文件含 TodoStatus/TodoPriority/TodoList）
└── Components/
    ├── IComponentManager.cs / IComponentLoader.cs / ComponentLoadResult.cs
    └── ComponentType.cs          # 重新设计：枚举 → 开放字符串（见 §6.5）
```

**留在主库的**：ToolManager/HookManager/ExtensionManager/AgentManager 等全部实现、ToolDiscovery/ReflectedTool、装饰器链、内置 Agent/Tool、UnifiedConfigManager、MCP 客户端、AgentExecutor。

### 4.3 纪律
- 接口不带默认实现（C# 8 DIM 仅 `IExtension` 元数据可保留默认值）
- Context 类零业务逻辑
- 迁移不改 API 形状（除明确重新设计的项）

---

## 5. 命名规范（写入 AGENTS.md）

### 5.1 后缀即职责

| 后缀 | 职责 | 判断标准 |
|------|------|---------|
| Store | 纯数据存取 | 无业务规则、无生命周期，只有 Load/Save/Get/Set |
| Registry | 集合管理 | 注册/查询/注销条目，不含执行与生命周期 |
| Manager | 生命周期+编排 | 管理生命周期/状态/配置，可组合 Store/Registry/Service |
| Service | 业务能力入口 | 请求-响应式操作，无注册表语义 |
| Provider | 能力适配器 | 可插拔实现 |
| Executor | 执行引擎 | 定义+上下文 → 事件/结果流 |
| Channel | 通信通道 | 请求/审批通道 |
| Loader | 加载器 | 从源加载组件 |
| Sink | 单向出口 | 执行器向工具提供的只写能力出口（事件推送/元数据回写），不可反向调用 |

层级：`Store < Registry < Manager`。接口名 = 实现类名去 I。

### 5.2 现有接口处理

| 接口 | 处理 |
|------|------|
| ITodoManager | **删除**（孤儿代码） |
| IProviderManager / IProviderRegistry | 实施时审查：职责重叠则合并到 IProviderRegistry（§6.7 已先行删除其 `GetDefaultProvider`/`SetDefaultProviderAsync`） |
| IModelManager / IModelConfigManager / IAgentConfigManager | 实施时审查三者职责边界 |
| IAgentManager | **删除**（26 方法 ISP 违反，职责由拆分后的接口承担） |
| IAgentExecutionRouter | **删除**（职责并入 IAgentExecutor） |

### 5.3 新接口命名（最终）

| 接口 | 后缀 | 依据 |
|------|------|------|
| ITodoStore | Store | TodoList 存取 |
| IAgentExecutor | Executor | 执行引擎 |
| IToolManager | Manager | 注册+执行+过滤编排 |
| ISkillManager | Manager | 技能目录管理 |
| IExtensionManager | Manager | 插件生命周期管理（对齐 ExtensionManager） |

---

## 6. 领域重构设计

### 6.1 Agent 体系（删除比新增多）

**删除**：`IAgent`、`AgentBase`、`AgentDecorator`（抽象装饰器，零子类死代码）、`AgentStatus.RequiresFactory`、`IAgentExecutionRouter`、`AgentDefinition.AgentFactory`、`AgentDefinition.FromAgent(IAgent)`（AgentDefinition.cs:153-155）、`AgentManager.AgentInfoWrapper`（AgentManager.cs:860）、`IAgentManager`（26 方法 ISP 违反）

**新建**：
```csharp
// 统一执行入口（输入消息流为显式入参，AgentContext 为环境快照）
public interface IAgentExecutor
{
    IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition definition,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
```

**执行器实现（沿用现有"默认 + 替换式"注册模式，不新建 DefaultAgentExecutor）**：
| 类 | 处理 |
|----|------|
| `NativeAgentExecutionRouter`（28 行纯包装） | **改名** `NativeAgentExecutor`，实现 `IAgentExecutor`（主库注册为默认） |
| `AcpAgentExecutionRouter`（按 Runtime 分流） | **改名** `AcpAgentExecutor`，实现 `IAgentExecutor`（ACP 包提供，替换式注册） |

**AgentExecutor 类自身签名承接**：现有 `AgentExecutor.ExecuteAsync(AgentDefinition, AgentContext, CancellationToken)`（AgentExecutor.cs:78-81，3 参，内部以 `context.History` 为初始消息源 :116,120）→ **签名变更为 4 参** `ExecuteAsync(AgentDefinition, IReadOnlyList<ChatMessage> messages, AgentContext, CancellationToken)`，与 `IAgentExecutor` 对齐；`NativeAgentExecutor` 直接透传；内部 `context.History` 读取（:116,120）改为使用 `messages` 入参。

**AgentDefinition 纯数据化**：删除 `AgentFactory`（AgentDefinition.cs:133）+ `FromAgent`。

**Agent 管理接口最终划分**（修正：不新建 IAgentModelManager/IAgentInstanceFactory）：
| 接口 | 职责 | 处理 |
|------|------|------|
| IAgentRegistry | 注册/查询/注销/权限筛选 | 拆分自现 IAgentRegistry 15 方法（GetAgentsAsync/GetAgentAsync/GetSubAgentsAsync/GetTaskableAgentsAsync/GetPrimaryAgentsAsync/RegisterAgentAsync/UnregisterAgent/HasAgent/GetAccessibleSubAgentsAsync） |
| IAgentRuntimeManager | 默认 Agent（GetDefaultAgentNameAsync/SetDefaultAgentAsync）/模型绑定/会话级模型覆盖（UpdateAgentModelAsync/GetEffectiveModelAsync/SetSessionModelOverrideAsync/SwitchAgentAsync/ModelChanged 事件） | **保留现状**（IAgentRuntimeManager.cs，123 行）——模型管理与默认 Agent 职责已在其内，**不新建** IAgentModelManager；**默认 Agent 方法去重**：仅保留此处，Registry 侧移除 |
| IAgentStore | 纯存取（5 方法） | 保留，作为 Registry 底层存储，进 Abstractions |
| IAgentManager | 配置编辑/MD 覆盖管理等 13 个方法 | **删除**：配置/MD 相关逻辑并入 AgentManager 实现内部，查询类方法由 IAgentRegistry 承接，消费者改注 IAgentRegistry/IAgentRuntimeManager |
| IAgentConfigManager | 待审查 | Phase 0 审查后决定合并去向 |
| GetAgentWithMergedConfigAsync（IAgentRegistry.cs:33） | 无调用方 | **随拆分删除**（如后续需要模型覆盖语义，由 IAgentRuntimeManager 提供） |

**执行链变化**：
```
改造前：外部 → IAgentRegistry.GetOrCreateAgentInstance → IAgent.ExecuteAsync → AgentBase → Router → AgentExecutor
改造后：外部 → IAgentRegistry.GetAgentAsync(definition) → IAgentExecutor.ExecuteAsync(definition, messages, context)
```

**IAgent 其余使用点处理**（全部列明）：
| 使用点 | 处理 |
|--------|------|
| `ToolContext.Agent`（ITool.cs:13） | → `AgentDefinition?` |
| `IExecutionContext.ActiveAgent` | → `AgentDefinition?` |
| `ExtensionManager.GetAgents()` 包装（ExtensionManager.cs:269 `AgentFactory = () => agent`） | 删除——扩展直接提供 `AgentDefinition` |
| `AgentManager.GetOrCreateAgentInstance`（AgentManager.cs:304-316） | 随 IAgentRegistry 拆分移除，调用方改走 IAgentExecutor |
| tests `Mock<IAgent>` 9 处（BackgroundTaskManagerTests.cs:55-331 8 处 + Wave1IntegrationTests.cs:79） | 改为 `Mock<AgentDefinition>` 或直接构造 |
| tests 3 个 AgentBase 桩子类（SchedulerIntegrationTests.cs:440 / SchedulerEngineTests.cs:315 / Wave1IntegrationTests.cs:203） | 改为构造 AgentDefinition + 验证执行器调用 |

**IAgentExecutionRouter 使用点改造**（全部列明）：
| 使用点 | 处理 |
|--------|------|
| `ChatOrchestrator.cs:30,44` / `ExecutionJobService.cs:316` / `HeartbeatJob.cs:23,35` / `AgentJob.cs:22,33` | 注入类型改 `IAgentExecutor`，调用签名补 messages 入参 |
| `ServiceCollectionExtensions.cs:626`（Router 注册行） | 注册 `IAgentExecutor` = NativeAgentExecutor |
| `NativeAgentExecutionRouter.cs` / `AcpAgentExecutionRouter.cs` | 改名（见上表） |
| tests `Mock<IAgentExecutionRouter>` 3 处 Mock + 2 处注册（SchedulerIntegrationTests.cs:408,365 / SchedulerEngineTests.cs:273,291 / ChatOrchestratorCreateSessionTests.cs:141） | 改为 `Mock<IAgentExecutor>` |

### 6.2 AgentContext 纯数据化（AgentModels.cs:12-116）

| 成员 | 处理 |
|------|------|
| `CreateSubAgentContext()`（:81-115） | **移除**——子代理上下文构建逻辑移入 AgentExecutor/子代理创建处 |
| `TotalSteps`/`TotalUsage`（:69-72） | **移除**——运行时状态移到执行结果/事件 |
| `History`（:39） | **移除**——消息输入流改为 `IAgentExecutor.ExecuteAsync` 的显式 `messages` 入参（§6.1）。**删除与执行链切换（AgentExecutor 4 参化）同 Task 落地**（否则中间态不可编译）。使用点分两类：**AgentContext.History（删除对象）**：`AgentExecutor.cs:116,120`（改用入参）、`TaskTool.cs:358`（构造点，改 messages 列表）、`HeartbeatJob.cs:289`、`AgentJob.cs:309`（构造点）、`AcpPassthroughExecutor.cs:69`、`ContentBlockMapper.cs:17`（改接收 messages 入参）、`ExecutionJobService.cs:377,800,850`（改从会话读取后作为入参传入，复用 `BuildHistoryFromSession` :746）；**ChatExecutionContext.History（App 独立类型属性，保留）**：`ExecutionJobService.cs:534`、`ChatExecutionContext.cs:96` 需核对归属 |
| `Services`（:26） | 保留（执行入口需服务定位） |
| 其余标识/环境/权限字段 | 保留 |
| 类型 | record 化候选 |

**消息扩展点预留（Hook 构造/修改消息历史）**：现状 hook 无法修改完整消息列表——LlmService 层仅有 `llm.system_prompt`（Blocking，可改 SystemPrompt）、`chat.params`/`chat.headers`（改请求参数）、`chat.message`（Parallel，只通知）；消息列表是 AgentExecutor 局部变量，无 hook 入口。删除 `History` 不改变此能力面。未来若需支持 hook 构造/修改消息：在执行链内 messages 构建处（`BuildRequestAsync` 前，消息已显式入参、来源单点）预留 `chat.before_llm` Blocking hook（payload 携带 messages 引用，hook 可增删改消息），此扩展点与 History 是否保留无关。

### 6.3 ToolContext 瘦身（ITool.cs:8-31）

| 成员 | 处理 |
|------|------|
| `EmitAsync` 委托（:22） | → `IToolEventSink` 接口 |
| `SetMetadata` 委托（:17） | → `IToolMetadataSink` 接口 |
| `Agent`（IAgent?，:13） | → `AgentDefinition?` |
| SessionId/MessageId/CallId/CancellationToken/PermissionChannel/Services | 保留 |

### 6.4 Todo 体系重构（端口-适配器）

**删除**：`TodoManager`、`ITodoManager`、DI 注册（ServiceCollectionExtensions.cs:578）、`TodoReadTool`（孤儿代码：无注册、无消费方，仅定义 3 处）

**新建**：
```csharp
// Abstractions/Todo/
public interface ITodoStore
{
    Task<TodoList> LoadAsync(string sessionId);
    Task SaveAsync(string sessionId, TodoList todos);
}
```

**实现**（主库）：
- `SessionContextTodoStore`（默认）：桥接 Session 内存 Context，魔法键 `"todos"` 封装在适配器内部，不再泄漏到领域层

**改动点**：
| 文件 | 改动 |
|------|------|
| TodoWriteTool.cs:16,21,71-78 | 注入 `ISessionManager` → `ITodoStore`；`session.SetContext` → `store.SaveAsync` |
| TodoReadTool.cs（:239,244,266） | **删除**（孤儿代码）；其读取场景由 `store.LoadAsync` 承接 |
| AgentExecutor.cs:823-833（LoadTodoList） | 注入 `ISessionManager` → `ITodoStore`；`LoadTodoList` 改 `store.LoadAsync` |
| AgentExecutor.cs:730-741（ResolvePolicy） | **保留 `ISessionManager`**——读的是类型化属性 `session.PermissionSnapshot`（SubAgent 会话权限快照合并），非键值后门，属正常会话协作 |
| AcpTool.cs:236-238 | 三个魔法键透传（parentSessionId/taskDescription/acpBackend）纳入审查：改为类型化会话数据或事件载体 |

**写路径语义**：同步请求-响应（写失败 → 工具 Failure 返回给 LLM）。不用事件做写路径（失去确定性结果、引入竞态）。

**主库 ISessionManager 保留使用点清单**（均为类型化访问，非 Context 键值后门，**保留不动**）：
- `AgentExecutor.ResolvePolicy`（读 PermissionSnapshot）
- `TaskTool` / `TaskStatusTool`（子会话生命周期协作）
- `BackgroundTaskManager`（注入结果到会话）
- `AgentLoopScheduler`（注入合成消息）
- `SessionTitleEnsuring`（读会话 + SetTitleAsync）
- DI 注册行：ServiceCollectionExtensions.cs:456,465

### 6.5 IComponentManager 修正（IComponentManager.cs:6-16）

`ComponentType` 固定枚举与"支持扩展自定义组件类型"矛盾：
- `IComponentLoader.Type` 改 `string`（开放类型键）
- 枚举保留为内置类型常量（`ComponentTypes.Skill` 等）

**全部签名变更点**（15 处）：
| 位置 | 变更 |
|------|------|
| `IComponentManager.cs:24` `ComponentLoadResult.Type` | `ComponentType` → `string` |
| `IComponentManager.cs:44` `IComponentLoader.Type` | `ComponentType` → `string` |
| `IComponentManager.cs:72` `LoadAsync(ComponentType type, ...)` | 参数改 `string` |
| `IComponentManager.cs:77` `GetLoadStatus()` 键 | `Dictionary<string, ComponentLoadResult>` |
| `ComponentManager.cs` 实现类：:30,31,74,103,150,209,251 等 | 字典/排序/3 个 Loader 类型标识同步改 string |

### 6.6 Gateway 解耦

| 项 | 改动 |
|----|------|
| `TokenUsage` | 迁入 Abstractions（Events 或 Llm 命名空间），`GatewayTokenUsage.FromTokenUsage` 引用新位置 |
| `IMessageEvent` | 迁入 Abstractions/Events，`GatewayEvent.cs:45` 注释引用更新 |
| `Seeing.Gateway.csproj:19` | 移除 `Seeing.Agent` 引用 → 添加 `Seeing.Agent.Abstractions` |
| 验证 | `Seeing.Gateway` 编译通过且零 `using Seeing.Agent;` |

### 6.7 Providers 配置独立化（providers.json + 删除 DefaultProvider）

**动机**：见 §1.6。Provider 连接与模型目录是与项目无关的用户级私有配置；独立文件后 `seeing.json` 的收敛/清理逻辑全部删除。

**方案**：复用现有配置节注册机制（`ConfigSectionMeta.FileName` 已支持独立文件，如 `mcp.json`），不改 `ProviderConfig` 格式。

| 项 | 改动 |
|----|------|
| 节注册（UnifiedConfigManager.cs:76） | `Providers` → `new("Providers", "providers.json", ConfigScope.UserOnly, ...)`；**删除** `DefaultProvider` 注册（:70） |
| 文件格式 | `~/.seeing/providers.json` 根级字典 `{ "openai": {...}, "anthropic": {...} }`（与 `mcp.json` 一致） |
| 旧配置处理 | **不迁移**：`seeing.json` 遗留的 `Providers`/`ProviderModels` 键不再解析（静默忽略）；项目级 `providers.json` 不存在、不读取 |

**UnifiedConfigManager 变更**：
- `LoadSectionToCacheAsync`（:599）：新增 UserOnly + 独立文件分支，只加载用户级 `providers.json`；Both 分支维持现状
- **删除** `EnsureProvidersUserOnlyAsync`（:163）及调用（:148）、`AbsorbProviders`/`CloneProvidersDictionary`/`CloneModelConfig` 私有辅助
- `SaveSectionAsync` 中项目级 `seeing.json` 遗留键（`Providers`/`ProviderModels`）的卫生清理（:439-445 + `RemoveSeeingAgentKeysAsync` :188）**保留**——拆分后不再有吸收语义，仅顺手清理旧文件误写键
- `GetFromSeeingAgent`（:783）：删除 `DefaultProvider`/`Providers` case
- `UpdateSeeingAgentProperty`（:821）：删除 `DefaultProvider`/`Providers` case（:825-841）
- `GetSection<T>`（:347）seeing.json 分支判断不变——`providers.json` 自动走缓存反序列化路径

**SeeingAgentOptions**：删除 `Providers`、`DefaultProvider` 属性（遗留键反序列化时被忽略）。

**消费方**（`SeeingAgent.Providers` → `GetSection<Dictionary<string, ProviderConfig>>("Providers")`）：
| 文件 | 改动 |
|------|------|
| `ProviderManager.cs` | `:59-60` 删除 `GetDefaultProvider`；`:206-220` 删除 `SetDefaultProviderAsync`；`:246,259,265` 改 `GetSection`；`:340-353` `LoadProvidersAtLevelAsync` 改用 `GetSectionAtLevelAsync<Dictionary<string, ProviderConfig>>("Providers", level)` |
| `IProviderManager.cs` | 删除 `GetDefaultProvider()`/`SetDefaultProviderAsync()`（全库无消费方） |
| `ModelConfigManager.cs` | `:440,489` 改 `GetSection`；`:521-524` `GetUserProviders()` 直接 `GetSection<...>("Providers")`（UserOnly 即用户级） |

**扩展 Provider 不受影响**：插件经 `IProviderRegistry` 注册的 Provider 与配置驱动 Provider 并存机制不变，`providers.json` 仅承载配置驱动的动态注册。

**测试**：
- 现有 `ProviderManagerTests`/`ProviderManagerCharacterizationTests`/`ModelConfigManagerCharacterizationTests`/`ModelCatalogAggregationTests` 中写 `seeing.json` 的 `Providers` 改为写用户级 `providers.json`（根级字典）
- 删除 ProviderModels 吸收相关用例（`Load_AbsorbsProjectProviderModelsBeforeRemovingProviders`）与 `DefaultProvider` 相关断言
- 新增：项目级 `providers.json` 被忽略；`seeing.json` 遗留 `Providers`/`ProviderModels` 键被忽略；`providers.json` 增删改 Provider 后 `ReloadAsync` 重建

---

## 7. 扩展体系重构

### 7.1 IExtension 拆分（IExtension.cs:98-194）

**删除**：
- `ConfigureServices(IServiceCollection)`（:138，死契约，全库无调用方；**同步删除实现处**：MemoryExtension.cs:31、AcpExtension.cs:43，并确认宿主已显式调用 `AddMemoryServices()`/`AddSeeingAcp()`）
- 7 个 `GetXxx()` 默认空集合方法（:161-191：GetAgents/GetTools/GetHookHandlers/GetMcpServers/GetSkillPaths/GetCommands/GetProviders）

**新建**（按组件类型拆小接口，ExtensionManager 用 `is` 聚合）：
```csharp
public interface IExtension { Id/Version/Name/Description/Target + InitializeAsync + DisposeAsync }
public interface IAgentExtension    { IEnumerable<AgentDefinition> GetAgents(); }
public interface IToolExtension     { IEnumerable<ITool> GetTools(); }
public interface IHookExtension     { IEnumerable<IHookHandler> GetHookHandlers(); }
public interface IProviderExtension { IEnumerable<ILlmProvider> GetProviders(); }
public interface IMcpExtension      { IEnumerable<McpServerConfig> GetMcpServers(); }
public interface ISkillPathExtension { IEnumerable<string> GetSkillPaths(); }
public interface ICommandExtension  { IEnumerable<ICommand> GetCommands(); }
```

**现有扩展适配**（实施时逐一更新）：
- MemoryExtension：实现 IToolExtension + IHookExtension
- AcpExtension：IToolExtension +（Acp 执行器注册）
- GatewayExtension：生命周期
- DeepSeekExtension：IProviderExtension

**ExtensionMeta 修正**：`State` string → `enum ExtensionLoadState { First, Updated, Same }`；`Source` string → `enum ExtensionSource { Npm, File }`

### 7.2 ExtensionContext 接口化

| 成员 | 改造后 |
|------|--------|
| HookManager（具体类） | IHookManager |
| ToolInvoker（具体类） | IToolManager |
| SkillManager（具体类） | ISkillManager |
| McpClientManager（具体类） | IMcpManager |
| PermissionService / AgentRegistry / CommandRegistry | 已是接口，保留 |

### 7.3 扩展注册方式变化

`ExtensionManager.RegisterComponents` 中 `GetAgents()` 包装逻辑（ExtensionManager.cs:269 `AgentFactory = () => agent`）删除——扩展直接提供 `AgentDefinition`（纯数据），执行走 `IAgentExecutor`。

---

## 8. 依赖调整（csproj 变更）

| 项目 | 变更 |
|------|------|
| Seeing.Agent.Abstractions（新建） | 零 ProjectReference；**加入 Seeing.Agent.slnx** |
| Seeing.Agent | + Abstractions（保留 Session/ConfigSchema）；`GlobalUsings.cs:11` 的 `global using TokenUsage = ...` 更新到新命名空间 |
| Seeing.Gateway | - Seeing.Agent；+ Abstractions（保留 ConfigSchema） |
| Seeing.Agent.Memory / Acp / Scheduler / TokenBudget / App | + Abstractions；**最终目标**：扩展包只依赖 Abstractions + 原语层。本次先全部加上 Abstractions 引用并迁移 using，具体类的引用随重构消除 |
| plugs/providers/Seeing.Provider.DeepSeek | + Abstractions（当前 csproj:10 引用主库，见 §11 验证） |
| Seeing.Session / TokenEstimation / ConfigSchema | 不变 |

**注**：扩展包对主库的引用**本次不强制移除**（涉及 Memory/Acp/Gateway 内部大量具体类使用，需在后续可逆注册阶段逐步替换）；本次强制移除的只有 `Seeing.Gateway → Seeing.Agent` 这一处反向引用。

---

## 9. 实施顺序

### Phase 0：接口审查与准备（0.5-1 天）
- 审查 IProviderManager/IProviderRegistry、IModelManager/IModelConfigManager/IAgentConfigManager 三者职责，确定合并/删除
- **全量接口审计**：列出全部 30+ 公共接口，按 §5.1 命名规范逐一出审计结论（含 IToolPermissionPolicy/IMetadataStore/IExecutionPipeline/ISkill 等此前未审的），输出审计表
- 确定 IAgentStore/IAgentRuntimeManager/IAgentRegistry 最终划分（按 §6.1）

### Phase 0a：Providers 配置独立化（0.5 天，独立 PR，不依赖 Abstractions）
按 §6.7 实施：节注册改 `providers.json` + UserOnly；删除 `EnsureProvidersUserOnlyAsync`/`RemoveSeeingAgentKeysAsync`/`DefaultProvider` 节点与 `IProviderManager.GetDefaultProvider`/`SetDefaultProviderAsync`；消费方改 `GetSection`；测试迁移（seeing.json 写 Providers → 写用户级 providers.json）。此步骤先行，为 Phase 0 的接口审查（IProviderManager 瘦身）提供干净的基线。

### Phase 1：建包 + 类型迁移（1-2 天）
1. 创建 `src/Seeing.Agent.Abstractions/` 项目 + 目录结构，**加入 slnx**
2. `git mv` 物理移动类型 + 统一 namespace → `Seeing.Agent.Abstractions.*`（含 ChatMessage/ToolCall/FunctionCall/ChatRole/ChatContentPart/MessageEventType/LoopPhase 依赖链，按 §4.2）
3. 按 §5 命名规范修正接口名
4. 修主库编译错误（批量 using 更新；GlobalUsings.cs:11 更新 TokenUsage 别名）

### Phase 2：核心重新设计落地（4-6 天，工作量已评估）
1. Agent 体系：删除 IAgent/AgentBase/AgentDecorator/IAgentExecutionRouter/AgentFactory/FromAgent/AgentInfoWrapper/IAgentManager；新建 IAgentExecutor（含 messages 入参）+ NativeAgentExecutor/AcpAgentExecutor 改名；AgentDefinition 纯数据化；IAgentRegistry 拆分（默认 Agent 方法移交 IAgentRuntimeManager）
2. 执行链改造：ChatOrchestrator/ExecutionJobService/HeartbeatJob/AgentJob/ServiceCollectionExtensions.cs:626 等 IAgentExecutionRouter 注入点 → IAgentExecutor；8 处 History 使用点改显式 messages 入参
3. Context 重构：AgentContext 纯数据化（移除 CreateSubAgentContext/TotalSteps/TotalUsage/History）；ToolContext 瘦身（IToolEventSink/IToolMetadataSink；Agent → AgentDefinition）
4. Todo：删除 TodoManager/ITodoManager/TodoReadTool；新建 ITodoStore + SessionContextTodoStore；TodoWriteTool/AgentExecutor.LoadTodoList 改用 ITodoStore；AcpTool 魔法键审查
5. 扩展体系：IExtension 拆分；ExtensionContext 接口化；ExtensionMeta 枚举化；四个现有扩展适配（含 Memory/Acp 的 ConfigureServices 实现删除）
6. ComponentType 开放化（15 处签名变更）
7. **测试改造**：tests 中 3 个 AgentBase 桩子类、9 处 Mock<IAgent>、3 处 Mock + 2 处注册 IAgentExecutionRouter 全部更新

### Phase 3：Gateway 解耦（0.5 天）
- 移除 Seeing.Gateway → Seeing.Agent 引用；验证零 `using Seeing.Agent;`

### Phase 4：验证（1 天）
- `dotnet build Seeing.Agent.slnx` 全绿
- `dotnet test` 全部测试通过（tests/Seeing.Agent.Tests 等）
- 新增测试：ITodoStore/SessionContextTodoStore、IAgentExecutor 执行链、命名规范抽样
- 更新 AGENTS.md（依赖方向规范 + 命名规范 + 项目结构）

---

## 10. 风险与缓解

| 风险 | 缓解 |
|------|------|
| 批量 using 更新引入回归 | 迁移不改 API 形状；编译错误即检查点；每 Phase 结束全量构建 |
| 扩展包内部具体类依赖 | 本次不强制移除扩展包→主库引用，Abstractions 接口逐步替换，分阶段推进 |
| IAgent 删除影响面（ToolContext.Agent/IExecutionContext.ActiveAgent/ExtensionManager/AgentManager/AgentDecorator/FromAgent/tests 桩与 Mock） | 已列明全部使用点（§6.1）；改 AgentDefinition? |
| AgentContext 移除 History 影响执行链 | 输入流改 `IAgentExecutor` 显式 messages 入参；8 处 History 使用点已列明（§6.2） |
| 删 AgentBase 后自定义 Agent 能力缺失 | src 零子类（tests 桩子类已列）；自定义行为通过 Hook/工具/子代理组合表达（harness 同哲学）；如未来需要可引入 Executor 自定义（Runtime 扩展） |
| AcpTool 魔法键重构影响 ACP 透传 | 独立小任务，纳入实施计划，ACP 测试覆盖 |
| Phase 2 工作量 | 已按 4-6 天评估（Agent 体系+执行链改造+Context+Todo+扩展+ComponentType+测试改造 7 块），风险表保留余量；超出则拆分为 Phase 2a/2b 两个 PR |

## 11. 验证标准（完成定义）

1. `Seeing.Gateway` 编译通过且零 `using Seeing.Agent;`
2. `dotnet build Seeing.Agent.slnx` 全绿（含 plugs/Seeing.Provider.DeepSeek），全部测试通过
3. `AgentDefinition` 无 `AgentFactory`/`FromAgent`；`IAgent`/`AgentBase`/`AgentDecorator`/`ITodoManager`/`TodoReadTool`/`IAgentExecutionRouter`/`IAgentManager`/`GetAgentWithMergedConfigAsync`/`AgentStatus.RequiresFactory`/`AgentManager.AgentInfoWrapper`/`AgentContext.History`/`TotalSteps`/`TotalUsage`/`CreateSubAgentContext`/`IExtension.ConfigureServices` 在代码库中不存在
4. `TodoWriteTool`/`AgentExecutor.LoadTodoList` 无 `ISessionManager` 注入（`AgentExecutor.ResolvePolicy` 保留，读类型化 PermissionSnapshot）
5. 所有公共接口符合命名规范（代码扫描验证）
6. 扩展包（Memory/Acp/DeepSeek）编译通过（引用 Abstractions）
7. `AgentExecutor.ExecuteAsync` 为 4 参签名（含 `messages`），且全库无 `context.History` 引用
8. `Providers` 节物理存储于 `~/.seeing/providers.json`（项目级不读取）；代码库中不存在 `EnsureProvidersUserOnlyAsync`/`AbsorbProviders`/`DefaultProvider` 节点/`ProviderModels` 解析，仅保留 `SaveSectionAsync` 对项目级 `seeing.json` 遗留键的卫生清理

---

## 12. Phase 0 审查结论

**日期:** 2026-08-18
**范围:** 接口职责审查（§5.2 遗留待审项）+ 全量接口审计表（详见 `docs/superpowers/plans/interface-audit-table.md`）

### 12.1 IProviderManager / IProviderRegistry：职责互补，两个都保留

**成员对比：**

| 成员 | IProviderRegistry | IProviderManager |
|------|------|------|
| GetProviders | ILlmProvider 字典（原始对象） | ProviderInfo 字典（信息视图包装） |
| GetProvider | ILlmProvider? | ProviderInfo? |
| Register / Unregister / UnregisterByOwner | ✓ | ✗（间接经 Registry） |
| GetOwnerExtensionId | ✓ | ✗ |
| ProvidersChanged 事件 | ✓ | ✗ |
| GetDefaultProvider / SetDefaultProviderAsync | ✗ | ✓（按 §6.7 删除） |
| TryGetConfigurable | ✗ | ✓ |
| GetClient / GetClientForModel | ✗ | ✓ |
| TestConnectionAsync | ✗ | ✓ |
| SaveProviderAsync / DeleteProviderAsync | ✗ | ✓ |

**消费方：** IProviderRegistry 注入点 6 处（AgentRuntimeManager.cs:26,65 / ExtensionManager.cs:29,151,223 / ModelConfigManager.cs:13,39 / OptionsProviderEndpointLookup.cs:12,16 / ProviderManager.cs:14,23 / DI 注册 ServiceCollectionExtensions.cs:673）；IProviderManager 注入点 2 处（LlmService.cs:53,63 / DI 注册 ServiceCollectionExtensions.cs:674）。**无重叠消费者。**

**结论：**
1. **两个接口均保留**，职责边界：`IProviderRegistry` = 纯集合管理（Provider 实例的注册/注销/查询/owner 跟踪/变更事件，Registry 后缀合规，进 Abstractions §4.2 Llm/）；`IProviderManager` = 配置驱动编排（Provider 配置持久化、客户端获取、连接测试、ProviderInfo 视图包装，Manager 后缀合规，留主库）。
2. `GetProviders`/`GetProvider` 虽同名但返回类型不同（`ProviderInfo` 视图 vs `ILlmProvider` 原始对象），属 Manager 对 Registry 的合法包装，**不构成重复**。
3. `GetDefaultProvider()`/`SetDefaultProviderAsync()` 全库无消费方（仅 ProviderManager.cs:59-60,206-220 自身实现），按 §6.7 删除（Phase 0a 落地）。
4. 消费方不改注：两个接口消费方独立清晰。

### 12.2 IModelManager / IModelConfigManager / IAgentConfigManager：保留两个门面，删除死接口

**IModelManager : IModelConfigManager（继承关系保留）：**

| 接口 | 职责 | 处置 |
|------|------|------|
| IModelConfigManager | 模型配置读写：目录查询（GetModels/GetModel/GetDefaultModel/GetModelsByProvider/GetEffectiveTypes/GetModelsByType/CanSetAsDefaultModel）+ 持久化（AddModelAsync/UpdateModelAsync/DeleteModelAsync/SaveModelsAsync/SetDefaultModelAsync）+ ModelConfigChanged 事件 | **保留**，进 Abstractions Llm/ |
| IModelManager | 模型解析门面：ResolveNativeModel / ResolveAcpModel / GetSessionModelRef / ApplyModelToSession / SeedSessionModel（配置读写的 13 个成员经继承转发到 ModelConfigManager，ModelManager.cs:81-137 纯委托） | **保留**，留主库 |

**消费方确认：** IModelManager 有 LlmService / ChatOrchestrator / ExecutionJobService / AgentExecutor / AcpExecutionOverrides / GatewaySessionResolver / GatewaySessionService / GatewayHost / AgentRuntimeManager 等大量消费（ResolveNativeModel 4 处、SeedSessionModel 3 处、ResolveAcpModel 2 处、ApplyModelToSession 2 处、GetSessionModelRef 2 处）；IModelConfigManager 有 ProviderManager.cs:22 与 ModelManager 门面包装。

**与 IAgentRuntimeManager 的关系（不重复）：** IAgentRuntimeManager 的模型成员（UpdateAgentModelAsync/GetEffectiveModelAsync/ApplyRuntimeModel/SetSessionModelOverrideAsync/SwitchAgentAsync）是 **Agent 级模型绑定与会话级覆盖的配置管理**（持久化 + 优先级），IModelManager 是**按优先级链（request > session > agent > default）的最终解析器**；IAgentRuntimeManager.cs:13-14 明确注释"有效模型解析委托 ResolveNativeModel"——两者互补，职责清晰。

**IAgentConfigManager：删除（孤儿接口）。** 全库**零实现类、零消费方**（唯一引用即定义自身 Configuration/IAgentConfigManager.cs:9）；其能力（AgentEditModel / AgentMdInfo / MD 配置编辑）由 `IAgentManager`（AgentManager.cs:323-822）承担，而 IAgentManager 按 §6.1 删除时配置/MD 逻辑并入 AgentManager 实现内部。**§5.2 中「IAgentConfigManager 待审查」一项至此定案：删除，不进 Abstractions。**

### 12.3 全量接口审计表

共 **135** 个公共接口（`src` 全量扫描去重），审计结论汇总：

| 处置 | 数量 | 明细 |
|------|------|------|
| 保留 | 120 | 主库 61 + 扩展包/原语层 59（本次不动） |
| 删除 | 5 | IAgent、IAgentConfigManager、IAgentExecutionRouter、IAgentManager、ITodoManager |
| 重新设计 | 1 | IExtension（§7.1 拆分） |
| 改名 | 1 | ISessionTitleEnsuring → ISessionTitleService（建议） |
| 审查中 | 7 | IAgentGenerator / IAgentLoopScheduler / ICommandDiscoveryInitializer / IExecutionPipeline（待 §5.1 补充 Generator/Scheduler/Initializer/Pipeline 后缀）+ IEvaluateMemoryAsync（Memory 包，低优先） |

**此前未审接口的核对结论：**
- `IToolPermissionPolicy`：**保留**（Policy 语义清晰，单方法 Evaluate，资源级权限策略，有真实评估场景）
- `IMetadataStore`：**保留**（纯 K-V 存取，Store 后缀完全合规，消费：DefaultExecutionContext.Metadata + DI 注册）
- `IExecutionPipeline`：**审查中**（有实现 ExecutionPipeline + DI 注册非孤儿；Pipeline 后缀待命名规范补充）
- `ISkill`：**接口不存在**（ISkill.cs 仅含 SkillInfo 数据类；设计文档 §4.2 的「ISkill.cs」条目实为 SkillInfo，实施时以 SkillInfo.cs 命名迁移）
- `ISessionEventBus`：**保留**（TaskTool 注入 + App 层 ExecutionSessionEventBus 适配，Bus 语义清晰）
- `ITaskEventProjector`：**已删除**（2026-08-27 会话归属重构：Task 事件体系整体移除，Task 卡片由 UI 从工具调用归纳）

**Phase 0a/1/2 执行说明：** 命名规范修订（§5.1 补充 Generator/Scheduler/Initializer/Pipeline 后缀）与「审查中」项定案放 Phase 2 前；`IAgentConfigManager` 删除随 §6.1 IAgentManager 删除任务一并落地（该接口无实现无消费，零改动成本）。

---

## 13. 实施记录

**完成日期:** 2026-08-18
**实施计划:** `docs/superpowers/plans/2026-08-17-decoupling-refactor.md`（Task 1-22 全部完成）

### Phase 0 接口审查

| 任务 | 内容 | Commit |
|------|------|--------|
| Task 1-3 | IProviderManager/IProviderRegistry 职责结论 + 模型管理三接口审查 + 全量接口审计表 | `0c09d3f` |

### Phase 1 建包 + 类型迁移

| 任务 | 内容 | Commit |
|------|------|--------|
| Task 4 | 创建 Seeing.Agent.Abstractions 契约包项目骨架 | `d5d5900` |
| Task 5 | 迁移 Hooks 契约组 | `26d81bc` |
| Task 6 | 迁移 Todo/Permissions/ConfigLevel，新建 ITodoStore | `d98a37e` |
| Task 7 | 迁移 Events/Llm 基础类型，TokenUsage 改 record | `8cc96b0` |
| Task 8 | 迁移 Agents DTO，AgentDefinition 纯数据化 | `461a1c6` |
| Task 9 | 迁移 Llm/Mcp 契约组 | `38f7579` |
| Task 10 | 迁移 Tools 契约，新建 IToolManager 与 Sink 接口 | `db08d4c` |
| Task 11 | 拆分 IExtension，新建扩展契约组 | `f98a8b2` |
| Task 12 | 迁移 Skills/Commands/Configuration/Components，ComponentType 开放化 | `397061e` |
| Task 13 | 全库批量修复混合命名空间 using，Phase 1 完成 | `de698a8` |

### Phase 2 核心重新设计

| 任务 | 内容 | Commit |
|------|------|--------|
| Task 14 | 删除 IAgent/AgentBase/AgentDecorator，新建 IAgentExecutor 契约 | `db29b51` |
| Task 15 | AgentExecutor 4 参化，删除 AgentContext.History，执行器改名接线 | `ad6fb8d` |
| Task 16 | IAgentRegistry 拆分，删除 IAgentManager | `2b80a73` |
| Task 17 | ToolContext 委托字段替换为 IToolEventSink/IToolMetadataSink 接线 | `21c224a` |
| Task 18 | Todo 端口-适配器化（ITodoStore），删除 TodoManager/TodoReadTool 孤儿 | `b31fe83` |
| Task 19 | AcpTool 魔法键透传改为类型化 Metadata | `59a04c8` |

### Phase 3 Gateway 解耦

| 任务 | 内容 | Commit |
|------|------|--------|
| Task 20 | Seeing.Gateway 移除主库依赖，仅依赖 Abstractions（协议层独立） | `8ea4efa` |

### Phase 4 验证收尾

| 任务 | 内容 | Commit |
|------|------|--------|
| Task 21 | 全量验证通过，收尾 §11 验收合规（清理 RequiresFactory/ConfigureServices/CreateSubAgentContext） | `2555dd6` |
| Task 22 | 文档与规范落地（依赖方向 + 命名规范 + 结构树 + 已知问题勾选） | 本任务 |

**Task 事件体系移除（2026-08-27 会话归属重构）**：Task 事件（TaskStartedEvent/TaskProgressEvent/TaskCompletedEvent/TaskFailedEvent/SubAgentEvent）+ TaskEventProjector + TaskSessionProjector + ITaskEventProjector + TaskProjectionContext + BackgroundTaskManager（含 IBackgroundTaskManager/IBackgroundTaskProgress/BackgroundTaskInfo/BackgroundTaskProgress）全部删除；SessionStreamEventApplier 删除；TaskTool 改为创建普通子会话走 ExecutionJobService；TaskStatusTool 查询执行状态；UI Task 卡片从工具调用事件归纳。详见 `fdd0437` 提交。

### 验证结论（§11 逐条）

1. **Seeing.Gateway 编译通过且零主库 using**：✓（`dotnet build src/Seeing.Gateway -v q` 0 错误；正则 `using Seeing\.Agent\.(?!Abstractions)` 零匹配）
2. **全量构建/测试绿**：✓（`dotnet build Seeing.Agent.slnx` 0 错误；`dotnet test` 全绿：Session.Tests 97/97 + Agent.Tests 765/765 + WebUI.Tests 69/69 + Gateway.Tests 31/31 + Acp.Tests 45/45）
3. **删除项不存在**：✓（`AgentFactory`/`FromAgent`/`IAgent`/`AgentBase`/`AgentDecorator`/`ITodoManager`/`TodoReadTool`/`IAgentExecutionRouter`/`IAgentManager`/`GetAgentWithMergedConfigAsync`/`AgentStatus.RequiresFactory`/`AgentManager.AgentInfoWrapper`/`AgentContext.History`/`TotalSteps`/`TotalUsage`/`CreateSubAgentContext`/`IExtension.ConfigureServices`/`ITaskEventProjector`/`TaskEventProjector`/`TaskProjectionContext`/`TaskSessionProjector`/`BackgroundTaskManager`/`IBackgroundTaskManager`/`IBackgroundTaskProgress`/`BackgroundTaskInfo`/`BackgroundTaskProgress`/`SessionStreamEventApplier`/`TaskStartedEvent`/`TaskProgressEvent`/`TaskCompletedEvent`/`TaskFailedEvent`/`SubAgentEvent`/`MessageEventType.TaskStarted`/`MessageEventType.TaskProgress`/`MessageEventType.TaskCompleted`/`MessageEventType.TaskFailed`/`MessageEventType.SubAgent`/`AppEvents.TaskStarted`/`AppEvents.TaskProgress`/`AppEvents.TaskCompleted`/`AppEvents.TaskFailed`/`AppEvents.SubAgent` 全部清零；残留仅为注释引用与无关接口 `IGatewayChannelPlugin.ConfigureServices`）
4. **TodoWriteTool/LoadTodoList 无 ISessionManager 注入**：✓（改走 ITodoStore；AgentExecutor 保留 `_sessionManager` 仅用于 ResolvePolicy 权限快照）
5. **命名规范**：✓（Task 3 审计表 + Phase 0 结论落地，§5.1 写入 AGENTS.md）
6. **扩展包编译**：✓（Memory/Acp/DeepSeek 全部编译通过，引用 Abstractions）
7. **AgentExecutor.ExecuteAsync 4 参**：✓（`(AgentDefinition, IReadOnlyList<ChatMessage>, AgentContext, CancellationToken)`；全库无 `context.History` 引用——仅 `ChatExecutionContext.History`（App 独立类型）保留）

### 实施偏差记录

| 偏差 | 说明 |
|------|------|
| Task 11 ConfigureServices 未按计划删除 | 前序任务保留 `IExtension.ConfigureServices` DIM 与 Acp/Memory 实现；Task 21 全量验证时确认其零调用方（ExtensionManager 不调用、宿主显式 `AddSeeingAcp/AddMemoryServices`），按 §11 标准 3 收尾删除 |
| Task 8 RequiresFactory 未删 | `AgentStatus.RequiresFactory` 零使用残留；Task 21 按 §11 标准 3 收尾删除 |
| Task 8 CreateSubAgentContext 未删 | `PermissionIntegrity.CreateSubAgentContext` 零调用孤儿（唯一调用方随 Task 8 删除后遗留）；Task 21 收尾删除；`PermissionDelegationException` 按 Task 6 决定保守保留 |
| Task 20 下游传递引用中断 | Seeing.Gateway 移除主库引用后，`Seeing.Gateway.QQ`/`Seeing.Gateway.WeCom`（扩展层，合法引用主库）及 `Seeing.Gateway.Tests` 的传递引用失效——QQ/WeCom csproj 显式添加主库 ProjectReference，测试删除冗余 `using Seeing.Agent.Llm;`，协议层独立性不受影响 |
| 完成日期 | 计划文本写「2026-08-17 完成」，实际完成于 2026-08-18（状态与记录按实际日期） |
| 会话归属重构 | 2026-08-27 完成：SessionMessage.SessionId 强归属 + Messages 封装 + 执行引擎下沉主库层 + Task 事件体系删除 + 子会话统一执行。commits: `fdd0437`（主重构）+ `7051c83`（切换流式修复）+ `0708c53`（测试期望修复） |
