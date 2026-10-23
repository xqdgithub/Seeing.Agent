## Why

当前 Seeing.Agent 框架已有基础的命令系统骨架（`ICommand`、`CommandRegistry`、`CommandDiscovery`、`CommandDispatcher`），支持斜杠命令和基于注解的发现机制。但缺少以下关键能力：

1. **Markdown 命令定义** - 无法通过 `.md` 文件定义命令，限制了非开发人员的扩展能力
2. **模板参数支持** - 命令无法使用参数模板（如 `{{1}}`、`{{arguments}}`）
3. **Agent 关联** - 命令无法指定执行时切换的目标 Agent

此变更以最小改动（约 109 行代码）扩展现有命令系统，支持 Markdown 命令发现和模板参数。

## What Changes

### 新增属性

- **`CommandMetadata.Template`** - 命令模板，支持 `{{1}}`、`{{arguments}}` 占位符
- **`CommandMetadata.Agent`** - 执行时切换的目标 Agent
- **`CommandContext.TargetAgent`** - 传递目标 Agent 到执行层

### 新增方法

- **`CommandDiscovery.DiscoverFromMarkdownAsync()`** - 从目录发现 Markdown 命令

### 新增内部类型

- **`MarkdownCommand`** - Markdown 文件命令包装器（内部类）
- **`CommandFrontmatter`** - YAML frontmatter 反序列化结构（内部类）

### 无破坏性变更

- 现有 `ICommand` 接口不变
- 现有 `CommandRegistry` 不变
- 现有命令实现无需修改

## Capabilities

### New Capabilities

- **`markdown-commands`**: 从 Markdown 文件发现和执行命令，支持 YAML frontmatter 元数据和模板参数

### Modified Capabilities

- **`command-core`**: 扩展 `CommandMetadata` 和 `CommandContext`，新增模板和 Agent 关联能力

## Impact

### Affected Code
- `src/Seeing.Agent/Commands/ICommand.cs` - 新增属性（约 8 行）
- `src/Seeing.Agent/Commands/Discovery/CommandDiscovery.cs` - 新增方法和内部类（约 100 行）
- `src/Seeing.Agent/Commands/CommandDispatcher.cs` - 传递 TargetAgent（1 行）

### Affected APIs
- `CommandMetadata` 新增 2 个可选属性
- `CommandContext` 新增 1 个可选字段
- `CommandDiscovery` 新增 1 个方法

### Dependencies
- 复用现有 `YamlDotNet` 依赖
- 无新增外部依赖

### Systems
- **Session** - 斜杠命令解析和执行
- **TUI** - 命令面板显示（可显示 Agent 归属）
- **Extension** - 扩展可提供 Markdown 命令目录
