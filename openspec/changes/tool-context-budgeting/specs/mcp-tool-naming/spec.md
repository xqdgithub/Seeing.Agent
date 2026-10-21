## ADDED Requirements

### Requirement: MCP tool ids use a reversible namespace form

MCP 工具的模型可见 id MUST 采用 `mcp__{server}__{tool}` 形式，其中 `server` 与 `tool` 为原始名称。该形式 MUST 能从 id 唯一还原出服务器名与工具名。

#### Scenario: id 可还原

- **WHEN** 服务器名为 `docs`、工具名为 `search`
- **THEN** 工具 id 为 `mcp__docs__search`

#### Scenario: 下划线歧义被消除

- **WHEN** 存在服务器 `a_b` 的工具 `c` 与服务器 `a` 的工具 `b_c`
- **THEN** 两者 id 分别为 `mcp__a_b__c` 与 `mcp__a__b_c`
- **AND** 两者不相等

### Requirement: All id production sites use the new form

所有生成 MCP 工具 id 的位置 MUST 使用新形式，包括工具适配器、连接协调器生成的注册表键与工具调用名、以及注册表内部键。

#### Scenario: 连接协调器使用新形式

- **WHEN** MCP 服务器连接并发现工具
- **THEN** 注册表中只存在 `mcp__{server}__{tool}` 形式的键
- **AND** 工具调用事件携带的名称也是该形式

#### Scenario: 重连后 id 不变

- **WHEN** MCP 服务器断开后重连并重新提供同名工具
- **THEN** 该工具 id 与断开前相同

### Requirement: MCP id change is a coordinated breaking change

工具 id 变更 MUST 在同一变更内同步更新全部消费点：工具禁用集匹配、Agent 工具白/黑名单匹配、权限规则中 `PermissionKind.Tool` 的 resource 匹配、以及 MCP 工具列表展示与过滤判定。所有消费点 MUST 只认新形式。

#### Scenario: 禁用列表按新 id 生效

- **WHEN** 用户在配置文件中按新 id 形式禁用某 MCP 工具
- **THEN** 该工具在结算时被排除

#### Scenario: 权限规则按新 id 匹配

- **WHEN** 权限规则针对 `mcp__docs__search` 声明 Ask
- **THEN** 调用该工具时进入审批流程

#### Scenario: MCP 工具列表可用

- **WHEN** 用户查看 MCP 工具列表
- **THEN** 列表基于新前缀正确识别全部 MCP 工具
- **AND** 不返回空列表

### Requirement: No compatibility path for legacy ids

系统 MUST NOT 为旧形式 `{server}_{tool}` 提供任何兼容处理：MUST NOT 做别名映射、MUST NOT 在匹配时同时接受旧形式、MUST NOT 对旧 id 做检测、转换或告警。旧 id 在升级后即视为不存在。

#### Scenario: 旧 id 不被识别

- **WHEN** 配置的禁用列表或权限规则中使用旧形式 `docs_search`
- **THEN** 该条目不匹配任何已注册工具
- **AND** 系统不输出任何"旧 id 已弃用"之类的提示日志

#### Scenario: 无别名注册

- **WHEN** MCP 服务器 `docs` 注册工具 `search`
- **THEN** 注册表中只存在 `mcp__docs__search`
- **AND** 不存在 `docs_search` 这一 id

### Requirement: Catalog namespaces derive from the new id form

目录条目 MUST 将同一 MCP 服务器的工具归入同一命名空间，命名空间名 MUST 可由工具 id 推导。

#### Scenario: 同服务器工具同组

- **WHEN** 服务器 `docs` 提供 `search` 与 `fetch` 两个工具
- **THEN** 两者都归入命名空间 `mcp__docs`

### Requirement: Duplicate server names are rejected

以相同服务器名注册第二个 MCP 连接 MUST 失败并给出明确错误，MUST NOT 静默覆盖已注册的工具。

#### Scenario: 重复服务器名被拒绝

- **WHEN** 两个连接以相同服务器名注册
- **THEN** 第二次注册失败并给出明确错误
- **AND** 已注册的工具不受影响
