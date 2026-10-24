# Task 6 + 7 实施报告（上下文源管道）

- 分支：`feature/context-source-pipeline`
- 范围：Task 6 生命周期接线（压缩 / 清除 / fork / revert）与 Task 7 技能目录核心（快照 / 双通道 / 冲突 / 原子替换）
- 日期：2026-10-24

## 1. 交付物

### Task 6（commit `f07b8830`）

| 文件 | 变更 |
|---|---|
| `src/spine/Seeing.Agent.Core/Compression/CompressionService.cs` | 新增可选构造参数 `ILoggerFactory?`（用于基线存储日志）；压缩成功后 `TryMarkCompactionPending` 置 `PendingBaselineReset.Compaction` 位（`try/catch` 仅告警，绝不影响压缩结果） |
| `src/primitives/Seeing.Session/Core/SessionData.cs` | `ClearMessages()` 移除 `SessionMetadataKeys.ContextBaseline`（与 `InstructionFingerprints` 并列） |
| `tests/spine/Seeing.Agent.Tests/Context/ContextLifecycleTests.cs` | 五个生命周期测试 |

`SessionForker` **未改**（只复制指令指纹）；新增测试锁定 fork 不携带 `context.baseline`。

### Task 7（本次提交）

| 文件 | 说明 |
|---|---|
| `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillCatalog.cs` | `SkillSource`、`SkillCatalogEntry`、`SkillConflict`、`SkillLoadIssue`、`SkillCatalogSnapshot`、`ISkillCatalog`（零实现） |
| `src/abstractions/Seeing.Agent.Abstractions/Skills/ISkillCatalogPin.cs` | 执行级钉住端口 |
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillFileScanner.cs` | `SkillRoot` / `SkillScanResult` + 扫盘、解析（迁移 `ParseSkillFile` 行为）、根优先级去重、冲突与问题收集 |
| `src/capabilities/Seeing.Agent.Skills/Catalog/SkillCatalogCore.cs` | `ISkillCatalog` 唯一实现：双通道、原子快照、单飞刷新、指纹、禁用层级、`SetRoots` |
| `tests/capabilities/Seeing.Agent.Skills.Tests/SkillCatalogCoreTests.cs` | 计划要求的 10 个测试 + 1 个事件一致性测试 |

`SkillManager` 与其字典**未删除**，`SkillInfo.DirectoryPath` **未移除**（留给 Task 10）。
未触碰 `ToolExposureEngine` / `TokenBudgetManager` / `ToolCatalogSectionContributor`；未实现技能上下文源、watcher、writer、UI。

## 2. TDD 证据

### Task 6

RED（`ContextLifecycleTests` 5 个测试，实现前）：

```
失败! - 失败: 3，通过: 2，已跳过: 0，总计: 5
- Compaction_ShouldSetPendingFlag_Once: Expected ... to have flag Compaction {value: 1}, but found None {value: 0}
- CompactionThenWorkspaceChange_ShouldKeepBothFlags: ... but found WorkspaceChanged {value: 2}（压缩覆盖了既有位）
- ClearMessages_ShouldRemoveContextBaseline: ... not to contain key "context.baseline", but found it anyhow
```

（Fork / Revert 两项在实现前即通过：Task 2/3 已保证行为，本任务用测试锁定。）

GREEN：

```
已通过! - 失败: 0，通过: 5，已跳过: 0，总计: 5
```

### Task 7

RED（实现前）：

```
error CS0234: 命名空间“Seeing.Agent.Skills”中不存在类型或命名空间名“Catalog”
error CS0246: 未能找到类型或命名空间名“SkillCatalogCore”
```

GREEN：

```
dotnet vstest ...Seeing.Agent.Skills.Tests.dll --TestCaseFilter:"FullyQualifiedName~SkillCatalogCoreTests"
已通过! - 失败: 0，通过: 11，已跳过: 0，总计: 11
```

**单飞测试的变异校验**：临时把 `RefreshAsync` 的合并判据改成恒真（每次都重建）后，`ConcurrentRefresh_ShouldRebuildOnce` 失败并报
`Expected catalog.Current.Version to be 2L, but found 17L`——证明 16 个并发调用确实重叠、测试能捕获「未合并」的回归；随后还原实现。

## 3. 全量回归

```
tests/spine/Seeing.Agent.Tests.dll                     1285 passed / 0 failed（原 1280 + 新增 5）
tests/capabilities/Seeing.Agent.Skills.Tests.dll         20 passed / 0 failed（原 9 + 新增 11）
dotnet build Seeing.Agent.slnx                          0 errors
```

## 4. 自审要点

- **位集合语义**：压缩只做 `|=`，不覆盖 `WorkspaceChanged`；测试用 `[Flags]` 组合值验证。
- **不静默**：解析 / 读取 / 扫描失败全部进入 `SkillLoadIssue` 并 `LogWarning`；禁用状态文件损坏 → 空集合 + 告警；注册通道遮蔽文件技能也记一条 `SkillConflict`（计划只要求文件同名冲突，这里按「覆盖不静默」补足）。
- **快照不可变 + 原子替换**：`Volatile.Write` 替换快照引用，读者永不看到空窗；事件在锁外触发，且同一版本只触发一次（测试 `Refresh_ShouldRaiseSingleEventPerPublish` 锁定）。
- **单飞**：`SemaphoreSlim(1,1)` + 「进入时快照实例」判据；等待者阻塞到在途刷新发布之后才返回，因此不会读到过期快照。
- **根优先级**：`SetRoots` 的索引即优先级（0 最高）；同名裁决在扫描器内完成，冲突记录胜/负来源与目录路径。
- **解析迁移**：frontmatter 正则、YamlDotNet、名称正则与 64 长度、描述必填与 1024 截断、名称与目录名不一致仅告警，逐条对齐旧 `ParseSkillFile`。
- **零实现纪律**：Abstractions 新文件只有 enum/record/interface。

## 5. 偏差与关注点

1. **`EnsureFreshAsync` 指纹额外包含两级 `skill-state.json`**（计划原文只写 `SKILL.md`）。理由：禁用开关不改变 SKILL.md，若不纳入指纹，切换禁用后廉价路径会永远命中旧快照。属加法改动。
2. **`CompressionService` 构造签名新增可选参数** `ILoggerFactory? loggerFactory = null`（为基线存储提供日志）。现有所有调用点不受影响；DI 无需改动。
3. **压缩置位写在 `SaveAndNotifyAsync` 之后**：元数据由紧随其后的装配（`ContextBaselineStore.Save` + 调用方保存）落盘，压缩本身不重复触发通知（按计划实现）。
4. **`SkillCatalogEntry.Info` 是可变 `SkillInfo` 引用**：快照本身不可变，但 `Info` 对象可被消费方改写。Task 10 重写门面时应把条目视为只读（或在构建时深拷贝）。
5. **`SkillRoot` / `SkillScanResult` 是能力包内的公开类型**（不是 Abstractions）：供 Task 8 的加载器 / watcher 复用；如 Task 8 需要其它根分类，可在彼时调整。
6. **未注册 DI**：`ISkillCatalog` 的容器注册与模块生命周期属 Task 8，本任务只交付实现与 `SetRoots` 挂载点。
7. **`Dispose` 会释放内部信号量**：若仍有在途刷新会抛 `ObjectDisposedException`；技能模块 Deactivate 应在停掉 watcher 之后调用（Task 8 责任）。
