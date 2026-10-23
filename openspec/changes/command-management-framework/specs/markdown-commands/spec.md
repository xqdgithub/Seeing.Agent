## ADDED Requirements

### Requirement: Markdown 命令发现

系统 SHALL 从指定目录发现 Markdown 格式的命令文件。

- 扫描目录：`.seeing/commands/**/*.md` 和 `.agents/commands/**/*.md`
- 文件格式：YAML frontmatter + Markdown 正文
- 支持异步发现

#### Scenario: 发现有效的 Markdown 命令

- **WHEN** 目录中存在包含有效 frontmatter 的 `.md` 文件
- **THEN** 系统解析文件并返回 `ICommand` 实例列表

#### Scenario: 跳过无效的 Markdown 文件

- **WHEN** 文件缺少 `name` 字段或 frontmatter 格式错误
- **THEN** 系统记录警告日志并跳过该文件，不影响其他命令发现

---

### Requirement: YAML Frontmatter 解析

系统 SHALL 解析 Markdown 文件中的 YAML frontmatter 元数据。

支持的字段：
- `name` (必需) - 命令名称
- `description` - 命令描述
- `aliases` - 命令别名数组
- `category` - 命令分类 (basic, navigation, agent, tools, system, extension)
- `agent` - 目标 Agent

#### Scenario: 解析完整 frontmatter

- **WHEN** Markdown 文件包含完整的 YAML frontmatter
- **THEN** 系统正确提取所有字段并映射到 `CommandMetadata`

#### Scenario: 使用默认值填充缺失字段

- **WHEN** frontmatter 中缺少可选字段
- **THEN** 系统使用默认值（如 `description` 为空字符串，`category` 为 `Other`）

---

### Requirement: 模板参数渲染

系统 SHALL 渲染命令模板中的参数占位符。

支持的占位符：
- `{{1}}` - 第一个参数
- `{{2}}` - 第二个参数  
- `{{arguments}}` 或 `{{args}}` - 完整参数字符串

#### Scenario: 渲染位置参数

- **WHEN** 命令模板包含 `{{1}}` 且用户输入 `/cmd hello world`
- **THEN** `{{1}}` 被替换为 `hello`

#### Scenario: 渲染完整参数

- **WHEN** 命令模板包含 `{{arguments}}` 且用户输入 `/cmd hello world`
- **THEN** `{{arguments}}` 被替换为 `hello world`

#### Scenario: 参数不足时使用空字符串

- **WHEN** 命令模板包含 `{{2}}` 但用户仅提供一个参数
- **THEN** `{{2}}` 被替换为空字符串

---

### Requirement: Markdown 命令执行

系统 SHALL 将 Markdown 命令作为可执行的 `ICommand` 实例。

#### Scenario: 执行带模板的命令

- **WHEN** 用户调用 Markdown 命令
- **THEN** 系统渲染模板并返回 `CommandResult.Ok(renderedContent)`

#### Scenario: 模板为空时返回错误

- **WHEN** Markdown 命令没有模板内容
- **THEN** 系统返回 `CommandResult.Fail("命令模板为空")`
