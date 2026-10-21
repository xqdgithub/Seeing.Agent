## ADDED Requirements

### Requirement: Discovery takes effect on the next execution

通过检索获得的工具 MUST 在**下一次执行**中可被调用。系统 MUST NOT 在单次执行内刷新 schema 工作集。检索结果 MUST 以文本明确告知模型"新工具将在下一轮可用"。

#### Scenario: 检索后下一轮可调用

- **WHEN** 模型在第 1 轮以 `detail=full` 检索并命中工具 X
- **THEN** 该工具被写入会话发现态
- **AND** 第 2 轮请求的 `tools` 数组包含 X

#### Scenario: 同一轮内不可调用

- **WHEN** 模型在同一轮内检索到工具 X 后立即尝试调用 X
- **THEN** 该调用因 X 不在本轮 `tools` 数组中而失败
- **AND** 检索结果已提前说明这一点

#### Scenario: 检索结果包含生效时机说明

- **WHEN** 检索以 `detail=full` 返回结果
- **THEN** 结果文本明确说明这些工具将在下一轮可用

### Requirement: Session discovery state is monotonic

系统 MUST 为每个会话维护"已发现工具 id 集"，该集合在会话生命周期内只增不减，除非发生显式裁剪（工具从可用集合中消失）。

#### Scenario: 发现后持久生效

- **WHEN** 模型通过检索加载某工具
- **THEN** 该工具 id 加入会话发现集
- **AND** 后续每次执行的结算中该工具为 `inline`

#### Scenario: 发现集跨轮保留

- **WHEN** 会话在后续轮次重新结算 schema
- **THEN** 已发现的工具仍为 `inline`
- **AND** 模型不需要重新检索

#### Scenario: 工具消失时裁剪

- **WHEN** 某工具从可用集合中消失（模块停用或 MCP 断开）
- **THEN** 该工具 id 从发现集中移除
- **AND** 结算不因该 id 失败

#### Scenario: 无容量上限淘汰

- **WHEN** 会话已发现大量工具且超过任意内部阈值
- **THEN** 已发现工具 MUST NOT 被自动淘汰
- **AND** 只允许因工具消失而裁剪

### Requirement: Discovery state persists with the session

会话发现态 MUST 随会话持久化，并在会话恢复后沿用。会话数据缺少该字段时 MUST 视为空集。会话消息被清空 MUST NOT 清除发现态。

#### Scenario: 恢复会话保留发现态

- **WHEN** 会话被持久化后重新加载
- **THEN** 已发现工具仍为 `inline`

#### Scenario: 旧会话无该字段

- **WHEN** 加载一个不含发现态字段的既有会话
- **THEN** 发现集为空
- **AND** 会话正常执行

#### Scenario: 清空消息不清发现态

- **WHEN** 会话消息被清空
- **THEN** 发现态保持不变

### Requirement: Forked sessions inherit discovery state

从父会话派生的子会话 MUST 继承父会话的发现工具 id 集。

#### Scenario: 子会话继承

- **WHEN** 会话 A 已发现工具 X 后 fork 出会话 B
- **THEN** 会话 B 的发现集包含 X
- **AND** 会话 B 的请求中 X 为 `inline`

### Requirement: Search tool supports three detail levels

系统 MUST 提供工具检索元工具，支持三档明细：`name`（仅 id）、`summary`（id + 单行摘要）、`full`（完整定义）。默认档位 MUST 可配置。

#### Scenario: 默认返回摘要

- **WHEN** 模型检索且未指定明细档位
- **THEN** 每条结果包含 id 与单行摘要
- **AND** 结果不包含完整参数 schema

#### Scenario: full 档位返回完整定义

- **WHEN** 模型以 `full` 档位检索并命中某工具
- **THEN** 结果包含该工具的完整参数 schema
- **AND** 该工具被写入会话发现集

#### Scenario: name 档位最省 token

- **WHEN** 模型以 `name` 档位检索
- **THEN** 每条结果只包含工具 id
- **AND** 结果不写入发现集

#### Scenario: summary 档位不写入发现集

- **WHEN** 模型以 `summary` 档位检索
- **THEN** 结果包含 id 与摘要
- **AND** 结果不写入发现集

### Requirement: Search supports namespace and keyword queries

检索 MUST 支持按命名空间限定、按关键词匹配工具 id 与描述，并 MUST 支持空查询浏览命名空间。

#### Scenario: 命名空间限定

- **WHEN** 检索查询为命名空间名
- **THEN** 结果只包含该命名空间下的工具

#### Scenario: 关键词匹配描述

- **WHEN** 关键词只出现在工具描述中而不在 id 中
- **THEN** 该工具仍被命中

#### Scenario: 空查询返回命名空间概览

- **WHEN** 检索查询为空
- **THEN** 结果列出可用命名空间及其工具数量

### Requirement: Search ranking is deterministic

检索结果的排序 MUST 确定性：先按匹配分数降序，同分按工具 id 序数升序。相同输入 MUST 产生相同顺序。

#### Scenario: 同分按 id 排序

- **WHEN** 两个工具匹配分数相同
- **THEN** 按工具 id 序数升序排列

#### Scenario: 重复检索顺序一致

- **WHEN** 以相同参数检索两次
- **THEN** 两次结果顺序完全相同

### Requirement: Search is paginated by offset

检索 MUST 支持 `offset`（起始偏移，默认 0）与 `limit`（本页条数）参数，并 MUST 返回 `total`（匹配总数）、`returned`（本页条数）、`remaining`（剩余条数）与 `next`（下一页偏移，无更多时为 null）。`total` MUST 反映匹配总数而非本页条数。

#### Scenario: 首页返回部分结果

- **WHEN** 命中 12 个工具且 `limit=2`
- **THEN** 结果包含 2 条
- **AND** `total=12`、`returned=2`、`remaining=10`、`next.offset=2`

#### Scenario: 按 next 继续翻页

- **WHEN** 模型以 `offset=2` 再次检索同一查询
- **THEN** 返回第 3、4 条
- **AND** `next.offset=4`

#### Scenario: 末页无下一页

- **WHEN** 模型取到最后一页
- **THEN** `remaining=0`
- **AND** `next` 为 null

#### Scenario: 偏移超出范围

- **WHEN** `offset` 大于等于匹配总数
- **THEN** 结果为空
- **AND** `total` 仍为真实匹配总数
- **AND** 结果说明偏移已超出范围并给出可用的 offset 范围

#### Scenario: total 区分"命中少"与"被截断"

- **WHEN** 命中 2 个且全部返回
- **THEN** `total=2`、`remaining=0`
- **AND** 结果不提示翻页

### Requirement: Pagination guidance prefers narrowing over paging

结果尾部 MUST 在 `remaining > 0` 时优先引导缩窄检索范围（`namespace` 或更精确的关键词），并 MAY 同时给出下一页的 `offset`。MUST NOT 仅提示"继续翻页"而不给出缩窄建议。

#### Scenario: 有剩余时优先建议缩窄

- **WHEN** `remaining > 0`
- **THEN** 结果包含"用 namespace 缩小范围"或"换更精确关键词"的建议
- **AND** 结果同时给出 `offset=M` 作为兜底

#### Scenario: 无剩余时建议换词

- **WHEN** `remaining == 0` 且 `total > 0`
- **THEN** 结果说明已列出全部匹配
- **AND** 若模型仍不满意，建议换用更宽的关键词或省略 query 浏览命名空间

### Requirement: Full detail pages are hard-capped at three entries

`detail=full` 时单页条数 MUST 硬上限为 3，即使 `limit` 传入更大值也 MUST 按 3 截断。`offset` 在 `full` 档位仍然可用。

#### Scenario: full 档位忽略过大的 limit

- **WHEN** 模型以 `detail=full` 且 `limit=10` 检索
- **THEN** 最多返回 3 条完整定义
- **AND** `returned <= 3`

#### Scenario: full 档位剩余时提示不要继续翻页

- **WHEN** `detail=full` 且 `remaining > 0`
- **THEN** 结果明确建议缩窄范围而非继续 full 加载
- **AND** 说明继续加载会把过多 schema 写入上下文

### Requirement: Empty search results are actionable

检索无命中时 MUST 返回可操作提示，包含可用命名空间列表与建议做法，MUST NOT 返回空字符串。

#### Scenario: 未命中给出命名空间

- **WHEN** 检索无任何命中
- **THEN** 结果说明未命中
- **AND** 结果列出可用命名空间及其工具数量

### Requirement: Exact-id lookup replaces a separate load tool

系统 MUST NOT 提供独立的"加载工具"元工具。按精确 id 取得完整定义 MUST 通过同一检索工具的精确匹配路径完成（`query` 等于某个工具 id，`detail=full`）。

#### Scenario: 按 id 精确加载

- **WHEN** 模型以某工具 id 作为 query 且 `detail=full`
- **THEN** 返回该工具的完整定义
- **AND** 该工具被写入会话发现集

#### Scenario: 精确 id 排名第一

- **WHEN** 模型以某工具 id 作为 query
- **THEN** 该工具排在结果首位

#### Scenario: 未知 id 不抛异常

- **WHEN** 模型以不存在或已失效的 id 检索
- **THEN** 返回明确的未命中说明与可用命名空间
- **AND** 不抛出异常、不中断执行

### Requirement: Discovery tool has no dedicated call quota

检索元工具 MUST NOT 引入独立的调用配额、限流或专用的循环检测特例。其每次调用 MUST 按一次普通工具调用计入既有的单执行步数上限（`AgentDefinition.MaxSteps`）。系统 MUST NOT 因检索次数而提前终止执行。

#### Scenario: 检索计入既有步数上限

- **WHEN** 模型在一次执行中多次调用检索元工具
- **THEN** 每次调用消耗一个执行步骤
- **AND** 达到 `MaxSteps` 时按既有逻辑终止

#### Scenario: 不引入独立限流

- **WHEN** 模型在同一执行中连续调用检索元工具 20 次且 `MaxSteps` 充裕
- **THEN** 系统不因"检索次数过多"而拒绝或截断
- **AND** 不存在仅针对检索工具的配额计数

#### Scenario: 不特例化循环检测

- **WHEN** 模型以不同 query 连续调用检索元工具
- **THEN** 既有循环检测不触发（因其按相同参数判定）
- **AND** 不为检索工具新增"同名即计数"的特例规则

### Requirement: Discovery tool is always available in tiered mode

分级模式下，检索元工具 MUST 始终为 `inline`，且 MUST NOT 被策略标记为 `deferred` 或 `hidden`。

#### Scenario: 元工具常驻

- **WHEN** 分级模式生效
- **THEN** 检索元工具出现在请求 `tools` 数组
- **AND** 目录摘要中包含"如何检索"的说明

### Requirement: Discovery tool is hidden in full exposure mode

全量暴露模式下，检索元工具 MUST 被标记为 `hidden`，MUST NOT 出现在请求 `tools` 数组与目录摘要中（此时不存在延迟工具，检索无意义）。

#### Scenario: 全量模式不暴露检索工具

- **WHEN** 暴露模式为全量
- **THEN** 检索元工具不出现在请求 `tools` 数组
- **AND** 模型无法调用它
