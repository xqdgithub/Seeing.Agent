# 命令上下文重构设计：命令自包含 + 薄上下文

**日期:** 2026-08-20
**状态:** 已确认设计（待实施）

## 背景与问题

当前命令执行链存在隐式契约与宿主魔法逻辑：

1. **隐式输入变换契约**：`CommandContext.Input` 可变，命令修改它后，宿主 `ProcessCommandAsync` 事后检测 `Input != RawInput`，再启发式定位 session 中"最后一条 user 消息"并改写其内容。命令作者不知道此约定，宿主猜测命令意图——抽象破坏。
2. **定位启发式不可靠**：`LastOrDefault(Role == User)` 在 `SkipUserMessagePersist`（输入未落盘）或压缩后场景会误改旧消息。
3. **History 快照无意义**：`CommandContext.History` 宿主传入空列表，命令看不到任何会话状态；而 `CompressionService` 等已证明命令可通过 DI 注入服务自包含操作会话（加载/修改/保存/通知）。
4. **双通道并存矛盾**：输入变换走"命令声明 → 宿主写"，领域操作（压缩）走"命令自包含服务调用"——不对称，且输入变换通道绕远路：命令既然能注入 `ISessionManager` 自己改 `session.Messages`，宿主同步逻辑即为多余。

## 设计决策

**命令自包含 + 薄上下文**：命令通过 DI 注入服务直接操作会话（读历史、改消息、调领域服务），`CommandContext` 回归纯信息传递，框架层不做任何写同步。

### 1. 契约层（`src/Seeing.Agent.Abstractions/Commands/ICommand.cs`）

- `CommandContext.Input` 由 `set` 改为 `init`（只读）：消除隐式可变魔法，破坏性变更（当前仅内置 skill 命令依赖修改 Input，已接受）。
- `CommandContext.History` 删除：命令需要历史时注入服务自读（`session.GetActiveMessages()`）。依赖方向合规：Abstractions 允许引用原语层 `Seeing.Session`（豁免先例 `AgentDefinition.BudgetConfig`）。
- `CommandContext.RawInput` 删除：Input 只读后与 RawInput 恒等，无独立语义；命令读取输入统一用 `Input`。
- `CommandResult` **不新增字段**：维持 `Success/Message/ErrorMessage/ShouldExit/NeedsRefresh/ShouldContinue/Metadata` 现状。

### 2. 宿主层（`src/Seeing.Agent.App/Execution/ExecutionJobService.cs`）

`ProcessCommandAsync` 简化：

```csharp
// 解析命令名 → 按 Runtime 查找 → 构造薄上下文（仅信息传递）→ 执行 → 短路判定
var cmdContext = new CommandContext
{
    CommandName, Input, Arguments, SessionId,
    WorkspaceRoot, CancellationToken
};
var result = await command.ExecuteAsync(cmdContext, cancellationToken);
// 删除：Input != RawInput 检测、user 消息定位、消息内容同步（约 25 行）
// 保留：短路判定 yield CommandResultEvent（ShouldContinue/ShouldExit）
```

宿主职责回归纯编排：解析命令 → 执行 → 按结果决定是否继续 Agent → finally 统一保存会话。

### 3. 命令层（`src/Seeing.Agent.App/Commands/SkillCommands.cs`）

`SkillCommands` / `DynamicSkillCommand` 注入 `ISessionManager`（原语层接口，模式同 `BuiltInCommands`）：

```csharp
// /skill 与动态 skill 命令：展开模板后写入会话
var session = await _sessionManager.GetOrLoadAsync(context.SessionId, ct);
// 定位本次 user 消息：串行队列内提交时刚追加，[^1] 即本次输入；Role 校验防 SkipUserMessagePersist 误改旧消息
if (session.Messages.Count > 0 && session.Messages[^1].Role == MessageRole.User)
    session.Messages[^1].Content = expanded;   // 无需自行保存：宿主 finally 统一 SaveAsync

return CommandResult.Ok($"Loaded skill: {skillName}", needsRefresh: true, shouldContinue: true);
```

- `NeedsRefresh: true`：命令修改了消息内容，UI 需刷新展示。**已知边界**：宿主仅在短路命令（shouldContinue=false）时发布 `CommandResultEvent`，skill 命令（shouldContinue=true）的 NeedsRefresh 不达 UI——UI 刷新由后续 Agent 事件流自然完成（`LoopStartEvent.UserInput` 携带展开内容），仅当 Agent 空流输出时 UI 停留原文，属可接受边界。
- 消息定位责任下沉命令：命令最懂自己语义；宿主不再猜测。

## 命令自治边界约束

| 约束 | 说明 |
|------|------|
| 串行执行 | 命令在会话串行队列内执行，无并发写冲突 |
| 统一保存 | 命令修改的会话由宿主 finally `SaveAsync` 统一落盘，命令无需自行保存 |
| 真相模型 | 命令读写需遵守消息真相模型（`GetActiveMessages()`、`IsSummary` 摘要标记） |
| UI 刷新 | 命令修改会话可见内容时应返回 `NeedsRefresh: true`（仅短路命令达 UI；继续命令靠 Agent 事件流刷新） |
| 消息定位边界 | `[^1] + Role==User` 校验防"最后一条非本次输入"误改；**已知限制**：`SkipUserMessagePersist=true` 且历史最后一条恰好是 User 时仍可能覆盖旧消息（实践风险低：续跑输入为空不触发命令；根治需消息 ID 关联，YAGNI 不实施） |

## 收益

- 删除隐式契约：`Input` 只读、无 `Input != RawInput` 魔法、无宿主同步逻辑（约删 25 行）
- 删除无意义的 `History` 快照字段
- 双通道统一为单通道：命令自包含（`/compact` 走 CompactionRunner、skill 走 ISessionManager，模式一致）
- 框架层回归纯编排，命令完全自治

## 影响范围

| 文件 | 变更 |
|------|------|
| `src/Seeing.Agent.Abstractions/Commands/ICommand.cs` | `Input` 改 `init`；删除 `History` 字段 |
| `src/Seeing.Agent.App/Execution/ExecutionJobService.cs` | `ProcessCommandAsync` 删除同步逻辑与 History 传参 |
| `src/Seeing.Agent.App/Commands/SkillCommands.cs` | 注入 `ISessionManager`；`LoadSkill`/`DynamicSkillCommand` 直接写会话；`NeedsRefresh: true` |
| `src/Seeing.Agent.App/Commands/BuiltIn/BuiltInCommands.cs` | 无变更（已是自包含模式，作模式基准） |
| 测试 | 新增/调整：skill 命令写会话、SkipUserMessagePersist 防误改、宿主无同步逻辑 |

## 测试策略

1. **SkillCommands 测试**（新增）：`/skill` 展开后 `session.Messages[^1].Content == 展开内容`；`Role != User`（Skip 场景）时不改写；返回 `NeedsRefresh=true`。
2. **宿主回归**：`ExecutionJobServiceCommandShortCircuitTests` 不变（短路语义未动）；确认宿主不再有消息同步逻辑。
3. **全量回归**：Seeing.Session.Tests（81）/ Seeing.Agent.Tests（671）/ Seeing.Agent.Acp.Tests（40）。

## 非目标（YAGNI）

- 不新增 `CommandResult.InputReplacement` 字段
- 不给 CommandContext 传只读历史快照
- 不开放命令写权限到 session 结构之外的持久层