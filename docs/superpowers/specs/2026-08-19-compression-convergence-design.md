# 压缩系统收敛设计

**日期:** 2026-08-19
**状态:** 已确认

## 背景

当前存在 7 处压缩实现，分散在 4 个层，语义互相冲突：

| # | 实现 | 位置 | 行为 | 问题 |
|---|------|------|------|------|
| 1 | `SessionCompressor` | `Seeing.Session.Compression` | 滑动窗口（保留第一条 + 最后 20） | 与 #3 重复 |
| 2 | `SlidingWindowCompression` | `Seeing.Session.Compression` | 滑动窗口 | 与 #1 重复 |
| 3 | `SlidingWindowTokenStrategy` | 策略 | 按 token 预算滑动窗口 | — |
| 4 | `SummarizingStrategy`/`HybridStrategy` | 策略 | AI 摘要 | `ISummarizer` 无实现类/无注册，`CompressionStrategyFactory` 直接抛 "not registered"；`SummarizingStrategy` 用 `Task.Run().GetAwaiter().GetResult()` 同步阻塞异步 |
| 5 | `SessionCompactionService` | WebUI | AI 摘要 + tail（非流式） | 唯一能跑通的 AI 摘要，但落在 UI 层，自己写 prompt/选模型 |
| 6 | `CompactHistory` | `/compact` 命令 | `session.Messages.RemoveRange` 保留 10 条 | 绕过一切策略；未设 `shouldContinue:false`，命令后 Agent 仍执行；前端无刷新信号 |
| 7 | `LlmSummarizer` | WebUI | 旧 `ISummarizer` 的 LLM 实现（收 `string text`） | 与 #5 并存，接口签名与新设计不兼容 |

此外：`SessionManager`（原语层）构造时默认注入 `SlidingWindowCompression` 承担压缩职责，压缩逻辑渗透进原语层会话管理器。

根因：`/compact` 命令返回 `CommandResult.Ok(..., needsRefresh: true)` 未传 `shouldContinue: false`（默认 true），导致 `ExecutionJobService.ProcessCommandAsync` 继续执行 Agent 且不发布 `CommandResultEvent`；前端 `ConsumeEventStreamAsync` 也无 `CommandResultEvent` 显式处理，时间线不刷新。

## 目标

端到端收敛压缩能力为**单一架构**：只保留一个默认实现（LLM 摘要），编排逻辑在主库 `CompressionService` 具体类内，两个触发点（手动 `/compact` 命令 + TokenBudget 自动压缩）统一走它，并打通"命令执行 → 事件 → 前端刷新"链路。

## 设计

### ① 目标分层

压缩能力整体从原语层抽离：

| 层 | 类型 | 动作 |
|----|------|------|
| `Seeing.Session`（原语层） | `Core/Budget/CompressionResult.cs`、`Compression/TokenCounterHelper.cs` 保留；`Core/Budget/TokenBudgetConfig.cs` 去掉 `CompactionStrategy` 属性（连同 `SlidingWindowKeepTokens`/`SummaryTargetTokens` 的取舍见 ③）；`Management/SessionManager.cs` 去掉 `ICompressionStrategy? compressor` 构造参数与默认 `SlidingWindowCompression` | **改造** |
| `Seeing.Session` | `ISummarizer`、`ICompressionStrategy`、`SessionCompressor`、`SlidingWindowCompression`、`Strategies/*`、`CompactionStrategyType` | **迁出/删除** |
| `Seeing.Agent.Abstractions` | `ISummarizer`（`Compression` 命名空间）；压缩事件（`Events` 命名空间，实现 `IMessageEvent`） | **新增**（唯一公共抽象） |
| `Seeing.Agent`（主库） | `LlmSummarizer`（默认摘要实现）、`CompressionService`（具体类，编排）、`CompressionOptions`、`CompressionOutcome` | **新增**（实现 + 编排） |
| `Seeing.Agent.App` | `BuiltInCommands.CompactHistory` 改造；`ExecutionJobService` 增加自动压缩检查 | 修改 |
| `Seeing.Agent.TokenBudget` | 删除 `CompressionService`/`ICompressionService`/`CompressionStrategyFactory`/`ICompressionStrategyFactory`/`Core/CompressionResult.cs`/`BudgetCheckHook`；`BudgetUpdateHook` 删旧 `CompactionEvent` 构造块；`TokenBudgetApi.TriggerCompactionAsync` 改走主库 `CompressionService` | 收敛 |
| `samples/Seeing.Agent.WebUI` | 删除 `SessionCompactionService` + DI 注册 + `Session.razor` 相关注入/方法 | 删除 |

依赖方向全程向下：原语层无 LLM 概念 → Abstractions 只放 `ISummarizer` 契约 → 主库放实现与编排 → TokenBudget 只做决策（设 `SessionData.PendingCompaction` 标记，不触达主库）→ App 层调主库具体类执行。

### ② 核心契约

公共抽象**只有 `ISummarizer`**（Abstractions）：

```csharp
// Seeing.Agent.Abstractions.Compression —— 摘要器（插件扩展点，极简契约）
public interface ISummarizer
{
    // 输入：要摘要的消息列表（结构化，含 Role/Content/Metadata）；输出：摘要文本
    Task<string> SummarizeAsync(IReadOnlyList<SessionMessage> messages, CancellationToken ct = default);
    IAsyncEnumerable<string> SummarizeStreamingAsync(IReadOnlyList<SessionMessage> messages, CancellationToken ct = default);
}
```

编排实现放主库（具体类，非公共抽象，App 层直接注入）：

```csharp
// Seeing.Agent.Compression —— 压缩编排（主库具体类）
public class CompressionService
{
    // 非流式：返回结果 + 需发布的事件
    public Task<CompressionOutcome> CompressAsync(SessionData session, CompressionOptions options, CancellationToken ct = default);
    // 流式：产出事件流供上层发布
    public IAsyncEnumerable<IMessageEvent> CompressStreamingAsync(SessionData session, CompressionOptions options, CancellationToken ct = default);
}

// Seeing.Agent.Compression —— 压缩选项（仅编排层信息）
public class CompressionOptions
{
    public int KeepRecentMessages { get; init; } = 10; // 保留尾部条数
    public int? TargetTokens { get; init; }            // 自动预算压缩用
}

// Seeing.Agent.Compression —— 压缩结果（结果 + 待发布事件）
public class CompressionOutcome
{
    public CompressionResult Result { get; init; } = default!;
    public IReadOnlyList<IMessageEvent> Events { get; init; } = Array.Empty<IMessageEvent>();
}
```

要点：
- `CompressionResult` **统一用 `Seeing.Session.Core.CompressionResult`**（现存带 `CompressedMessages` 的版本），删除 TokenBudget 的重复定义。
- **删掉 `ICompressionService` 抽象**（YAGNI）：默认只实现一个 LLM 摘要，编排逻辑（head/tail 切分、摘要消息构造、Session 更新、事件产出）在主库具体类 `CompressionService` 内，App 层直接注入该具体类，无需公共接口。
- **删掉 `ICompressionStrategy` 接口与键控 DI 体系**：不内置 sliding-window/hybrid 多策略。
- 插件扩展路径收敛为唯一：**覆盖 `ISummarizer`**（换摘要实现）。如需完全自定义压缩，可整体替换主库 `CompressionService` 的 DI 注册（非接口，属于主库内部可替换实现）。
- **默认压缩只实现一个 LLM 摘要**：`LlmSummarizer : ISummarizer`（主库，基于 `ILlmService`）。prompt 模板、模型/provider 选择等实现细节**全部由 `LlmSummarizer` 内部自决**（构造注入 `ILlmService` + 可选内部配置），不暴露到公共契约。

实现细节注意：
- `TokenCounterHelper`（`Seeing.Session.Compression`）为 `internal`，主库 `CompressionService` 无法访问。`TokensBefore`/`TokensAfter` 计算改用 `ITokenCounter`（原语层 `Seeing.TokenEstimation`，主库可引用）；`TokenCounterHelper` 随旧策略删除。
- 旧 `CompactionEvent`（`Seeing.Agent.TokenBudget/Events/BudgetEvents.cs`）随 `BudgetUpdateHook` 旧代码块一并删除；新压缩事件（`CompactionStarted/Delta/Completed/Failed`）在子项目① `MessageEventType` 常量类登记字符串值。
- 自动压缩路径的 `CompressionOptions` 由 `ExecutionJobService` 从 `session.BudgetConfig` 映射构造（`KeepRecentMessages`=10 默认，`TargetTokens`=`SummaryTargetTokens`）。

### ③ 调用链路

```
手动 /compact：
  CompactHistory（App 层，注入主库 CompressionService 具体类）
    → CompressionService.CompressStreamingAsync(session, options)
    → 逐个 Publish 压缩事件：CompactionStarted / CompactionDelta（流式摘要 token）/ CompactionCompleted（before/after token、压缩条数）/ CompactionFailed
    → 返回 CommandResult.Ok(msg, shouldContinue: false, needsRefresh: true)

自动 TokenBudget：
  BudgetUpdateHook（TokenBudget）：预算达 Critical/Overflow → session.PendingCompaction = true + 发预算状态事件（不执行压缩）
  ExecutionJobService（App 层，新一轮执行前）：
    → 检查 session.PendingCompaction == true
    → 门控：AutoCompactionEnabled（读 session.BudgetConfig/agent 配置，通过 TokenBudgetConfigResolver）为 false → 清标记、跳过
    → 注入主库 CompressionService，CompressAsync(session, options)  // 非流式，返回 CompressionOutcome
    → 更新 Session.Messages
    → Publish outcome.Events（CompactionStarted/Completed/Failed）——前端收到更新
    → 清 PendingCompaction 标记
```

**压缩事件 Type 字符串值**（新增到 `MessageEventType` 常量类，子项目① 已建立该约定）：
- `CompactionStarted` = `"compaction.started"`
- `CompactionDelta` = `"compaction.delta"`
- `CompactionCompleted` = `"compaction.completed"`
- `CompactionFailed` = `"compaction.failed"`

**`CompressionOptions` 来源**（自动压缩路径）：由 `ExecutionJobService` 从 `session.BudgetConfig` 映射构造——`KeepRecentMessages` 对应原 `SlidingWindowKeepTokens` 语义按条数折算（默认 10），`TargetTokens` 取 `SummaryTargetTokens`（默认 4000）。原 `TokenBudgetConfig.CompactionStrategy` 枚举属性删除，策略选择不再存在（单一 LLM 摘要）。

`CompressionService.CompressAsync` 内部：无消息/消息过少 → 返回"无需压缩"；未配置 LLM → 返回失败（含明确原因）；否则 `headCount = count - KeepRecentMessages`，把 head 消息列表传给 `ISummarizer.SummarizeAsync(headMessages)` 生成摘要，构造摘要消息（`is_compaction_summary` 元数据），保留 tail，返回 `CompressionResult`。流式版本逐段产出事件。

`ExecutionJobService` 检查点：在 `ProcessExecutionAsync` 开头（构建 LLM 上下文之前）检查 `session.PendingCompaction`，为 true 则执行压缩。原 TokenBudget `BudgetCheckHook`（`ChatBeforeStart`）删除（其职责——门控 + 执行——迁至 `ExecutionJobService`），`BudgetUpdateHook` 中构造旧 `CompactionEvent` 的代码块删除（其 `CompactionStrategy.ToString()` 依赖已删枚举，且 `compactionResult` 不再经 payload 传递）。

### ④ 命令执行链修复（支撑 `/compact` 生效）

- `CommandResultEvent`（`Seeing.Agent.App/Events/AppEvents.cs`）：**已存在 `NavigationTarget` 字段**，只需新增 `bool NeedsRefresh`。
- `ExecutionJobService.ProcessCommandAsync`：生成 `CommandResultEvent` 时**补齐 `NavigationTarget` 填充**（当前未填充），并从 `CommandResult.NeedsRefresh` 读出填充新字段。
- `CompactHistory` 返回 `CommandResult.Ok(..., shouldContinue: false, needsRefresh: true)`。
- 同批修正其他终端型 System 命令的 `shouldContinue`（`/clear`、`/help`、`/new`、`/fork`、`/agent`、`/model`、`/mcp`、`/tools`）——同一根因，改动为在返回处补 `shouldContinue: false`。
- 前端 `ConsumeEventStreamAsync`（`Session.razor`）显式处理 `CommandResultEvent`：`NeedsRefresh` → `ResetTimelineFromSession()`；`NavigationTarget` → `NavigationManager.NavigateTo`。
- 前端处理压缩事件：`CompactionDelta` → 渲染摘要气泡（流式）；`CompactionCompleted` → `ResetTimelineFromSession()`；`CompactionFailed` → 提示错误。
- `EventStreamHandler`：原 `case MessageEventType.Compaction: HandleCompaction((CompactionEvent)evt)` 强转分支删除（旧 `TokenBudget.CompactionEvent` 已删），改为按 `"compaction.*"` 字符串值处理新事件类型。

### ⑤ 删除清单

**被删类型（及宿主文件的连带改造）：**

- `src/Seeing.Session/Compression/ISummarizer.cs`（上移 Abstractions）
- `src/Seeing.Session/Compression/ICompressionStrategy.cs`、`SessionCompressor.cs`、`SlidingWindowCompression.cs`、`Strategies/{SummarizingStrategy,SlidingWindowTokenStrategy,HybridStrategy}.cs`（删除）
- `src/Seeing.Session/Core/Budget/CompactionStrategyType.cs`（删除）
- `src/Seeing.Session/Core/Budget/TokenBudgetConfig.cs`：去掉 `CompactionStrategy` 属性（`CompactionStrategyType` 已删），`SlidingWindowKeepTokens`/`SummaryTargetTokens` 保留（自动压缩 TargetTokens 来源）
- `src/Seeing.Session/Management/SessionManager.cs`：去掉 `ICompressionStrategy? compressor` 构造参数、`_compressor` 字段及 `CompressAsync` 内压缩调用（改由主库 `CompressionService` 承担，SessionManager 回归纯会话管理）
- `src/Seeing.Session/SessionServiceExtensions.cs`：去掉 `compressor: sp.GetService<ICompressionStrategy>()` 注入
- `src/Seeing.Agent.TokenBudget/Services/{CompressionService,ICompressionService,CompressionStrategyFactory,ICompressionStrategyFactory}.cs`（删除）
- `src/Seeing.Agent.TokenBudget/Core/CompressionResult.cs`（删除，统一用 Session.Core 版）
- `src/Seeing.Agent.TokenBudget/Api/TokenBudgetApi.cs`：`TriggerCompactionAsync(string, CompactionStrategyType?)` 无任何调用方，改为 `TriggerCompactionAsync(string sessionId)` 并注入主库 `CompressionService` 具体类执行（TokenBudget.csproj 已 `ProjectReference` 主库 `Seeing.Agent`，可直接引用；此引用是既有现状，AGENTS.md 的"扩展层禁止引用主库具体类"在本包已被 TokenBudgetManager 依赖 `IOptions<SeeingAgentOptions>` 突破）
- `src/Seeing.Agent.TokenBudget/Configuration/GlobalTokenBudgetOptions.cs`：去掉 `CompactionStrategy` 属性
- `src/Seeing.Agent.TokenBudget/Services/TokenBudgetConfigResolver.cs`：去掉 `CompactionStrategy` 解析
- `src/Seeing.Agent.TokenBudget/Hooks/BudgetCheckHook.cs`：删除（职责迁至 ExecutionJobService）
- `src/Seeing.Agent.TokenBudget/Hooks/BudgetUpdateHook.cs`：删除第 166-184 行构造旧 `CompactionEvent` 的代码块（依赖已删枚举，`compactionResult` 不再经 payload 传递）
- `src/Seeing.Agent.TokenBudget/Extensions/TokenBudgetServiceExtensions.cs`：去掉 `ICompressionStrategy`/`ICompressionStrategyFactory`/`ICompressionService`/`SlidingWindowTokenStrategy` 注册
- `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`：去掉 `CompactionStrategy` 属性
- `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`：去掉压缩策略相关注册（约 :401）
- `samples/Seeing.Agent.WebUI/Services/SessionCompactionService.cs` + `samples/Seeing.Agent.WebUI/Services/LlmSummarizer.cs`（旧 `ISummarizer` 实现，接口变更后不兼容）+ `Program.cs` 中相关 DI 注册 + `Session.razor` 注入/`OnCompactRequested`/`CompactSessionAsync`/`FallbackCompactAsync` + `ChatTokenStatusBar.razor` 的"压缩会话"按钮（改为发 `/compact` 命令或调主库压缩服务）
- 测试：`tests/Seeing.Session.Tests/Compression/{SummarizingStrategyTests,SlidingWindowTokenStrategyTests,HybridStrategyTests}.cs`、`tests/Seeing.Session.Tests/Management/SessionManagerTests.cs` 中 `Mock<ICompressionStrategy>` 相关（删除/改造）

### ⑥ 错误处理

| 场景 | 手动 `/compact` | 自动 TokenBudget |
|------|-----------------|------------------|
| 未配置压缩用模型 | `CompactionFailedEvent`（"未配置压缩用模型"），前端提示 | `CompressionService` 返回失败 → `ExecutionJobService` 记日志、清标记、跳过 |
| LLM 调用失败 | `CompactionFailedEvent`（含错误信息） | `ExecutionJobService` 记日志、清标记、跳过 |
| `AutoCompactionEnabled` 为 false | 不受影响（手动命令始终允许） | `ExecutionJobService` 清标记、跳过（门控迁移自原 `BudgetCheckHook`） |
| 无消息 / 消息过少 | `CommandResult.Ok("无需压缩")`（不产出事件） | `ExecutionJobService` 跳过（`BudgetUpdateHook` 置位按 token 级别，不保证有足量消息） |

### ⑦ 测试

- `LlmSummarizer`：mock `ILlmService`，验证流式/非流式摘要调用与模型选择。
- `CompressionService`（主库具体类）：mock `ISummarizer`，验证结果构造、事件产出、Session 更新、错误分支。
- `CompactHistory` 命令：验证 `shouldContinue:false`、`needsRefresh:true`、事件发布、无消息场景。
- `ExecutionJobService`：验证 `PendingCompaction` 标记触发压缩、`AutoCompactionEnabled` 门控、事件发布、标记清除。
- `TokenBudgetApi.TriggerCompactionAsync`：验证改走主库 `CompressionService` 后行为（mock 主库具体类）。
- `ProcessCommandAsync`：验证 `CommandResultEvent` 携带 `NeedsRefresh`、补全 `NavigationTarget`。
- WebUI 压缩事件处理：`CompactionDelta`/`Completed`/`Failed` 分支渲染与刷新。
- 被删类型相关测试同步删除/改造：`tests/Seeing.Session.Tests/Compression/*`、`SessionManagerTests` 的 `Mock<ICompressionStrategy>`。

## 非目标

- 不做 `MessageEventType` 枚举 → 字符串（子项目 ①，独立先行，本设计的事件类型遵循其字符串约定）。
- 不做 agent 循环集成：压缩是框架级轻量 LLM 任务，不经 `IAgentExecutor`。
- 不保留滑动窗口/混合策略；插件只需覆盖 `ISummarizer` 提供不同摘要实现。
- 不改 TokenBudget 的预算状态计算与 `PendingCompaction` 置位逻辑（`BudgetUpdateHook` 仅删旧 `CompactionEvent` 构造块，其余保留）；`BudgetCheckHook` 删除。
