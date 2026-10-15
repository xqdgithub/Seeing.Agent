# 消息事件类型字符串化设计

**日期:** 2026-08-19
**状态:** 已确认

## 背景

`MessageEventType` 是封闭枚举，插件/扩展实现无法向其中新增事件类型——要发布自定义事件就必须修改框架枚举，破坏扩展性。

现有证据表明枚举已形同虚设：

1. `AppEventType` 枚举用 `(MessageEventType)AppEventType.X` 这种 int cast（值 1000+）绕过封闭枚举扩展值范围（`AppEvents.cs`）。
2. 前端 `ConsumeEventStreamAsync` 主要靠 `is` 类型判断（`evt is StreamDeltaEvent`）而非枚举 switch，枚举的编译期安全价值很低。
3. `CommandResult` 与 `Navigate` 在 `MessageEventType` 枚举和 `AppEventType` 中重复定义且 int 值不同，是潜在隐患。
4. 项目已有成熟约定：Hook 点是点分字符串（`{领域}.{事件}`，如 `tool.before_execute`），事件类型应沿用同一风格。

## 目标

将 `MessageEventType` 从枚举改为开放的字符串标识，使插件/扩展可自由发布自定义事件类型，同时消除 `AppEventType` 的 cast hack 与重复定义。

## 设计

### ① 枚举改为字符串常量类

将 `MessageEventType` 从 `enum` 改为 `static class`，成员从枚举值改为 `const string`，成员名保持不变、值变为点分小写字符串。成员名不变意味着 `MessageEventType.LoopStart` 这类引用写法无需改动。

```csharp
public static class MessageEventType
{
    public const string LoopStart = "loop.start";
    public const string LoopComplete = "loop.complete";
    public const string StreamStart = "stream.start";
    public const string StreamDelta = "stream.delta";
    public const string StreamComplete = "stream.complete";
    public const string ToolCallPending = "tool.call.pending";
    public const string ToolCallRunning = "tool.call.running";
    public const string ToolCallComplete = "tool.call.complete";

    [Obsolete("Use TaskStarted")]
    public const string SubAgentStarted = "subagent.started";
    [Obsolete("Use TaskCompleted")]
    public const string SubAgentCompleted = "subagent.completed";

    public const string TaskStarted = "task.started";
    public const string TaskProgress = "task.progress";
    public const string TaskCompleted = "task.completed";
    public const string TaskFailed = "task.failed";

    public const string PermissionRequest = "permission.request";
    public const string PermissionResponse = "permission.response";
    public const string LoopCancelled = "loop.cancelled";
    public const string Error = "error";
    public const string CommandResult = "command.result";
    public const string BudgetStatus = "budget.status";
    public const string Compaction = "compaction";
    public const string BudgetWarning = "budget.warning";
    public const string Navigate = "navigate";
    public const string TodoUpdate = "todo.update";
    public const string ModeUpdate = "mode.update";
}
```

### ② `IMessageEvent.Type` 改为 `string`

`IMessageEvent` 的 `Type` 属性类型从 `MessageEventType` 改为 `string`。所有事件 record 的 `public MessageEventType Type => ...` 改为 `public string Type => ...`；`ToolCallEvent`、`SubAgentEvent` 的 `public MessageEventType Type { get; init; }` 改为 `public string Type { get; init; }`。

返回值仍写 `MessageEventType.LoopStart` 等常量，写法不变（`const string` 可直接赋值给 `string`）。

### ③ 删除 `AppEventType` 枚举

删除 `src/Seeing.Agent.App/Events/AppEvents.cs` 中的 `AppEventType` 枚举，将 5 处 cast 改为字符串常量：

| 原写法 | 新字符串 |
|--------|----------|
| `(MessageEventType)AppEventType.SessionUpdated` | `"session.updated"` |
| `(MessageEventType)AppEventType.CommandResult` | `MessageEventType.CommandResult`（`"command.result"`） |
| `(MessageEventType)AppEventType.Navigate` | `MessageEventType.Navigate`（`"navigate"`） |
| `(MessageEventType)AppEventType.SkillContent` | `"skill.content"` |
| `(MessageEventType)AppEventType.SessionTitleChanged` | `"session.title.changed"` |

`CommandResult`、`Navigate` 与枚举中的重复定义在字符串化后自然统一。

`SessionUpdated`、`SkillContent`、`SessionTitleChanged` 是 App 层独有事件，作为**局部常量保留在 `AppEvents.cs`**（不并入 Abstractions 的 `MessageEventType`，保持 Abstractions 不感知 App 层应用事件）。

### ④ `EventStreamHandler` 的分发逻辑

`EventStreamHandler.cs` 中 22 分支的 `switch (evt.Type)`：21 个 `case MessageEventType.X:` 写法**无需改动**（`const string` 可作为 `switch` case label，字符串 switch 合法）。**唯一例外**：第 225 行的 `case (MessageEventType)AppEventType.SkillContent:` cast case 必须改为引用 App 层局部常量（如 `AppEventTypeConstants.SkillContent`，见 ③）或直接字符串字面量。可选优化：改为 `is` 类型判断，与 `ConsumeEventStreamAsync` 风格统一——非本变更必需，仅作可选项。

### ⑤ 其余引用点机械替换

以下文件中的 `MessageEventType` 类型标注/比较改为 `string`（`const string` 引用写法不变）：

- `src/Seeing.Agent.Acp/Mapping/AcpEventMapper.cs`
- `src/Seeing.Gateway/Mapping/GatewayEventMapper.cs`
- `src/Seeing.Gateway/Models/GatewayEvent.cs`（注释提及，无代码改动）
- `src/Seeing.Agent/Core/Events/TaskEventProjector.cs`
- `src/Seeing.Agent/Core/AgentExecutor.cs`
- `src/Seeing.Agent.TokenBudget/Events/BudgetEvents.cs`
- `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`（④ 的 cast case）
- 测试：`AcpEventMapperTests`、`TaskEventProjectorTests`、`GatewayEventMapperTests`

任何 `(MessageEventType)` 强制转换、`MessageEventType` 作为参数/变量/字典键类型标注的地方改为 `string`。

**测试中的 `nameof(MessageEventType.X)` 断言必须改为直接引用 `MessageEventType.X` 常量**（如 `GatewayEventMapperTests` 中 `result.SourceType.Should().Be(nameof(MessageEventType.LoopStart))` → 改 `MessageEventType.LoopStart`），否则 `nameof` 返回标识符字符串、与 `"loop.start"` 值断言必然失败。

`ToolCallEvent`/`SubAgentEvent` 的 `string Type { get; init; }` 需设默认值（`= string.Empty` 或 `required`）消除 CS8618 告警。

### ⑥ 扩展机制

插件/扩展只需实现 `IMessageEvent` 接口并让 `Type` 返回任意自定义字符串；前端 `is` 类型判断收不到即落入 `else` 兜底分支，天然兼容。内置事件类型通过 `MessageEventType` 常量类保留编译期安全引用，字符串值本身保持开放。

## 测试

- 单元测试：验证所有内置事件 record 的 `Type` 返回预期字符串常量（快照式断言，防止值漂移）。
- `EventStreamHandler` 分发回归：确保字符串 switch 后各分支行为不变（`Seeing.Agent.WebUI.Tests`）。
- `AcpEventMapper` / `GatewayEventMapper` / `TaskEventProjector` 映射回归测试。

## 非目标

- 不改前端 `ConsumeEventStreamAsync` 的 `is` 判断结构（除非顺手统一风格，不作为本变更目标）。
- 不改 `LoopPhase`、`ToolCallStatus` 等其他枚举——本变更仅针对 `MessageEventType`。
- 不涉及压缩系统收敛（子项目 ②，另行设计）。
