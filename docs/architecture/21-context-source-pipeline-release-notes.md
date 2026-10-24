# 21 上下文源管道 Release Notes

> **实施日期：** 2026-10-24
> **规格：** [上下文源管道设计](../superpowers/specs/2026-10-24-context-source-pipeline-design.md)
> **实施计划：** [context-source-pipeline 计划](../superpowers/plans/2026-10-24-context-source-pipeline.md)
> **适用范围：** `Seeing.Agent.Abstractions.Context`（契约）、`Seeing.Agent.Core`（管道实现）、`Seeing.Agent.Skills`（技能源 + 目录 + 写入）、`Seeing.Agent.Hosting`（主链接线）、`Seeing.Agent.Scheduler`（旁路只读）

---

## 1. 一句话

每种上下文内容实现一个 `IContextSource`（读值 + 两段渲染），唯一的 `ContextAssembler` 负责规范化哈希、diff、增量消息与会话级状态：
**初始渲染字节稳定（不破坏前缀缓存），运行时变化只产生一条增量叙述**。项目指令与技能清单是第一批两个内置源；技能状态同时收敛到 `ISkillCatalog` 单一真相源。

---

## 2. 机制：基线 + 增量 + epoch

### 2.1 装配入口

| 位置 | 文件 |
|---|---|
| 契约（零实现） | `src/abstractions/Seeing.Agent.Abstractions/Context/IContextAssembler.cs`、`IContextSource.cs`、`ContextSourceModels.cs`、`IContextExecutionScope.cs`、`IContextSourceMessageMetadata.cs` |
| 实现 | `src/spine/Seeing.Agent.Core/Context/ContextAssembler.cs`（编排）、`ContextBaselineState.cs`（状态模型）、`ContextBaselineStore.cs`（读写会话元数据）、`CanonicalJson.cs`（规范化 + sha256） |
| DI 注册 | `AddSeeingCore` → `AddContextPipeline()`（`src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs`） |
| 装配点（主链） | `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs`：命令处理之后、构建历史之前 |
| 状态键 | `SessionMetadataKeys.ContextBaseline = "context.baseline"`（`src/primitives/Seeing.Session/Core/SessionMetadataKeys.cs`） |
| 增量消息元数据键 | `SessionMetadataKeys.ContextUpdateSources = "context.update.sources"`（本次叙述涉及的源键，供 UI 折叠 / 标题计数） |

### 2.2 值模型（不落正文）

- `Canonical`：参与 diff 的规范化值（如项目指令只含 `{"files":[{"path","sha256"}]}`，技能只含 `{"skills":[{"id","name","description"}]}`），**落盘**。
- `Payload`：正文等原始数据，只在本次渲染使用，**不落盘**；每次读取重新取得。
- 状态只保存「规范化值 + 已渲染文本 + 增量账本（`deltaLedger`，上限 32 条）+ 待处理重置位（`pending`）」。

### 2.3 三态与「不叙述掉线」

`ContextValueState` 四态：`Initial` / `Value` / `Unavailable` / `Removed`。

- **不可读 ≠ 删除**：任一项目指令文件被占用/无权限时整体判为 `Unavailable`，保留上一次已叙述内容；下一次读取成功再恢复。
- 确认消失才是 `Removed`，并叙述 “must not be used”。
- `Render` 返回 null 时不推进已叙述值（下次重试）。

### 2.4 字节稳定与硬不变量

- 系统基线在**变量替换之后**追加（`PromptBuilder`），`{{timestamp}}` 之类的字面量原样进入提示词，不击穿缓存。
- 同一快照重复渲染逐字节相等；同一执行内两次 `BuildSystemBaseline` 逐字节相等。
- `ValueSchemaVersion` 变化 → 该源整体重建并全量叙述；同版本渲染不一致 → 保留旧文本 + 记录 `Issues`（不静默）。
- 截断在源内完成，截断文本即存库文本（临界点不再每轮告警）。

### 2.5 生命周期（epoch）

| 事件 | 行为 |
|---|---|
| `/clear`（`SessionData.ClearMessages`） | 移除 `context.baseline`（新 epoch，下轮全量重建） |
| fork | 状态**不复制**；分支首次装配全量渲染，不产生重复叙述 |
| revert | 增量消息被移除后，账本折叠（`FoldLedgerToLive`）到存活的最晚条目；**全部增量都被回退时折回 `baselineValues`**（模型只剩基线文本，不能继续声称「已叙述」），下一次改回旧值会重新叙述 |
| 压缩（`CompressionService`） | 置 `PendingBaselineReset.Compaction` 位；下一次装配按源消费一次：**能读到当前值的源用当前值重渲染基线并成为新基线（新 epoch）**，`Target=Message` 的初始渲染随新基线重新注入；不可读的源保留存库基线与已叙述值（文件被临时占用不得毁掉基线）。重新锚定的源本轮不产生增量叙述，也不触发「渲染不确定」告警 |
| 工作区变更 | `PendingBaselineReset.WorkspaceChanged` 位：处理方式与压缩相同（可读源按当前值重新锚定）。**该位目前为预留**：生产代码尚未置位（工作区切换实际靠 canonical 中的绝对路径变化自然产生 diff），宿主如需要可显式写入 |

### 2.6 并发与单飞

同一会话并发装配由**进程内固定条纹锁**（64 个 `SemaphoreSlim`，按 `SessionId` 取模）串行化，**只保证本进程内不重复推进**；
跨进程写入仍是最后写者胜（不做 CAS，也不经 `ISessionManager.UpdateSessionAsync`）。调用方必须把**权威会话实例**传给装配器：
装配器对 `ISessionManager` 只做实例一致性诊断告警，不替换实例、不重跑（否则会出现「已叙述但模型看不到」的丢失更新）。
不同会话可并行。技能目录刷新另有进程级 `SemaphoreSlim` 单飞。

---

## 3. 两个内置源

| 源 | 文件 | Target | Scope | 上限 | Canonical |
|---|---|---|---|---|---|
| 项目指令（`AGENTS.md` 等） | `src/spine/Seeing.Agent.Core/Context/Sources/ProjectInstructionsContextSource.cs`，读取端口 `InstructionFileReader.cs`，搜索根规则 `Core/Instructions/InstructionDiscovery.cs`（仅 `EnumerateCandidatePaths`） | `Message` | `Project` | 32,000 字符（按文件块截断，信封保持可解析） | `{files:[{path,sha256}]}`（path ordinal 排序，不含 mtime） |
| 技能清单 | `src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs`，格式化唯一入口 `Catalog/SkillCatalogFormatter.cs` | `System` | `Project` | 8,000 字符（按行边界截断） | `{skills:[{id,name,description}]}`（按 id ordinal 排序，不含正文） |

项目指令消息保留既有 `project-instructions` 信封与元数据（`ProjectInstructions.MetadataKeys.*`），UI 折叠与标题服务契约不变。

技能源**只读执行级钉住的快照**（`IContextExecutionScope.SkillSnapshot`），不读 `ISkillCatalog.Current`：同一次执行内提示词清单与 `skill` 工具看到同一个对象，安装/删除中途不漂移。

---

## 4. 运行时矩阵

| 运行时 | 是否参与 | 行为 |
|---|---|---|
| 主链 `ExecutionRunner`（含子代理/子会话） | 是（`Persist`） | 读源 → diff → 叙述 → 提交状态；钉住技能快照并把 `ContextBaseline` 写入 `AgentContext` |
| 一次性旁路（`Scheduler` `AgentJob` / `HeartbeatJob`） | **只读**（`ReadOnly`） | 只渲染当前值作为本次请求基线，**不写状态、不叙述、不推进**；不保存会话 |
| ACP passthrough | 否 | `AcpPassthroughExecutor` 不构建系统提示词，无落点；**不支持**且不写状态 |
| 极简宿主（未接入 assembler） | 否 | 源不被装配，行为与之前一致 |

`ReadOnly` 与 `Persist` 是同一装配函数的两种提交策略，不是两条实现路径。

---

## 5. 行为变更（兼容性）

| 变更 | 影响 | 说明 |
|---|---|---|
| **压缩后项目指令重新可见** | 修复既有 bug | 以前压缩后永久消失；现在 `Compaction` 位触发一次重放 |
| **技能安装 / 改正文下一条消息生效** | 新能力 | `ISkillWriter` 写完即 `RefreshAsync`；watcher 兜底 |
| **插件技能同名优先于文件技能** | 之前由执行顺序决定 | 显式规则：`Registered` > `ProjectFile`(`.seeing` > `.agents` > `skills`) > `UserFile`(`.seeing` > `.agents`)；冲突进 `SkillCatalogSnapshot.Conflicts` |
| **`ISkillManager` 写面 `[Obsolete(error: true)]`** | 破坏性（源码级） | `AddSearchDirectory` / `DiscoverSkillsAsync` / `RegisterEmbeddedSkill` / `Unregister` 全部指向 `ISkillCatalog`（`RegisterAsync` / `UnregisterAsync` / `RefreshAsync` / `EnsureFreshAsync`） |
| **`ISkillWriter.SetEnabledAsync` 需显式层级** | 破坏性（新增契约） | `SetEnabledAsync(name, enabled, ConfigLevel level)`；启用时清除**所有**层级禁用项，禁用只写指定层级；UI 预选当前生效层级 |
| **`IToolManager.ExecuteAsync` 新增可选参数 `skillSnapshot`** | 破坏性（源码级，第三方实现需补参数） | 契约新增 `SkillCatalogSnapshot? skillSnapshot = null` 末位参数（透传到 `ToolContext.SkillSnapshot`，供 `skill` 工具按执行级钉住解析）。`ToolManager` 的便捷重载（`ExecuteAsync(List<ToolCall>, …)` 与 `ExecuteAsync(string toolId, Dictionary<…>, …)`）**不透传**该快照：走这两条路径时钉住退化为「目录当前态」，仅主链 `AgentExecutor` 路径保证钉住 |
| **`SkillManager` 不再是提示词分节贡献者** | 提示词结构变化 | `## Skills` 由基线提供；agent 模板中如有同名占位应删除（`DynamicPromptBuilder` 已删除） |
| **`SkillInfo.DirectoryPath` 移除** | 破坏性（罕见使用） | 迁到 `SkillCatalogEntry.DirectoryPath`（快照构建时写入的数据字段） |
| **结算失败时不再注入指令增量** | 行为变化 | 装配在结算之后；执行未发生则不应宣称「已告知」 |
| **`/help`、`/clear`、`/compact` 等短路命令不再注入指令** | 行为变化（无实际影响） | 这些命令不调用模型 |
| **迁移适配器 `IInstructionManager` 删除** | 破坏性（源码级） | 主链早已直连 `IContextAssembler`；`InstructionInjectResult` 无生产消费者。需要「触发一次注入」请直接注入 `IContextAssembler` |
| 新增 `context.baseline` / `context.update.sources` 元数据键 | 纯增量 | Clear 清除基线；fork 不复制基线 |
| **旧会话首次装配会多一条基线指令消息** | 迁移行为 | 升级前的历史里已有旧 `InstructionManager` 注入的指令消息；首次装配会再插入一条新基线，同一 epoch 内可能出现重复（其中一条可能过期）的指令块。属一次性迁移噪声，新会话不受影响 |

---

## 6. 技能单一真相源（配套改造）

| 组件 | 位置 | 职责 |
|---|---|---|
| `ISkillCatalog` | `Abstractions/Skills/ISkillCatalog.cs` | 不可变快照 + 单调版本 + 变更事件；查询在接口上（零实现） |
| `SkillCatalogCore` | `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogCore.cs` | 唯一实现：注册通道 + 磁盘通道，原子替换（不再 clear-then-fill），指纹廉价校验 |
| `SkillFileScanner` | `.../Catalog/SkillFileScanner.cs` | 扫盘、解析、冲突与问题（半写目录 → `SkillLoadIssue`，不崩溃） |
| `SkillCatalogFormatter` | `.../Catalog/SkillCatalogFormatter.cs` | 清单文本唯一格式化入口（基线 / 列表 / 增量），ordering 与字节稳定 |
| `SkillRootWatcher` | `.../Watching/SkillRootWatcher.cs` | 每根一个 `FileSystemWatcher` + 400ms 去抖；生命周期归 `SkillsModule` |
| `SkillWriteGate` | `.../Catalog/SkillWriteGate.cs` | `skill-state.json` 单写者（`SemaphoreSlim(1,1)`） |
| `ISkillWriter` / `SkillWriter` | `Abstractions/Skills/ISkillWriter.cs`、`.../SkillWriter.cs` | 文件通道唯一写入口：路径安全、临时目录 + `Directory.Move` 原子写、写完 `RefreshAsync` |
| 执行级钉住 | `Hosting/Execution/ExecutionRunner.ResolveSkillSnapshotAsync`、`Core/Context/ContextExecutionScope.cs` | 执行开始固化一次；`IContextExecutionScope` 与 `ISkillCatalogPin` 解析到同一实例 |

`SkillLoader.LoadAsync/ReloadAsync` 合并为一条幂等路径（解析根 → `RefreshAsync`），修掉切工作区不清旧技能的问题。
`SkillsPage` / `SkillCreatePage` / `SkillDetailPage` 改为订阅 `ISkillCatalog` + 调用 `ISkillWriter`，页面不再碰文件系统、不再用路径前缀嗅探来源。

---

## 7. 删除 / 退役清单（本次落地）

| 删除项 | 原因 |
|---|---|
| `Core/Prompts/DynamicPromptBuilder.cs` | `{{skills}}`/`{{tools}}` 占位符渲染，与「单一渲染点」冲突且无生产引用 |
| `SessionMetadataKeys.InstructionFingerprints` | 指纹双轨退役；状态由 `context.baseline` 承载 |
| `InstructionFingerprintStore` / `InstructionFingerprintSnapshot` | 同上（含 `ProjectInstructions.FingerprintMetadataKey`） |
| `SessionForker.CopyInstructionFingerprints`、`SessionData.ClearMessages` 中的指纹清除 | 键已退役；`context.baseline`（清除保留、fork 不复制）语义不变 |
| `IInstructionManager` / `InstructionManager` / `InstructionInjectResult` | 迁移适配器无生产消费者，且其路径会让技能快照为 null（不可用）——删除优于保留半正确实现 |
| `InstructionDiscovery.DiscoverAsync` / `TryReadAsync` | 生产只使用 `EnumerateCandidatePaths`；读取与三态分类由 `InstructionFileReader` 独占，避免两套规则漂移 |
| `ProjectInstructions.Reasons.CwdChange` | 无生产消费者（cwd 变化表现为内容增删） |
| `SkillManager` 内部字典 / `RefreshSkillsAsync` / `embedded://` 分支 | 收敛到 catalog |
| `SkillsPage` 本地列表缓存与路径前缀嗅探 | 改为订阅快照 |
| `SkillCreatePage`/`SkillDetailPage` 内的 `Directory.CreateDirectory`/`File.WriteAllText` | 改为 `ISkillWriter` |

**保留说明：** `ProjectInstructionsRenderer.TryParse` 有生产消费者（`GetMessageMetadata` 反解信封、标题服务、WebUI 消息工厂），不属于可删除的反向解析；`AddSearchDirectory` 仅剩 `AgentDiscovery`（Agent MD 发现，与技能无关）与 `ISkillManager` 的 obsolete 声明，均无调用方。

---

## 8. 已知边界

1. **watcher 去抖是真实时间**（400ms，仓库无虚拟时钟包）：批量安装会合并成一次刷新；测试允许 5s 内完成。
2. **同秒同长度编辑可能延迟一次装配**：廉价指纹 = 路径 + 长度 + `LastWriteTimeUtc`；未变化时直接复用快照，因此这类编辑可能到下一次装配/观察才生效（§11.3 已记录）。
3. **本期不含工具目录源**：管道为其预留位置（实现 `IContextSource` 即可接入，不改契约）；工具暴露/预算逻辑未改动。
4. **旁路（Scheduler/Heartbeat）不推进状态**：它们自建消息列表，会话历史不参与本轮输入；写状态会造成「已叙述但模型没看到」的丢失更新。
5. **ACP 不支持**：passthrough 执行器不构建系统提示词。
6. **装配失败不阻断对话**：主链记录错误日志并沿用已存基线（不静默、不冒充成功）。
7. **fork 不复制基线**：分支首轮全量渲染；已叙述过的事实不会跨分支重复。
8. **watcher 对缺失根只监听一层**：根不存在时监听最近存在祖先的非递归 watcher，且只回应通向根的下一个路径段；根一旦出现立即升级为递归监听根本身（不会把主目录 / 工作区根变成常驻递归扫描源）。
9. **`skill-state.json` 损坏按「不可用」处理**：写入为同目录临时文件 + `File.Move(overwrite: true)` 原子替换；读侧在 `SkillWriteGate` 内解析，失败时沿用上一次成功读取的禁用集合（绝不当作「无禁用项」）；写侧读到损坏文件会拒绝覆盖写并返回可展示的原因（不会静默清空用户禁用项）。

---

## 9. 验证

- `dotnet build Seeing.Agent.slnx`：0 error。
- 全量测试按程序集 `dotnet vstest <dll>` 运行（本环境 MTP 发现不到测试）。
- 唯一失败为既有 WebUI CSS 契约断言 `SidebarShutdown_ShouldReserveCollapseTriggerSpace`（`tests/apps/Seeing.Agent.WebUI.Tests/Infrastructure/WebUiScriptInteropContractTests.cs`），与本变更无关。
