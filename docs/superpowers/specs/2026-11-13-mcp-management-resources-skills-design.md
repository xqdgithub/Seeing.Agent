# MCP 管理工具、Resources、Skills 与 Prompts 设计

**日期:** 2026-11-13  
**状态:** 已按审查修订（待实现）  
**范围:** `Seeing.Agent.Mcp` 模块内：会话面拆分、agent 可调用的管理工具与 Resource 工具、MCP Skills 并入现有 Skill 体系、MCP Prompts 接到用户侧 slash/命令。不引入跨模块通用「能力管理」协议。

---

## 1. 问题

MCP 模块今天几乎是 **Tools-only**：

1. **管理面不对 agent 开放。** `IMcpManager`（配置 CRUD、启停、重连）与 WebUI `/mcp`、半成品 `/mcp list` 存在；`McpModule.ProvidedTools` 为空。Agent 无法自行增删改 server，只能人手改配置或点 UI。
2. **Resources / Prompts 未接线。** SDK（`ModelContextProtocol.Core` 2.2.0）已有 `ListResources` / `ReadResource` / `ListPrompts` / `GetPrompt` 等 API，但被 `IMcpClientWrapper` 的「只暴露 ListTools/CallTool」截断。Abstractions 无对应 DTO；状态模型只有工具计数。
3. **Skills 扩展未支持。** 官方扩展 `io.modelcontextprotocol/skills`（SEP-2640，Final）基于 Resources：`skills/list`、`skills/get`，文件经 `resources/read`。Seeing 本地已有 Skill 体系，与 MCP 侧未打通。
4. **传输与编排缠在一起。** `McpClientManager` 巨石（配置 + 生命周期 + 内嵌 wrappers）；再堆 Resources/Skills/管理工具会继续恶化。架构债文档已指出 wrappers 仍是生产传输层，不宜误删，但应外置。

对标：Scheduler 用 `ProvidedTools` + 薄工具封装 `IScheduleManager` / `IHeartbeatService`。MCP 管理应对齐该模式，而不是扩未完成的 `/mcp` slash 子命令。

---

## 2. 目标与非目标

### 2.1 目标（同一规格，分阶段落地）

| 阶段 | 交付 | 成功标准 |
|------|------|----------|
| **P0** | 会话面拆分 + 管理工具 + 工作区对齐 | Agent 可 list/get/add/update/remove/enable/disable/pause/resume/reconnect（及 JSON 导入）；写入持久化到独立 `mcp.json`；切换工作区不会把上一项目的 server 写进新根；管理权限与 `mcp.execute` 分离；现有 `mcp__{server}__{tool}` 行为不变 |
| **P1** | Resources 专用工具 | 按 `ServerCapabilities.resources` 门控；`list` / `read`（及可选 templates）；独立权限，记忆键为 `server\|uri`；**不**把每个 resource 注册进模型 `tools[]` |
| **P2** | MCP Skills → 现有 Skill 体系 | 声明了 skills 扩展的 server：经 Abstractions 贡献端口进入技能目录快照（不走 `RegisterAsync`）；正文与附件在加载时回源 `resources/read`；`resources/read` 本身不激活技能 |
| **P3** | Prompts → 用户侧 | 有 `prompts` capability 时经 `ICommandRegistry` 注册斜杠命令；命令结束本轮后只投递一条用户消息；**不**提供 agent 用的 prompt 工具 |

### 2.2 非目标

- 不抽通用 `IManagedCapability` / 跨模块统一管理工具框架（可留命名与权限形状以便将来演进；本期不做）。
- 不做 Sampling、Roots、Completion；规格仅记「未做」。
- 不做 resource `subscribe` 实时推送（后续可加；本期 list/read 即可）。
- 不把 `/mcp` slash 做成完整管理 CLI（管理以 **agent 工具 + `/mcp` 页** 为主；现有 `/mcp list` 可保留或薄委托到同一 API）。
- 不改变远端工具 id 约定 `mcp__{server}__{tool}`（`ToolIdConventions.McpPrefix`）。
- 不把 MCP Prompts 做成 agent 专用工具（协议语义为 user-controlled）。

### 2.3 已定决策摘要

| 项 | 决定 |
|----|------|
| 管理工具归属 | MCP 模块自贡献（对标 Scheduler），不先做通用能力管理协议 |
| Resources 消费 | 专用工具 `mcp_list_resources` / `mcp_read_resource` 等 |
| Skills 消费 | Skills 模块消费 Abstractions 贡献端口；发现期只有元数据，正文加载时回源 |
| Prompts 消费 | 动态斜杠命令；本轮不继续，再投递一条用户消息 |
| 架构 | 拆出 `IMcpSession`；OAuth 是 HTTP 会话上的装饰，不是第三种传输；`IMcpManager` 保留为编排门面 |
| 配置 | 继续独立 `mcp.json`（先用户级再项目级，同名项目覆盖）；MCP 模块实现 `IWorkspaceApplyParticipant` |
| 权限 kind | 字符串契约：`mcp.manage.read` / `mcp.manage.write` / `mcp.manage.lifecycle` / `mcp.resource.read`。不新增 `PermissionKind` 枚举。grant 按字符串精确匹配，与 `mcp.execute` 不共用 |
| 交付 | 一规格四阶段；实现可拆 PR，但契约与权限模型一次定清 |

---

## 3. 架构

### 3.1 分层

```
McpModule（能力模块 id=mcp）
├── IMcpManager（编排门面，保留对外 API）
│   ├── Config     → mcp.json（Add/Remove/Update/Enable/Disable/Import/Save）
│   ├── Lifecycle  → Connect/Disconnect/Pause/Resume/Reconnect
│   ├── Status     → 含 ServerCapabilities / 扩展声明摘要
│   └── Sessions   → serverId → IMcpSession
├── IMcpSession（新建：每连接一会话，替代窄 IMcpClientWrapper）
│   ├── Tools      List / Call
│   ├── Resources  List / Read / ListTemplates（无 capability → NotSupported）
│   ├── Prompts    List / Get
│   └── SkillsExt  List / Get（仅当声明 io.modelcontextprotocol/skills）
├── 动态工具       McpTool（mcp__*）经 McpToolRegistry + IDynamicToolContributor
├── ProvidedTools  管理工具 + Resource 工具（Activate 注册 / Deactivate 注销）
├── Skill 贡献     实现 Abstractions 端口；只由 Skills 模块并进目录快照
├── 工作区         IWorkspaceApplyParticipant（切换时断开项目级会话并重载）
└── 命令贡献       MCP Prompts → ICommandRegistry（P3）
```

### 3.2 职责边界

| 组件 | 职责 | 不职责 |
|------|------|--------|
| `IMcpManager` | 配置真相、连接生命周期、状态广播、持有 session 表 | 不直接解析 SDK 消息；不做 UI |
| `IMcpSession` | 对单个已连接 server 的协议调用；按 capabilities 门控 | 不写 `mcp.json`；不注册 `ITool` |
| 管理工具 | 参数校验 + 权限 + 调用 Manager | 不内嵌传输 |
| Resource 工具 | 参数校验 + 权限 + 经 Manager 取 session 再读 | 不把 URI 注册成工具 |
| MCP 技能贡献 | 发现期提供 name / description / uri；加载期按请求回源正文与清单内附件 | 不调用 `ISkillCatalog.RegisterAsync`；不在 read 时激活 |
| Prompt 命令 | 收齐参数 → `prompts/get` → 结束本轮 → 投递一条用户消息 | 不进 `ProvidedTools`；不 `AddMessage`；不 `ShouldContinue = true` |
| `/mcp` 页 | 继续只调 `IMcpManager` | 不维护第二套配置缓存为真相源 |

### 3.3 重构要点（P0 必做）

1. 将 Stdio 与 HTTP 客户端包装从 `McpClientManager` **外置**为 `IMcpSession` 实现。OAuth 保持为 HTTP 会话上的装饰（连接前注入 Bearer，401 时刷新并重试），不是并列的第三种传输。
2. 连接成功后读取并缓存 `ServerCapabilities`（含 `extensions`）；写入 `McpServerStatus`。新增字段必须进入手写的构造与 `Clone()`，因为 `McpStateService` 缓存的是 `Clone()`。
3. 删除或降级公开的 `IMcpClientWrapper`（现为 Connect / Disconnect / ListTools / CallTool）。测试改为 mock `IMcpSession`。禁止继续以该窄接口为扩展点。
4. Tool 结果中的 `EmbeddedResourceBlock`：输出可跟进的 URI，并提示可用 `mcp_read_resource`（P1 落地后）。不内联二进制。

---

## 4. 管理工具（P0）

### 4.1 工具清单

命名与动态代理 `mcp__*` **分开**，建议前缀 `mcp_` + 动词/名词（与 `cron_*` 同类可读风格）：

| 工具 id | 作用 | 权限建议 |
|---------|------|----------|
| `mcp_list_servers` | 列表 + 状态 + capabilities 摘要 | `mcp.manage.read` |
| `mcp_get_server` | 单机详情（工具数；P1+ 含 resource/prompt 提示） | `mcp.manage.read` |
| `mcp_add_server` | 增加配置。必填 `ConfigLevel`。`connect` 显式覆盖 `McpGlobalPolicy.AutoStartOnAdd` | `mcp.manage.write` |
| `mcp_update_server` | 更新配置 | `mcp.manage.write` |
| `mcp_remove_server` | 删除 | `mcp.manage.write` |
| `mcp_enable_server` / `mcp_disable_server` | 启用/禁用 | `mcp.manage.write` |
| `mcp_pause_server` / `mcp_resume_server` | 暂停/恢复 | `mcp.manage.lifecycle` |
| `mcp_reconnect_server` | 重连 | `mcp.manage.lifecycle` |
| `mcp_import_servers` | JSON 导入。必填 `ConfigLevel` | `mcp.manage.write` |

均实现为薄 `ToolBase`（或项目惯用基类），注入 `IMcpManager`。`McpModule.ProvidedTools` 列出上述 id；`Activate` 注册、`Deactivate` 按 id 注销。

描述与参数 Schema 遵守 `docs/architecture/04` §9（R1–R9）。

### 4.2 权限

Kind 名是字符串契约，不新增 `PermissionKind` 枚举。`PermissionGrantStore` 按 kind 字符串精确匹配，所以 `mcp.manage.*` 与 `mcp.execute` 不会共用一条 grant。

| kind | 用途 | `Resource` |
|------|------|------------|
| `mcp.manage.read` | list / get | list 用 `*`；get 用 server 名 |
| `mcp.manage.write` | add / update / remove / enable / disable / import | server 名；import 用目标级别 |
| `mcp.manage.lifecycle` | pause / resume / reconnect | server 名 |
| `mcp.execute` | 既有远端工具调用，本期不改语义 | server 名 |
| `mcp.resource.read` | 读 resource（P1） | `server\|uri` |

写与生命周期默认 Ask。`PermissionKindMapper` 继续把 `mcp.*` 映射到 `PermissionKind.McpTool`，这只影响 Agent 规则与审计，不影响 grant。

已知副作用，实现时按此写测试，不改语义：

- `IsResourceKind` 已把全部 `mcp.*` 当资源类。Agent 的 `Allow(Tool, "*")` 不会短路管理或读资源，默认落到询问。
- `Deny(PermissionKind.McpTool)` 以及能匹配 `McpTool` 的 `Deny(Tool)` 会同时拒绝 `mcp.execute` 和管理操作。`Tool` 规则同时匹配 `McpTool` 和 `Skill`。

### 4.3 与 UI / slash

- WebUI `/mcp` 与工具共享 `IMcpManager`，无双写。
- 不扩展 `/mcp` 未实现子命令为第二管理入口；避免与工具/UI 三处语义漂移。`/mcp list` 若保留，应委托同一状态 API。

### 4.4 数据流（添加 server）

```
Agent: mcp_add_server(configLevel, connect, ...)
  → 工作区未选定且 configLevel=Project → 拒绝，不写盘
  → 授权 mcp.manage.write
  → 写入该级别的 mcp.json
  → connect 显式值决定是否连接（覆盖 AutoStartOnAdd）
  → 连接成功则 ListTools → McpToolRegistry（动态 mcp__*）
  → 缓存 capabilities；刷新 Skill/Prompt 贡献（P2/P3）
  → StatusChanged → WebUI（读 Clone() 后的状态）
```

更新、删除、启停作用于该 server 已记录的 `ConfigLevel`，不在保存时改写到另一个级别。

### 4.5 工作区切换

MCP 模块实现 `IWorkspaceApplyParticipant`。`SaveConfigAsync` 会把内存里该 `ConfigLevel` 的全部 server 写到持久化层当前路径；`AddServerAsync` 默认项目级。未实现参与者时，切换后的第一次项目级保存会把上一工作区的 server 写进新根。

硬规则：

1. `OnApplyingAsync` 只断开项目级会话。此阶段禁止 `Save`。
2. `OnAppliedAsync` 再加载：先用户级，再项目级，同名以项目级覆盖。
3. 用户级 server 跨切换保留。禁止把切换前的项目级内存集保存到新根。
4. `IWorkspaceProvider.IsResolved == false` 时，拒绝一切项目级写入（含 add 与 import）。
5. 断开项目级会话时，同时撤掉该 server 的动态工具、技能贡献和 prompt 命令。

---

## 5. Resources（P1）

### 5.1 工具

| 工具 id | 参数 | 行为 |
|---------|------|------|
| `mcp_list_resources` | `server` 必填；可选 `cursor` | 无 resources capability → 明确失败（非空列表伪装） |
| `mcp_read_resource` | `server` + `uri` | 返回文本与元数据；二进制给 mime/size/摘要，避免把巨量 bin 塞进上下文 |
| `mcp_list_resource_templates` | `server` | 可选提供；不支持则明确错误 |

**禁止**为每个 remote resource 注册 `ITool` 进模型工具表（与 tool-context-economy 规格一致：只读数据不应伪装成工具）。

### 5.2 权限

- Kind：`mcp.resource.read`。
- 记忆键定为 `server|uri`。不先做 server 级再迁移。
- 批准「调用该 server 的工具」不批准读取任意 resource URI。技能加载时的 `resources/read` 走同一 kind 与同一记忆键。

### 5.3 Capabilities 门控

所有 Resource 调用前检查会话缓存的 capabilities；缺失则返回稳定错误码/文案，便于模型改策略，禁止对不支持的 server 盲打 SDK 再吞异常。

---

## 6. Skills 并入现有体系（P2）

### 6.1 协议侧

Server 须声明：

- `capabilities.resources`
- `capabilities.extensions["io.modelcontextprotocol/skills"]`（可含 `directoryRead`）

并实现 `skills/list`、`skills/get`；文件经 `resources/read`（约定 `skill://…`）。

规格依据：SEP-2640 / [Skills 扩展文档](https://modelcontextprotocol.io/extensions/skills/overview)。SDK 若尚未封装扩展方法，允许经 session 发原始 JSON-RPC；**仅当** capabilities 声明了该扩展才调用。

### 6.2 宿主集成

现有技能没有外部目录源。`ISkillCatalog.RegisterAsync` 进入 `SkillSource.Registered`，同名时会遮蔽文件技能。`SkillTool` 与 `SkillsContextSource` 只读执行级钉住快照里的 `SkillInfo.Content`，没有加载期回源。宿主没有单独的技能审批。因此 MCP 技能不走注册表。

```
声明了 skills 扩展的已连接 server
    → MCP 实现 Abstractions 贡献端口
         发现：name / description / uri（frontmatter 可带）
         ProviderId = mcp:{serverId}
    → 只由 Skills 模块消费，并进目录快照
    → 选用后，Skills 模块回调该端口取正文与清单内附件
         回调内部走 resources/read，并经 mcp.resource.read
```

硬规则：

1. **禁止**用 `ISkillCatalog.RegisterAsync` 或 `SkillSource.Registered` 登记 MCP 技能。
2. 贡献端口定义在 Abstractions。只由 **Skills 模块**消费并写入目录快照。MCP 包只实现该端口。禁止由 Core 编排消费。MCP 包只依赖 Abstractions，禁止引用 Core 具体类型。
3. 发现期只放 name、description、uri。正文和附件在加载时回源。贡献元数据进入本轮钉住快照；正文允许在加载时按该来源回源。`resources/read` 本身不构成激活。
4. 键是单个命令 token，且不得与本地技能名相同。限定名的最终字符串在实现计划锁定，但必须满足这两条，并加测试。`ProviderId` 记 `mcp:{serverId}`。
5. 断开、禁用、`Deactivate` 时撤掉该 server 的条目。
6. 这些条目会进入 `DynamicSkillCommandSync`。与内置命令重名时跳过并记日志。禁止靠 `CommandRegistry.Register` 的同名覆盖。
7. 附件来自清单 URI，不走 `Directory.GetFiles`，不输出 `file:///` 基目录。`SkillManager.GetSkillFiles` 对无目录条目返回空列表，MCP 技能不得依赖该路径。
8. 声明了扩展但 list/get 失败：该 server 技能不可发现，打 Warning，不影响该 server 的 tools 与 resources。
9. 不把 MCP 技能复制进磁盘技能根。在线发现加按需 read。允许按 digest/uri 做内容缓存。

P2 依赖 P1 的 resource read。两者可以同一迭代，但加载路径以 Session 能读 resource 为前提。

### 6.3 与本地 Skill 模块关系

Skills 模块负责把贡献并进快照，并在加载时回调。MCP 模块不调用 `SkillTool`，不改技能审批（宿主没有这条路径）。同名冲突按 6.2 第 4 条处理，本地文件技能保持可调用。

---

## 7. Prompts → 用户侧（P3）

### 7.1 语义

MCP Prompts 是 **user-controlled** 模板（斜杠命令、菜单），不是模型随意调用的工具。Seeing 已有 slash 命令体系；远端 prompt 映射为命令贡献。

### 7.2 行为

斜杠命令在 `ExecutionRunner.ProcessCommandAsync` 里执行，此时这条用户消息已经被消费循环标成已消费，本轮还在跑。`GetPromptResult.Messages` 可以是多条 `PromptMessage`。`InboxMessage` 只有一个 `Body`。技能展开（`SkillLoadCommand`）是改写当前消息并 `ShouldContinue = true`，不再 `DeliverAsync`。Prompt 不走那条路：本轮里再 `DeliverAsync(Wake)` 时，只有命令返回 `ShouldContinue = false`，下一次循环才会消费新消息；若同时改写并继续本轮，会投递两次。

写死：

1. 连接后若 `capabilities.prompts` 存在：`prompts/list`，经 `ICommandRegistry` 登记。具体斜杠语法在实现计划锁定。示例 `/mcp-prompt` 与现有 `/mcp` 不冲突。登记前查重；重名则跳过并记日志。`Register` 会覆盖，禁止靠覆盖抢名。
2. 参数在 `prompts/get` 之前由命令收齐。
3. 命令 `RemoveCommandMessage = true` 且 `ShouldContinue = false`。
4. 然后投递一条 `InboxDelivery.Wake`、`InboxPresentation.User` 的 `InboxMessage`。`Body` 为各 `PromptMessage` 的文本拼接；assistant 段带角色前缀；embedded resource 只留 URI，并提示用 `mcp_read_resource`，不内联二进制。
5. 禁止 `SessionManager.AddMessage`。禁止本轮 `ShouldContinue = true`。
6. **不**注册 `mcp_list_prompts` / `mcp_get_prompt` 为 agent `ITool`。
7. Disconnect、Disable、`Deactivate` 时注销该 server 的命令。

### 7.3 权限

用户显式触发命令本身不再套一层管理权限。`prompts/get` 若只返回消息文本，不另开 resource 门。消息里的 embedded resource 不在这一步读取；之后的 `mcp_read_resource` 走 `mcp.resource.read`。

---

## 8. 状态模型扩展

`McpServerStatus` 在现有 ToolCount / ToolNames 之外增加下列字段。构造函数和 `Clone()` 是手写字段列表，新字段必须同时进入这两处。并列只读投影可以代替改状态类型，但 UI 与管理工具必须读同一份摘要，不能一边有字段一边被 `Clone()` 丢掉。

| 字段 | 说明 |
|------|------|
| Capabilities 摘要 | tools / resources / prompts / skills 扩展是否声明 |
| ResourceCount（可选） | 连接后 list 成功则填；失败或未支持则为 null |
| PromptCount（可选） | 同上 |
| SkillCount（可选） | 同上 |
| LastError | 保持现有错误可见性 |

UI `/mcp` 可展示能力徽章；管理工具 `mcp_list_servers` / `mcp_get_server` 返回同一摘要。

---

## 9. 配置与迁移

- **配置格式不破坏：** 继续 `~/.seeing/mcp.json` 与 `{项目}/.seeing/mcp.json`。加载顺序保持先用户级、后项目级，同名项目覆盖。
- **工作区：** 见 §4.5。未选工作区时项目路径是占位目录，项目级写入必须拒绝。
- **无强制用户迁移步骤。**
- **代码迁移：** 外置 Session；OAuth 装饰保留在 HTTP 会话外层；删除公开窄 Wrapper；测试改为 mock `IMcpSession` / Manager。
- **权限规则：** 既有 `mcp.execute` grant 继续只匹配远端工具调用。新 kind 没有历史 grant，写与生命周期默认 Ask。

---

## 10. 测试

| 层 | 覆盖 |
|----|------|
| 单元 | Session capabilities 门控；管理工具 kind 与 `mcp.execute` grant 不互通；`mcp.resource.read` 记忆键为 `server\|uri`；未选定工作区时拒绝项目级写入；切换中 `OnApplyingAsync` 不调用 Save |
| 集成 | 假 MCP server：tools + resources；技能经贡献端口进入快照且不调用 `RegisterAsync`；加载时回源；prompt 命令 `ShouldContinue = false` 且只产生一条 `InboxDelivery.Wake` |
| 回归 | 既有 `mcp__*` 调用、`mcp.execute` server 粒度授权、OAuth 仍包在 HTTP 会话外、`/mcp` 页 CRUD、切换工作区后项目级 `mcp.json` 不被上一项目覆盖 |
| 禁止 | 测试中用固定 `Task.Delay` 等状态；用条件等待或完成信号（项目硬规则） |

---

## 11. 风险与开放实现细节

| 风险 | 缓解 |
|------|------|
| Skills 扩展 SDK 封装不全 | Session 层按声明发方法；无声明不调用；失败降级 |
| `McpClientManager` 拆分回归 | P0 以「行为不变」回归套件守门后再加管理工具 |
| Prompt 命令命名冲突 | 登记前查重，重名跳过并记日志；禁止靠 `Register` 覆盖 |
| Resource 大体积 | read 工具截断/摘要策略与文件系统工具输出限长对齐 |
| 工作区切换写错根 | §4.5：Applying 阶段禁止 Save；Applied 后再加载 |

实现计划锁定、本设计已约束边界的细节：

- 管理工具的精确 JSON Schema（遵守 `docs/architecture/04` §9；`ConfigLevel` 与 `connect` 已是必填语义）
- Skill 限定名的最终字符串（必须是单个命令 token，且不得与本地技能名相同）
- Prompt 斜杠的具体语法（投递契约见 §7.2；不得与 `/mcp` 冲突）

---

## 12. 预期文件落点

| 区域 | 路径 |
|------|------|
| 契约 | `src/abstractions/Seeing.Agent.Abstractions/Mcp/`（`IMcpSession`、capabilities/resource/prompt 只读模型） |
| 技能贡献端口 | `src/abstractions/Seeing.Agent.Abstractions/Skills/`；消费方在 `src/capabilities/Seeing.Agent.Skills/` |
| 会话实现 | `src/capabilities/Seeing.Agent.Mcp/Session/`（HTTP 会话 + OAuth 装饰） |
| 管理工具 | `.../Tools/Manage/` |
| Resource 工具 | `.../Tools/Resources/` |
| 技能贡献实现 | `Seeing.Agent.Mcp` 内实现上述端口，不调用 `RegisterAsync` |
| 工作区参与者 | `Seeing.Agent.Mcp` 内 `IWorkspaceApplyParticipant` |
| Prompt 命令 | `.../Commands/McpPromptCommands.cs` |
| 模块 | `McpModule.cs`：ProvidedTools、Activate/Deactivate、工作区参与者 |
| 测试 | `tests/capabilities/Seeing.Agent.Mcp.Tests/` |
| 本规格 | `docs/superpowers/specs/2026-11-13-mcp-management-resources-skills-design.md` |


---

## 13. 实现顺序建议

1. **P0a** — `IMcpSession` + 外置传输（OAuth 仍装饰 HTTP）+ capabilities 写入 `Clone()` + 回归绿灯
2. **P0b** — 管理工具、字符串 kind、`IWorkspaceApplyParticipant`
3. **P1** — Resource 工具；记忆键 `server|uri`
4. **P2** — Abstractions 贡献端口 + Skills 模块消费 + MCP 发现/回源
5. **P3** — Prompt 命令：结束本轮后投递一条用户消息

每阶段可独立 PR。Session、kind 字符串、工作区参与者契约在 P0 合并前冻结。技能贡献端口在 P2 开工前冻结。
