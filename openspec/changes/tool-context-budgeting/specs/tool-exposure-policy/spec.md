## ADDED Requirements

### Requirement: Exposure policy decides inline, deferred and hidden

系统 MUST 提供工具暴露策略，为每个工具决定三种暴露模式之一：`inline`（完整 schema 进入请求 `tools` 数组）、`deferred`（仅出现在目录摘要中）、`hidden`（本轮完全不可见）。

#### Scenario: 策略产出三种模式

- **WHEN** 策略对一组工具求值
- **THEN** 每个工具被标记为 `inline`、`deferred` 或 `hidden`
- **AND** `inline` 工具的完整 schema 进入请求 `tools` 数组
- **AND** `deferred` 工具不出现在请求 `tools` 数组中

#### Scenario: 隐藏工具不出现在目录

- **WHEN** 某工具被策略标记为 `hidden`
- **THEN** 该工具不出现在请求 `tools` 数组
- **AND** 不出现在目录摘要

### Requirement: Exposure policy is a pure function

暴露策略 MUST 是纯函数：MUST NOT 访问服务容器、会话管理器或任何存储；MUST NOT 读取工具注册表。其全部输入 MUST 由调用方以不可变快照提供，输出 MUST 仅依赖该输入。

#### Scenario: 相同输入产出相同计划

- **WHEN** 以相同输入调用策略两次
- **THEN** 两次产出的暴露计划完全相同

#### Scenario: 策略不读取注册表

- **WHEN** 策略被调用时工具注册表发生变化
- **THEN** 该次调用结果不受影响（使用调用方快照）

### Requirement: Exposure mode is configurable per scope

暴露策略模式 MUST 可通过全局配置设定，并 MUST 支持 Scenario 级与 Agent 级覆盖。会话级覆盖 MUST 在下一轮执行生效。优先级 MUST 为：会话覆盖 > Agent 覆盖 > Scenario 覆盖 > 全局默认。

#### Scenario: 默认模式为自动

- **WHEN** 未配置暴露模式
- **THEN** 生效模式为自动

#### Scenario: Agent 覆盖全局配置

- **WHEN** 全局为自动模式且某 Agent 声明为全量模式
- **THEN** 该 Agent 的所有会话工具为 `inline`

#### Scenario: 会话覆盖下一轮生效

- **WHEN** 会话在两次执行之间切换暴露模式
- **THEN** 第二次执行使用新模式的暴露计划

#### Scenario: 优先级顺序

- **WHEN** 全局为自动、Scenario 为全量、Agent 为自动、会话为全量
- **THEN** 生效模式为全量（会话覆盖胜出）

### Requirement: Pinned tools are always inline

策略 MUST 支持"常驻工具"集合，集合内工具 MUST 始终为 `inline`，且其顺序 MUST 稳定（按配置声明顺序）。

#### Scenario: 常驻工具不进入目录

- **WHEN** 分级模式启用且常驻集合包含 `read`
- **THEN** `read` 出现在请求 `tools` 数组
- **AND** `read` 不出现在目录摘要中

#### Scenario: 常驻集合为空时退化为目录

- **WHEN** 常驻集合为空
- **THEN** 所有非隐藏、非已发现工具均进入目录摘要
- **AND** 请求 `tools` 数组只包含检索元工具与已发现工具

### Requirement: Catalog obeys a fixed character budget

目录摘要 MUST 受固定字符预算约束（默认 2000 字符）。当条目总量超出预算时，系统 MUST 保留每个命名空间至少一条可见记录，并在命名空间行标注工具总数与已展示数量。

#### Scenario: 超预算时每命名空间仍可见

- **WHEN** 目录包含 5 个命名空间共 60 个工具且预算只能容纳 12 行
- **THEN** 每个命名空间至少出现一条记录
- **AND** 未完整展示的命名空间行包含总数与已展示数

#### Scenario: 预算内完整展示

- **WHEN** 目录条目总量在预算内
- **THEN** 全部条目被展示
- **AND** 命名空间行不标注截断

### Requirement: Escalation to tiered uses two orthogonal conditions

自动模式 MUST 在满足以下任一条件时进入分级暴露：

- 可用工具数量达到或超过数量阈值（默认 30）；
- 工具定义估算 token 达到或超过成本阈值（默认 8000）。

两个条件 MUST 独立判定并以"或"组合，两个阈值 MUST 可配置。

#### Scenario: 数量达标即触发

- **WHEN** 可用工具为 30 个且定义估算 token 仅 3000
- **THEN** 进入分级暴露模式

#### Scenario: 成本达标即触发

- **WHEN** 可用工具为 5 个且定义估算 token 达到 8000
- **THEN** 进入分级暴露模式

#### Scenario: 双条件均未达标则全量

- **WHEN** 可用工具为 11 个且定义估算 token 为 4400
- **THEN** 所有工具为 `inline`
- **AND** 不注入目录摘要

### Requirement: Escalation is monotonic and persisted per session

进入分级暴露后，同一会话 MUST NOT 自动退回全量暴露。升级状态 MUST 随会话持久化，MUST 在会话恢复后保留，MUST 在 fork 时被子会话继承。只有用户显式修改模式或新会话重新判定时才 MAY 退出。

#### Scenario: 工具减少后不自动降级

- **WHEN** 会话已进入分级暴露，随后禁用工具使数量与 token 均低于阈值
- **THEN** 该会话仍使用分级暴露
- **AND** 已发现工具仍为 `inline`

#### Scenario: 会话恢复后保持升级

- **WHEN** 已升级的会话被持久化后重新加载
- **THEN** 该会话仍为分级暴露

#### Scenario: 子会话继承升级状态

- **WHEN** 从已升级的会话 fork 出子会话
- **THEN** 子会话为分级暴露

#### Scenario: 显式切换可退出

- **WHEN** 用户显式把该会话的暴露模式改为全量
- **THEN** 下一次执行使用全量暴露

#### Scenario: 新会话重新判定

- **WHEN** 新建会话且其可用工具为 4 个
- **THEN** 该会话使用全量暴露

### Requirement: Escalation decision is stable within one execution

一次执行内的暴露模式 MUST 只判定一次；执行期间可用集合变化 MUST NOT 改变本次执行的模式。

#### Scenario: 同一执行内不翻转

- **WHEN** 一次执行期间有工具注册或注销
- **THEN** 本次执行仍使用进入时的模式

### Requirement: Hidden mode is produced only by the exposure policy

`hidden` MUST 只由暴露策略产生。除策略显式标记外，任何工具 MUST NOT 因其他原因被隐藏。

#### Scenario: 仅策略可隐藏

- **WHEN** 工具通过结算与 Agent 过滤进入可用集合
- **THEN** 其最终模式由策略决定
- **AND** 不存在绕过策略的隐藏路径

### Requirement: Exposure never changes authorization

暴露策略 MUST 只影响工具对模型的可见性，MUST NOT 授予或撤销任何执行权限。被延迟或隐藏的工具在执行请求到达时 MUST 仍按原有权限链判定。

#### Scenario: 延迟工具调用仍走权限链

- **WHEN** 模型调用一个已发现但需审批的工具
- **THEN** 该调用进入审批流程
- **AND** 审批拒绝时调用失败

#### Scenario: 目录不含权限判定结果

- **WHEN** 目录摘要被渲染
- **THEN** 目录中不包含"允许/拒绝"等授权信息
- **AND** 目录条目不因权限规则而增减
