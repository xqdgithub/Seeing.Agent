## ADDED Requirements

### Requirement: CommandMetadata Template 属性

`CommandMetadata` 类 SHALL 包含 `Template` 属性用于存储命令模板。

- 类型：`string?`（可选）
- 用途：存储 Markdown 命令的模板内容或内联模板

#### Scenario: 模板属性初始化

- **WHEN** 创建 `CommandMetadata` 实例并设置 `Template` 属性
- **THEN** 属性值可被后续执行逻辑访问

#### Scenario: 模板属性默认值

- **WHEN** 未设置 `Template` 属性
- **THEN** 属性值为 `null`

---

### Requirement: CommandMetadata Agent 属性

`CommandMetadata` 类 SHALL 包含 `Agent` 属性用于指定目标 Agent。

- 类型：`string?`（可选）
- 用途：指定命令执行时切换的目标 Agent

#### Scenario: Agent 属性初始化

- **WHEN** 创建 `CommandMetadata` 实例并设置 `Agent` 属性
- **THEN** 属性值可通过 `CommandContext.TargetAgent` 传递到执行层

#### Scenario: Agent 属性默认值

- **WHEN** 未设置 `Agent` 属性
- **THEN** 属性值为 `null`，不触发 Agent 切换

---

### Requirement: CommandContext TargetAgent 字段

`CommandContext` 类 SHALL 包含 `TargetAgent` 字段用于传递目标 Agent。

- 类型：`string?`（可选）
- 来源：从 `CommandMetadata.Agent` 复制

#### Scenario: TargetAgent 传递

- **WHEN** `CommandDispatcher` 创建执行上下文
- **THEN** `TargetAgent` 字段值等于 `command.Metadata.Agent`

#### Scenario: TargetAgent 默认值

- **WHEN** 命令未设置 `Agent` 属性
- **THEN** `TargetAgent` 为 `null`