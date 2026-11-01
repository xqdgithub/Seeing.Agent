# 22 Memory 死账本清理 Release Notes

> **实施日期：** 2026-11-01
> **规格：** [Agent 自动反思设计](../superpowers/specs/2026-10-31-agent-reflection-design.md)（P0-g）
> **实施计划：** [agent-reflection 计划](../superpowers/plans/2026-11-01-agent-reflection-plan.md)（Task 7）
> **适用范围：** `Seeing.Agent.Memory`（`public` 类型对外可见，含破坏性变更）

---

## 1. 一句话

Memory 包内两套「token 记账」实现（`ITokenTracker`/`SqliteTokenTracker`、`IQuotaManager`/`DailyQuotaManager`）
及其配套的死 DDL（`SchemaManager` + V1 迁移的 `token_usage`/`quota_state` 表）与死配置（`MemoryCostOptions`）
全部删除。**Token 用量的唯一权威来源是 `ExecutionOutcome.Usage`。**

---

## 2. 删除清单（`Seeing.Agent.Memory` 公共 API 破坏）

### 2.1 接口与实现

| 删除项 | 原位置 | 说明 |
|---|---|---|
| `ITokenTracker` | `Abstractions/ITokenTracker.cs` | 零生产消费者 |
| `SqliteTokenTracker` | `Core/CostControl/SqliteTokenTracker.cs` | `ITokenTracker` 的唯一实现 |
| `IQuotaManager` | `Abstractions/IQuotaManager.cs` | 零生产消费者 |
| `DailyQuotaManager` | `Core/CostControl/DailyQuotaManager.cs` | `IQuotaManager` 的唯一实现 |
| `TokenUsage`（Memory 版 record） | `Abstractions/ITokenTracker.cs` | 与 `Seeing.Agent.Abstractions.Llm.TokenUsage` **重名并存** |
| `QuotaUsage`（record） | `Abstractions/IQuotaManager.cs` | 随 `IQuotaManager` 一并移除 |

`TokenUsage` 的重名是本次删除的主要动因之一：两个同名 record 分别位于两个**都被 using** 的命名空间
（`Seeing.Agent.Memory.Abstractions` 与 `Seeing.Agent.Abstractions.Llm`），一旦有人写下裸 `TokenUsage`
就会得到 CS0104 歧义错误。Memory 包内有 4 个文件同时 using 两者
（`EmbeddingConnectionTester`、`ProviderEmbeddingService`、`LlmMemoryEvolution`、`LlmMemoryExtractor`），
它们此前从未写过该标识符，因此**没有已发生的错绑**；本次收益是清掉一个「未被使用但会诱使误用」的重复定义。

### 2.2 死 DDL（`Core/Schema/` 整目录）

| 删除项 | 原位置 |
|---|---|
| `SchemaManager` | `Core/Schema/SchemaManager.cs` |
| `IMigration` | `Core/Schema/Migrations/IMigration.cs` |
| `V1_InitialSchema` | `Core/Schema/Migrations/V1_InitialSchema.cs` |

**判定依据**（证据，不是推测）：

- `SchemaManager` 在生产代码中**零调用**：`ApplyMigrationsAsync` / `EnsureInitializedAsync` 的调用点
  **仅在（本轮已删除的）`tests/capabilities/Seeing.Agent.Memory.Tests/Schema/SchemaManagerTests.cs` 中**，
  生产代码从未调用。
- `git log --all --oneline -S "SchemaManager" -- src samples plugs` **在删除前**只返回 `da7a150b`
  （新增该文件那次提交），**全历史从未**有 DI 注册或 `new SchemaManager(...)`。
  > 注：该命令在**删除后**会额外返回删除提交本身，因此这条证据只在删除前成立，不能事后原样复跑核对。
- `MemoryModule.ActivateAsync` 只做 `_connectionOwner.Open()` + Hook 注册 + Tool 注册，不碰 Schema。
- 运行时建表走各实现自己的 `EnsureInitializedCoreAsync`，表名与 V1 的 `vectors`/`keywords`/`links`
  **根本不是一套**：

  | 实际运行时表 | 建表点 |
  |---|---|
  | `vector_index` | `Core/Index/VectorIndex.cs:52` |
  | `keyword_index`（FTS5） | `Core/Index/KeywordIndex.cs:46` |
  | `graph_nodes` / `graph_edges` | `Core/Graph/SqliteMemoryGraph.cs:55/60` |
  | `embedding_cache` | `Core/Embedding/SqliteEmbeddingCache.cs:48` |

即：**没有任何生产数据库执行过该迁移**，`token_usage` / `quota_state` 从未被创建过。

### 2.3 死配置

| 删除项 | 原位置 |
|---|---|
| `MemoryOptions.Cost` 属性 | `Configuration/MemoryOptions.cs` |
| `MemoryCostOptions` 类 | `Configuration/MemoryOptions.cs` |

`MemoryCostOptions`（`DailyTokenQuota` / `RateLimitPerMinute`）**从未被任何代码读取**——删除前的父提交里
它只出现在自身定义处。但 `MemoryOptionsProvider.SaveAsync` 会把整个 `MemoryOptions` 序列化写进
`memory.json`，所以它是「**配置项真实可写、真实落盘、永久无效**」的用户可见谎言。

> **落盘位置（易错点，已钉测）**：Memory 节**不在** `seeing.json`，而在**独立文件 `memory.json`**。
> 证据是 `AddMemoryServices` 的注册：
> `new ConfigSectionMeta(ConfigSectionMemoryOptionsStore.SectionName, "memory.json", ConfigScope.Both, typeof(MemoryOptions))`
> （`Extensions/MemoryServiceExtensions.cs:36-40`），其中 `SectionName = "Memory"`。
> `UnifiedConfigManager` 对 `FileName != "seeing.json"` 的节走独立文件分支（`CollectSectionToCacheAsync`
> 的 `else` 分支 + `GetFilePath(level, meta.FileName)`）。
> 守门用例 `AddMemoryServices_Should_Register_Memory_Section_On_MemoryJson` 断言 `meta.FileName == "memory.json"`。

**兼容性**：STJ 默认忽略未知成员，存量 `memory.json` 里的 `Cost` 键不会导致加载失败。
守门用例 `MemoryOptionsDeserializationTests.GetSection_WithLegacyCostKey_ShouldNotThrow_AndIgnoreIt`
**走真实 `UnifiedConfigManager.LoadAsync` + `GetSection<MemoryOptions>("Memory")`**（不是自造
`JsonSerializerOptions`），因此能捕获"生产序列化器变严"这类回归：若给 `UnifiedConfigManager.JsonOptions`
加上 `UnmappedMemberHandling.Disallow`，该用例会抛
`JsonException: The JSON property 'Cost' could not be mapped to any .NET member contained in type
'Seeing.Agent.Memory.Configuration.MemoryOptions'` 并变红（已实测，见报告变异 D）。
WebUI 无引用。**不选**"接上 IRateLimiter"——那是加功能，不是净删除。

---

## 3. 未改动（明确保留）

| 保留项 | 理由 |
|---|---|
| `SqliteConnectionGate` / `SqliteConnectionOwner` / `SqliteConnectionSource` | 4 个生产类仍在用（`VectorIndex` / `KeywordIndex` / `SqliteMemoryGraph` / `SqliteEmbeddingCache`） |
| `IRateLimiter` / `TokenBucketRateLimiter`（`Core/CostControl/`） | 与死账本无关；`Core/CostControl/` 目录因此只删2个文件、留1个文件 |
| `MemoryOptions` 其余节 | 全部有真实消费者 |

---

## 4. 用量统计的替代数据源

**唯一权威来源：`ExecutionOutcome.Usage`**
（`src/abstractions/Seeing.Agent.Abstractions/Execution/ExecutionOutcome.cs`）

- 由 `ExecutionRunner` 从 `LoopCompleteEvent.Usage` **累计**填充，随 `ExecutionCompleteEvent` 扇出到终态观察者。
- `null` 表示「未知」，**不等价于零消耗**。
- **不要**用 `SessionData.CachedInputTokens` 做账本：它是 `BudgetUpdateHook` 每次 `chat.after_complete`
  的**单次覆盖值**，语义是上下文占用，不是累计用量。

---

## 5. 测试影响

| 变化 | 说明 |
|---|---|
| 删除 `CostControl/SqliteTokenTrackerTests.cs`（3 例） | 直接 `new` 被删类型 |
| 删除 `CostControl/DailyQuotaManagerTests.cs`（4 例） | 同上 |
| 删除 `Schema/SchemaManagerTests.cs`（14 例） | 直接 `new` 被删类型 |
| 新增 `CostControl/CostLedgerRemovalTests.cs`（7 例） | 程序集级 + DI 级守门 |
| 新增 `Configuration/MemoryOptionsDeserializationTests.cs`（4 例） | 含存量 `Cost` 键的生产路径兼容 + 落盘位置钉测 |
| 新增 `Core/SqliteConnectionGateTests.cs`（5 例） | gate 并发覆盖：**两个重载各自**一条临界区互斥 + 64 并发写 + 各自取消传播 |

### 5.1 `SqliteConnectionGate` 覆盖的完整性与判别力

`SqliteConnectionGate` 有**两个独立锁路径**：非泛型 `RunAsync` 与泛型 `RunAsync<T>`。
全包共 **26 处**调用点：**泛型 12 处、非泛型 14 处**。

| 重载 | 处数 | 调用点（`路径:行`） |
|---|---|---|
| 泛型 `RunAsync<T>` | 12 | `KeywordIndex.cs:157/190`、`VectorIndex.cs:184/226/260`、`SqliteMemoryGraph.cs:191/236/269/291`、`SqliteEmbeddingCache.cs:65/144/159` |
| 非泛型 `RunAsync` | 14 | `KeywordIndex.cs:61/87/132/201`、`VectorIndex.cs:76/108/160/271`、`SqliteMemoryGraph.cs:79/102/126/160/171`、`SqliteEmbeddingCache.cs:112` |

判定方法：**lambda 体是否产出值**。注意「表达式体方法」不出现 `return` 关键字，但签名返回 `Task<T>`
已强制走泛型重载，例如：

```csharp
public Task<GraphStats> GetStatsAsync(CancellationToken ct = default) =>
    _gate.RunAsync(async token => { ...; return new GraphStats(...); }, ct);   // 泛型
```

上述数字经**编译器改名探针**独立复核：把泛型重载临时改名为 `RunAsyncG<T>`，编译报出的
「无法从异步 lambda 返回值」错误行恰好落在泛型那 12 处的 `return` 语句上。

因此两个重载都必须单独覆盖——只测一个会让另一个的锁失效在测试中隐形。

**判别力按「逐个变异点」验证**（不是一次性删掉整个锁）：

| 变异 | 改动 | 结果 |
|---|---|---|
| A | 两个重载都去锁 | 红（失败 4 / 通过 1 / 总计 5） |
| B | **只**去泛型 `RunAsync<T>` | 红：`Expected maxObserved to be 1 because 泛型 RunAsync<T> 的临界区必须互斥, but found 64` |
| C | **只**去非泛型 `RunAsync` | 红：`Expected maxObserved to be 1 because 非泛型 RunAsync 的临界区必须互斥, but found 64` |

三个变异各自只让对应重载的用例失败，证明覆盖与锁路径一一对应。
（历史：本测试最初只有非泛型互斥用例 + 一条泛型"64 并发写不抛异常"弱用例；弱用例在变异 B 下仍绿，
已改为按重载分别断言临界区互斥。）

---

## 5.2 Follow-up：Memory 包已无任何 schema 演进机制（**必读，勿直接改表**）

删除 `SchemaManager` 后，Memory 包**不再具备任何 schema 演进能力**：

- `ALTER TABLE` 在 `src/` 下 **0 命中**
- `user_version` / `PRAGMA` 在 `src/` 下 **0 命中**（仅有 `#pragma warning` 的误匹配）
- 所有表都只靠 `CREATE TABLE IF NOT EXISTS` 建立：

  | 表 | 建表点 |
  |---|---|
  | `vector_index` | `Core/Index/VectorIndex.cs:52` |
  | `keyword_index`（FTS5） | `Core/Index/KeywordIndex.cs:46` |
  | `graph_nodes` / `graph_edges` | `Core/Graph/SqliteMemoryGraph.cs:55/60` |
  | `embedding_cache` | `Core/Embedding/SqliteEmbeddingCache.cs:48` |

**后果**：既有用户的 `memory.db` **无法再做任何列变更**。`CREATE TABLE IF NOT EXISTS` 对已存在的表是空操作，
加列不会被应用、改类型不会被应用、删列不会被应用。想演进只能手工迁移或新建表 + 拷数据。

**这不是行为回归**——`SchemaManager` 本来就是死的（从未被任何生产代码调用），所以删除前后行为一致。
但删掉的是唯一的"演进占位"：下一个人看到这套 `CREATE TABLE IF NOT EXISTS` 时，必须知道
**没有迁移框架兜底**，不能直接把新列加进那几条 SQL 就以为老库会跟上。

（若要恢复演进能力，正确做法是新增一个**被真正调用**的迁移入口 —— 在 `MemoryModule.ActivateAsync`
打开连接后执行版本化迁移，而不是复活旧的 `SchemaManager`。）

---

## 6. 破坏性变更摘要（外部消费者须知）

1. `Seeing.Agent.Memory.Abstractions.ITokenTracker` / `IQuotaManager` / `TokenUsage` / `QuotaUsage` 删除。
   若外部代码引用过，改用 `Seeing.Agent.Abstractions.Llm.TokenUsage` + `ExecutionOutcome.Usage`。
2. `Seeing.Agent.Memory.Core.CostControl.SqliteTokenTracker` / `DailyQuotaManager` 删除。
3. `Seeing.Agent.Memory.Core.Schema.SchemaManager` / `IMigration` / `V1_InitialSchema` 删除。
4. `Seeing.Agent.Memory.Configuration.MemoryOptions.Cost` / `MemoryCostOptions` 删除；
   存量配置文件里的 `Cost` 节被静默忽略，**无迁移动作、无告警**。

---

## 7. 防回归扫描（PR 可粘贴）

```bash
# 死账本类型不得回归
rg "ITokenTracker|IQuotaManager|SqliteTokenTracker|DailyQuotaManager|QuotaUsage" src

# Memory 包内不得再定义同名 TokenUsage
rg "record TokenUsage|class TokenUsage" src/capabilities/Seeing.Agent.Memory

# 死表名不得回归（`token_usage` 仅允许出现在 SessionMessage 的 JSON 属性名中，
# 且该处在 primitives 层、与本包无关）
rg "token_usage|quota_state" src/capabilities/Seeing.Agent.Memory

# 死配置不得回归
rg "MemoryCostOptions|DailyTokenQuota|RateLimitPerMinute" src
```

期望：前三条 0 命中；第四条 0 命中。
