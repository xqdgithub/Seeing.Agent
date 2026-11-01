# LlmService 职责收缩与记忆注入重做（设计）

- 日期：2026-11-01
- 状态：待审批
- 前置：Phase 0 已完成（`49c3a7d0`，T1–T4/T6–T8 全部关闭）
- 关联：本文档**推翻** `2026-10-31-agent-reflection-design.md` §3.3 的两版方案（ContextSource 迁移版、ChatBeforeStart 修复版）

## 1. 为什么推翻上一版

### 1.1 已撤销的工作

提交 `c3ee3a66`、`4545fdac`、`14c15e1a`（T9 三轮）已 `reset` 撤销，仓库回到 `49c3a7d0`。

撤销原因不是实现质量，而是**设计前提错误**。三轮修复的轨迹本身就是证据：

| 轮次 | "修复" | 实际后果 |
|---|---|---|
| R1 | 让 `MemoryRecallHandler` 剥离自己写的块（标题定位） | 引入"用户 systemPrompt 以该标题开头 → 清空整个提示" |
| R2 | 改用 sentinel 定界 | 只换了个字面量，根因未变；且记忆内容含 sentinel 会永久停更 |
| R3 | 准备加 sentinel 中和 + Source 过滤 | 仍在"内容嗅探"上叠补丁 |

**三轮都在解"如何从字符串里识别并删除上次写入的内容"。这个问题的前提本身就是错的。**

正确的问题是：**为什么 `LlmService` 要修改一个不属于它的共享可变对象？**

### 1.2 三个独立缺陷

#### 缺陷 1：`ChatBeforeStart` 是 call 级钩子，被当成 turn 级在用

`LlmService` 每次 `CompleteAsync` / `CompleteStreamAsync` 都触发一次 `ChatBeforeStart`。

而 `AgentExecutor` 的结构（`:178-194`）：

```csharp
for (var step = 0; step < maxSteps; step++)
{
    var request = await BuildRequestAsync(...);   // 每步新建
    for (var attempt = 1; ; attempt++)            // 重试
        _llm.CompleteStreamAsync(..., request, ...);   // 每次调用触发 ChatBeforeStart
}
```

一次 10 步工具循环 → `ChatBeforeStart` 触发 **≥10 次**。

**后果**：任何"每轮一次"语义（记忆召回是典型）贴在它上面就会变成"每步一次"。上一版把它记成"性能问题，建议按 turn 缓存"——**那是把设计错误当性能问题记录**。

#### 缺陷 2：`LlmService` 原地修改调用方的 `ChatRequest`

```csharp
// LlmService.cs:364
request.SystemPrompt = newPrompt;
```

`LlmService` 是无状态服务，却原地改调用方传入的可变对象。**它无法知道自己上次改过什么**，所以重试时第二次调用读到的 `SystemPrompt` 已含上次写入的内容 → 重复追加。

这是 T9 三轮补丁的唯一根因。

#### 缺陷 3：`request` 在 attempt 循环外创建，跨尝试复用

```csharp
var request = await BuildRequestAsync(...);   // :187，attempt 循环外
for (var attempt = 1; ; attempt++)            // 同一对象
```

即使不改 `SystemPrompt`，`ApplyChatParamsToRequest` 也在改 `Temperature`/`TopP`/`MaxTokens`/`ThinkingEffort`——同样跨尝试累积。这是**既存问题**，非 T9 引入。

## 2. 目标与非目标

### 目标

1. **`LlmService` 退回纯传输层**：只负责"发请求、收响应、触发可观测性钩子"，**不参与构建或改写 prompt**。
2. **记忆注入回到 `IContextSource` 管道**：与技能清单同构，每轮一次，无累积。
3. **消除 `request` 跨尝试复用**：每次尝试拿到干净的请求对象。

### 非目标

- 不改 `PromptBuilder` 的分节模型。
- 不改 `ContextSource` 管道的既有契约（`IContextSource` / `IAssembly` / 基线存储）。
- 不改 `ChatParams` / `ChatHeaders` / `ChatMessage` / `ChatAfterComplete` / `ChatOnError` 的行为。
- 不做跨轮缓存（`MemoryContextSource` 的召回结果在本轮内是常量，无需额外缓存）。

### 反目标

- 保留两套 prompt 构建路径（`PromptBuilder` + hook 改 prompt）。
- 用内容嗅探（标题/sentinel）实现幂等。
- 让 Core 知道 Memory 的渲染格式。

## 3. 事实核实（设计前提）

| 断言 | 证据 |
|---|---|
| `ChatBeforeStart` 唯一生产订阅者是 `MemoryRecallHandler` | 全仓 grep：`src/` 仅 `MemoryRecallHandler.cs` 与 `LlmService.cs`（触发处）；`samples/`、`plugs/` 零命中 |
| `LlmSystemPrompt` 生产零订阅 | 全仓 grep：`src/` 仅 `LlmService.cs` 两处触发；无任何 handler 实现 |
| `request` 仅在 `CompleteStreamAsync` 一处被使用 | `AgentExecutor.cs` 内 `request.` 零命中（仅作为实参传入） |
| `EnsureFreshAsync` 每轮调用一次 | `ExecutionRunner.cs:270`，在 `if (!commandConsumedExecution)` 块内，非循环内 |
| `InboxBatch` 已携带本轮首条用户文本 | `ExecutionRunner.cs:284` 构造时传 `firstUserText`；`:274` 用 `batch.FirstUserMessageId` |

## 4. 方案

### 4.1 `LlmService` 的职责边界（改后）

**保留**（真正的传输层关注点）：

| Hook | 作用 | 判定 |
|---|---|---|
| `ChatParams` | 调 temperature / topP / maxTokens / thinkingEffort | 模型参数适配 = "怎么发请求" |
| `ChatHeaders` | 加请求头 | 传输层 |
| `ChatMessage` | 响应消息观测（Parallel） | 可观测性 |
| `ChatAfterComplete` | 完成后观测（F&F） | 可观测性 |
| `ChatOnError` | 错误观测（F&F） | 可观测性 |

**删除**：

| Hook | 当前作用 | 删除理由 |
|---|---|---|
| `ChatBeforeStart` | 传 `userMessage`，可写 `systemPrompt` | 改 prompt 属构建职责，与 `PromptBuilder` 重叠；唯一订阅者随记忆迁移消失 |
| `LlmSystemPrompt` | 可写 `promptOutput["prompt"]` | 同上；且生产零订阅，是空跑 |

**判据**：如果一个钩子的效果是"改变发出去的内容语义"，它属于构建层，不属于传输层。"改 headers / 改 temperature"仍是传输层——它们不改 **prompt 语义**，只改**传输参数**。

### 4.2 记忆注入迁 ContextSource

```
ExecutionRunner（每轮一次，:270）
  → IContextAssembler.EnsureFreshAsync(session)
      ├─ ProjectInstructionsContextSource （已有）
      ├─ SkillsContextSource             （已有）
      └─ MemoryContextSource             ← 新增
            ReadAsync: 用 IContextExecutionScope.UserQuery 召回
            RenderInitial: 渲染 "## Relevant memories" 分节
            Render: 变化只叙述一条增量消息
  → PromptBuilder(context.ContextBaseline)    ← 组装，纯函数
  → AgentExecutor → LlmService.CompleteStreamAsync   ← 纯传输，不改 prompt
```

**新增契约**：`IContextExecutionScope.UserQuery`（`string?`）

```csharp
/// <summary>
/// 本轮执行的用户查询文本（本批最后一条用户输入）。
/// <para>供「查询依赖」的上下文源使用（如记忆召回）。为空时源按不可用处理。</para>
/// <para>由宿主在装配前填充一次，执行期内只读。</para>
/// </summary>
string? UserQuery { get; set; }
```

**填充点**：`ExecutionRunner.cs:224-228` 现有的 `executionScope` 赋值块内，加一行：

```csharp
executionScope.UserQuery = record.Input?.Text;
```

**为什么用 `record.Input.Text` 而不是 `batch.FirstUserText`**（已核实）：

| 来源 | 语义 | 证据 |
|---|---|---|
| `batch.FirstUserText` | 本批**第一条** user 消息 | `SessionRuntime.cs:460`：`firstUserText = firstUserMessage?.Content` |
| `record.Input.Text` | 本批**最后一条** user 文本 | `SessionRuntime.cs:467`：`record.Input = new ChatInput { Text = lastUserMessage?.Content ?? string.Empty }` |

「本轮输入」应该是最后一条——合并消费时（如 `hello` + `/compact`）用户的最新意图在末条。这与 `SessionRuntime` 注释 `:465-466` 的判断一致（"命令判定与标题统一取本批最后一条 user 文本"）。

**为什么这样满足"字节稳定"**：

`EnsureFreshAsync` 每轮调用一次（`ExecutionRunner.cs:270`）。一次执行内：

- `batch.FirstUserText` 固定 → `UserQuery` 固定
- 召回结果固定 → `Canonical` 固定
- `RenderInitial` 纯函数 → 基线字节稳定 ✓

"跨执行变化"正是 `Render(previous, current)` 的设计用途。

### 4.3 `MemoryContextSource` 设计

照 `SkillsContextSource` 的形状（`src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs`）。

**关键决策（v2，与 v1 不同）**：

| 项 | v1 | v2 | 理由 |
|---|---|---|---|
| `Target` | `System` | **`Message`** | `ContextAssembler` 对 System 源只在首次建立时渲染基线，之后只叙述 delta。而 `UserQuery` 每轮变 → 召回每轮变 → 系统提示永久停在第一轮，但每轮还在产生 delta 消息。`Message` 让内容以消息形式进入历史，语义一致 |
| 无命中 | `Removed` | **`Unavailable`** | `RecallAsync` 内部 catch 所有异常返回空列表，源无法区分「确实无命中」与「IO 失败」。`Removed` 会在一次瞬时失败时叙述「记忆已移除」，违反 `IContextSource` 纪律 |

```csharp
public sealed class MemoryContextSource : IContextSource
{
    public const int BaselineCharLimit = 8_000;

    public ContextSourceKey Key => new(typeof(MemoryContextSource).FullName!);
    public ContextSourceScope Scope => ContextSourceScope.Project;
    public int ValueSchemaVersion => 1;
    public ContextSourceTarget Target => ContextSourceTarget.Message;
    public int MaxBaselineChars => BaselineCharLimit;

    public async Task<ContextSourceValue> ReadAsync(CancellationToken ct)
    {
        // 1. 模块停用 → Unavailable
        // 2. ToolsOnly 模式 → Unavailable
        // 3. UserQuery 为空 → Unavailable
        // 4. RecallAsync(UserQuery)（内部 150ms 超时兜底）
        // 5. 无命中 → Unavailable（不是 Removed）
        // 6. 有命中 → Value，Canonical = {"hits":[{"path","score"}]}
    }

    public string RenderInitial(ContextSourceValue value) { /* 渲染 ## Relevant memories */ }
    public string? Render(ContextSourceValue previous, ContextSourceValue current) { /* 变化叙述 */ }
}
```

**`Canonical` 必须是 JSON 对象**：`ContextAssembler.cs:261-275` 会把数组形态判为「声明有值但没有可用 Canonical」并跳过整个源。

**`Render(previous, current)` 的 `previous.Payload` 恒为 null**（`ContextAssembler.cs:1012-1018`）——增量叙述只能读 `Canonical`（path/score），不能依赖标题或正文。

**截断上限统一用 `MaxBaselineChars = 8000`**（不与 `MaxInjectTokens * 4` 并用；后者只约束召回条数）。

**注册**：必须用 `services.TryAddEnumerable(ServiceDescriptor.Scoped<IContextSource, MemoryContextSource>())`，与 `SkillsModule.cs:113` 一致。用 `TryAddScoped<IContextSource, T>()` 会按 ServiceType 去重，在已有其它 `IContextSource` 描述符时**整条静默跳过**。

### 4.4 `AgentExecutor` 的 request 生命周期

`request` 移进 `attempt` 循环：

```csharp
for (var attempt = 1; ; attempt++)
{
    var request = await BuildRequestAsync(agent, history, context, effectiveToken);
    ...
    await foreach (var update in _llm.CompleteStreamAsync(..., request, ...))
}
```

**成本评估**：`BuildRequestAsync` 内含 `PromptBuilder.BuildAsync`（分节拼接）。每 attempt 重跑一次。

- 正常路径：attempt 几乎总是 1，无额外成本
- 重试路径：每次重试多一次字符串拼接（毫秒级），**换来每次尝试都拿到干净请求**

**替代方案（不采纳）**：只在重试时重建。会让 `AgentExecutor` 多一个分支，且"首次与重试走不同路径"本身就是缺陷温床。

### 4.5 分两步交付

**A-1：`LlmService` 职责收缩（独立可合入）**

| 改动 | 文件 |
|---|---|
| 删 `ChatBeforeStart` 触发（非流式 + 流式） | `LlmService.cs` |
| 删 `LlmSystemPrompt` 触发（非流式 + 流式） | `LlmService.cs` |
| 删 `ApplyChatBeforeStartOutput` | `LlmService.cs` |
| `request` 移进 attempt 循环 | `AgentExecutor.cs` |
| 删 hook 点定义 | `HookRegistry.cs` |
| 删 `MemoryRecallHandler` 及其注册 | `MemoryRecallHandler.cs`、`MemoryModule.cs`、`MemoryServiceExtensions.cs` |
| 删相关测试 | `MemoryRecallHandler*Tests.cs`、`HookManagerTests` 里的相应断言 |

**中间态**：记忆注入完全失效。**但它在 A-1 之前也从未工作过**（handler 恒提前返回），所以**不是回归**。

**A-2：记忆迁 ContextSource**

| 改动 | 文件 |
|---|---|
| 加 `UserQuery` | `IContextExecutionScope.cs` |
| 填充 `UserQuery` | `ExecutionRunner.cs` |
| 新增 `MemoryContextSource` | `capabilities/Seeing.Agent.Memory/Integration/MemoryContextSource.cs` |
| 注册 `IContextSource` | `MemoryServiceExtensions.cs` |
| 测试 | 新建 `MemoryContextSourceTests.cs` |

**为什么不合并**：A-1 是**纯删除/收敛**（负行数，风险低，可立即验证 `ChatParams` 等仍工作）；A-2 是**新增功能**。分开后，若 A-2 出问题，A-1 的收益（`request` 生命周期修复、hook 收敛）不会回滚。

## 5. 风险与边界

| 风险 | 缓解 |
|---|---|
| A-1 删 `ChatBeforeStart` 后，第三方插件若订阅了它会静默失效 | `HookRegistry` 删除该字段会导致**编译期错误**（好于运行期静默） |
| `Seeing.Agent.Memory` 是发布包，删除 `MemoryRecallHandler` 对包消费者是 source-breaking | release notes 明确标注 |
| `BuildRequestAsync` 每 attempt 重跑可能拖慢重试路径 | 可接受（重试本身是异常路径） |
| `Message` 目标每轮值变化会新增一条消息，长会话中稀释历史 | 用户已裁决采用。同 turn 内 `UserQuery` 固定 → 不重复；只跨 turn 变化 |
| `RecallAsync` 无法区分失败与无命中 | 都返回 `Unavailable`，表现为"本轮无注入"；内部已有 Warning 日志 |
| Scheduler/Heartbeat 旁路不填 `UserQuery` | 定时任务里记忆永不注入。登记为已知边界；覆盖需另改 Scheduler 包 |
| `record.Input?.Text` 可能为空（纯系统批次） | 源返回 `Unavailable`，不注入 |

## 6. 验收标准

### A-1

- `LlmService` 内不再出现 `request.SystemPrompt =` 赋值（grep 零命中）
- `HookRegistry` 不再含 `ChatBeforeStart` / `LlmSystemPrompt` 字段
- 同一 `ChatRequest` 连调两次 `CompleteAsync`，`SystemPrompt` 不被修改
- `ChatParams` / `ChatHeaders` / `ChatMessage` / `ChatAfterComplete` / `ChatOnError` 行为不变（既有测试全绿）
- 全量 `dotnet build Seeing.Agent.slnx` 0 error

### A-2

- 记忆命中时，系统基线含 `## Relevant memories` 分节
- 同一轮内多次 LLM 调用，基线**不累积**（ContextSource 每轮只装配一次）
- 第二次执行若召回结果相同，**不产生增量消息**（字节稳定）
- 召回结果变化时，产生**一条**增量消息（`Render(previous, current)`）
- `ToolsOnly` 模式下不注入
- 全量测试绿

## 7. 已确认事项

1. **`UserQuery` 取 `record.Input?.Text`**（本批最后一条 user 文本），不是 `batch.FirstUserText`。已核实 `InboxBatch` 只有 `FirstUserText` 字段，而 `record.Input.Text` 才是最后一条。
2. **`ChatBeforeStart` 直接删除**，不保留 `[Obsolete]` 空壳。理由：编译期错误 > 运行期静默失效。
3. **`LlmSystemPrompt` 直接删除**。生产零订阅，是空跑；语义与 `PromptBuilder` 重叠。
