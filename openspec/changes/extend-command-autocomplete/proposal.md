## Why

当前命令自动完成候选列表仅包含工具（Tools）和技能（Skills），缺少基础的 Agent 操作命令（如 `/help`、`/clear`、`/model`、`/agent` 等）。用户无法通过斜杠命令快捷访问常用会话操作，导致操作效率低下。同时，用户自定义的 Markdown 命令（`.seeing/commands/*.md`）也未出现在候选列表中，限制了命令扩展能力。

## What Changes

- **新增内置命令候选列表**：在自动完成下拉框中添加基础会话命令（`/help`、`/clear`、`/new`）、Agent 管理命令（`/model`、`/agent`）、工具管理命令（`/mcp`、`/skill`）、会话操作命令（`/undo`、`/redo`、`/compact`、`/fork`、`/share`）等
- **新增用户自定义命令支持**：加载项目级（`./.seeing/commands/*.md`）和用户级（`~/.seeing/commands/*.md`）自定义 Markdown 命令到候选列表
- **扩展命令视图模型**：新增分类、优先级、快捷键提示、禁用状态等字段
- **实现命令可用性动态检查**：根据会话状态动态启用/禁用命令（如无历史时禁用 `/undo`）
- **实现命令冲突处理**：自定义命令与内置命令同名时，自定义命令标记为禁用并提示冲突
- **优化命令排序**：按优先级排序（项目级自定义 > 用户级自定义 > 内置 > 工具 > 技能）

## Capabilities

### New Capabilities

- `command-autocomplete`: 命令自动完成系统，包含候选列表生成、命令加载、排序与过滤
- `builtin-commands`: 内置斜杠命令定义，提供会话操作、Agent 管理、工具管理等基础命令
- `custom-commands`: 用户自定义 Markdown 命令加载，支持项目级和用户级命令目录
- `command-availability`: 命令可用性动态检查，根据会话状态判断命令是否可执行

### Modified Capabilities

（无现有 capability 需要修改）

## Impact

- **受影响文件**:
  - `samples/Seeing.Agent.WebUI/Models/QuickCommandViewModel.cs` - 扩展 `CommandItemViewModel` 字段
  - `samples/Seeing.Agent.WebUI/Components/PromptInput.razor` - 命令加载与选择逻辑
  - `samples/Seeing.Agent.WebUI/Pages/Session.razor` - 自定义命令加载、内置命令回调处理
- **新增文件**:
  - `samples/Seeing.Agent.WebUI/Commands/BuiltInCommands.cs` - 内置命令定义
  - `samples/Seeing.Agent.WebUI/Commands/CustomCommandLoader.cs` - 自定义命令加载器
  - `samples/Seeing.Agent.WebUI/Services/CommandAvailabilityChecker.cs` - 命令可用性检查服务
- **依赖关系**: 无新增外部依赖，复用现有 `CommandDiscovery` 的 Markdown 解析逻辑
