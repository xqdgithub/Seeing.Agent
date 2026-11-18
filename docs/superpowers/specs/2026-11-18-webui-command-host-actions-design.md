# WebUI 斜杠命令与宿主动作（Host Actions）设计

- 日期：2026-11-18
- 状态：已实现（P1）
- 相关：`docs/architecture/14-session-inbox-runtime-release-notes.md`（唯一执行入口）、`docs/architecture/19-tool-context-budgeting-release-notes.md`（上下文源与会话暴露态）、`docs/architecture/40-ui-extension-unified-architecture-release-notes.md`（UI 信封）

## 1. 问题与根因

用户在 WebUI 输入 `/model`、`/cron-list` 等命令后，界面没有任何反应。

已核实的链路：

1. `PromptInput` 把所有输入（含斜杠命令）原样交给 `SessionRuntime.DeliverAsync`。
2. `ExecutionRunner.ProcessCommandAsync` 在服务端执行命令，把结果塞进 `CommandResultEvent.Message`，并默认 `RemoveCommandMessage` 把命令文本从会话移除（`ExecutionRunner.cs:1077`、`1086`）。
3. WebUI 只消费该事件的两个字段：
   - `SessionWindow.razor:1420`：`NavigationTarget` → 导航
   - `SessionWindowTimelineSync.cs:221`：`NeedsRefresh` → 刷新时间线
   - `Message` **没有任何呈现**
4. TUI 相反：`TuiEventInterpreter.ApplyCommandResult` 把 `Message` 渲染成系统块（`TuiEventInterpreter.cs:289`）。

因此所有“纯输出型”命令在 WebUI 都表现为“执行了但画面不变”；`/clear`、`/compact`、`/fork` 因为有 `NeedsRefresh` / `NavigationTarget` 才看起来“有反应”。

另有一处不一致：WebUI 命令面板里列出的 `/undo`、`/redo`、`/share`、`/terminal`、`/init` 在服务端没有对应命令，输入后会被当普通消息发给 LLM。

## 2. 目标

1. **交互型命令**（`/model`、`/agent`、`/mcp`…）在 WebUI 打开对应的现有 UI，而不是只返回文本。
2. **数据型命令**（`/cron-list`、`/tools`、`/help`…）的结果在当前页面可见，**不落盘、不进 LLM 上下文**。
3. 机制对**插件贡献的命令**（MCP prompt、技能、自定义 Markdown、ACP）同样开放：命令声明“请求宿主执行某个动作”，宿主认识就执行，不认识就回退显示文本。
4. 动作是宿主的通用能力，不是命令名白名单；`/new`、`/fork`、`/clear` 等可以后续自然接入。
5. **子会话默认只读，接管后放开编辑**：编辑能力 = `!isChild || Owner == Human`。任意人类输入（含斜杠命令）在 `DeliverAsync` 投递时就会把有父的会话置为 `Owner=Human`（现有运行时行为，保留）；UI 另外提供显式“接管”按钮，供只想改配置、不想发消息的场景使用。ACP 透传会话的模型切换仍不生效。

## 3. 非目标

- 不做命令 CRUD，不改 `ICommandRegistry` 的注册/查找语义。
- 不改 TUI 的本地命令实现（TUI 继续在本地处理 `/model`、`/agent` 等）。
- 不做跨进程/远程动作调用；动作只在同一宿主进程内生效。
- 不把命令结果写入会话（不落盘）。
- 不在本期把全部命令迁移到动作通道；按第 9 节分期。

## 4. 已考虑的做法

| 做法 | 结论 | 原因 |
|------|------|------|
| 只按命令名在 WebUI 硬编码映射 | 否决 | 前端复制命令语义；插件命令无法声明交互 |
| 复用 `NavigationTarget` 编码动作（`app://model-picker`） | 否决 | 把 UI 动作混进 URL，弹窗类动作语义别扭，载荷受限 |
| **命令声明宿主无关动作 + 宿主动作注册表** | **采纳** | 契约可扩展；插件可参与；宿主不认识时天然回落 |
| 命令直接依赖 WebUI 组件/服务 | 否决 | 破坏分层（能力包只依赖 Abstractions），且非 WebUI 宿主会编译失败 |

## 5. 契约

### 5.1 命令动作（Abstractions/Commands）

新增 `CommandAction`（承载“命令想请宿主做什么”）：

```csharp
public sealed record CommandAction
{
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, object?> Payload { get; init; } = CommandAction.Empty;
}
```

- `Name` 是**宿主无关**的字符串 token；内置常量集中在 `CommandActionNames`，禁止调用方写魔法字符串。
- `Payload` 是弱类型字典（与 `CommandResult.Metadata` 同风格）；动作处理器自行解释。
- **不引入 `Applied` 之类的“已执行”标志**：P1 没有任何生产方与消费方，先发布语义未定的字段会在 P2 造成破坏性变更。若将来出现“服务端已执行、宿主只做同步”的动作，再以新的动作名或独立字段表达。

### 5.2 结果与事件透传

- `CommandResult` 增加 `CommandAction? Action { get; init; }`。
- `CommandResultEvent` 增加 `CommandAction? Action { get; init; }`（record 的 init 属性，保持既有形状；不改成 positional record）。
- `ExecutionRunner.ProcessCommandAsync` 在构造事件时原样透传 `result.Action`（唯一构造点，`ExecutionRunner.cs:1086`）。
- `GatewayEventMapper.MapCommandResult` 不读该字段，行为不变。

### 5.3 内置动作名

| 常量 | 名称 | 载荷 | WebUI 行为 |
|------|------|------|-----------|
| `ModelPicker` | `model.picker` | — | 打开模型选择弹窗 |
| `ModelSet` | `model.set` | `modelId` | 会话级切换模型（`Context.SetModelAsync`）。子会话在命令执行时已隐式接管；ACP 透传不可用 |
| `AgentPicker` | `agent.picker` | — | 展开 Agent 下拉 |
| `AgentSet` | `agent.set` | `agentId` | 会话级切换 Agent（`Context.SetAgentAsync`）。子会话在命令执行时已隐式接管；ACP 透传不可用 |
| `PageNavigate` | `page.navigate` | `path` | `NavigationManager.NavigateTo(path)` |
| `Notice` | `notice` | `text`、`level`（info/error） | 追加一条临时系统条目 |
| `SessionNew` | `session.new` | `title?` | 新建并切换会话（P2 才由 `SessionWindow` 注册处理器） |
| `SessionFork` | `session.fork` | — | 分支当前会话（P2） |
| `SessionClear` | `session.clear` | — | 清空当前会话（P2） |

**P1 的 `session.*` 常量只定义、不接线**：现有 `/new`、`/fork`、`/clear` 已通过服务端 + `NavigationTarget`/`NeedsRefresh` 正常工作，P1 不允许两条并行路径；`SessionWindow` 不注册这三个处理器，也没有命令发出这三个动作。

未列出的名称是**扩展动作**：插件可自造，但只有宿主显式注册了同名处理器才会被执行；否则按第 6.3 节回落。

## 6. 宿主侧

### 6.1 动作注册表（WebUI，circuit 级）

新增 `HostActionRegistry`（`samples/Seeing.Agent.WebUI/Services/`，Scoped）：

```csharp
public enum HostActionStatus
{
    /// 已由宿主处理
    Handled,
    /// 宿主认识该动作，但当前上下文不可用（如 ACP 透传下的 model.set）
    Unavailable,
    /// 宿主没有注册该动作
    NotHandled
}

public sealed record HostActionResult(HostActionStatus Status, string? Message = null);

public interface IHostActionRegistry
{
    /// 返回撤销句柄；同一 sessionId 重键时必须先撤销再注册
    IDisposable Register(string sessionId, string actionName, Func<CommandAction, Task<HostActionResult>> handler);
    Task<HostActionResult> InvokeAsync(string sessionId, CommandAction action);
}
```

- 键为 `(sessionId, actionName)`，避免会议大屏多窗口互相抢动作。
- **注册必须跟随窗口当前绑定会话**：`Session.razor` 切换 active 会话时复用同一个 `SessionWindow` 实例（Full 窗口没有 `@key`），所以不能只在 `OnInitialized` 注册一次。窗口在“会话加载完成”与“`SessionId` 参数变化”两个时机撤销旧键、按新 sessionId 重注册；子组件（`ComposerModelPicker`、`SessionToolbar`）使用同一条规则。
- 同一键重复注册：后注册者生效，记录一条 Warning（不静默覆盖）。
- `InvokeAsync` 找不到处理器 → `NotHandled`；处理器抛异常 → 记录日志并返回 `NotHandled`（按回落处理，不中断事件流）。

### 6.2 处理器注册点

| 组件 | 注册的动作 | 说明 |
|------|-----------|------|
| `ComposerModelPicker` | `model.picker` | 通过受控参数打开 `ModelSelectBadge` 的弹窗 |
| `SessionToolbar` | `agent.picker` | AntDesign `Select` 增加受控 `Open` |
| `SessionWindow` | `page.navigate`、`notice`、`model.set`、`agent.set` | 复用窗口已有的方法（`Context.SetModelAsync` / `SetAgentAsync`、导航、临时提示） |

组件在**会话绑定完成时**注册、`SessionId` 变化时换键、`Dispose` 撤销注册（见 6.1）。

### 6.3 事件处理与回落

把分派从 `SessionWindow.razor` 的 `OnHandlerChanged` 抽成宿主侧服务 `CommandResultDispatcher`（可单测），窗口只负责调用它：

1. **有 `Action`**：
   - `InvokeAsync(sessionId, action)`。
   - `Handled`：若返回 `Message` 非空，追加临时提示；不显示命令的 `Message`（避免“已打开面板”同时重复一段长文本）。
   - `Unavailable`：显示 `HostActionResult.Message`（说明为何不可用，如“ACP 透传会话不支持切换模型”），不显示命令的原始 `Message`。
   - `NotHandled`：回退显示 `CommandResultEvent.Message`（与 2 相同）。
2. **无 `Action` 且 `Message` 非空**：追加一条临时系统条目。
3. **`NavigationTarget`**：保持现状（先导航后呈现的顺序不变）。
4. **`NeedsRefresh`**：保持现状。

失败（`Success=false`）时临时条目使用错误样式；同时保留现有 `LastError`/提示条行为不变。

### 6.4 临时系统条目（不落盘）

`MessageTimelineStore` 增加：

```csharp
public void AppendTransientNotice(string key, string markdown, bool isError = false);
```

- 构造 `MessageViewModel { Id = key, Role = "system", Content = markdown, IsComplete = true }`，
  生成 `TimelineItem { Key = key, Kind = TimelineItemKind.System }` 追加到 `_items`。
- Key 统一加前缀 `cmd:`；同一命令的重复输出用时间戳后缀避免覆盖（如 `cmd:cron-list:20261118103000`）。
- `ResetFromSession`（切换会话、刷新、压缩重建）会清空 `_items`，临时条目随之消失 —— 符合“只在当前页面临时显示”。
- `ReconcileAppendFromSession` 不删除临时条目（不做反向对账）。
- 渲染复用现有 `TimelineItemKind.System → ChatMessageItem → 文本块`，不新增渲染组件。

### 6.5 子会话：默认只读，接管后放开编辑

会话入站运行时之后，子会话已允许输入并接管；但“可输入”不等于“默认可改配置”。规则：

```
canEdit = !isChild || Owner == Human
```

- 主会话（`!isChild`）始终可编辑。
- 子会话只有在 `Owner == Human`（已接管）时才可编辑；`Owner == Parent`（默认）时所有编辑入口禁用。
- `Owner == Self` 只出现在“子会话分叉为独立 Root”的场景（`isChild == false`），不参与本判定。

**编辑能力清单**（未接管时全部禁用；接管后与主会话一致）：

| 标记 | 未接管（子会话） | 已接管 |
|------|-----------------|--------|
| `ShowModelSelector` | false | true（ACP 透传仍由组件自身排除） |
| `ShowAcpModeSelector` | false | true |
| `AllowAutoApproveChange` | false | true |
| `CanRename` | false | true |
| `CanClear` | false | true |
| `CanEditOutboundBinding` | false | true |
| `CanCreateSession` | false | true |
| `CanBranch` | false | true |

保留子会话专属语义不变：`ShowSubAgentBadge`、`CanDetach`、`ShowReturnToParent`、`CanPushToParent`、`CanReleaseToParent`。

**接管入口**：子会话操作条（现有“推送产出 / 交还父会话”同一区域）在 `Owner != Human` 时显示“接管”按钮；点击后 `session.SetOwner(Human, by)` + `SaveAsync` + 刷新上下文。`Owner == Human` 时显示“交还父会话”（现有逻辑），交还后编辑能力立即收回。

**接管时机**（以现有运行时为准）：任何人类输入（普通消息或斜杠命令）经 `SessionRuntime.DeliverAsync` 投递时会自动置 `Owner=Human`（`SessionRuntime.cs:243-254`）。因此**命令层不增加“未接管拒绝”**：用户敲命令本身就是接管动作；UI 只需在收到会话更新后立刻放开编辑控件。

**命令层的行为**：
- `/model`、`/agent` 等动作在子会话里正常生效（命令执行时已接管）。
- ACP 透传会话的 `model.set` 由宿主返回 `Unavailable`（模型由后端决定）。
- `SessionToolbar` 需要把“子会话徽标/只读元信息”与“Agent 选择器”解耦：徽标照常渲染，Agent 下拉按 `canEdit` 独立渲染。

## 7. 命令侧改造（首批）

| 命令 | 现状 | 改造后 |
|------|------|--------|
| `/model` 无参 | 返回模型列表文本，WebUI 无呈现 | `Action = model.picker`；`Message` 保留列表作为回退 |
| `/model <id>` | 只校验并返回 metadata，实际切换无人执行 | `Action = model.set { modelId }`；`Message` 改为“请在支持交互的宿主中切换模型”（不得宣称已切换）。ACP 透传会话由宿主返回 `Unavailable` 并说明原因 |
| `/agent` 无参 | 返回 Agent 列表文本 | `Action = agent.picker`；`Message` 保留列表 |
| `/agent <name>` | 服务端 `SetDefaultAgentAsync`（全局默认） | 改为**只校验并返回** `agent.set { agentId }`；不再改全局默认（与 TUI 的会话级语义一致；破坏性变更，见 §11）。子会话同样生效 |
| `/mcp` 无参 | 返回服务器文本列表 | `Action = page.navigate { path: "/mcp" }`；`Message` 保留文本作为回退 |
| `/mcp <sub>` | 报错“未实现” | 不变 |
| `/cron-list`、`/tools`、`/help`、`/skill` 等 | 只有 `Message` | **不改命令**，由 6.3 的“无动作显示 Message”自动获得呈现 |

技能命令、MCP prompt 命令、Markdown 自定义命令继续走“展开 + 继续执行 Agent”，不受影响；它们想声明交互时，只需在 `CommandResult` 上设置 `Action`。

## 8. 非 WebUI 宿主

- **TUI**：已有本地 `/model`、`/agent` 处理，忽略 `Action`；服务端命令仍按 `Message` 显示（现有行为不变）。
- **Gateway**：`GatewayEventMapper` 不读 `Action`，继续把 `Message` 作为回复文本；交互动作不生效。
- **无交互宿主**（Headless）：同 Gateway，取得 `Message` 回退文本。

## 9. 分期

- **P1（本次）**：契约 `CommandAction` + 事件透传 + `HostActionRegistry` + `CommandResultDispatcher` + 临时条目 + `/model`、`/agent`、`/mcp` 接入 + 无动作命令自动呈现 + 子会话“默认只读、接管后放开编辑”（`ApplyCapabilities` 纳入 `Owner`、接管/交还按钮、`SessionToolbar` 徽标与选择器解耦）。
- **P2**：`session.new`、`session.fork`、`session.clear` 改为动作通道，WebUI 与 TUI 各自实现；服务端命令去掉对宿主 UI 的隐性假设。
- **P3**：为插件命令补文档与示例（技能 README、MCP prompt 说明、自定义 Markdown 命令），说明如何声明 `Action` 与未知动作的回落行为。
- **附带修复**：命令面板里的 `/undo`、`/redo`、`/share`、`/terminal`、`/init` 与服务端不一致 —— 本期只记录，不在本次实现（需要产品确认是删除还是补齐）。

## 10. 测试计划

| 层 | 用例 |
|----|------|
| 契约/执行器 | `CommandResult.Action` 被原样放进 `CommandResultEvent.Action` |
| 命令 | `/model` 无参 → `model.picker`；`/model x` → `model.set` 且 Message 不宣称已切换；`/mcp` → `page.navigate` |
| 动作注册表 | 按 sessionId 隔离；未注册 → `NotHandled`；`Unavailable` 透传原因；重复注册后注册者生效；处理器异常 → `NotHandled`；切换 sessionId 后旧键失效、新键生效 |
| 时间线 | `AppendTransientNotice` 追加可渲染的 System 条目；`ResetFromSession` 后消失；不进入 `GetActiveMessages`/LLM 历史 |
| 事件分派 | 有动作且 `Handled` → 不重复显示长文本；`Unavailable` → 显示原因；`NotHandled` → 回退显示原 `Message`；无动作 → 显示临时条目；`NeedsRefresh`/`NavigationTarget` 行为不变 |
| 子会话能力 | `ApplyCapabilities` 纳入 `Owner`：未接管子会话上述八项均为 false；`Owner=Human` 时与主会话一致；`Owner=Self` 的 Root 会话不受影响；子会话专属标记（`CanDetach`/`ShowReturnToParent`/`CanPushToParent`/`CanReleaseToParent`）不变 |
| 接管/交还 | 点“接管”后 `Owner=Human`、编辑放开；点“交还”后 `Owner=Parent`、编辑立即收回；隐式接管（发送消息/命令）后 UI 立即放开 |

手动验证：`/model`（打开弹窗并切换）、`/agent`（展开下拉并切换）、`/mcp`（跳转页面）、`/cron-list`、`/tools`、`/help`（临时条目可见，刷新后消失）。

## 11. 风险与边界

1. **`/agent` 语义变化**：服务端不再写全局默认 Agent。TUI 本来就按会话级处理；Gateway 不再能通过 `/agent` 改默认值。接受，因为“改默认但 UI 不同步”更糟。
2. **动作注册冲突**：同会话多 Full 窗口时后者覆盖前者并告警；Summary 模式不处理命令事件，不参与注册。
3. **未知动作**：回退到 `Message` 是**约定**，不是错误；处理器不得为了“必须有反应”而抛异常。
4. **临时条目生命周期**：只活在当前时间线实例里；页面刷新/切会话即消失，这是明确的产品选择（用户已确认“只在当前页面临时显示”）。
5. **事件契约字段扩展**：所有宿主重新编译；不改变既有字段语义。
6. **命令文本移除不受影响**：`RemoveCommandMessage` 默认仍为 true。
7. **子会话默认只读**：编辑能力由“是否子会话 + 是否接管”共同决定；接管 = 任意人类输入或显式按钮。改配置而不发消息时，必须先点“接管”。ACP 透传会话的模型切换仍不生效（动作返回 `Unavailable`）。
8. **工具/权限边界不变**：能力放开仅作用于 UI 编辑入口，不放宽工具权限、路径门闸或审批判定。
9. **接管影响父 agent 唤醒**：`Owner=Human` 时子会话完成上报用 `Notify` 而非 `Wake`（现有 `TaskTool` 语义，不变）。

## 12. 留给实现者的决定

- `MessageViewModel` 上错误样式复用哪个字段（本实现用内容前缀 `❌`，未引入独立错误块）。
- 动作载荷的键名大小写约定（本实现用 camelCase：`modelId` / `agentId` / `path` / `text`）。
- 临时条目的 Key 生成细节（本实现为 `cmd:{commandName}:{timestampTicks}`）。
- `HostActionRegistry` 是否再暴露“已注册动作列表”供命令探测（本期不做；回退机制已足够）。

## 13. 实现与验证记录

- 自动化：`Seeing.Agent.Hosting.Tests` 114 通过、`Seeing.Agent.WebUI.Tests` 544 通过、`Seeing.Agent.Mcp.Tests` 148 通过；全量 `dotnet build Seeing.Agent.slnx` 通过。
- 手动交互验证（`/model`、`/agent`、`/mcp`、`/cron-list`、子会话接管/交还）需要在实际 WebUI 页面操作，未在自动化环境中执行，待人工确认。
