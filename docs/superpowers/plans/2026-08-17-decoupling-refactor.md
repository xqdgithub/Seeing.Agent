# Seeing.Agent 解耦重构实施计划（Abstractions 包 + Agent 纯数据化 + Todo 端口-适配器）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建零实现契约包 `Seeing.Agent.Abstractions` 并迁移全部公共表面；删除 IAgent/AgentBase/IAgentExecutionRouter/IAgentManager/TodoManager 等死代码；Agent 体系纯数据化 + `IAgentExecutor` 统一执行入口；`ITodoStore` 端口-适配器替代 Session 魔法键；拔除 Seeing.Gateway 对主库的引用。

**Architecture:** 三层依赖规范（原语层 → 主库层 → 扩展层）。Abstractions 只放接口/DTO/注解/常量/事件声明（零实现逻辑；豁免：允许引用原语层 `Seeing.Session`，解决 `AgentDefinition.BudgetConfig` 的 TokenBudgetConfig 依赖）。执行链从"实例包装"（IAgent→AgentBase→Router→AgentExecutor）变为"定义 + 消息入参 → IAgentExecutor"。Todo 从"工具直写 Session 键值后门"变为"工具依赖 ITodoStore 抽象端口"。

**Tech Stack:** .NET 10 / C# / xUnit + Moq / 中央包管理（CPM）

**设计依据:** `docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md`（三轮审查通过；含 Phase 0 审查结论追加位）

**计划审查记录:** 本计划经子代理三轮审查（2026-08-17）。第一轮 14 P1 + 16 P2 全量修正（Phase 1 顺序按依赖拓扑重排、tests/samples 修复前移、IToolManager/ISkillManager 契约按真实签名、Task 13/17 顺序与分工统一）。第二轮复审 7 新 P1 + 6 新 P2 全量修正（TaskTool 四处使用点、PermissionKind.cs 补列、PermissionContext 拆分、IExecutionContext.ActiveAgent 提前改、ICommandExtension 推迟 Task 12、IToolManager.ExecuteAsync 收录真实签名、IAgentExecutionRouter.cs 删除步骤）。第三轮终审 3 新 P1 + 3 新 P2 全量修正（**GetOrCreateAgentInstance 全量调用方清单**（4 生产 + 10 测试，BackgroundTaskManager 留 Task 14）、**AgentContext.History 删除并入执行链切换任务**（否则中间态不可编译）、PermissionContext 拆分成员归属补全、ComponentManager/ExtensionManager 临时移除点、Task 14+15 合并为类型层/执行链两任务）。

---

## 文件结构总览

### 新建（项目）
- `src/Seeing.Agent.Abstractions/Seeing.Agent.Abstractions.csproj` — 零实现契约包（允许引用原语层 `Seeing.Session`）
- `src/Seeing.Agent.Abstractions/{Events,Hooks,Tools,Agents,Extensions,Llm,Mcp,Permissions,Skills,Commands,Configuration,Todo,Components}/` — 契约类型

### 新建（主库内实现）
- `src/Seeing.Agent/Todo/SessionContextTodoStore.cs` — ITodoStore 默认实现
- `src/Seeing.Agent/Core/NativeAgentExecutor.cs` — 原 NativeAgentExecutionRouter 改名
- `src/Seeing.Agent.Acp/Execution/AcpAgentExecutor.cs` — 原 AcpAgentExecutionRouter 改名
- `src/Seeing.Agent/Core/ToolSinkAdapter.cs` — ToolContext Sink 接线适配器

### 删除
- `src/Seeing.Agent/Core/Abstractions/AgentBase.cs`、`Core/Interfaces/IAgent.cs`、`Core/Interfaces/IAgentExecutionRouter.cs`、`Core/NativeAgentExecutionRouter.cs`、`Core/AgentDecorator.cs`、`Core/Todo/TodoManager.cs`、`Core/Todo/ITodoManager.cs`、`Tools/BuiltIn/Todo/TodoReadTool.cs`、`Configuration/IAgentManager.cs`（随拆分）

### 修改（重点）
- `src/Seeing.Agent/Core/AgentExecutor.cs`（4 参签名、删 History 依赖；Todo 改造集中在 Task 19）
- `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs`（ITodoStore）
- `src/Seeing.Agent/Core/Interfaces/IExtension.cs`（拆分 7 接口）
- `src/Seeing.Agent/Extensions/ExtensionManager.cs`（GetAgents 包装删除）
- `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（注册调整）
- `src/Seeing.Gateway/*`（移除主库引用）

---

## Phase 0：接口审查

### Task 1: IProviderManager / IProviderRegistry 职责审查

**Files:**
- Read: `src/Seeing.Agent/Llm/IProviderManager.cs`
- Read: `src/Seeing.Agent/Llm/IProviderRegistry.cs`
- Read: `src/Seeing.Agent/Llm/ProviderManager.cs`
- Read: `src/Seeing.Agent/Llm/ProviderRegistry.cs`

- [ ] **Step 1: 对比两个接口的成员与调用方**

运行：`Get-ChildItem src -Recurse -Filter *.cs | Select-String -Pattern "IProviderManager|IProviderRegistry" | Select-Object Path,LineNumber,Line`

预期：得到两个接口的全部声明与消费点清单。

- [ ] **Step 2: 判断职责重叠并记录结论**

若 `IProviderManager` 与 `IProviderRegistry` 成员重叠（如都有 Register/GetProviders/Remove），在审查记录中写：合并到 `IProviderRegistry`，`IProviderManager` 删除，消费者改注。若职责互补（如 Manager 管配置、Registry 管注册），则两个都保留并记录分工。**此步骤只产出结论，不动代码**。

- [ ] **Step 3: 将结论追加到设计文档 Phase 0 记录**

在设计文档 `docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md` 末尾追加「Phase 0 审查结论」小节，记录 IProviderManager/IProviderRegistry 的处置。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md
git commit -m "docs: Phase 0 审查 IProviderManager/IProviderRegistry 职责结论"
```

### Task 2: IModelManager / IModelConfigManager / IAgentConfigManager 审查

**Files:**
- Read: `src/Seeing.Agent/Llm/IModelManager.cs`
- Read: `src/Seeing.Agent/Llm/IModelConfigManager.cs`
- Read: `src/Seeing.Agent/Configuration/IAgentConfigManager.cs`
- Read: `src/Seeing.Agent/Llm/ModelManager.cs`

- [ ] **Step 1: 对比三个接口成员与调用方**

运行：`Get-ChildItem src -Recurse -Filter *.cs | Select-String -Pattern "IModelManager|IModelConfigManager|IAgentConfigManager" | Select-Object Path,LineNumber,Line`

预期：得到三者的声明与消费点。

- [ ] **Step 2: 判断合并/删除方向并记录**

原则：模型解析（ResolveNativeModel）归 `IModelManager`；配置读写归 `IModelConfigManager`；`IAgentConfigManager` 若与 IAgentRuntimeManager 的模型覆盖重复则删除。**只产出结论，不动代码**。

- [ ] **Step 3: 追加审查结论到设计文档**

同 Task 1 Step 3 方式追加记录。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md
git commit -m "docs: Phase 0 审查模型管理三接口结论"
```

### Task 3: 全量接口审计表

**Files:**
- Create: `docs/superpowers/plans/interface-audit-table.md`（实施辅助表）

- [ ] **Step 1: 收集全部公共接口**

运行：
```powershell
Get-ChildItem src -Recurse -Filter *.cs | Select-String -Pattern "public interface I\w+" | ForEach-Object { $_.Matches[0].Value -replace "public interface ","" } | Sort-Object -Unique
```

预期：完整接口清单（约 30+ 个）。

- [ ] **Step 2: 对照 §5.1 命名规范逐一审计并输出表格**

在 `interface-audit-table.md` 中列出：接口名 | 现状职责 | 命名合规 | 处置（保留/删除/拆分/审查中/改名）。重点核对：IToolPermissionPolicy、IMetadataStore、IExecutionPipeline、ISkill、ISessionEventBus、ITaskEventProjector 等此前未审接口。

- [ ] **Step 3: 审计表纳入设计文档**

将审计表追加到设计文档「Phase 0 审查结论」小节。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md docs/superpowers/plans/interface-audit-table.md
git commit -m "docs: Phase 0 全量接口审计表"
```

---

## Phase 1：建包 + 类型迁移（按依赖拓扑严格顺序）

> Phase 1 迁移顺序依据依赖拓扑：无依赖组最先，被依赖组先于依赖方。每 Task 完成需两个项目编译通过（最后 Task 13 全量）。

### Task 4: 创建 Seeing.Agent.Abstractions 项目

**Files:**
- Create: `src/Seeing.Agent.Abstractions/Seeing.Agent.Abstractions.csproj`
- Modify: `Seeing.Agent.slnx`
- Create: `src/Seeing.Agent.Abstractions/GlobalUsings.cs`

- [ ] **Step 1: 创建 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Abstractions</RootNamespace>
    <Description>Seeing.Agent 契约包：接口/DTO/注解/常量/事件声明，零实现逻辑</Description>
    <PackageId>Seeing.Agent.Abstractions</PackageId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <ProjectReference Include="..\Seeing.Session\Seeing.Session.csproj" />
  </ItemGroup>

</Project>
```

注意：
- **唯一 ProjectReference** 是原语层 `Seeing.Session`（设计文档 §4.1 豁免：AgentDefinition.BudgetConfig 依赖 Session/Core/Budget/TokenBudgetConfig.cs:6，按 §3 层级规范原语层同级可引用）
- `Microsoft.Extensions.Logging.Abstractions` 走中央包管理（CPM，`Directory.Packages.props` 中已有版本则不加 Version 属性；若无则查 CPM 现有条目后添加）
- **禁止引用** `Seeing.Agent` 主库与任何扩展包

- [ ] **Step 2: 创建 GlobalUsings**

```csharp
global using System.Text.Json;
```

- [ ] **Step 3: 加入解决方案**

用 VS 或编辑 `Seeing.Agent.slnx` 加入新项目条目（与 src 其他项目同级，按现有格式）。

- [ ] **Step 4: 验证空项目可编译**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q`
预期：BUILD SUCCEEDED

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent.Abstractions Seeing.Agent.slnx Directory.Packages.props
git commit -m "feat: 创建 Seeing.Agent.Abstractions 契约包项目骨架"
```

### Task 5: 迁移 Hooks 契约组（无跨组依赖，最先）

**Files:**
- Move: `src/Seeing.Agent/Core/Hooks/IHookManager.cs` → `src/Seeing.Agent.Abstractions/Hooks/IHookManager.cs`
- Move: `src/Seeing.Agent/Core/Hooks/IHookHandler.cs` → `src/Seeing.Agent.Abstractions/Hooks/IHookHandler.cs`
- Move: `src/Seeing.Agent/Core/Hooks/HookPayload.cs`、`HookResult.cs`、`HookSpec.cs`、`HookPolicy.cs`、`HookDataContract.cs`、`DataField.cs`、`HookRegistry.cs` → `src/Seeing.Agent.Abstractions/Hooks/`
- Modify: 全部迁移文件的 namespace → `Seeing.Agent.Abstractions.Hooks`
- Modify: 上述文件内的 `using Seeing.Agent.*` 引用

- [ ] **Step 1: git mv + namespace 替换**

```powershell
git mv src/Seeing.Agent/Core/Hooks/IHookManager.cs src/Seeing.Agent.Abstractions/Hooks/IHookManager.cs
git mv src/Seeing.Agent/Core/Hooks/IHookHandler.cs src/Seeing.Agent.Abstractions/Hooks/IHookHandler.cs
git mv src/Seeing.Agent/Core/Hooks/HookPayload.cs src/Seeing.Agent.Abstractions/Hooks/HookPayload.cs
git mv src/Seeing.Agent/Core/Hooks/HookResult.cs src/Seeing.Agent.Abstractions/Hooks/HookResult.cs
git mv src/Seeing.Agent/Core/Hooks/HookSpec.cs src/Seeing.Agent.Abstractions/Hooks/HookSpec.cs
git mv src/Seeing.Agent/Core/Hooks/HookPolicy.cs src/Seeing.Agent.Abstractions/Hooks/HookPolicy.cs
git mv src/Seeing.Agent/Core/Hooks/HookDataContract.cs src/Seeing.Agent.Abstractions/Hooks/HookDataContract.cs
git mv src/Seeing.Agent/Core/Hooks/DataField.cs src/Seeing.Agent.Abstractions/Hooks/DataField.cs
git mv src/Seeing.Agent/Core/Hooks/HookRegistry.cs src/Seeing.Agent.Abstractions/Hooks/HookRegistry.cs
```

将每个文件内 `namespace Seeing.Agent.Core.Hooks;` 改为 `namespace Seeing.Agent.Abstractions.Hooks;`，并修正文件中 `using Seeing.Agent.*` 引用（被引用的类型若已迁移到 Abstractions，则改为 `using Seeing.Agent.Abstractions.*`；若仍在主库，保持）。

- [ ] **Step 2: 验证 Abstractions 编译**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q`
预期：BUILD SUCCEEDED（Hooks 组内部无未迁移依赖）

- [ ] **Step 3: 修主库编译错误（using 更新）**

运行：`dotnet build src/Seeing.Agent -v q 2>&1 | Select-String "error" | Select-Object -First 40`
逐个修复报错文件的 `using Seeing.Agent.Core.Hooks;` → `using Seeing.Agent.Abstractions.Hooks;`。批量替换命令（在 src/Seeing.Agent 下）：
```powershell
Get-ChildItem -Recurse -Filter *.cs | ForEach-Object { $c = Get-Content $_.FullName -Raw; $n = $c.Replace("using Seeing.Agent.Core.Hooks;","using Seeing.Agent.Abstractions.Hooks;"); if ($c -ne $n) { Set-Content -LiteralPath $_.FullName -Value $n -NoNewline } }
```
注意：`HookManager.cs`（实现类）留在主库，只改 using；`HookInterruptedException.cs` 若引用 HookPayload 同理只改 using。

- [ ] **Step 4: 验证主库编译**

运行：`dotnet build src/Seeing.Agent -v q`
预期：BUILD SUCCEEDED（或仅剩其他组未迁移导致的错误，记录并继续——本任务内只保证 Hooks 相关错误清零）

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: 迁移 Hooks 契约组到 Seeing.Agent.Abstractions"
```

### Task 6: 迁移前置 DTO 组（Todo + Permissions + ConfigLevel）

> 前置原因（依赖拓扑）：TodoUpdateEvent→TodoItem、AgentDefinition→PermissionRuleEntry/IPermissionChannel、IAgentRuntimeManager→ConfigLevel 均依赖本组。本组内部无跨组依赖，必须先于 Task 7/8。

**Files:**
- Move: `src/Seeing.Agent/Core/Todo/TodoItem.cs` → `src/Seeing.Agent.Abstractions/Todo/TodoItem.cs`（同文件含 TodoStatus/TodoPriority/TodoList，无独立 TodoList.cs）
- Move: `src/Seeing.Agent/Core/Permission/` 中接口与 DTO：`IPermissionService.cs`、`IPermissionChannel.cs`、`IRuleEvaluator.cs`、`PermissionRuleEntry.cs`、`PermissionContext.cs`、`PermissionResult.cs`、`AgentPermissionPolicy.cs` → `Abstractions/Permissions/`
- Move: `src/Seeing.Agent/Core/Permission/PermissionKind.cs` → `Abstractions/Permissions/`（**同文件含 4 个枚举**：PermissionKind :6、**PermissionEffect :36**、ConditionLogic :51、FileOperation :63；PermissionRuleEntry.cs:14,23 直接引用 PermissionKind.Kind/PermissionEffect.Effect，漏迁必破编译）
- Move: `src/Seeing.Agent/Configuration/WorkspaceProvider.cs` 中的 `ConfigLevel` 枚举 → `Abstractions/Configuration/ConfigLevel.cs`
- Create: `Abstractions/Todo/ITodoStore.cs`（IToolManager 同组新建，本 Task 一并创建）

- [ ] **Step 1: 迁移 Todo + Permissions 契约**

逐个 git mv，namespace → `Seeing.Agent.Abstractions.Todo` / `Seeing.Agent.Abstractions.Permissions`。`ConfigLevel` 枚举原样迁出（namespace → `Seeing.Agent.Abstractions.Configuration`），原文件删除该枚举定义。

**PermissionContext 拆分（零实现纪律，全文 161 行核验）**：迁入 Abstractions 的**仅属性 DTO 部分**（字段/属性/常量，无参构造、无 `_hmacKey` 私有字段）。以下成员**全部留主库**（新建 `src/Seeing.Agent/Core/Permission/PermissionIntegrity.cs`，主库 internal 静态类，逻辑原样搬移）：
- `GenerateHmacKey()`（:43-46 构造函数内）/`ComputeIntegrityHash()`（:52+，HMAC-SHA256）——调用方 `PermissionService.cs:57` 改调 `PermissionIntegrity`
- `FromAgentContext()`（:122-136，引用主库 `AgentContext`——Task 8 才迁，拓扑上不能随迁）——留主库作扩展方法；调用方 `AgentExecutor.cs:126,701` 保持调用（Task 8 后 AgentContext 迁入 Abstractions，扩展方法签名不变）
- `CreateSubAgentContext()`（:89-105，含 IsDelegableTo/Intersect 策略逻辑 + `_hmacKey` 依赖）——留主库；其唯一调用方 `AgentModels.cs:91`（AgentContext.CreateSubAgentContext 实现内）随 Task 8 删除 AgentContext.CreateSubAgentContext 后变孤儿，**Task 8 一并删除**
- `VerifyIntegrity()`（:80，无调用方）——留主库（保守保留，不入契约）
- `PermissionDelegationException`（:154-161，同文件，被 CreateSubAgentContext 抛出）——留主库

- [ ] **Step 2: 新建 ITodoStore**

```csharp
// src/Seeing.Agent.Abstractions/Todo/ITodoStore.cs
namespace Seeing.Agent.Abstractions.Todo;

public interface ITodoStore
{
    Task<TodoList> LoadAsync(string sessionId);
    Task SaveAsync(string sessionId, TodoList todos);
}
```

- [ ] **Step 3: 修主库编译错误**

`using Seeing.Agent.Core.Todo;` → `using Seeing.Agent.Abstractions.Todo;`（TodoWriteTool/TodoManager/AgentExecutor 等）；`using Seeing.Agent.Core.Permission;` 中涉及已迁移类型的使用点拆分（PermissionService.cs 实现类留主库仅改 using）。`WorkspaceProvider.cs` 中 ConfigLevel 引用改 using。

- [ ] **Step 4: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q && dotnet build src/Seeing.Agent -v q`
预期：均 BUILD SUCCEEDED（主库仅剩未迁移组错误时记录并继续）
```bash
git add -A
git commit -m "refactor: 迁移 Todo/Permissions 契约与 ConfigLevel 到 Abstractions，新建 ITodoStore"
```

### Task 7: 迁移 Events/Llm 基础类型组（含 ChatMessage 依赖链）

**Files:**
- Move: `src/Seeing.Agent/Core/Events/MessageEventTypes.cs` → `src/Seeing.Agent.Abstractions/Events/MessageEvents.cs`（拆文件：IMessageEvent/事件类型/MessageEventType/LoopPhase）
- Move: `src/Seeing.Agent/Llm/LlmModels.cs` 中的 `ChatMessage`/`ChatRole`/`ToolCall`/`FunctionCall`/`TokenUsage` → `src/Seeing.Agent.Abstractions/Llm/`（拆文件）
- Move: `src/Seeing.Agent/Llm/ChatContentPart.cs` → `src/Seeing.Agent.Abstractions/Llm/ChatContentPart.cs`

**迁移顺序（依赖链）**：ChatRole → ChatMessage → ToolCall → FunctionCall → MessageEventType/LoopPhase → IMessageEvent → 具体事件类型 → TokenUsage。`TodoUpdateEvent.Todos`（MessageEventTypes.cs:489）引用 `Core.Todo.TodoItem`——已在 Task 6 迁入 Abstractions.Todo ✓。

- [ ] **Step 1: 拆 LlmModels.cs 并迁移基础类型**

从 `src/Seeing.Agent/Llm/LlmModels.cs` 中抽出并迁移（**原样复制全部成员与注释，以下为真实成员结构，仅示意**）：

```csharp
// src/Seeing.Agent.Abstractions/Llm/ChatRole.cs
namespace Seeing.Agent.Abstractions.Llm;

// 真实形态：static class 字符串常量（LlmModels.cs:10-16：System/User/Assistant/Tool）
public static class ChatRole
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}
```

```csharp
// src/Seeing.Agent.Abstractions/Llm/ChatMessage.cs —— 真实成员（LlmModels.cs:21）：
//   Role (string)、Content (非空 string)、Name (string?)、Parts (IReadOnlyList<ChatContentPart>?)、
//   ReasoningContent (string?)、ToolCallId (string?)、ToolCalls、Usage、Metadata 等
//   以原文件为准原样复制，不要按骨架编写
```

> **注意**：骨架仅示意。实施时必须以原 LlmModels.cs 中 ChatMessage/ChatRole/TokenUsage 的实际成员为准原样复制（含所有属性、JsonPropertyName 特性与注释）。拆出后原 LlmModels.cs 中删除这些类型。

- [ ] **Step 2: TokenUsage 迁移并改 record**

真实成员（LlmModels.cs:178-195）：`InputTokens`/`OutputTokens`/`ReasoningTokens` + 计算属性 `TotalTokens`（含 JsonPropertyName 特性）。迁移时改为 `public record TokenUsage { ... }`，成员原样保留（init 访问器），缓存逻辑（若有）移入主库包装。

- [ ] **Step 3: 迁移事件类型文件**

`MessageEventTypes.cs` 拆为 `Abstractions/Events/MessageEvents.cs`：namespace → `Seeing.Agent.Abstractions.Events`，类型原样复制（IMessageEvent 及全部具体事件、MessageEventType 枚举、LoopPhase）。同时迁移 `ChatContentPart.cs`。

- [ ] **Step 4: 修主库编译错误**

同 Task 5 Step 3 模式：`using Seeing.Agent.Core.Events;` → `using Seeing.Agent.Abstractions.Events;`；`using Seeing.Agent.Llm;` 中涉及已迁移类型的使用点拆分（主库 Llm 命名空间仍存在，需按报错逐个处理）。检查 `GlobalUsings.cs`（src/Seeing.Agent/GlobalUsings.cs）的 **5 个别名**全部更新：
- `:11` `global using TokenUsage = Seeing.Agent.Llm.TokenUsage;` → `Seeing.Agent.Abstractions.Llm.TokenUsage`
- `:5` ChatMessage、`:7` ChatRole、`:8` FunctionCall、`:12` ToolCall 的别名同理改指向 Abstractions.Llm

- [ ] **Step 5: 验证两个项目编译**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q && dotnet build src/Seeing.Agent -v q`
预期：均 BUILD SUCCEEDED（主库仅剩未迁移组错误时记录并在后续任务清零）

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: 迁移消息事件与 LLM 基础类型到 Abstractions（含 ChatMessage 依赖链）"
```

### Task 8: 迁移 Agents DTO 组

> 前置满足：PermissionRuleEntry/IPermissionChannel（Task 6 ✓）、TokenBudgetConfig 依赖 Seeing.Session（Task 4 ✓）。

**Files:**
- Move: `src/Seeing.Agent/Core/Models/AgentModels.cs` 中的 `AgentMode`/`AgentStatus`（删 RequiresFactory）`/ModelReference`/`AgentContextKeys` → `src/Seeing.Agent.Abstractions/Agents/`
- Create: `src/Seeing.Agent.Abstractions/Agents/AgentContext.cs`（纯数据化：删除 CreateSubAgentContext/TotalSteps/TotalUsage/History——**CreateSubAgentContext 经 grep 全库无调用方，直接删除**；BuildPermissionPolicy 调用点移主库）
- Move: `src/Seeing.Agent/Core/Models/AgentDefinition.cs` → `src/Seeing.Agent.Abstractions/Agents/AgentDefinition.cs`（**删除 AgentFactory/FromAgent**；`BuildPermissionPolicy()` 是主库扩展方法，从类中移出为主库静态扩展 `AgentDefinitionPermissions.BuildPermissionPolicy(this AgentDefinition)`，放在 `src/Seeing.Agent/Core/Permission/`）
- Move: `src/Seeing.Agent/Core/Interfaces/IAgentStore.cs`、`IAgentRuntimeManager.cs` → `src/Seeing.Agent.Abstractions/Agents/`
- Move: `src/Seeing.Agent/Core/Interfaces/IAgentRegistry.cs` → `src/Seeing.Agent.Abstractions/Agents/`（原样迁移现状，拆分在 Task 17）

- [ ] **Step 1: 迁移 DTO 并纯数据化**

按上述清单 git mv + namespace → `Seeing.Agent.Abstractions.Agents`。`AgentDefinition` 删除 `AgentFactory`（原 :133）与 `FromAgent`（原 :153-155）；`BuildPermissionPolicy` 改为主库扩展方法（实现体原样搬到扩展类，用 `PermissionRuleEntry` 构建 `AgentPermissionPolicy`，逻辑不变）。**ModelReference 随迁豁免说明**：其 `Parse`/`Format` 为无外部依赖的纯静态逻辑（AgentModels.cs:139-182），允许随 DTO 一起迁入（与 ModelRef 留主库不冲突——ModelRef 依赖 provider 查询逻辑，ModelReference 无依赖；若实施时发现 Parse 有外部依赖，则改为主库扩展方法）。

- [ ] **Step 2: AgentContext 纯数据化**

删除 `CreateSubAgentContext()`（无调用方，直接删）、`TotalSteps`/`TotalUsage`（无使用方）。**`History` 本 Task 保留**——其删除必须与 AgentExecutor 4 参化同 Task（Task 14 执行链切换）落地，否则 AgentExecutor.cs:116,120 与 HeartbeatJob.cs:289/AgentJob.cs:309 等构造点在中间态无法编译。`Services` 保留。**`PermissionContext.CreateSubAgentContext` 一并删除**（其唯一调用方 AgentModels.cs:91 随本步删除变孤儿）。

- [ ] **Step 3: 修主库编译错误**

处理所有 `using Seeing.Agent.Core.Models;` 对已迁移类型的引用；`AgentManager`/`ExtensionManager` 中 `AgentFactory`/`FromAgent` 的调用点删除或改写（ExtensionManager.cs:269 的包装删除；**`AgentManager.GetOrCreateAgentInstance`（:304-316）本 Task 不动实现**——BackgroundTaskManager.cs:302 在 Task 14 前仍依赖它，Task 8-14 之间保留运行时行为，不得"默认构造替代"）。

**FromAgent/GetOrCreateAgentInstance 全部调用方改造（本 Task 内完成，否则删 FromAgent 后立即破编译）**：

| 文件:行 | 原代码 | 改法 |
|---|---|---|
| TaskTool.cs:335 | `GetOrCreateAgentInstance(agentName)` | `await GetAgentAsync(agentName) ?? throw`（TaskTool.cs:26 `_agentRegistry : IAgentRegistry`，GetAgentAsync 存在 :24；TaskTool.cs:82,123 已有同模式异步调用先例） |
| TaskTool.cs:366 | `AgentDefinition.FromAgent(agent)` | 删除（:335 已直接拿 AgentDefinition；:358 History.Add 保留，Task 15 改 messages） |
| ExecutionJobService.cs:505 | `GetOrCreateAgentInstance(agentId)` | `await GetAgentAsync(agentId)` |
| ExecutionJobService.cs:508 | `agent.Disabled` | `def.Disabled` |
| ExecutionJobService.cs:511 | `AgentDefinition.FromAgent(agent)` | 删除（def 已就绪；:534 是 **ChatExecutionContext.History**（App 独立类型）保留不动） |
| HeartbeatJob.cs:275 | `GetOrCreateAgentInstance(resolvedAgentId)` | `await GetAgentAsync(resolvedAgentId)` |
| HeartbeatJob.cs:278 | `FromAgent(agentInstance)` | 删除（:289 History 构造保留，Task 15 改 messages） |
| AgentJob.cs:283 | `GetOrCreateAgentInstance(resolvedAgentId)` | `await GetAgentAsync(resolvedAgentId)` |
| AgentJob.cs:286 | `FromAgent(agentInstance)` | 删除（:309 History 构造保留，Task 15 改 messages） |
| CommandListService.cs:67 | `GetOrCreateAgentInstance(session.SelectedAgent)` | `await GetAgentAsync(...)`（方法改 async）；:68 `agent?.Runtime` → `def?.Runtime` |
| tests/SchedulerIntegrationTests.cs:428 | `Setup(GetOrCreateAgentInstance)` | `Setup(r => r.GetAgentAsync(It.IsAny<string>())).ReturnsAsync(definition)` |
| tests/SchedulerEngineTests.cs:287 | 同上 | 同上 |

> BackgroundTaskManager.cs:302 与 tests/BackgroundTaskManagerTests.cs 8 处 Setup **Task 14 处理**（此时仍走 GetOrCreateAgentInstance，Task 8 不动）。

- [ ] **Step 4: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q && dotnet build src/Seeing.Agent -v q`
预期：均 BUILD SUCCEEDED
```bash
git add -A
git commit -m "refactor: 迁移 Agent DTO 组到 Abstractions，AgentDefinition 纯数据化"
```

### Task 9: 迁移 Llm/Mcp 契约组

> 前置原因：Task 11（IExtension 拆分）依赖 `ILlmProvider`/`McpServerConfig`，故本组先于 Extensions 组。

**Files:**
- Move: `src/Seeing.Agent/Llm/ILlmProvider.cs`、`IProviderRegistry.cs`、`IConfigurableLlmProvider.cs`、`ProviderConfig.cs`、`ProviderInfo.cs`、`ModelType.cs`、`ModelConfig.cs`、`ProvidersChangedEventArgs.cs` → `Abstractions/Llm/`
- Move: `src/Seeing.Agent/MCP/Core/IMcpManager.cs`（+IMcpStatusProvider/IMcpController/IMcpConfigManager）→ `Abstractions/Mcp/`
- Move: `src/Seeing.Agent/MCP/Core/` 下 DTO（`McpServerConfig.cs`、`McpConnectionState.cs`、`McpServerStatus.cs` 及事件参数类等纯数据文件）→ `Abstractions/Mcp/`
- Modify: 主库 + 扩展包全部 using

- [ ] **Step 1: 迁移 Llm/Mcp 契约**

namespace → `Seeing.Agent.Abstractions.Llm` / `Seeing.Agent.Abstractions.Mcp`。`ModelType` 两处定义（Llm/ModelType.cs:6 与 Core/Models/ConfigurationModels.cs:45）**均迁移**，若类型完全一致则只保留一份并修引用。**ModelRef 留主库**（static class 含 Format/Parse/IsKnownProvider 实现逻辑，设计文档 §4.2 明确留主库）。`McpClientManager` 等实现类留主库。

- [ ] **Step 2: 验证 Abstractions 编译**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q`
预期：BUILD SUCCEEDED

- [ ] **Step 3: 修主库编译错误**

主库与扩展包（plugs/providers/Seeing.Provider.DeepSeek 等）的 using 更新：`using Seeing.Agent.Llm;` 中已迁移类型 → `using Seeing.Agent.Abstractions.Llm;`；`using Seeing.Agent.MCP.Core;` → `using Seeing.Agent.Abstractions.Mcp;`（注意：MCP namespace 原为大写 `Seeing.Agent.MCP.Core`，替换串必须区分大小写）。逐个报错处理。

- [ ] **Step 4: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent -v q && dotnet build src/Seeing.Agent.Memory -v q && dotnet build src/Seeing.Agent.Acp -v q && dotnet build src/Seeing.Agent.Gateway -v q && dotnet build plugs/providers/Seeing.Provider.DeepSeek -v q`
预期：全部 SUCCEEDED
```bash
git add -A
git commit -m "refactor: 迁移 Llm/Mcp 契约组到 Abstractions"
```

### Task 10: 迁移 Tools 契约组 + 新建接口

**Files:**
- Move: `src/Seeing.Agent/Core/Interfaces/ITool.cs` → `src/Seeing.Agent.Abstractions/Tools/ITool.cs`（拆分：ITool/ToolCategory/ToolContext）
- Move: `src/Seeing.Agent/Core/Models/ToolResult.cs` → `src/Seeing.Agent.Abstractions/Tools/ToolResult.cs`（同文件含 FileAttachment，:6/:30）
- Move: `src/Seeing.Agent/Tools/Attributes/ToolAttributes.cs` → `src/Seeing.Agent.Abstractions/Tools/ToolAttributes.cs`
- Create: `src/Seeing.Agent.Abstractions/Tools/IToolManager.cs`
- Create: `src/Seeing.Agent.Abstractions/Tools/IToolEventSink.cs`、`IToolMetadataSink.cs`

- [ ] **Step 1: 迁移 ITool.cs / ToolResult.cs / ToolAttributes.cs**

namespace → `Seeing.Agent.Abstractions.Tools`。**`ToolContext.Agent` 直接改为 `AgentDefinition?`**（ITool.cs:13 原 `IAgent?`，AgentDefinition 已迁入 Abstractions.Agents ✓；IAgent 永不入 Abstractions，将在 Task 14 删除）。`ToolContext.PermissionChannel`（:27）改 `IPermissionChannel?`（Task 6 已迁 ✓）。注意：ITool.cs 中现有 DIM 成员（:45,48）与零实现纪律冲突——**若为默认实现，迁移时移入主库包装或删除，契约不留 DIM**（例外：IExtension 元数据默认值，见 Task 11）。

- [ ] **Step 2: 新建 IToolManager（按真实签名定稿）**

先列出 `ToolManager.cs`（733 行）全部 public 成员，再按真实签名定义契约（已验证的真实签名）：

```csharp
// src/Seeing.Agent.Abstractions/Tools/IToolManager.cs
namespace Seeing.Agent.Abstractions.Tools;

public interface IToolManager
{
    IReadOnlyCollection<ITool> GetTools();                              // 同步，ToolManager.cs:151
    ITool? GetTool(string id);                                          // 同步，:465
    Task RegisterToolAsync(ITool tool, CancellationToken cancellationToken = default);  // 无 bool 返回，:362
    bool UnregisterTool(string toolId);                                 // 同步，:452
    Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken cancellationToken = default); // :204
    Task<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        string sessionId = "",
        CancellationToken cancellationToken = default,
        Func<IMessageEvent, ValueTask>? emitAsync = null,
        IPermissionChannel? permissionChannel = null);                  // 真实签名，ToolManager.cs:490-495
}
```

> **注意**：`ExecuteAsync` **必须收录真实签名**（ToolManager.cs:490-495：`(ToolCall, string sessionId = "", CancellationToken, Func<IMessageEvent,ValueTask>? emitAsync, IPermissionChannel?)`）——唯一调用点 `AgentExecutor.cs:654-655` 传的就是 5 参形态，`ToolManager : IToolManager` 直接实现，不做参数折叠（折叠进 ToolContext 属更大重构，不在本次范围）。`ToolCall` 来自 `Seeing.Agent.Abstractions.Llm`（Task 7 ✓）、`IMessageEvent` 来自 `.Events`（Task 7 ✓）、`IPermissionChannel` 来自 `.Permissions`（Task 6 ✓）。

- [ ] **Step 3: 新建 Sink 接口**

```csharp
// src/Seeing.Agent.Abstractions/Tools/IToolEventSink.cs
namespace Seeing.Agent.Abstractions.Tools;

public interface IToolEventSink
{
    ValueTask EmitAsync(IMessageEvent evt);
}

// src/Seeing.Agent.Abstractions/Tools/IToolMetadataSink.cs
namespace Seeing.Agent.Abstractions.Tools;

public interface IToolMetadataSink
{
    void SetMetadata(string key, Dictionary<string, object>? value);
}
```

- [ ] **Step 4: 修主库编译 + ToolContext 委托字段改 Sink 接口类型**

`ToolContext` 中 `EmitAsync`/`SetMetadata` 委托字段替换为 `IToolEventSink? EventSink` / `IToolMetadataSink? MetadataSink`（类型保留 `?` 可空，兼容未接线场景）；`AgentExecutor` 中接线处**本任务只改委托字段类型为接口引用（可暂传 null），适配器 ToolSinkAdapter 统一在 Task 18 创建并接线**（避免重复创建）。

- [ ] **Step 5: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q && dotnet build src/Seeing.Agent -v q`
预期：均 BUILD SUCCEEDED
```bash
git add -A
git commit -m "refactor: 迁移 Tools 契约组到 Abstractions，新增 IToolManager 与 Sink 接口"
```

### Task 11: 迁移/重新设计 Extensions 组（IExtension 拆分）

> 前置满足：ILlmProvider/McpServerConfig（Task 9 ✓）。

**Files:**
- Modify: `src/Seeing.Agent/Core/Interfaces/IExtension.cs` → 拆分为 `Abstractions/Extensions/` 下多个文件（**ExtensionContext/ExtensionMeta 均在此文件内**，无独立 ExtensionContext.cs/ExtensionMeta.cs）
- Create: `Abstractions/Extensions/IExtension.cs`（元数据+生命周期）
- Create: `Abstractions/Extensions/IAgentExtension.cs`、`IToolExtension.cs`、`IHookExtension.cs`、`IProviderExtension.cs`、`IMcpExtension.cs`、`ISkillPathExtension.cs`（**ICommandExtension 推迟到 Task 12**——其 `GetCommands()` 引用 ICommand，ICommand 在 Task 12 才迁入 Abstractions，本 Task 定义必破编译）
- Modify: `src/Seeing.Agent/Extensions/ExtensionManager.cs`（实现 IExtensionManager）

- [ ] **Step 1: 定义拆分后的接口**

```csharp
// src/Seeing.Agent.Abstractions/Extensions/IExtension.cs
namespace Seeing.Agent.Abstractions.Extensions;

public interface IExtension
{
    string? Id => null;              // 仅元数据默认值允许 DIM
    string Version => "1.0.0";
    string Name => "";
    string Description => "";
    string Target => "server";

    Task InitializeAsync(ExtensionContext context, ExtensionMeta meta) => Task.CompletedTask;
    Task DisposeAsync() => Task.CompletedTask;
}

public interface IAgentExtension
{
    IEnumerable<AgentDefinition> GetAgents();
}

public interface IToolExtension
{
    IEnumerable<ITool> GetTools();
}

public interface IHookExtension
{
    IEnumerable<IHookHandler> GetHookHandlers();
}

public interface IProviderExtension
{
    IEnumerable<ILlmProvider> GetProviders();        // ILlmProvider：Task 9 ✓
}

public interface IMcpExtension
{
    IEnumerable<McpServerConfig> GetMcpServers();    // McpServerConfig：Task 9 ✓
}

public interface ISkillPathExtension
{
    IEnumerable<string> GetSkillPaths();
}
// ICommandExtension 在 Task 12 追加（ICommand 迁入后）：
// public interface ICommandExtension { IEnumerable<ICommand> GetCommands(); }
```

- [ ] **Step 2: ExtensionMeta/ExtensionContext 修正**

```csharp
// ExtensionMeta.cs 关键修正（原在 IExtension.cs 内 :56-87）
public enum ExtensionLoadState { First, Updated, Same }
public enum ExtensionSource { Npm, File }
// State: ExtensionLoadState；Source: ExtensionSource
```

`ExtensionContext` 服务引用全部改接口：`IHookManager HookManager`、`IToolManager ToolManager`、`IPermissionService PermissionService`、`IAgentRegistry AgentRegistry`、`IMcpManager McpClientManager`（原具体类引用删除）。**`ISkillManager`/`ICommandRegistry` 两个属性本 Task 不定义**（其契约类型在 Task 12 才迁入 Abstractions，Abstractions 禁止引用主库）——**两个临时移除执行点**（否则删除属性后立即破编译）：
- **构造点**：`src/Seeing.Agent/Core/ComponentManager.cs:265-278`（:274 SkillManager、:277 CommandRegistry 赋值——**ExtensionContext 构造在这里，不在 ExtensionManager**）
- **读取点**：`src/Seeing.Agent/Extensions/ExtensionManager.cs:205`（`context.SkillManager.AddSearchDirectory`）、`:213`（`context.CommandRegistry.Register`）——本 Task 临时移除（GetSkillPaths 处理逻辑暂存变量），Task 12 恢复接口注入

- [ ] **Step 3: IExtensionManager 契约 + 实现**

```csharp
// src/Seeing.Agent.Abstractions/Extensions/IExtensionManager.cs
namespace Seeing.Agent.Abstractions.Extensions;

public interface IExtensionManager
{
    Task InitializeAsync(string workspaceRoot, CancellationToken ct = default);
    IReadOnlyList<LoadedExtensionInfo> GetLoadedExtensions();
    Task<bool> ActivateAsync(string id);
    Task<bool> DeactivateAsync(string id);
    Task<bool> AddAsync(PluginSpec spec);
    Task DisposeAllAsync();
}
```

> **注意**：`LoadedExtensionInfo` 若主库已有对应模型则复用；`InitializeAsync` 的形态以现有 `ExtensionManager.InitializeAsync` 为准。`ExtensionManager : IExtensionManager` 并保持现有全部行为（RegisterComponents 逻辑改为 `is` 判断拆分接口）。

- [ ] **Step 4: 删除 ConfigureServices 契约 + 适配现有扩展**

`IExtension` 删除 `ConfigureServices`；删除 `MemoryExtension.cs:31` 与 `AcpExtension.cs:43` 的实现；`MemoryExtension` 改为 `IToolExtension + IHookExtension`、`AcpExtension` 改为 `IToolExtension`、`DeepSeekExtension` 改为 `IProviderExtension`、`GatewayExtension` 仅 `IExtension`。各扩展的 `GetAgents()`（若有）改返回 `AgentDefinition`（纯数据）。

- [ ] **Step 5: 修主库 + 扩展包编译**

批量更新 using：`using Seeing.Agent.Core.Interfaces;` 对已迁移类型 → `using Seeing.Agent.Abstractions.Extensions;` 等。ExtensionManager 中 `GetAgents()` 的 AgentFactory 包装（:269）删除，改为直接注册扩展提供的 AgentDefinition（AgentDefinition 已于 Task 8 纯数据化）。

- [ ] **Step 6: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q && dotnet build src/Seeing.Agent -v q && dotnet build src/Seeing.Agent.Memory -v q && dotnet build src/Seeing.Agent.Acp -v q && dotnet build src/Seeing.Agent.Gateway -v q && dotnet build plugs/providers/Seeing.Provider.DeepSeek -v q`
预期：全部 SUCCEEDED
```bash
git add -A
git commit -m "refactor: IExtension 按组件类型拆分，ExtensionContext 接口化，ExtensionMeta 枚举化"
```

### Task 12: 迁移 Skills / Commands / Configuration / Components 组

**Files:**
- Move: `src/Seeing.Agent/Core/Interfaces/ISkill.cs`（+SkillContext/SkillResult/SkillInfo）→ `Abstractions/Skills/`
- Create: `Abstractions/Skills/ISkillManager.cs`
- Move: `src/Seeing.Agent/Commands/` 的 ICommandRegistry + ICommand → `Abstractions/Commands/`
- Move: `src/Seeing.Agent/Configuration/PluginSpec.cs` → `Abstractions/Configuration/PluginSpec.cs`（DTO 部分）
- Move: `src/Seeing.Agent/Core/Interfaces/IComponentManager.cs`（含 ComponentLoadResult）+ `IExecutionContext.cs` + `IMetadataStore.cs` + `IExecutionPipeline.cs` + `IToolPermissionPolicy.cs` → `Abstractions/Components/` 等对应目录
- Modify: `src/Seeing.Agent/Core/Todo/TodoListExtensions.cs`（using 更新，随迁移）

- [ ] **Step 1: 批量迁移并统一 namespace**

逐个 git mv，namespace 分别改为 `Seeing.Agent.Abstractions.Skills` / `.Commands` / `.Configuration` / `.Components`。**追加 ICommandExtension**（Task 11 推迟至此）：创建 `Abstractions/Extensions/ICommandExtension.cs`（`IEnumerable<ICommand> GetCommands()`，ICommand 已迁入 ✓）。ISkillManager 定义（**按真实签名**，已验证 `GetSkillDirectories()` 存在于 SkillManager.cs:426）：

```csharp
// src/Seeing.Agent.Abstractions/Skills/ISkillManager.cs
namespace Seeing.Agent.Abstractions.Skills;

public interface ISkillManager
{
    IReadOnlyList<string> GetSkillDirectories();
    // 其余成员：先列出 SkillManager 全部 public 方法，仅收录主库消费方（ExtensionManager/命令）
    // 实际使用的成员；多余成员不进契约
}
```

- [ ] **Step 2: 修主库编译 + ComponentType 开放化 + ExtensionContext 补齐**

`IComponentLoader.Type`/`ComponentLoadResult.Type`/`LoadAsync(ComponentType)`/`GetLoadStatus()` 键改 `string`；`ComponentManager` 实现同步（15 处）。**`IExecutionContext.ActiveAgent` 直接改 `AgentDefinition?`**（IExecutionContext.cs:17 原 `IAgent?`——本 Task 迁移时立即改，Task 14 删除 IAgent 后只需核对；**不可**暂保持 IAgent 引用，Abstractions 禁止引用主库）。`TodoListExtensions.cs` 的 using 改 `Seeing.Agent.Abstractions.Todo`。**ExtensionContext 补齐**（Task 11 临时移除的恢复）：`ComponentManager.cs:274,277` 恢复 `SkillManager`/`CommandRegistry` 接口注入赋值（ExtensionContext 构造点在此，:265-278）+ `ExtensionManager.cs:205,213` 恢复 `context.SkillManager`/`context.CommandRegistry` 读取逻辑（GetSkillPaths 暂存变量改回接口调用）。

- [ ] **Step 3: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent.Abstractions -v q && dotnet build src/Seeing.Agent -v q && dotnet build src/Seeing.Gateway -v q && dotnet build src/Seeing.Agent.Memory -v q && dotnet build src/Seeing.Agent.Acp -v q`
预期：Abstractions 与 Gateway 必须 SUCCEEDED；主库与扩展包 SUCCEEDED（Memory/Acp 实现 IExtension，ExtensionContext 补齐后需复核）
```bash
git add -A
git commit -m "refactor: 迁移技能/命令/配置/组件契约到 Abstractions，ComponentType 开放化"
```

### Task 13: 全量编译修复（src + plugs + samples + tests）— Phase 1 完成标志

**Files:**
- Modify: `src/Seeing.Agent/GlobalUsings.cs`（已处理于 Task 7 Step 4，此处核对）
- Modify: src/plugs/samples/tests 全部项目的 using

- [ ] **Step 1: 全库 using 批量修复**

对 **src、plugs、samples、tests** 全部项目执行批量替换（逐个错误处理）：

```powershell
Get-ChildItem src,plugs,samples,tests -Recurse -Filter *.cs | ForEach-Object {
  $c = Get-Content $_.FullName -Raw
  $n = $c.Replace("using Seeing.Agent.Core.Events;","using Seeing.Agent.Abstractions.Events;")
        .Replace("using Seeing.Agent.Core.Hooks;","using Seeing.Agent.Abstractions.Hooks;")
        .Replace("using Seeing.Agent.Core.Todo;","using Seeing.Agent.Abstractions.Todo;")
        .Replace("using Seeing.Agent.Core.Permission;","using Seeing.Agent.Abstractions.Permissions;")
        .Replace("using Seeing.Agent.MCP.Core;","using Seeing.Agent.Abstractions.Mcp;")
  if ($c -ne $n) { Set-Content -LiteralPath $_.FullName -Value $n -NoNewline }
}
```

> **注意编码**：PS7 `Set-Content` 默认 utf8NoBOM。替换前先检查仓库文件是否含 BOM（`git ls-files | Select-String ...` 或抽查 `GlobalUsings.cs` 十六进制头）；若含 BOM，改用 `[System.IO.File]::WriteAllText($_.FullName, $n, New-Object System.Text.UTF8Encoding($true))`。commit 前检查 `git diff --stat` 无异常全文件重写。

> **重要**：替换表**不含** `using Seeing.Agent.Core.Models;` 与 `using Seeing.Agent.Core.Interfaces;`——这两个命名空间是"混合区"（既含已迁移类型 AgentDefinition/ToolContext，又含未迁移类型具体类/IAgent），**整行替换会破坏未迁移类型的引用**。涉及这两个 using 的文件必须按编译报错逐个手动处理（把已迁移类型的使用点改为 Abstractions using，未迁移类型保持）。samples 重点文件：WebUI 的 `AppState.cs`/`BlazorPermissionChannel.cs`/`McpStateService.cs` 等引用 Core.Interfaces/Llm/Core.Events/Core.Permission/MCP.Core 的 using 更新。

- [ ] **Step 2: 全量构建**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 60`
预期：全部项目 BUILD SUCCEEDED（Phase 1 完成标志）

- [ ] **Step 3: 全量测试**

运行：`dotnet test Seeing.Agent.slnx --no-build 2>&1 | Select-String "Passed!|Failed!|error" | Select-Object -First 20`
预期：Passed!（迁移不改行为，测试应全绿；若有失败，修复为 namespace 引用问题）

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: 全量修复 namespace 引用，Phase 1 完成"
```

---

## Phase 2：核心重新设计落地

### Task 14: 删除 IAgent/AgentBase/AgentDecorator，新建 IAgentExecutor（类型层）

> **任务边界**：本任务完成 IAgent 体系的**类型级**替换（删除/新建/调用方类型切换），不动 AgentExecutor 签名（Task 15 执行链切换）。`AgentContext.History` 在 Task 15 随 4 参化一并删除（Task 8 已保留）。

**Files:**
- Delete: `src/Seeing.Agent/Core/Interfaces/IAgent.cs`
- Delete: `src/Seeing.Agent/Core/Abstractions/AgentBase.cs`
- Delete: `src/Seeing.Agent/Core/AgentDecorator.cs`
- Create: `src/Seeing.Agent.Abstractions/Agents/IAgentExecutor.cs`
- Modify: `src/Seeing.Agent/Core/Background/BackgroundTaskManager.cs:302-321`
- Modify: tests 中 `Mock<IAgent>` 9 处 + 3 个 AgentBase 桩子类 + `Mock<IAgentExecutionRouter>` 5 处 + `BackgroundTaskManagerTests.cs` 8 处 Setup

- [ ] **Step 1: 新建 IAgentExecutor**

```csharp
// src/Seeing.Agent.Abstractions/Agents/IAgentExecutor.cs
namespace Seeing.Agent.Abstractions.Agents;

public interface IAgentExecutor
{
    IAsyncEnumerable<IMessageEvent> ExecuteAsync(
        AgentDefinition definition,
        IReadOnlyList<ChatMessage> messages,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: 删除三个文件**

```powershell
git rm src/Seeing.Agent/Core/Interfaces/IAgent.cs
git rm src/Seeing.Agent/Core/Abstractions/AgentBase.cs
git rm src/Seeing.Agent/Core/AgentDecorator.cs
```

- [ ] **Step 3: 修主库编译错误**

逐一处理（清单见设计文档 §6.1）：
- `ToolContext.Agent` → `AgentDefinition?`（Task 10 已改 ✓，核对）
- `IExecutionContext.ActiveAgent` → `AgentDefinition?`（Task 12 已改 ✓，核对）
- `AgentManager.GetOrCreateAgentInstance`（:304-316）与 `AgentInfoWrapper`（:860）→ **本任务删除**（Task 8 已确认除 BackgroundTaskManager 外无其他调用方，BackgroundTaskManager 本任务同步改造）
- 其余 `IAgent` 引用点：改为 `AgentDefinition` 或删除

**BackgroundTaskManager.cs:302-321 切换**（唯一残留的 GetOrCreateAgentInstance 生产调用方 + IAgent.ExecuteAsync 调用；**BackgroundTaskManager 字段仅 `_agentRegistry/_sessionManager/_logger`（:16-23），无 `_services`——构造注入 IAgentExecutor**；`args.Input` 为 `Llm.ChatMessage`（BackgroundTaskInfo.cs:88），非 string）：
```csharp
// 构造：private readonly IAgentExecutor _executor;（构造函数加参，DI 提供）
// 原：var agent = _agentRegistry.GetOrCreateAgentInstance(args.AgentName);
//     ...agent.ExecuteAsync(args.Input, args.Context, cancellationToken)
// 改：
var def = await _agentRegistry.GetAgentAsync(args.AgentName)
    ?? throw new InvalidOperationException($"Agent '{args.AgentName}' 不存在");
var messages = new List<ChatMessage> { args.Input };   // 原单条入参 → 单元素列表，语义等价
await foreach (var message in _executor.ExecuteAsync(def, messages, args.Context, cancellationToken))
{
    // 原循环体不变（Content 输出 / ToolCalls 输出）
}
```
> **注**：`args.Context` 为 AgentContext（History 字段仍在，Task 15 删除后此构造不受影响）；IAgentExecutor 的 DI 注册在 Task 15，本任务内编译通过即可（运行时由 Task 15 恢复）。

- [ ] **Step 4: tests 同步改造（与删除同一 Task 内完成，保证 slnx 全量绿）**

- 9 处 `Mock<IAgent>` → `Mock<AgentDefinition>` 或直接构造（模式）：
```csharp
// 原：Mock<IAgent> agent = new();
// 改：var definition = new AgentDefinition { Name = "test", Mode = AgentMode.SubAgent };
```
- 3 个 AgentBase 桩子类（`tests/Seeing.Agent.Scheduler.Tests/SchedulerIntegrationTests.cs:440`、`SchedulerEngineTests.cs:315`、`Wave1IntegrationTests.cs:203`）→ 构造 AgentDefinition
- 5 处 `Mock<IAgentExecutionRouter>`/注册 → `Mock<IAgentExecutor>`。**注意：Moq 表达式强绑定接口签名**——`SchedulerEngineTests.cs:274`、`SchedulerIntegrationTests.cs:409` 等处的 3 参 Setup/Returns（`Setup(r => r.ExecuteAsync(It.IsAny<AgentDefinition>(), It.IsAny<AgentContext>(), It.IsAny<CancellationToken>()))`）改接口类型后**必须同步 4 参化**（插入 `It.IsAny<IReadOnlyList<ChatMessage>>()` 为第二个参数），否则 CS1501 编译失败。**回调体暂用 `ctx.History`**（History 在 Task 15 才删，Task 14 可编译；Task 15 Step 5 统一替换为 messages）
- `tests/Seeing.Agent.Tests/Background/BackgroundTaskManagerTests.cs` 8 处 `Setup(r => r.GetOrCreateAgentInstance("test-agent"))` → `Setup(r => r.GetAgentAsync("test-agent")).ReturnsAsync(definition)`，并按 Step 3 的执行链切换调整断言（executor mock 注入构造）
- 若测试直接调用 IAgent.ExecuteAsync 的位置（如 Wave1IntegrationTests 执行段），同步改为 `IAgentExecutor` 调用

- [ ] **Step 5: 验证 + Commit**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 30`
预期：Build succeeded
```bash
git add -A
git commit -m "refactor: 删除 IAgent/AgentBase/AgentDecorator，新建 IAgentExecutor 契约"
```

### Task 15: 执行链切换（AgentExecutor 4 参 + History 删除 + 执行器改名）

> **任务边界**：AgentExecutor 4 参化、`AgentContext.History` 删除（含全部构造点/读取点）、执行器改名、注入点与 DI 更新。与 Task 14 共同构成完整执行链切换——**中间态（Task 14 后）编译通过但执行器未接 IAgentExecutor**，本任务完成后行为恢复。

**Files:**
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`（ExecuteAsync 签名 :78-81、History 读取 :116,120；构造注入点）
- Modify: `src/Seeing.Agent.Abstractions/Agents/AgentContext.cs`（**删除 History**）
- Rename: `src/Seeing.Agent/Core/NativeAgentExecutionRouter.cs` → `src/Seeing.Agent/Core/NativeAgentExecutor.cs`
- Rename: `src/Seeing.Agent.Acp/Execution/AcpAgentExecutionRouter.cs` → `src/Seeing.Agent.Acp/Execution/AcpAgentExecutor.cs`
- Delete: `src/Seeing.Agent/Core/Interfaces/IAgentExecutionRouter.cs`（改名后死接口）
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:626`（注册行）
- Modify: 全部 IAgentExecutionRouter 注入点（ChatOrchestrator.cs:30,44 / ExecutionJobService.cs:316 / HeartbeatJob.cs:23,35 / AgentJob.cs:22,33 / TaskTool.cs:363-368 / BackgroundTaskManager 核对）
- Modify: History 全部使用点（下述清单）
- Modify: 引用执行器的测试（改名/签名适配）

- [ ] **Step 1: AgentExecutor 签名改造 + History 删除**

```csharp
// AgentExecutor.cs 现有 :78-81 改为
public IAsyncEnumerable<IMessageEvent> ExecuteAsync(
    AgentDefinition agent,
    IReadOnlyList<ChatMessage> messages,
    AgentContext context,
    CancellationToken cancellationToken = default)
```
内部 `UserInput = context.History.LastOrDefault()?.Content`（:116）→ `UserInput = messages.LastOrDefault()?.Content`；`var messages = context.History.ToList()`（:120）→ 直接使用入参（变量改名避免冲突，如 `var history = messages.ToList()`）。**`AgentContext.History` 属性从 Abstractions 删除**。

- [ ] **Step 2: 执行器改名**

`NativeAgentExecutionRouter` → `NativeAgentExecutor`，实现 `IAgentExecutor`（4 参），签名补 `messages` 参数并透传。`AcpAgentExecutionRouter` → `AcpAgentExecutor`，同样适配（ACP 包内 History 读取点 AcpPassthroughExecutor.cs:69、ContentBlockMapper.cs:17 改为接收 messages 入参）。`git rm src/Seeing.Agent/Core/Interfaces/IAgentExecutionRouter.cs`。

- [ ] **Step 3: History 全部使用点改造**

先运行 `Get-ChildItem src,tests,samples -Recurse -Filter *.cs | Select-String -Pattern "\.History\b" | Select-Object Path,LineNumber,Line` 得到全量清单，**区分两类**：
- **`AgentContext.History`**（删除对象）→ 改为显式 messages 入参：
  - `AgentExecutor.cs:116,120`（Step 1 已改）
  - `TaskTool.cs:358` `agentContext.History.Add(...)` → `var messages = new List<ChatMessage> { new ChatMessage { Role = ChatRole.User, Content = prompt } };`（:368 调用补 messages 入参）
  - `HeartbeatJob.cs:289-299`、`AgentJob.cs:309-319` History 构造 → 改为 messages 列表变量（后续传给执行器）
  - `ExecutionJobService.cs:377,800,850` → 从会话读取后作为 messages 入参（**复用现有转换 `ExecutionJobService.BuildHistoryFromSession`（:746）**；`:534` 是 **ChatExecutionContext.History**（App 独立类型）保留不动）
  - `ChatExecutionContext.cs:96` → 核对：若读的是 AgentContext.History 则改；若读自己的 History 属性则保留
  - `AcpPassthroughExecutor.cs:69`、`ContentBlockMapper.cs:17` → 接收调用方传入的 `IReadOnlyList<ChatMessage> messages`
- **`ChatExecutionContext.History`**（App 层独立类型属性）→ 保留，不在删除范围

- [ ] **Step 4: 注入点改造 + DI 注册**

`ChatOrchestrator`/`ExecutionJobService`/`HeartbeatJob`/`AgentJob` 的 `IAgentExecutionRouter` → `IAgentExecutor`，调用处补 `messages` 参数；TaskTool 中 `GetService(typeof(AgentExecutor))` → `GetService(typeof(IAgentExecutor))`；`ServiceCollectionExtensions.cs:626` 注册 `IAgentExecutor`（AgentExecutor 现实现 IAgentExecutor）。

- [ ] **Step 5: tests 签名适配**

Task 14 已 4 参化的 Mock 处，**回调体内 `ctx.History` → `messages.LastOrDefault()` 替换**（History 已删除）；其余引用执行器的测试（改名后 NativeAgentExecutor/AcpAgentExecutor 相关）补 `messages` 参数（如 `new List<ChatMessage>()`）。

- [ ] **Step 6: 验证 + Commit**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 30`
预期：Build succeeded
```bash
git add -A
git commit -m "refactor: AgentExecutor 4 参签名，删除 AgentContext.History，执行器改名并接线 IAgentExecutor"
```

### Task 16: IAgentRegistry 拆分 + IAgentManager 删除（原 Task 17）

**Files:**
- Modify: `src/Seeing.Agent.Abstractions/Agents/IAgentRegistry.cs`（15 方法拆分为注册/查询/权限筛选）
- Modify: `src/Seeing.Agent.Abstractions/Agents/IAgentRuntimeManager.cs`（**移除**默认 Agent 方法，不新增）
- Delete: `src/Seeing.Agent/Configuration/IAgentManager.cs`
- Modify: `src/Seeing.Agent/Configuration/AgentManager.cs`（实现拆分接口；删除 AgentInfoWrapper（:860）/GetOrCreateAgentInstance（:304-316，Task 14 已删主逻辑，核对）/GetAgentWithMergedConfigAsync（:146）；MD 覆盖逻辑 ApplyMdOverridesAsync（:101）保留内部）
- Modify: 消费者（WebUI/Cli/App 中 `IAgentManager` 注入点改 `IAgentRegistry`/`IAgentRuntimeManager`）

- [ ] **Step 1: 定义拆分后 IAgentRegistry**

```csharp
// IAgentRegistry.cs（拆分后）
public interface IAgentRegistry
{
    Task<IReadOnlyList<AgentDefinition>> GetAgentsAsync();
    Task<AgentDefinition?> GetAgentAsync(string name);
    Task<IReadOnlyList<AgentDefinition>> GetSubAgentsAsync();
    Task<IReadOnlyList<AgentDefinition>> GetTaskableAgentsAsync();
    Task<IReadOnlyList<AgentDefinition>> GetPrimaryAgentsAsync();
    Task<IReadOnlyList<AgentDefinition>> GetAccessibleSubAgentsAsync(IReadOnlyList<PermissionRuleEntry> callerPermissions);
    Task RegisterAgentAsync(AgentDefinition agentInfo);
    bool UnregisterAgent(string name);
    bool HasAgent(string name);
}
```

- [ ] **Step 2: IAgentRuntimeManager 移除默认 Agent 方法**

`GetDefaultAgentNameAsync()`/`SetDefaultAgentAsync(string)` 等默认 Agent 方法**已存在**于 IAgentRuntimeManager（:34,40,44，实现于 AgentRuntimeManager.cs:138,156）——本步骤从 `IAgentRegistry`（:62）移除这些方法 + 删除 `AgentManager` 的转发（AgentManager.cs:254-262）+ 消费者改注（如 AgentCommands.cs:65）。**不要重复添加**。

- [ ] **Step 3: 删除 IAgentManager + 改造 AgentManager**

`git rm src/Seeing.Agent/Configuration/IAgentManager.cs`；`AgentManager` 实现 `IAgentRegistry`（+内部配置逻辑保留，供注册表使用）；`AgentManager.cs:146 GetAgentWithMergedConfigAsync` 删除（无调用方，Task 8 已确认）、`:304-316 GetOrCreateAgentInstance` 与 `:860 AgentInfoWrapper` 删除（Task 14 已删主逻辑，核对）；MD 覆盖逻辑（ApplyMdOverridesAsync）保留为内部方法。消费者改注 `IAgentRegistry`/`IAgentRuntimeManager`。

- [ ] **Step 4: 验证 + Commit**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 30 && dotnet test tests/Seeing.Agent.Tests --no-build 2>&1 | Select-String "Passed!|Failed!"`
预期：Build succeeded、Passed!（AgentManager 相关测试若因接口变化失败，同步更新测试引用）
```bash
git add -A
git commit -m "refactor: IAgentRegistry 拆分，删除 IAgentManager"
```

### Task 17: ToolContext 瘦身收尾（ToolSinkAdapter 统一接线）（原 Task 18）

**Files:**
- Create: `src/Seeing.Agent/Core/ToolSinkAdapter.cs`
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`（构造 ToolContext 时注入 ToolSinkAdapter 实例）

- [ ] **Step 1: ToolSinkAdapter**

```csharp
// src/Seeing.Agent/Core/ToolSinkAdapter.cs
namespace Seeing.Agent.Core;

internal sealed class ToolSinkAdapter : IToolEventSink, IToolMetadataSink
{
    private readonly Func<IMessageEvent, ValueTask> _emit;
    private readonly Action<string, Dictionary<string, object>?> _setMetadata;

    public ToolSinkAdapter(Func<IMessageEvent, ValueTask> emit, Action<string, Dictionary<string, object>?> setMetadata)
    {
        _emit = emit;
        _setMetadata = setMetadata;
    }

    public ValueTask EmitAsync(IMessageEvent evt) => _emit(evt);
    public void SetMetadata(string key, Dictionary<string, object>? value) => _setMetadata(key, value);
}
```

- [ ] **Step 2: AgentExecutor 接线**

`AgentExecutor` 构造 `ToolContext` 处（搜索 `EmitAsync =` / `SetMetadata =` 赋值点）：改为 `EventSink = new ToolSinkAdapter(emit, setMetadata), MetadataSink = new ToolSinkAdapter(emit, setMetadata)`（同一实例实现两接口）。

- [ ] **Step 3: 验证 + Commit**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 30`
预期：Build succeeded
```bash
git add -A
git commit -m "refactor: ToolContext 委托字段替换为 IToolEventSink/IToolMetadataSink 接线"
```

### Task 18: Todo 体系重构（ITodoStore 实现 + 删除孤儿 + AgentExecutor 接入）（原 Task 19）

> **顺序说明**：ITodoStore 契约（Task 6）+ TodoItem 迁移（Task 6）已就绪。本 Task 一次性完成实现/注册/执行器接入/工具改造/测试，避免跨 Task 出现"契约无实现"的空窗。

**Files:**
- Create: `src/Seeing.Agent/Todo/SessionContextTodoStore.cs`
- Delete: `src/Seeing.Agent/Core/Todo/TodoManager.cs`、`ITodoManager.cs`
- Delete: `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoReadTool.cs`
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Todo/TodoWriteTool.cs`（:16,21,71-78 → ITodoStore）
- Modify: `src/Seeing.Agent/Core/AgentExecutor.cs`（构造注入 ITodoStore :39,55；LoadTodoList :823-833 改 ITodoStore；**AgentExecutor 构造 10 参 → 11 参**——**全仓无 `new AgentExecutor` 直接构造**（src/tests/samples/plugs 已 grep 确认），仅 DI 注册受影响）
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:578`（ITodoManager 注册 → ITodoStore）

- [ ] **Step 1: SessionContextTodoStore**

```csharp
// src/Seeing.Agent/Todo/SessionContextTodoStore.cs
using Seeing.Agent.Abstractions.Todo;
using Seeing.Session.Core;

namespace Seeing.Agent.Todo;

public sealed class SessionContextTodoStore : ITodoStore
{
    private const string TodoContextKey = "todos";
    private readonly ISessionManager _sessionManager;

    public SessionContextTodoStore(ISessionManager sessionManager)
        => _sessionManager = sessionManager;

    public Task<TodoList> LoadAsync(string sessionId)
    {
        var session = _sessionManager.Get(sessionId);
        var items = session?.GetContext<List<TodoItem>>(TodoContextKey) ?? new List<TodoItem>();
        return Task.FromResult(new TodoList { SessionId = sessionId, Items = items });
    }

    public Task SaveAsync(string sessionId, TodoList todos)
    {
        var session = _sessionManager.Get(sessionId);
        if (session == null)
            throw new InvalidOperationException($"会话不存在: {sessionId}");
        session.SetContext(TodoContextKey, todos.Items);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: TodoWriteTool 改造**

构造注入 `ISessionManager` → `ITodoStore`；`ExecuteAsync` 中：
```csharp
// 原：var session = _sessionManager.Get(context.SessionId); session.SetContext(TodoContextKey, todos);
// 改：
try
{
    await _todoStore.SaveAsync(context.SessionId, new TodoList { SessionId = context.SessionId, Items = todos });
}
catch (Exception ex)
{
    return Failure($"保存 Todo 失败: {ex.Message}");
}
```
`internal const string TodoContextKey` 删除（魔法键封装进 Store）。

- [ ] **Step 3: AgentExecutor 接入 + 删除孤儿 + DI 更新**

- AgentExecutor 构造注入 `ITodoStore`（替换 ISessionManager 的 Todo 用途，**保留 `_sessionManager`**——ResolvePolicy :733 读 PermissionSnapshot 仍需要；AgentExecutor 构造 10 参 → 11 参，**全仓无 `new AgentExecutor` 直接构造**，仅 ServiceCollectionExtensions.cs:578 的 DI 注册受影响）；`LoadTodoList`（:823-833）改为 `return await store.LoadAsync(sessionId);`
- ```powershell
git rm src/Seeing.Agent/Core/Todo/TodoManager.cs src/Seeing.Agent/Core/Todo/ITodoManager.cs src/Seeing.Agent/Tools/BuiltIn/Todo/TodoReadTool.cs
```
- `ServiceCollectionExtensions.cs:578`：`AddSingleton<ITodoManager, TodoManager>()` → `AddSingleton<ITodoStore, SessionContextTodoStore>()`

- [ ] **Step 4: 新增 ITodoStore 测试**

创建 `tests/Seeing.Agent.Tests/Todo/SessionContextTodoStoreTests.cs`（**SessionData 无带参构造函数**，仅隐式无参 + 静态 `Create()`——用无参构造 + 属性初始化；Id 若为只读，改用 `SessionData.Create()` 后通过可写属性或直接操作 SessionData 公开字段/属性构造实例）：

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Todo;
using Seeing.Session.Core;

namespace Seeing.Agent.Tests.Todo;

public class SessionContextTodoStoreTests
{
    private sealed class FakeSessionData : SessionData { }

    private static (SessionContextTodoStore store, Mock<ISessionManager> manager, FakeSessionData session) CreateSut()
    {
        var session = new FakeSessionData { Id = "s1" };
        var manager = new Mock<ISessionManager>();
        manager.Setup(m => m.Get("s1")).Returns(session);
        return (new SessionContextTodoStore(manager.Object), manager, session);
    }

    [Fact]
    public async Task SaveAsync_Then_LoadAsync_ShouldRoundtrip()
    {
        var (store, _, _) = CreateSut();
        var todos = new TodoList { SessionId = "s1", Items = { new TodoItem { Content = "任务1", Status = TodoStatus.Pending } } };

        await store.SaveAsync("s1", todos);
        var loaded = await store.LoadAsync("s1");

        loaded.Items.Should().HaveCount(1);
        loaded.Items[0].Content.Should().Be("任务1");
    }

    [Fact]
    public async Task LoadAsync_WithoutSave_ShouldReturnEmptyList()
    {
        var (store, _, _) = CreateSut();

        var loaded = await store.LoadAsync("s1");

        loaded.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_WhenSessionMissing_ShouldThrow()
    {
        var (store, manager, _) = CreateSut();
        manager.Setup(m => m.Get("missing")).Returns((SessionData?)null);
        var todos = new TodoList { SessionId = "missing" };

        var act = async () => await store.SaveAsync("missing", todos);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*会话不存在*");
    }
}
```

> **注意**：若 `SessionData.Id` 无公开 setter，改为调用静态 `SessionData.Create()` 后经 `GetContext/SetContext` 可用的实例（Id 由 Create 内部赋值），`CreateSut` 中 `session` 构造方式按实际调整。

- [ ] **Step 5: 验证 + Commit**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 30 && dotnet test tests/Seeing.Agent.Tests --no-build 2>&1 | Select-String "Passed!|Failed!"`
预期：Build succeeded、Passed!
```bash
git add -A
git commit -m "refactor: Todo 端口-适配器化（ITodoStore），删除 TodoManager/TodoReadTool 孤儿代码"
```

### Task 19: AcpTool 魔法键透传审查（原 Task 20）

**Files:**
- Modify: `src/Seeing.Agent.Acp/Tools/AcpTool.cs:236-238`
- Modify: `src/Seeing.Agent/Core/Models/AgentModels.cs` → `src/Seeing.Agent.Abstractions/Agents/AgentModels.cs` 的 `AgentContextKeys`（新增常量）

- [ ] **Step 1: 审查三个键的消费方**

运行：`Get-ChildItem src -Recurse -Filter *.cs | Select-String -Pattern "parentSessionId|taskDescription|acpBackend" | Select-Object Path,LineNumber,Line`

预期：得到三个键的写入方（AcpTool）与读取方清单。

- [ ] **Step 2: 按审查结果改造**

若读取方仅在 ACP 会话初始化路径（AcpPassthroughExecutor 等），则将三个值改为 `AgentContext.Metadata` 透传（**`AgentContextKeys` 现有常量仅 RequestModelId/AcpModeId（AgentModels.cs:235-238），需新增 3 个类型化常量** `ParentSessionIdKey`/`TaskDescriptionKey`/`AcpBackendKey`，Metadata 已是 Context 的正式字段），AcpTool 不再调用 `session.SetContext`。若读取方依赖 Session 持久化（重启后恢复），则保留 Session 存储但改用类型化属性（SessionData 增加强类型属性，SetContext 降级为内部 API）。

- [ ] **Step 3: 验证 + Commit**

运行：`dotnet build src/Seeing.Agent.Acp -v q 2>&1 | Select-String "error|Build succeeded" && dotnet test tests/Seeing.Agent.Acp.Tests --no-build 2>&1 | Select-String "Passed!|Failed!"`
预期：Build succeeded、Passed!
```bash
git add -A
git commit -m "refactor: AcpTool 魔法键透传改为类型化 Metadata"
```

---

## Phase 3：Gateway 解耦

### Task 20: Seeing.Gateway 移除主库引用（原 Task 21）

**Files:**
- Modify: `src/Seeing.Gateway/Seeing.Gateway.csproj:19`（- Seeing.Agent，+ Seeing.Agent.Abstractions）
- Modify: `src/Seeing.Gateway/Mapping/GatewayEventMapper.cs`（using 更新）
- Modify: `src/Seeing.Gateway/Models/GatewayTokenUsage.cs`（using 更新）
- Modify: `src/Seeing.Gateway/Models/GatewayEvent.cs`（注释引用更新）

- [ ] **Step 1: csproj 引用调整**

`Seeing.Gateway.csproj`：`<ProjectReference Include="..\Seeing.Agent\Seeing.Agent.csproj" />` 删除，添加 `<ProjectReference Include="..\Seeing.Agent.Abstractions\Seeing.Agent.Abstractions.csproj" />`。

- [ ] **Step 2: using 更新**

`GatewayEventMapper.cs:1-2`：`using Seeing.Agent.Core.Events;` → `using Seeing.Agent.Abstractions.Events;`；`using Seeing.Agent.Llm;` → `using Seeing.Agent.Abstractions.Llm;`。`GatewayTokenUsage.cs:1` 同理。`GatewayEvent.cs:45` 的 cref 注释改为 `Seeing.Agent.Abstractions.Events.IMessageEvent`。

- [ ] **Step 3: 验证零引用**

运行：`dotnet build src/Seeing.Gateway -v q`
预期：BUILD SUCCEEDED
运行（**正则匹配**，`using Seeing.Agent;` 精确子串会漏掉 `using Seeing.Agent.Core.Events;` 等变体）：
```powershell
Get-ChildItem src/Seeing.Gateway -Recurse -Filter *.cs | Select-String -Pattern "using Seeing\.Agent(\.|;)" | Measure-Object | Select-Object Count
```
预期：Count = 0

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: Seeing.Gateway 移除主库依赖，仅依赖 Abstractions（协议层独立）"
```

---

## Phase 4：验证收尾

### Task 21: 测试改造 + 全量验证（原 Task 22）

**Files:**
- Modify: 其余受迁移影响的测试 using（Task 14/15 已改 Mock<IAgent>/桩子类/Router Mock/执行器签名，本 Task 收尾）
- Modify: 若有遗留的测试失败修复

- [ ] **Step 1: 全量构建 + 测试**

运行：`dotnet build Seeing.Agent.slnx -v q 2>&1 | Select-String "error|Build succeeded" | Select-Object -First 30`
预期：Build succeeded
运行：`dotnet test Seeing.Agent.slnx --no-build 2>&1 | Select-String "Passed!|Failed!"`
预期：Passed!

若失败：修复为迁移/重构相关的引用问题（测试代码中残留的 `using Seeing.Agent.Core.*`、直接构造已删除类型等）。

- [ ] **Step 2: 验证标准核对**

逐条核对设计文档 §11 的 7 条验证标准：
1. `Seeing.Gateway` 编译通过且零 `using Seeing.Agent;`（Task 20 已验）
2. 全量构建/测试绿（Step 1）
3. 删除项不存在：运行
```powershell
Get-ChildItem src -Recurse -Filter *.cs | Select-String -Pattern "AgentFactory|FromAgent|IAgentExecutionRouter|\bIAgent\b|AgentBase|AgentDecorator|ITodoManager|TodoReadTool|IAgentManager|RequiresFactory|AgentInfoWrapper|GetAgentWithMergedConfigAsync|GetOrCreateAgentInstance|context\.History|ConfigureServices\(" | Select-Object Path,LineNumber,Line
```
预期：无匹配（`ConfigureServices(` 若 Session 包等有同名方法需人工确认）
4. `TodoWriteTool`/`LoadTodoList` 无 ISessionManager 注入：grep 确认
5. 命名规范：Task 3 审计表逐项核对
6. 扩展包编译（Task 11/13 已验）
7. `AgentExecutor.ExecuteAsync` 4 参：grep 签名确认

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test: 测试改造适配解耦重构，全量验证通过"
```

### Task 22: 文档与规范落地（原 Task 23）

**Files:**
- Modify: `AGENTS.md`（依赖方向规范 + 命名规范 + 项目结构）
- Modify: `docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md`（状态：已实施）

- [ ] **Step 1: AGENTS.md 更新**

- 项目结构树：加 `src/Seeing.Agent.Abstractions/`，更新包说明
- 新增「依赖方向规范」章节（三层：原语层/主库层/扩展层，依赖只能向下，Abstractions 零实现纪律 + Seeing.Session 豁免说明）
- 新增「命名规范」章节（§5.1 后缀即职责表 + 层级关系 + 接口名=实现类名去 I）
- 「WHERE TO LOOK」表：新增 IAgentExecutor/ITodoStore/IExtensionManager 条目
- 更新「已知问题」表：勾选相关修复项

- [ ] **Step 2: 设计文档状态更新**

`2026-08-17-decoupling-refactor-design.md` 头部状态改为「已实施（2026-08-17 完成）」并追加实施记录（各 Phase 完成日期与提交号）。

- [ ] **Step 3: Commit**

```bash
git add AGENTS.md docs/superpowers/specs/2026-08-17-decoupling-refactor-design.md
git commit -m "docs: 落地依赖方向与命名规范，更新架构文档"
```

---

## 任务依赖关系

```
Task 1-3 (Phase 0 审查，无依赖，可并行)
Task 4 (建包) → 5 (Hooks，无跨组依赖)
Task 6 (Todo/Permissions/ConfigLevel，前置 DTO) → 7 (Events/Llm 基础类型，依赖 6)
Task 8 (Agents DTO，依赖 6/4) → 9 (Llm/Mcp 契约)
Task 10 (Tools 契约，依赖 7/8/6) → 11 (Extensions 拆分，依赖 9/10)
Task 12 (Skills/Commands/Configuration/Components，依赖 6；完成 Task 11 的 ICommandExtension 追加与 ExtensionContext 补齐收尾)
Task 13 (全量编译，依赖 5-12 全部)
Task 14 (删 IAgent + IAgentExecutor 类型层，依赖 8/10/13)
Task 15 (执行链切换：4 参 + History 删除 + 改名，依赖 14)
Task 16 (Registry 拆分，依赖 8/14)
Task 17 (Sink 接线，依赖 10/14)
Task 18 (Todo 重构，依赖 6 的 ITodoStore 契约 + 15)
Task 19 (AcpTool，依赖 15)
Task 20 (Gateway，依赖 13)
Task 21 → 22 (Phase 4)
```

## 提交策略

- 每个 Task 一个 commit，commit message 已给出
- Phase 1 每 Task 完成即可编译/测试（迁移不改行为）
- Phase 2 中 Task 14/15/16 破坏性较大，允许拆为 2 个 commit（删除 + 适配）但必须在同一 Task 内完成全部编译修复
- 涉及 tests/samples 的改动与主库改动同一 Task 提交（保证每个 commit 后 slnx 全量可构建）