## ADDED Requirements

### Requirement: Schema settlement targets only the effective tool set

schema 结算 MUST 先求出"目标工具 id 集"（会话结算集 ∩ 全局启用态 ∩ Agent 过滤），再仅对该集合内的工具生成 schema。MUST NOT 对全部已注册工具生成 schema 后再过滤。

#### Scenario: 未结算的已注册工具不触发 schema 生成

- **WHEN** 已注册 51 个工具，会话结算集为 12 个，Agent 未额外限制
- **THEN** 只有这 12 个工具被读取 `Description` 与 `ParametersSchema`
- **AND** `tool.definition` Hook 仅被触发 12 次

#### Scenario: Agent 黑名单在生成前生效

- **WHEN** 会话结算集为 12 个且 Agent `DeniedTools` 排除其中 1 个
- **THEN** 仅 11 个工具参与 schema 生成
- **AND** 被拒绝的工具不触发 `tool.definition` Hook

### Requirement: Schema output is deterministically ordered

schema 结算结果 MUST 按工具 id 的序数（`OrdinalIgnoreCase`）稳定排序。同一输入集合在任意次结算中 MUST 产生完全相同的顺序。

#### Scenario: 连续结算顺序一致

- **WHEN** 对同一会话与同一 Agent 连续执行 100 次 schema 结算，且期间没有工具注册或注销
- **THEN** 100 次结果中工具 id 序列完全相同

#### Scenario: 注册顺序不影响输出顺序

- **WHEN** 两个进程以相反顺序注册同一组工具，随后各自结算
- **THEN** 两者输出的工具 id 序列相同

### Requirement: Final ordering composes pinned, discovered and remaining

最终暴露的 schema 顺序 MUST 为：pinned（配置声明顺序）→ 已发现（发现顺序）→ 其余 inline（工具 id 序数）。该顺序 MUST 保证已发现部分只向后追加，MUST NOT 重排既有条目。

#### Scenario: 已发现部分只追加

- **WHEN** 会话已发现工具 A、B，随后发现 C
- **THEN** 新顺序中 A、B 的相对位置不变
- **AND** C 出现在 B 之后

#### Scenario: pinned 始终在最前

- **WHEN** pinned 集合包含 `read` 且会话已发现 `session_list`
- **THEN** `read` 在 `session_list` 之前

### Requirement: Schema results are cached with explicit invalidation

schema 结算 MUST 使用缓存，缓存键 MUST 至少包含目标工具 id 集、Agent 的 `AllowedTools` 与 `DeniedTools`、工具禁用集版本、`tool.definition` Hook 版本。任一组件变化 MUST 使对应缓存失效。

#### Scenario: 重复结算命中缓存

- **WHEN** 相同键连续结算两次
- **THEN** 第二次不读取工具元数据且不触发 `tool.definition` Hook

#### Scenario: 禁用工具导致缓存失效

- **WHEN** 用户在两次结算之间禁用一个工具
- **THEN** 下一次结算重新生成 schema
- **AND** 输出中不含被禁用的工具

#### Scenario: Hook 版本变化导致缓存失效

- **WHEN** 通过 `tool.definition` Hook 改写某工具描述后再次结算
- **THEN** 输出中的描述为改写后的值

### Requirement: Hook manager exposes a monotonic version

Hook 管理器 MUST 提供一个单调递增的版本号，注册、移除、清空 Hook 时 MUST 递增。该版本 MUST 可被 schema 缓存读取用于失效判定。

#### Scenario: 注册 Hook 递增版本

- **WHEN** 注册一个 `tool.definition` Hook
- **THEN** 版本号大于注册前的值

#### Scenario: 版本只增不减

- **WHEN** 连续执行注册、移除、清空操作
- **THEN** 版本号严格递增

### Requirement: Tool definitions participate in token accounting

工具定义占用的估算 token MUST 并入 token 预算分解，并 MUST 可从执行轨迹观测。目录摘要占用的估算 token MUST 单独计量，MUST NOT 混入工具定义口径。

#### Scenario: 预算分解包含工具定义

- **WHEN** 一轮执行产生 12 个 inline 工具 schema
- **THEN** token 预算分解的 `ToolDefinitions` 字段为非零值
- **AND** 该值等于对当前 schema 集的估算值

#### Scenario: 目录单独计量

- **WHEN** 本轮注入了目录摘要
- **THEN** 目录的估算 token 计入独立的 `Catalog` 字段
- **AND** `ToolDefinitions` 不包含目录占用

### Requirement: Schema resolver is consumed through its abstraction

执行链 MUST 通过 `ISessionToolSchemaResolver` 依赖 schema 解析能力，MUST NOT 依赖具体实现类型。执行链 MUST NOT 在解析器未注册时自行构造实例。

#### Scenario: 可替换解析器实现

- **WHEN** 容器中注册了 `ISessionToolSchemaResolver` 的自定义实现
- **THEN** 主执行链使用该实现产出的 schema
- **AND** 无需修改执行链代码

#### Scenario: 解析器缺失时明确失败

- **WHEN** 容器中未注册 `ISessionToolSchemaResolver`
- **THEN** 执行链在构造期失败并给出明确错误
- **AND** 不以空 schema 静默继续

### Requirement: Schema computation remains single-source

schema 的计算 MUST 仍只发生在会话级 schema 解析出口。执行器 MUST NOT 自行计算、缓存第二份 schema，MUST NOT 在单次执行内替换 schema 工作集。

#### Scenario: 执行器不自算 schema

- **WHEN** 执行器构造 LLM 请求
- **THEN** `ChatRequest.Tools` 来自执行上下文提供的 schema 集
- **AND** 执行器中不存在对工具注册表的 schema 查询

#### Scenario: 单次执行内 schema 不变

- **WHEN** 一次执行包含多个步骤
- **THEN** 各步骤使用同一份 schema 集合
- **AND** 工具结果不会导致执行器重新取 schema

#### Scenario: 旁路执行共用同一出口

- **WHEN** 调度器直连执行路径发起一轮执行
- **THEN** 其 LLM 请求携带的 schema 与主执行链来自同一解析出口
