# 斜杠命令面板：技能可见性 / 键盘跟随 / 分组

日期：2026-11-18
范围：`samples/Seeing.Agent.WebUI`（命令面板 UI 与数据映射）
规模：小改动（短设计）

## 意图

输入框里敲 `/` 弹出的命令面板目前是「一条平铺列表 + 行尾小徽标」：

1. 技能（`/<技能名>`）在后端已由 `DynamicSkillCommandSync` 注册，但面板里区分不出来，
   且面板数据是打开会话时取的一份快照，注册表后续变化（技能加载/禁用、工作区切换重载）
   不会反映，表现为「技能没被注册成斜杠命令」。
2. 上下键只改选中值，不滚动容器；面板 `max-height:300px`，选中项很快就会跑出可视区。
3. 没有分组，内置命令、技能、自定义 `.md` 命令、MCP 命令混在一起。

## 目标

- 技能以独立分组出现在面板中，并标注为「技能」。
- 面板每次打开时读取 `ICommandRegistry` 的最新状态（技能增删立即生效，无需刷新页面）。
- 上下键移动选中项时，容器自动滚动使该项可见。
- 按来源分组展示：`内置命令` / `技能` / `自定义命令` / `扩展（MCP 等）`；组内保留现有 Category 徽标。

## 非目标

- 不改后端命令注册、执行与权限语义（`ICommandRegistry` / `DynamicSkillCommandSync` 零改动）。
- 不动 AntDesign `Mentions` 遗留代码路径（`LoadCommandOptions` / `HandleCommandSearch`）。
- 不做模糊匹配 / 拼音搜索等检索增强。

## 方案（已选定）

### A. 数据来源：面板打开时现取

`PromptInput` 直接注入 `CommandListService`，在检测到 `/` 触发面板时拉一次命令列表
（沿用 `SessionWindow` 现有的 `Task.Run(...)` 包裹，因为 `GetAvailableCommands` 内部对
`IAgentRegistry.GetAgentAsync` 做了同步等待）。

备选（已否）：保留 `AvailableCommands` 参数，另加跨组件「命令变更」事件刷新。
需要在 Registry 侧引入订阅/通知端口，改动面远大于收益。

随之删除 `SessionWindow` 的 `_availableCommands` 字段、`LoadCommandsFromBackend()` 与三处调用
（初始化、会话装载、切换 Agent/Scenario），消除一份冗余快照与双份真相源。

### B. 分组：数据侧判定，展示侧顺序渲染

`CommandItemViewModel`（`QuickCommandViewModel.cs`）补 `GroupKey` / `GroupTitle`；
`CommandListService` 映射时按下面的规则落组，并补全 `IsSkill` / `IsCustom` / `IsBuiltIn` / `Source`。

| 判定 | 分组 |
|------|------|
| `Metadata.Type == CommandType.Skill` | 技能 |
| `Metadata.Source` 是 `.md` 文件路径 | 自定义命令 |
| `Metadata.Source == "mcp"` 或 `Category == Extension` | 扩展（MCP 等） |
| 其余 | 内置命令（组内显示 Category 徽标） |

组顺序：内置命令 → 技能 → 自定义命令 → 扩展。空组不渲染。

排序：先按组序，再按 `Priority`（= `SortOrder`），最后按名称。
`FilteredCommands` 保持**扁平有序**列表（键盘导航索引不变），渲染时顺序遍历、
在分组变化处插入组标题 `<li class="command-group-header">`；组标题不可选中。

### C. 滚动跟随

每个选项 `<li>` 带 `id="command-opt-{index}"`；`ArrowUp` / `ArrowDown` 更新选中后调用
新增的 JS 助手 `scrollCommandOptionIntoView(id)`（`scrollIntoView({ block: 'nearest' })`）。
用 JS 而不是 `ElementReference`：Blazor 没有暴露 `scrollIntoView`，且面板是条件渲染。

## 影响面

- `samples/Seeing.Agent.WebUI/Services/CommandListService.cs`：分组与字段映射
- `samples/Seeing.Agent.WebUI/Services/UiText.cs`：组标题 / 来源徽标 / `CommandCategory` 中文映射
- `samples/Seeing.Agent.WebUI/Models/QuickCommandViewModel.cs`：新增分组字段
- `samples/Seeing.Agent.WebUI/Components/PromptInput.razor`：现取数据、分组渲染、滚动跟随
- `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`：删除命令列表参数与加载调用
- `samples/Seeing.Agent.WebUI/wwwroot/js/app.js`：新增滚动助手
- `samples/Seeing.Agent.WebUI/wwwroot/css/quick-command.css`：`.command-group-header` 样式
- 测试：`tests/apps/Seeing.Agent.WebUI.Tests/Services/CommandListServiceTests.cs`（分组判定与排序）
  + `Rendering/CommandPaletteGroupingTests.cs`（源文件守卫：组标题、id、滚动调用）
  + `Services/UiTextTests.cs`（新增分组 / 分类文案映射断言）

## 文案出口（U3 / U4）

面板新增文案不写在组件里：组标题、来源徽标、分类徽标统一走 `UiText`
（`CommandGroupTitle` / `CommandGroupBadge` / `CommandCategoryText`）。
顺带修掉旧缺陷：分类徽标此前直接 `CommandCategory.ToString()` 展示 `Basic` / `Tools` 等英文枚举名。

## 根因与契约修正（2026-11-18 复查）

面板里没有技能命令，不是 UI 过滤问题，而是**注册表层面的误判**。实机日志取证：

```
技能命令同步：可用=85, 新增=0, 撤销=0, 跳过重名=85, 累计持有=0
```

85 个技能全部被当成「重名」。原因链：

1. `AcpModule` 为同一批技能登记 `AcpDynamicSkillCommand`，`SupportedRuntimes = [AcpPassthrough]`；
2. `DynamicSkillCommandSync` 的判重用的是 `HasCommand(name)` —— **只按名、不分 Runtime**，
   于是每个技能名都「已存在」，Native 侧的命令一个都登记不上；
3. 反向同样有雷：两边撤销都是 `GetCommand(name)` + `Unregister(name)`，
   同名时可能删掉**另一个 Runtime** 的那份（表现为「关掉 ACP 后技能命令消失」）。

结论：**按 Runtime 分区的注册设计是对的，缺的是「撤销也要能定位到实例」这一维**。
按名撤销是契约缺口，不是某个同步器的实现瑕疵，因此修正落在契约与两个调用方：

- `ICommandRegistry` 增加 `Unregister(ICommand command)`（按实例撤销，与 `Register(command)` 对称）；
  `Unregister(string name)` 保留给默认命令 / 唯一登记的场景，并在文档里写明其「任意命中一份」的语义。
- `CommandRegistry` 实现按实例移除：默认桶按引用比对，Runtime 桶按 `(name, runtime)` 逐项比对值。
- `DynamicSkillCommandSync`：保存自己创建的实例（名字 → 实例），撤销按实例；
  判重改为 `GetCommand(name, AgentRuntime.Native)`（只看本 Runtime）。
- `AcpModule`：同样保存实例、按实例撤销，判重改为 `GetCommand(name, AgentRuntime.AcpPassthrough)`。

修完后实机日志：`可用=85, 新增=78（+首轮 7）, 跳过重名=0, 累计持有=85`。

## Runtime 选择（ACP 与 Native 并存）
同名技能在两个 Runtime 各有一条命令，**面板按当前选中智能体的 Runtime 取那一组**——
这是既有行为，本次补上守门测试：

- `CommandListService.GetCurrentRuntime(SelectedAgent)` → `GetCommandsByRuntime(runtime)`；
- 测试 `GetAvailableCommands_Should_Follow_Selected_Agent_Runtime`：
  同一技能名分别以 Native / ACP 登记，Native 智能体只出 Native 版，切到 ACP 智能体只出 ACP 版。

## 自定义 Markdown 命令接线（原「自定义命令」组为空）

问题：解析能力 `ICommandDiscovery.DiscoverFromMarkdownAsync` **零调用方**，
`~/.seeing/commands/*.md`、`{项目}/.seeing/commands/*.md` 从不登记——面板里那一组永远空着。

设计（`MarkdownCommandLoader`，放 **Hosting** 的 `Commands/`：`ICommandDiscovery` 只在 `AddSessionHosting`
注册，核心程序集不承担这条链；`InitializeCommands()` 也在 Hosting）：

| 维度 | 决定 |
|------|------|
| 目录 | 用户级 `{UserSeeingDirectory}/commands` → 项目级 `{ProjectSeeingDirectory}/commands`（后者覆盖同名） |
| 生命周期① | 宿主命令初始化入口 `InitializeCommands()` 调一次（必须在内置命令之后，同名时内置胜出） |
| 生命周期② | `IReloadHandler`：`WorkspaceChange` + 全量 `ConfigChange` 时重扫；局部配置变更不扫盘 |
| 撤销 | 按实例（`Unregister(ICommand)`），文件删除/正文改动都能生效，不误删同名命令 |
| 冲突 | 与已有命令同名 → 跳过 + 告警，不静默覆盖（AGENTS.md 反模式） |
| 归属展示 | `CommandMetadata.Source` 已是文件路径 → 面板自动归入「自定义命令」组 |

**展开语义（本次一并修正）**：模板命令不是"展示命令"，而是"把命令原文那条 user 消息改写成展开结果，再继续执行 Agent"。
旧实现直接 `RemoveCommandMessage=true` 且不写回展开结果 → 命令消息被删、模型这一轮收不到任何用户输入（模板等于没生效）。
现在与 `/skill` 同一条规则：按原文匹配最后一条 user 消息并替换，`RemoveCommandMessage=false` + `NeedsRefresh=true`；
未命中会话时保留原文并告警，绝不静默丢弃用户输入。匹配规则收敛到原语层 `SessionCommandMessage`（Skills 与 Markdown 共用）。

守卫：`MarkdownCommandLoaderTests`（7 条：双目录、项目覆盖、删除、改动、冲突、局部/全量配置、ChangeTypes）。

## 一次性初始化的边界（`WorkspaceReadyInitializer`）

`_ran` 把补初始化锁成**进程级一次性**：只做「命令发现 / ACP 命令 / TokenBudget 钩子 / 首个工作区的渠道 bootstrap」。
每次切换都要做的重活一律走 `IReloadHandler`（`ComponentManager`、`MarkdownCommandLoader`、
`DynamicSkillCommandSync`、`GatewayChannelRegistryReloadHandler`）。

守卫：`RunIfNeeded_Should_Not_Contain_Workspace_Scoped_Reload_Work`（IL 扫描，禁止出现
`LoadAllAsync` / `SettleAsync` / `AttachReloadHandlers` 调用）。



## 验证

```bash
dotnet build Seeing.Agent.slnx
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll \
  --TestCaseFilter:"FullyQualifiedName~CommandListService|FullyQualifiedName~CommandPalette"
```

## 留待实现阶段决定

- 组标题是否显示计数（`技能 · 12`）：先不加，保持简洁。
- 技能名过长时是否截断：沿用现有 `text-overflow: ellipsis`。
