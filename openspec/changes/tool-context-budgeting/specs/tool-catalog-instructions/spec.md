## ADDED Requirements

### Requirement: Prompt sections declare their own heading and placement

提示分节贡献者 MUST 自述分节标题与放置位置。提示词构建器 MUST NOT 维护分节名称白名单。未自述标题的贡献者 MUST 被明确拒绝（记录错误），MUST NOT 被静默跳过。

#### Scenario: 贡献者自述标题与位置

- **WHEN** 某贡献者声明标题 `## Tool Catalog` 与放置位置 `AppendAtEnd`
- **THEN** 该分节出现在系统提示中
- **AND** 出现在已声明锚点分节之后

#### Scenario: 未自述标题被明确拒绝

- **WHEN** 某贡献者未实现标题契约
- **THEN** 构建过程记录一条错误日志
- **AND** 该分节不被注入
- **AND** 其余分节正常注入

#### Scenario: 新增分节无需修改构建器

- **WHEN** 新增一个能力包并注册自述标题的贡献者
- **THEN** 该分节无需修改提示词构建器即可生效

### Requirement: Catalog section is injected only when deferral occurs

当且仅当本轮存在 `deferred` 工具时，系统 MUST 向系统提示注入目录分节。全量暴露模式下 MUST NOT 注入该分节。

#### Scenario: 分级模式注入分节

- **WHEN** 本轮存在至少一个 `deferred` 工具
- **THEN** 系统提示包含目录分节

#### Scenario: 全量模式不注入

- **WHEN** 所有工具均为 `inline`
- **THEN** 系统提示不包含目录分节

### Requirement: Catalog section enumerates namespaces with counts

目录分节 MUST 按命名空间分组，每个命名空间行 MUST 包含名称与工具总数；条目未完整展示时 MUST 标注已展示数量。

#### Scenario: 分组与计数

- **WHEN** 目录包含 `filesystem`（8 个）与 `mcp__docs`（35 个）且仅展示 2 条与 0 条
- **THEN** `filesystem` 行标注 8 个工具与 2 个已展示
- **AND** `mcp__docs` 行标注 35 个工具与 0 个已展示

### Requirement: Catalog entries use single-line signatures

目录条目 MUST 为单行，包含工具签名与截断后的描述。描述 MUST 截断到可配置字符上限（默认 120 字符），超出部分 MUST 以省略号表示。

#### Scenario: 单行签名

- **WHEN** 某工具参数为 `(path, offset?, limit?)`
- **THEN** 目录条目包含该签名的单行表示

#### Scenario: 描述截断

- **WHEN** 工具描述超过字符上限
- **THEN** 目录条目中的描述被截断并以省略号结尾

### Requirement: Catalog section states that unlisted tools are not callable

目录分节 MUST 明确说明：未出现在请求工具数组中的工具不可直接调用，必须先检索；并且检索到的工具将在下一轮生效。

#### Scenario: 提示包含调用约束

- **WHEN** 目录分节被渲染
- **THEN** 分节包含"未加载的工具不可直接调用"的说明
- **AND** 分节包含检索方式说明
- **AND** 分节说明检索结果自下一轮生效

### Requirement: Catalog rendering is deterministic

目录渲染 MUST 对同一输入产生相同输出，排序 MUST 为确定性（命名空间按名称、条目按路径）。

#### Scenario: 重复渲染一致

- **WHEN** 对同一目录输入渲染两次
- **THEN** 两次输出文本完全相同

### Requirement: Catalog data reaches the prompt through a generic slot

目录数据 MUST 通过提示上下文的通用分节槽传递，MUST NOT 在核心提示契约中引入暴露策略专有类型。

#### Scenario: 不污染核心契约

- **WHEN** 目录数据传入提示上下文
- **THEN** 核心提示上下文契约中不存在暴露策略专有的条目类型
- **AND** 目录内容以文本形式提供
