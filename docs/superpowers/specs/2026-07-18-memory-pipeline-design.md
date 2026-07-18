# Seeing.Agent.Memory 异步管道完善设计

**版本**: 1.0  
**日期**: 2026-07-18  
**状态**: 待用户审阅  
**作者**: AI Assistant  
**前置文档**: `docs/superpowers/specs/2025-01-15-memory-system-redesign.md`（存储/检索/图谱已部分落地；演化/配置/真实 Embedding/捕获过滤未完成）

---

## 一、背景与目标

### 1.1 问题

当前记忆系统存在产品级缺陷：

1. **写入过宽**：`ChatMemoryHandler` / `ToolMemoryHandler` 在 Hook 内直接 `SaveAsync` 全文，无价值判断，导致大量无意义聊天/工具输出进入 `session/`。
2. **演化缺失**：README 与 2025 规范中的 `session → daily → digest` 流水线未实现；`MemoryEvaluator` 为占位。
3. **假 Embedding**：默认 `RandomEmbeddingService`，语义检索无意义且无 UI/配置暴露。
4. **配置与 UI 缺口**：无 `MemoryOptions`、无 `.seeing/seeing.json` 的 `Memory` 节、WebUI 仅浏览/搜索无设置页。

### 1.2 目标

端到端完善记忆架构与流程，并保证：

- **对话热路径零阻塞**：捕获仅入队，过滤/抽取/落盘/索引/演化均在后台异步执行。
- **两级漏斗写入**：启发式粗筛 → LLM 抽取（事实/偏好/决策），不把原始 transcript 当长期记忆。
- **轻量 session 索引 + 提炼稿**：`session/` 只存短索引；提炼结果进 `daily/` / `digest/`。
- **Embedding 诚实**：未配置真实 Embedding 时禁用向量检索，仅 BM25；禁止静默随机向量。
- **可配置 + WebUI 可编辑**：`.seeing/seeing.json` 的 `Memory` 节 + 记忆设置页。
- **召回可切换**：`AutoInject` / `ToolsOnly` / `Both`。

### 1.3 非目标（本期不做）

- 分布式队列（Redis 等）
- 多租户隔离
- 自动删除历史全文垃圾 session（可另提供手动清理入口）
- 本地嵌入模型运行时打包

---

## 二、设计决策摘要

| 决策项 | 选择 | 理由 |
|--------|------|------|
| 改造路径 | 管道化补齐现有模块（非旁路重写） | 复用 FileStore/Index/Graph/配额 |
| 处理模型 | 异步：Hook 入队 + Background Worker | 不影响对话延迟 |
| 写入策略 | 启发式 → LLM 抽取两级漏斗 | 质量与成本平衡 |
| 落盘形态 | 轻量 session index + daily/digest | 可回溯摘要，无全文噪音 |
| Embedding | 未配置则禁向量，不回退随机 | 避免假语义 |
| 演化触发 | 会话结束 / 空闲 N 分钟 | 成本与时效折中 |
| 配置 | JSON + 可编辑 WebUI | 可发现、可运维 |
| 召回 | Both（自动注入 + 工具），可切换 | 体验与可控兼得 |

---

## 三、整体架构

### 3.1 原则

1. 记忆系统故障不得导致对话失败。
2. Hook / 召回注入以外的重活全部异步。
3. 每阶段受 `MemoryOptions` 控制，可关可开。
4. 未配置真实 Embedding → `VectorIndex` 关闭，仅 KeywordIndex。

### 3.2 分层

```
对话 / Tool 完成
       │
       ▼
┌──────────────────────┐
│ Integration          │
│  CaptureHook         │  组装 Candidate → Enqueue → 立即返回
│  RecallHook (可选)   │  硬超时 Top-K 注入（可关）
│  MemoryTools         │  memory_search / memory_get
└──────────┬───────────┘
           │ 有界内存队列
           ▼
┌──────────────────────┐
│ Background           │
│  MemoryPipelineWorker│  过滤 → 抽取 → 落盘 → 索引
│  MemoryEvolutionWorker│ 会话结束/空闲 → 合并/升 digest
│  MemoryIndexingService│ 仅扫漏兜底（主索引由 Pipeline 完成）
└──────────┬───────────┘
           ▼
┌──────────────────────┐
│ Storage (已有)       │  FileStore / KeywordIndex / VectorIndex* / Graph
│ Infra                │  IEmbeddingService(可空) / ILlmClient / MemoryOptions
└──────────────────────┘
* 仅 Embedding 已配置且健康时启用
```

### 3.3 组件职责

| 组件 | 职责 | 同步/异步 |
|------|------|-----------|
| `ChatMemoryHandler` / `ToolMemoryHandler` | 生成 `MemoryCandidate`，入队 | 同步极短 |
| `IMemoryWorkQueue` | 有界队列 + 背压 | 同步入队 |
| `MemoryPipelineWorker` | 消费队列，跑管道 | 后台 |
| `IMemoryHeuristicFilter` | 规则过滤 | 后台 |
| `IMemoryExtractor` | LLM 抽取与打分 | 后台 |
| `IMemoryEvolutionService` | 会话级合并 / 升 digest | 后台 |
| `IMemoryRecallService` | 检索供注入或工具 | 按需（注入带硬超时） |
| `MemoryService` | CRUD / 搜索 / 统计 | 按需 |
| WebUI 记忆设置页 | 读写 `Memory` 配置 | UI |

---

## 四、端到端数据流

### 4.1 捕获（热路径）

```
ChatAfterComplete / ToolExecuteAfter
  → AutoCapture 关闭则返回
  → 组装 MemoryCandidate（Snippet 截断至 MaxSnippetChars）
  → IMemoryWorkQueue.TryEnqueue
  → 成功或队列满均立即返回 Hook Success（满则记指标，不阻塞）
```

约束：

- Hook **不写盘、不调 LLM、不做 Embedding**。
- Tool 可经 Allowlist/Blocklist 预过滤后再入队。

### 4.2 后台管道（写入）

1. **HeuristicFilter**：过短、纯 ACK、近重复、工具噪声 → 丢弃。
2. **LlmExtract**（配额/限流）：抽取事实/偏好/决策/待办，打 `importance`；低于阈值不落盘。LLM 失败 → **Skip，不回退全文落盘**。
3. **Persist**：
   - `session/{sessionId}/index.md`：追加短索引行（时间、来源、一句话摘要、链到 daily）。
   - `daily/{yyyy-MM-dd}/{id}.md`：提炼记忆（YAML frontmatter含 type、importance、tags、source_session）。
4. **Index + Graph**：始终更新 FTS5；Embedding 可用时更新向量；解析 wikilink 更新图谱。

### 4.3 会话级演化

触发：`SessionEnded` 或空闲 `Evolution.IdleMinutes`（默认 15）。

流程：读取该 session 的 index + 关联 daily → LLM 合并去重、修正矛盾、补 tags → 写回 daily；条目 `importance >= PromoteToDigestMinImportance` → 升或写入 `digest/` → 更新索引与图谱。

说明：本期 digest 升格仅按 importance 阈值，不做跨会话稳定性追踪（避免额外状态机）。与捕获管道共用配额，避免打爆 LLM。

### 4.4 召回

| `Retrieval.Mode` | 行为 |
|------------------|------|
| `AutoInject` | 用户消息前检索并注入；超时/失败则跳过，不拖慢对话 |
| `ToolsOnly` | 不自动注入；Agent 用 memory 工具 |
| `Both`（默认） | 自动注入 + 工具可用 |

约束：

- 只检索 `daily/` + `digest/`（不用原始 transcript）。
- Top-K、MaxInjectTokens、InjectTimeoutMs 可配。
- 硬超时默认 150ms：超时本轮不注入。

---

## 五、配置模型与 WebUI

### 5.1 落点

节名 `Memory`，写入 `.seeing/seeing.json`（项目级覆盖用户级），经 `UnifiedConfigManager` / `SeeingConfigService` 读写。不使用 `appsettings.json`。

### 5.2 `MemoryOptions` 结构

```text
Memory
├── Enabled
├── Capture
│   ├── AutoCapture, CaptureChat, CaptureTools
│   ├── ToolAllowlist[], ToolBlocklist[]
│   ├── MaxSnippetChars          // 默认 4096
│   └── QueueCapacity            // 默认 256
├── Filter
│   ├── MinChars, AckPatterns[], NearDuplicateWindow
├── Extraction
│   ├── Enabled, MinImportance   // 默认 0.5
│   ├── Model                    // 空=DefaultModel
│   └── MaxCandidatesPerMinute
├── Evolution
│   ├── Enabled, IdleMinutes     // 默认 15
│   ├── OnSessionEnd             // 默认 true
│   └── PromoteToDigestMinImportance
├── Embedding
│   ├── Provider, Model, Dimensions  // 空=禁向量
├── Retrieval
│   ├── Mode                     // AutoInject | ToolsOnly | Both
│   ├── TopK, MaxInjectTokens, InjectTimeoutMs
│   └── SearchTypes[]            // 默认 daily, digest
└── Cost
    ├── DailyTokenQuota, RateLimitPerMinute
```

### 5.3 硬规则

1. `Embedding.Provider` 或 `Model` 为空 → **禁用 VectorIndex**，不得使用 `RandomEmbeddingService`。
2. `Extraction.Enabled=false` → 启发式后停止，**不写全文替代**。
3. `Enabled=false` 或 `AutoCapture=false` → Capture Hook no-op。

### 5.4 Embedding 与 Providers

- Memory 节只存 Provider 名 + Model 引用；密钥留在既有 `Providers`。
- 校验失败 → UI 显示错误，向量保持关闭，BM25 可用。

### 5.5 WebUI

新增 `/memory/settings`（记忆首页入口；也可作为全局 Settings「记忆」Tab）。

区块：总览状态、捕获、过滤与抽取、演化、Embedding（含测试连接）、召回。

默认：`AutoCapture=on`，`Extraction=on`，`Embedding` 空，`Retrieval.Mode=Both`，`Evolution` on 且空闲 15 分钟。

---

## 六、接口与后台实现要点

### 6.1 核心类型（示意）

```csharp
record MemoryCandidate(
    string Id,
    string SessionId,
    string? AgentId,
    MemorySource Source,
    string? ToolId,
    string Snippet,
    DateTimeOffset CreatedAt);

interface IMemoryWorkQueue
{
    bool TryEnqueue(MemoryCandidate candidate);
    IAsyncEnumerable<MemoryCandidate> DequeueAllAsync(CancellationToken ct);
    int Count { get; }
}

interface IMemoryPipeline
{
    Task<PipelineResult> ProcessAsync(MemoryCandidate candidate, CancellationToken ct);
}

interface IMemoryHeuristicFilter
{
    FilterDecision Evaluate(MemoryCandidate candidate);
}

interface IMemoryExtractor
{
    Task<ExtractionResult?> ExtractAsync(MemoryCandidate candidate, CancellationToken ct);
}

interface IMemoryEvolutionService
{
    Task EvolveSessionAsync(string sessionId, CancellationToken ct);
}

interface IMemoryRecallService
{
    Task<IReadOnlyList<SearchHit>> RecallAsync(
        string query, RecallOptions options, CancellationToken ct);
}

interface IEmbeddingStatus
{
    bool IsAvailable { get; }
    string? Reason { get; }
}
```

### 6.2 Worker

| Worker | 触发 | 职责 |
|--------|------|------|
| `MemoryPipelineWorker` | 队列有数据 | 跑 `IMemoryPipeline` |
| `MemoryEvolutionWorker` | SessionEnd / 空闲计时 | `EvolveSessionAsync` |
| `MemoryIndexingService` | 文件变更 | **仅扫漏**；主索引由 Pipeline 完成，避免双写竞态 |

并发：默认单消费者或小并发（2）；同 session 对 `index.md` 追加使用 per-session 锁保证有序。

### 6.3 与旧代码关系

- 改造 Hook：去掉直接 `SaveAsync` 全文。
- 删除生产路径默认 `RandomEmbeddingService`。
- `AddMemoryServices` 绑定 `IOptions<MemoryOptions>`，注册 Queue、Pipeline、Evolution、Recall。
- 召回挂接点：现有 Chat 生命周期 Hook 或 ChatOrchestrator 扩展点，受 `Retrieval.Mode` 控制。

---

## 七、错误处理与可观测性

| 场景 | 行为 | 指标/日志 |
|------|------|-----------|
| 队列满 | 丢弃，对话继续 | `memory.queue.dropped` |
| 过滤拒绝 | 不落盘 | `memory.filter.rejected` |
| 抽取失败/超时 | Skip | `memory.extract.failed` |
| 配额耗尽 | 默认丢弃并记原因 | `memory.quota.exhausted` |
| Embedding 不可用 | 仅 BM25 | UI「向量未启用」 |
| 召回超时 | 跳过注入 | `memory.recall.timeout` |
| 演化失败 | 保留已有 daily，下次重试 | 错误日志 |
| 单条落盘/索引异常 | 隔离失败，Worker 继续 | 错误日志 |

---

## 八、测试范围

| 层级 | 覆盖 |
|------|------|
| 单元 | HeuristicFilter；Queue 满/空；Extractor Mock；无 Embedding → 向量关 |
| 管道集成 | Candidate → Mock LLM → daily + session index，无全文倾倒 |
| Worker | 入队后后台完成；手动触发演化 |
| 召回 | ToolsOnly 不注入；Both 仅 daily/digest；超时跳过 |
| 回归 | 现有 Keyword/Graph 测试；Vector 在无 Embedding 时不调用或跳过 |

---

## 九、实施里程碑

| 阶段 | 内容 |
|------|------|
| **P0** | `IMemoryWorkQueue` + Handler 入队改造 + Heuristic + `MemoryOptions` + 禁假向量 + 设置页骨架 |
| **P1** | LLM Extract + 轻量 session index / daily 落盘 + `MemoryPipelineWorker` |
| **P2** | `MemoryEvolutionWorker`（会话结束/空闲） |
| **P3** | Recall（Both/AutoInject/ToolsOnly）+ Memory Tools |
| **P4** | 真实 Embedding Provider 接线 + 设置页「测试连接」 |

依赖顺序：P0 → P1 → P2 / P3（P3 可与 P2 并行）→ P4。

---

## 十、与 2025-01-15 规范的关系

本设计是对 2025 规范的 **补全与纠偏**，不是推倒重来：

- **保留**：FileStore、HybridIndex、Graph、成本控制骨架、三层目录语义。
- **纠偏**：禁止 Random Embedding 作为默认语义；禁止 Hook 同步全文落盘。
- **补全**：异步管道、抽取/演化、Options、WebUI 设置、召回模式。

实施计划应引用本文档，并在任务中标注对应里程碑（P0–P4）。
