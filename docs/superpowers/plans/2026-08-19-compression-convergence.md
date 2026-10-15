# 压缩系统收敛实施计划（子项目②）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **⚠️ 硬性前置：子项目①（事件字符串化）必须已合入**。本计划依赖 `IMessageEvent.Type` 为 `string`（审查 B1：当前仓库仍是枚举，若未合入①，任务 2 事件、任务 10 WebUI 分支全部无法编译）。开始前先执行 `git log --oneline -5` 确认子项目①的提交存在；否则先按 `2026-08-19-message-event-type-string.md` 实施。

**Goal:** 收敛 7 处重复压缩实现为单一架构——唯一公共抽象 `ISummarizer`（Abstractions）+ 主库 `CompressionService`（编排）+ `LlmSummarizer`（默认 LLM 摘要）。删 `ICompressionService`/`ICompressionStrategy`/`CompactionStrategyType`/键控 DI/旧策略。TokenBudget 只决策不执行，`ExecutionJobService` 门控并调主库；手动 `/compact` 修复 `shouldContinue:false` + 事件 + 前端刷新。

**Architecture（最终形态）：**

```
Abstractions (零实现):
  ISummarizer (接口)  ──┐
  SummarizeRequest     │
  SummarizeResult      │  插件可替换默认实现
  CompactionEvents (record, string Type)
主库 Seeing.Agent:
  CompressionService   ──┘  编排：调用 ISummarizer → 写回 session → 持久化
  CompressionOptions        tokens 目标/保留条数映射
  CompressionOutcome        结果 DTO
  LlmSummarizer             默认 ISummarizer（ITextCompletion 摘要）
TokenBudget:
  (仅) BudgetStatus 决策 + PendingCompaction 门控标记 → 不再执行压缩
App:
  CompactHistory 命令 → shouldContinue:false + CommandResultEvent(NeedsRefresh=true)
  ExecutionJobService → 门控检查 + 调 CompressionService + 发压缩事件
WebUI:
  删 SessionCompactionService/LlmSummarizer(旧)/ChatTokenStatusBar 按钮改发 /compact
  EventStreamHandler 显式处理 compaction.* 事件
```

**分层约束：**
- `TokenBudget.csproj` 已 ProjectReference 主库 `Seeing.Agent`（现状，合规依据：`TokenBudgetManager` 依赖 `IOptions<SeeingAgentOptions>`），故 TokenBudgetApi 可直接注入主库 `CompressionService`。
- Abstractions 只允许引用原语层 Seeing.Session（`SummarizeRequest`/`SummarizeResult` 引用 `SessionMessage`）。

**关键既有事实（审查核对）：**
- `SessionMessage` 无 `Create(role, content)` 工厂，用 `UserMessage/AssistantMessage/SystemMessage/ToolMessage` 工厂（`src/Seeing.Session/Core/SessionMessage.cs`）。
- `LlmSummarizer` 用 `ITextCompletion`（`src/Seeing.Agent/Llm/ITextCompletion.cs`，`CompleteAsync(systemPrompt, userPrompt, model, maxTokens, ct)`），非 `ILlmService`。
- `SessionData.Messages` 为 `List<SessionMessage>`；`SessionManager.Compress(id)` 无生产调用方，仅测试调用。
- `ITokenBudgetApi.TriggerCompactionAsync` 无任何外部调用方，可安全改签名。
- `ExecutionJobService` 已持有 `IOptionsMonitor<SeeingAgentOptions>`（门控读取源）。

**Tech Stack:** C# / .NET 10 / xUnit / FluentAssertions / Moq
**测试命令：** `dotnet build Seeing.Agent.slnx`、`dotnet test tests/Seeing.Agent.Tests`、`dotnet test tests/Seeing.Session.Tests`。

---

## 任务 1：Abstractions 新增 `ISummarizer` 契约

**Files:**
- Create: `src/Seeing.Agent.Abstractions/Summarization/ISummarizer.cs`
- Create: `src/Seeing.Agent.Abstractions/Summarization/SummarizeRequest.cs`
- Create: `src/Seeing.Agent.Abstractions/Summarization/SummarizeResult.cs`
- Test: `tests/Seeing.Agent.Tests/Summarization/SummarizerContractTests.cs`（新建）

- [ ] **Step 1: 写契约测试（TDD 红）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Summarization;

public class SummarizerContractTests
{
    [Fact]
    public void SummarizeRequest_ShouldCarryMessagesAndOptions()
    {
        var messages = new List<SessionMessage> { SessionMessage.UserMessage("hi") };
        var request = new SummarizeRequest(messages, maxOutputTokens: 2000, keepRecentCount: 10);

        request.Messages.Should().BeEquivalentTo(messages);
        request.MaxOutputTokens.Should().Be(2000);
        request.KeepRecentCount.Should().Be(10);
    }

    [Fact]
    public void SummarizeResult_ShouldCarrySummaryAndTrimmedList()
    {
        var trimmed = new List<SessionMessage>();
        var result = new SummarizeResult("summary", trimmed, summaryTokenCount: 300);

        result.Summary.Should().Be("summary");
        result.TrimmedMessages.Should().BeSameAs(trimmed);
        result.SummaryTokenCount.Should().Be(300);
    }
}
```

（`SessionMessage` 无 `Create(role, content)` 工厂，改用 `UserMessage("hi")` 等工厂；`.Role`/`.Content` 属性存在，`SystemMessage/UserMessage/AssistantMessage/ToolMessage` 工厂见 `src/Seeing.Session/Core/SessionMessage.cs`。）

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SummarizerContractTests"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 2: 实现契约**

`SummarizeRequest.cs`：
```csharp
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 摘要请求参数
/// </summary>
public sealed record SummarizeRequest(
    IReadOnlyList<SessionMessage> Messages,
    int MaxOutputTokens,
    int KeepRecentCount);
```

`SummarizeResult.cs`：
```csharp
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 摘要结果（Summary 写入 System 占位；TrimmedMessages 替换原历史）
/// </summary>
public sealed record SummarizeResult(
    string Summary,
    IReadOnlyList<SessionMessage> TrimmedMessages,
    int SummaryTokenCount);
```

`ISummarizer.cs`：
```csharp
using Seeing.Session.Core;

namespace Seeing.Agent.Abstractions.Summarization;

/// <summary>
/// 会话摘要器 - 唯一公共压缩抽象（插件可替换默认 LlmSummarizer）
/// </summary>
public interface ISummarizer
{
    /// <summary>
    /// 生成摘要并返回压缩后的消息列表
    /// </summary>
    Task<SummarizeResult> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: 测试通过 + 提交**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SummarizerContractTests"`
```bash
git add src/Seeing.Agent.Abstractions/Summarization tests/Seeing.Agent.Tests/Summarization
git commit -m "feat: Abstractions 新增 ISummarizer 契约"
```

---

## 任务 2：Abstractions 新增压缩事件（string Type）

**Files:**
- Create: `src/Seeing.Agent.Abstractions/Events/CompactionEvents.cs`
- Test: `tests/Seeing.Agent.Tests/Events/CompactionEventTests.cs`（新建）

- [ ] **Step 1: 写测试（TDD 红）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Events;
using Xunit;

namespace Seeing.Agent.Tests.Events;

public class CompactionEventTests
{
    [Fact]
    public void CompactionStartedEvent_ShouldUseStringType()
    {
        var evt = new CompactionStartedEvent
        {
            SessionId = "s1",
            Reason = "manual"
        };

        evt.Type.Should().Be("compaction.started");
        evt.SessionId.Should().Be("s1");
        evt.Reason.Should().Be("manual");
    }

    [Fact]
    public void CompactionCompletedEvent_ShouldCarryTokenStats()
    {
        var evt = new CompactionCompletedEvent
        {
            SessionId = "s1",
            TokensBefore = 5000,
            TokensAfter = 1200,
            MessagesRemoved = 3
        };

        evt.Type.Should().Be("compaction.completed");
        evt.TokensBefore.Should().Be(5000);
        evt.TokensAfter.Should().Be(1200);
        evt.MessagesRemoved.Should().Be(3);
    }
}
```

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CompactionEventTests"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 2: 实现事件**

`CompactionEvents.cs`（**non-positional record + `required` 属性，照抄 `LoopStartEvent` 模板 `MessageEventTypes.cs:132-144`，杜绝 CS0102 重复成员**）：
```csharp
namespace Seeing.Agent.Abstractions.Events;

/// <summary>压缩开始</summary>
public sealed record CompactionStartedEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Started;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>压缩触发原因（manual/auto/api）</summary>
    public required string Reason { get; init; }
}

/// <summary>压缩增量进度</summary>
public sealed record CompactionDeltaEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Delta;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>进度阶段（summarizing/trimming/…）</summary>
    public string Stage { get; init; } = string.Empty;
}

/// <summary>压缩完成</summary>
public sealed record CompactionCompletedEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Completed;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>压缩前 token 数</summary>
    public int TokensBefore { get; init; }

    /// <summary>压缩后 token 数</summary>
    public int TokensAfter { get; init; }

    /// <summary>移除消息条数</summary>
    public int MessagesRemoved { get; init; }

    /// <summary>生成的摘要文本</summary>
    public string? Summary { get; init; }
}

/// <summary>压缩失败</summary>
public sealed record CompactionFailedEvent : IMessageEvent
{
    /// <inheritdoc />
    public string Type => CompactionEventTypes.Failed;

    /// <inheritdoc />
    public required string SessionId { get; init; }

    /// <inheritdoc />
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; init; }
}
```

（事件采用 `LoopStartEvent` 模板：non-positional record + `required string SessionId { get; init; }`，实现 `IMessageEvent` 的 `Type`/`SessionId`/`Timestamp` 成员。）

在 `MessageEventTypes.cs`（子项目①改造后）新增常量——**替代**旧的 `MessageEventType.Compaction` 单一值：

```csharp
// CompactionEvents 专用常量（点分，与子项目①约定一致）
public static class CompactionEventTypes
{
    public const string Started = "compaction.started";
    public const string Delta = "compaction.delta";
    public const string Completed = "compaction.completed";
    public const string Failed = "compaction.failed";
}
```

（删除原 `MessageEventType.Compaction = "compaction"`，避免与 `compaction.started` 等歧义。）

- [ ] **Step 3: 测试通过 + 提交**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CompactionEventTests"`
```bash
git add src/Seeing.Agent.Abstractions/Events/CompactionEvents.cs src/Seeing.Agent.Abstractions/Events/MessageEventTypes.cs tests/Seeing.Agent.Tests/Events/CompactionEventTests.cs
git commit -m "feat: 新增压缩事件（compaction.* 字符串类型）"
```

---

## 任务 3：主库新增 `CompressionService`（编排核心）

**Files:**
- Create: `src/Seeing.Agent/Compression/CompressionService.cs`
- Create: `src/Seeing.Agent/Compression/CompressionOptions.cs`
- Create: `src/Seeing.Agent/Compression/CompressionOutcome.cs`
- Test: `tests/Seeing.Agent.Tests/Compression/CompressionServiceTests.cs`（新建）

- [ ] **Step 1: 写测试（TDD 红）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Compression;
using Seeing.Session.Core;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Compression;

public class CompressionServiceTests
{
    [Fact]
    public async Task CompressAsync_ShouldSummarizeThenReplaceHistory()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        session.Messages.Add(SessionMessage.AssistantMessage("b"));
        session.Messages.Add(SessionMessage.UserMessage("c"));

        var summarizer = new Mock<ISummarizer>();
        summarizer.Setup(s => s.SummarizeAsync(It.IsAny<SummarizeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SummarizeResult(
                "summary text",
                new[] { SessionMessage.UserMessage("c") },
                200));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);

        var service = new CompressionService(
            summarizer.Object,
            sessionManager.Object,
            new CompressionOptions());

        var outcome = await service.CompressAsync(session.Id, reason: "manual");

        outcome.Success.Should().BeTrue();
        outcome.TokensBefore.Should().BeGreaterThanOrEqualTo(1);
        outcome.TokensAfter.Should().BeGreaterThanOrEqualTo(1);
        outcome.MessagesRemoved.Should().Be(2);
        outcome.Summary.Should().Be("summary text");
        session.Messages.Count.Should().Be(1);
        session.Messages[0].Content.Should().Be("c");
        sessionManager.Verify(m => m.SaveAndNotifyAsync(session.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompressAsync_WhenNoSummarizer_ShouldFailFast()
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.GetOrLoadAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(SessionData.Create());

        var service = new CompressionService(
            summarizer: null!,
            sessionManager.Object,
            new CompressionOptions());

        var outcome = await service.CompressAsync("s1", reason: "manual");

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("未配置摘要器");
    }
}
```

（`SessionMessage` 用 `UserMessage/AssistantMessage` 工厂；`TokensBefore` 断言用 `GreaterThanOrEqualTo(1)`——短消息 `EstimateTokens` 经 `Math.Max(1, len/4)` 至少为 1。`GetOrLoadAsync`/`SaveAndNotifyAsync` 带可选 `CancellationToken` 参数，Moq Setup 用 `It.IsAny<CancellationToken>()` 避免默认值不匹配。）

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CompressionServiceTests"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 2: 实现**

`CompressionOptions.cs`：
```csharp
namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩选项 - 映射自 TokenBudgetConfig（保留字段作来源）
/// </summary>
public sealed class CompressionOptions
{
    /// <summary>摘要目标 token 数（默认 4000）</summary>
    public int SummaryTargetTokens { get; set; } = 4000;

    /// <summary>保留最近消息条数（默认 10）</summary>
    public int KeepRecentMessages { get; set; } = 10;
}
```

`CompressionOutcome.cs`：
```csharp
namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩执行结果
/// </summary>
public sealed class CompressionOutcome
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int TokensBefore { get; init; }
    public int TokensAfter { get; init; }
    public int MessagesRemoved { get; init; }
    public string? Summary { get; init; }
    public string? Strategy { get; init; }
}
```

`CompressionService.cs`：
```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Session.Core;
using Seeing.Session.Management;

namespace Seeing.Agent.Compression;

/// <summary>
/// 压缩编排服务 - 唯一压缩执行入口（TokenBudget 只决策、本服务执行）
/// </summary>
public class CompressionService
{
    private readonly ISummarizer? _summarizer;
    private readonly ISessionManager _sessionManager;
    private readonly CompressionOptions _options;
    private readonly ILogger<CompressionService> _logger;

    public CompressionService(
        ISummarizer? summarizer,
        ISessionManager sessionManager,
        CompressionOptions options,
        ILogger<CompressionService>? logger = null)
    {
        _summarizer = summarizer;
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _options = options ?? new CompressionOptions();
        _logger = logger ?? NullLogger<CompressionService>.Instance;
    }

    /// <summary>
    /// 执行压缩：摘要 → 写回历史 → 持久化
    /// </summary>
    public async Task<CompressionOutcome> CompressAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (_summarizer == null)
        {
            return new CompressionOutcome
            {
                Success = false,
                ErrorMessage = "未配置摘要器（ISummarizer），无法压缩"
            };
        }

        var session = await _sessionManager.GetOrLoadAsync(sessionId);
        var messages = session.Messages.ToList();
        if (messages.Count == 0)
        {
            return new CompressionOutcome { Success = false, ErrorMessage = "会话无消息，无需压缩" };
        }

        try
        {
            var request = new SummarizeRequest(
                messages,
                MaxOutputTokens: _options.SummaryTargetTokens,
                KeepRecentCount: _options.KeepRecentMessages);

            var result = await _summarizer.SummarizeAsync(request, cancellationToken);

            session.Messages.Clear();
            session.Messages.AddRange(result.TrimmedMessages);
            session.UpdatedAt = DateTime.Now;
            await _sessionManager.SaveAndNotifyAsync(session.Id, persist: true);

            _logger.LogInformation("压缩完成: {SessionId} 移除 {Count} 条", sessionId, messages.Count - result.TrimmedMessages.Count);

            return new CompressionOutcome
            {
                Success = true,
                TokensBefore = EstimateTokens(messages),
                TokensAfter = EstimateTokens(result.TrimmedMessages) + result.SummaryTokenCount,
                MessagesRemoved = messages.Count - result.TrimmedMessages.Count,
                Summary = result.Summary,
                Strategy = "LlmSummarizer"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "压缩失败: {SessionId}", sessionId);
            return new CompressionOutcome { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static int EstimateTokens(IReadOnlyList<SessionMessage> messages)
    {
        // 粗估：1 token ≈ 4 字符，每条至少计 1（占位实现，后续可注入 ITokenCounter）
        return messages.Sum(m => Math.Max(1, (m.Content?.Length ?? 0) / 4));
    }
}
```

（注：`using System.Linq;` 依赖 `messages.Sum`——确认文件头已含或补充。`CompressionService` 不发布事件；事件由调用方 `ExecutionJobService` 发布，保持单一职责。）

- [ ] **Step 3: 测试通过 + 提交**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CompressionServiceTests"`
```bash
git add src/Seeing.Agent/Compression tests/Seeing.Agent.Tests/Compression
git commit -m "feat: 主库 CompressionService 编排压缩执行"
```

---

## 任务 4：主库新增 `LlmSummarizer`（默认 ISummarizer）

**Files:**
- Create: `src/Seeing.Agent/Compression/LlmSummarizer.cs`
- Test: `tests/Seeing.Agent.Tests/Compression/LlmSummarizerTests.cs`（新建）

- [ ] **Step 1: 写测试（TDD 红）**

```csharp
using FluentAssertions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Compression;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Compression;

public class LlmSummarizerTests
{
    [Fact]
    public async Task SummarizeAsync_ShouldReturnSummaryAndTrimmedMessages()
    {
        var messages = new List<SessionMessage>
        {
            SessionMessage.UserMessage("问题一"),
            SessionMessage.AssistantMessage("回答一"),
            SessionMessage.UserMessage("问题二"),
        };

        var textCompletion = new Mock<ITextCompletion>();
        textCompletion.Setup(c => c.CompleteAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("【摘要】这是压缩后的对话摘要");

        var summarizer = new LlmSummarizer(textCompletion.Object);
        var request = new SummarizeRequest(messages, MaxOutputTokens: 4000, KeepRecentCount: 1);

        var result = await summarizer.SummarizeAsync(request);

        result.Summary.Should().Contain("摘要");
        result.TrimmedMessages.Count.Should().Be(1);
        result.TrimmedMessages[0].Content.Should().Be("问题二");
        result.SummaryTokenCount.Should().BeGreaterThan(0);
    }
}
```

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~LlmSummarizerTests"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 2: 实现**

`LlmSummarizer.cs`（**用 `ITextCompletion`，非 `ILlmService`**——`ITextCompletion.CompleteAsync(systemPrompt, userPrompt, model, maxTokens, ct)` 走 `CompleteRawAsync` 旁路，无 Hook 副作用，见 `src/Seeing.Agent/Llm/ITextCompletion.cs`）：
```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.Compression;

/// <summary>
/// 默认摘要器 - 基于 ITextCompletion 生成会话摘要
/// </summary>
/// <remarks>
/// prompt/model/provider 选择由本类内部自决（用户决策：压缩是框架的事）
/// </remarks>
public class LlmSummarizer : ISummarizer
{
    private const string SystemPrompt =
        "你是一个会话压缩助手。将以下对话压缩为精炼摘要，保留关键事实、决策、结论与待办事项，不要遗漏用户的核心意图，不要添加对话历史中不存在的信息。直接输出摘要文本。";

    private readonly ITextCompletion _textCompletion;
    private readonly ILogger<LlmSummarizer> _logger;

    public LlmSummarizer(
        ITextCompletion textCompletion,
        ILogger<LlmSummarizer>? logger = null)
    {
        _textCompletion = textCompletion ?? throw new ArgumentNullException(nameof(textCompletion));
        _logger = logger ?? NullLogger<LlmSummarizer>.Instance;
    }

    /// <inheritdoc />
    public async Task<SummarizeResult> SummarizeAsync(
        SummarizeRequest request,
        CancellationToken cancellationToken = default)
    {
        var history = string.Join("\n", request.Messages.Select(m => $"{m.Role}: {m.Content}"));
        var userPrompt = $"""
            请压缩以下对话历史，输出精炼摘要：

            对话历史：
            {history}
            """;

        var summary = await _textCompletion.CompleteAsync(
            SystemPrompt,
            userPrompt,
            model: null,
            maxTokens: request.MaxOutputTokens,
            ct: cancellationToken);

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("摘要生成失败：模型返回空内容");
        }

        var keepCount = Math.Min(request.KeepRecentCount, request.Messages.Count);
        var trimmed = request.Messages.Skip(request.Messages.Count - keepCount).ToList();

        return new SummarizeResult(
            Summary: summary,
            TrimmedMessages: trimmed,
            SummaryTokenCount: Math.Max(1, (summary.Length) / 4));
    }
}
```

（`ITextCompletion` 已在 `ServiceCollectionExtensions.cs:690` 注册，`CompleteAsync` 走 `CompleteRawAsync` 旁路无 Hook；`LlmSummarizer` 依赖注入 `ITextCompletion` 即可。）

- [ ] **Step 3: 测试通过 + 提交**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~LlmSummarizerTests"`
```bash
git add src/Seeing.Agent/Compression/LlmSummarizer.cs tests/Seeing.Agent.Tests/Compression/LlmSummarizerTests.cs
git commit -m "feat: 默认 LlmSummarizer（ITextCompletion 摘要）"
```

---

## 任务 5：`SessionManager` 去压缩职责 + 原语层旧策略清理

**Files:**
- Modify: `src/Seeing.Session/Management/SessionManager.cs`
- Modify: `src/Seeing.Session/SessionServiceExtensions.cs`
- Modify: `src/Seeing.Session/Core/Budget/TokenBudgetConfig.cs`
- Modify: `src/Seeing.Session/Core/Budget/CompressionResult.cs`
- Delete: `src/Seeing.Session/Compression/Strategies/*`（`SlidingWindowCompression.cs`/`SlidingWindowTokenStrategy.cs`/`SummarizingStrategy.cs`/`HybridStrategy.cs`，以实际文件为准）
- Delete: `src/Seeing.Session/Compression/`（`ICompressionStrategy.cs`、`ISummarizer.cs`（旧 WebUI 用）、`SessionCompressor.cs`、`TokenCounterHelper.cs`（internal，改用 `ITokenCounter`）等旧压缩实现）
- Test: `tests/Seeing.Session.Tests/Management/SessionManagerTests.cs`（适配）
- Delete: `tests/Seeing.Session.Tests/Compression/*`（旧策略测试）

- [ ] **Step 1: `SessionManager` 移除 `_compressor`**

- 删除字段 `private readonly ICompressionStrategy? _compressor;`（第 25 行）
- 构造函数参数 `ICompressionStrategy? compressor = null` 删除，`_compressor = compressor ?? new SlidingWindowCompression();`（第 69 行）删除
- `Compress(string id)` 方法（第 300-…行）重构为不依赖 `_compressor`——直接返回 `session.Messages` 并记录日志"压缩已移交主库 CompressionService，此处不再执行"，或整方法标记 `[Obsolete]`。**选择：保留签名、内部移除压缩逻辑，返回原消息。**
- `HookPoints.Compressed` 触发块（第 328-336 行）随压缩逻辑一并删除（`HookPoints.Compressed` 常量若仅供此用，也删；压缩事件统一由主库 `CompactionEvents` 承担）
- 删除 `using Seeing.Session.Compression;`（若仅用于 `_compressor`）
- 更新构造函数 XML doc（删 `compressor` 参数说明）

Run: `dotnet build src/Seeing.Session/Seeing.Session.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 2: `SessionServiceExtensions` 移除 compressor 参数**

第 63 行 `compressor: sp.GetService<ICompressionStrategy>(),` 删除。

Run: `dotnet build src/Seeing.Session/Seeing.Session.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 3: `TokenBudgetConfig` 删 `CompactionStrategy`**

`src/Seeing.Session/Core/Budget/TokenBudgetConfig.cs` 第 30 行 `CompactionStrategy` 属性删除；保留 `SlidingWindowKeepTokens`（36 行）/`SummaryTargetTokens`（42 行）作为主库 `CompressionOptions` 的来源。`CompactionStrategyType` 枚举（若在 Seeing.Session.Core）一并删除。

Run: `dotnet build src/Seeing.Session/Seeing.Session.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 4: 删除旧压缩实现与测试**

- 删除 `src/Seeing.Session/Compression/` 下旧策略与 `TokenCounterHelper.cs`（`TokenCounterHelper` 为 internal，若被外部引用改用 `ITokenCounter`；删除后构建确认无残留引用）
- 删除 `tests/Seeing.Session.Tests/Compression/` 旧策略测试
- 适配 `SessionManagerTests.cs:441-496`：删除/改写 `Mock<ICompressionStrategy>` 相关用例（`Compress` 不再压缩，断言返回原消息）

Run: `dotnet build src/Seeing.Session/Seeing.Session.csproj && dotnet test tests/Seeing.Session.Tests`
Expected: BUILD SUCCEEDED；TEST PASS。

- [ ] **Step 5: 提交**

```bash
git add -A src/Seeing.Session tests/Seeing.Session.Tests
git commit -m "refactor: SessionManager 去压缩职责，删原语层旧压缩策略，压缩移交主库 CompressionService"
```

---

## 任务 6：TokenBudget 收敛（只决策 + 门控）

**Files:**
- Modify: `src/Seeing.Agent.TokenBudget/Extensions/TokenBudgetServiceExtensions.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Api/TokenBudgetApi.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Api/ITokenBudgetApi.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Hooks/BudgetUpdateHook.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Configuration/GlobalTokenBudgetOptions.cs`
- Modify: `src/Seeing.Agent.TokenBudget/Events/BudgetEvents.cs`（删旧 `CompactionEvent`）
- Delete: `src/Seeing.Agent.TokenBudget/Services/CompressionService.cs`
- Delete: `src/Seeing.Agent.TokenBudget/Services/ICompressionService.cs`
- Delete: `src/Seeing.Agent.TokenBudget/Services/CompressionStrategyFactory.cs`
- Delete: `src/Seeing.Agent.TokenBudget/Services/ICompressionStrategyFactory.cs`
- Delete: `src/Seeing.Agent.TokenBudget/Hooks/BudgetCheckHook.cs`
- Delete: TokenBudget 旧压缩死代码：`ICompressionTrigger`/`DefaultCompressionTrigger`/`CompressionDecision`/`CompressionReason`/`CompactionFailedException`（以实际位置为准）

**审查修正说明：**
- **H1**：`TokenBudgetApi.GetBudgetStatusAsync`/`GetEffectiveConfigAsync` 仍依赖 `_configResolver`——**`TokenBudgetConfigResolver` 保留**，不删除（只从计划删除清单移除）。
- **H2**：`CompactionStrategyType` 类型删除后 `ITokenBudgetApi.TriggerCompactionAsync(string, CompactionStrategyType?)` 无法保留参数——因无外部调用方，**直接改无参** `TriggerCompactionAsync(string sessionId)`，彻底删 `CompactionStrategyType`。
- **M3**：`GlobalTokenBudgetOptions` 不整文件删除（其 `DefaultConfig` 含 `AutoCompactionEnabled` 默认值来源，且 `TokenBudgetServiceExtensions` 有 `services.Configure<GlobalTokenBudgetOptions>`）——**只删其 `CompactionStrategy` 属性**。
- **M6**：`CompressionService` 只在主库 `AddSeeingAgent` 注册（任务 9），TokenBudget 扩展**不重复注册**（WebUI 同时调 `AddSeeingAgent` + `AddTokenBudgetIntegration`）。

- [ ] **Step 1: `TokenBudgetServiceExtensions` 删除压缩注册**

删除第 49-52 行（`SlidingWindowTokenStrategy` + `ICompressionStrategy` 别名）、第 77 行（`ICompressionStrategyFactory`）、第 78 行（`ICompressionService`）。保留 `ITokenBudgetApi`（第 57 行）、`ITokenBudgetManager`、`TokenBudgetConfigResolver`、`GlobalTokenBudgetOptions` 配置。**不新增 `CompressionService` 注册**（主库统一注册，见 M6）。

Run: `dotnet build src/Seeing.Agent.TokenBudget/Seeing.Agent.TokenBudget.csproj`
Expected: BUILD SUCCEEDED（若旧文件仍被引用则先删）。

- [ ] **Step 2: 删除旧压缩文件与死代码**

删除列表中的压缩服务文件、`BudgetCheckHook.cs`、死代码类型（`ICompressionTrigger`/`DefaultCompressionTrigger`/`CompressionDecision`/`CompressionReason`/`CompactionFailedException`）。删除后重新构建，修残留引用（`using Seeing.Agent.TokenBudget.Compression` 等）。

Run: `dotnet build src/Seeing.Agent.TokenBudget/Seeing.Agent.TokenBudget.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 3: `TokenBudgetApi` 改走主库**

- 构造函数删 `ICompressionStrategy _compressionStrategy`，**保留** `ITokenBudgetConfigResolver _configResolver` 与 `ITokenCounter _tokenCounter`（`GetBreakdownAsync`/`GetBudgetStatusAsync`/`GetEffectiveConfigAsync` 仍用），注入主库 `CompressionService`。
- `TriggerCompactionAsync` 改为无参：

```csharp
public async Task<CompactionResponse> TriggerCompactionAsync(string sessionId)
{
    var session = await GetSessionOrThrowAsync(sessionId);
    var breakdownBefore = _budgetManager.CalculateBreakdown(session);

    var outcome = await _compressionService.CompressAsync(sessionId, reason: "api");

    return new CompactionResponse
    {
        Success = outcome.Success,
        TokensBefore = outcome.TokensBefore,
        TokensAfter = outcome.TokensAfter,
        MessagesRemoved = outcome.MessagesRemoved,
        StrategyUsed = outcome.Strategy,
        ErrorMessage = outcome.ErrorMessage
    };
}
```

`ITokenBudgetApi` 接口同步改签名（删 `CompactionStrategyType? strategy = null` 参数）。删除第 100 行 `StrategyUsed = effectiveStrategy.ToString()` 相关分支——若 `CompactionStrategyType` 类型删除导致残留引用，一并清理。

Run: `dotnet build src/Seeing.Agent.TokenBudget/Seeing.Agent.TokenBudget.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 4: `BudgetUpdateHook` 收敛 + 删旧 `CompactionEvent`**

- `BudgetUpdateHook.cs` 第 166-184 行旧 `CompactionEvent` 发布块删除；第 109 行 `PendingCompaction` 标记保留（作为"仅决策、不执行"信号）。
- `BudgetEvents.cs` 第 36-60 行旧 `CompactionEvent` record 删除（全库无读取方，`MessageEventType.Compaction` 常量在子项目②任务 2 已删）。
- `GlobalTokenBudgetOptions.cs` 只删 `CompactionStrategy` 属性，其余保留。

Run: `dotnet build src/Seeing.Agent.TokenBudget/Seeing.Agent.TokenBudget.csproj`
Expected: BUILD SUCCEEDED。

- [ ] **Step 5: 提交**

```bash
git add -A src/Seeing.Agent.TokenBudget
git commit -m "refactor: TokenBudget 收敛——只决策+门控，压缩移交主库 CompressionService"
```

---

## 任务 7：`ExecutionJobService` 门控 + 自动压缩 + 命令事件修复

**Files:**
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs`
- Modify: `src/Seeing.Agent.App/Events/AppEvents.cs`（`CommandResultEvent` 补 `NeedsRefresh`）
- Test: `tests/Seeing.Agent.Tests/Execution/ExecutionJobServiceCompactionTests.cs`（新建）

- [ ] **Step 1: `CommandResultEvent` 补 `NeedsRefresh` 字段**

`AppEvents.cs` 的 `CommandResultEvent`（现有为 non-positional record，字段含 `Message`，`NavigationTarget` 已存在——**保持形状，仅新增 `bool NeedsRefresh`**，审查 M1：重写为 positional record 会破坏 `ExecutionJobService.cs:936` 与 `GatewayEventMapper.cs:167` 消费方）：

```csharp
public record CommandResultEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type => MessageEventType.CommandResult;

    /// <summary>命令名称</summary>
    public required string CommandName { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>结果消息</summary>
    public string? Message { get; init; }

    /// <summary>导航目标（可选）</summary>
    public string? NavigationTarget { get; init; }

    /// <summary>是否需要前端刷新时间线（压缩等变更会话内容的命令）</summary>
    public bool NeedsRefresh { get; init; }
}
```

- [ ] **Step 2: `ExecutionJobService` 门控检查 + 自动压缩**

在 Agent 循环执行前（含命令处理前）检查门控。**审查 H3**：`session.BudgetConfig` 通常为 null（仅 `UpdateConfigAsync` 才非 null），不能作为门控源；改用 `ExecutionJobService` 已持有的 `_seeingAgentOptions`（`IOptionsMonitor<SeeingAgentOptions>`，见 `ExecutionJobService.cs:43,66`）+ `PendingCompaction` 标记：

```csharp
// 自动压缩门控：TokenBudget 标记 + 配置开启时，执行前触发压缩
var autoCompaction = _seeingAgentOptions.CurrentValue.TokenBudget?.AutoCompactionEnabled == true;
if (autoCompaction &&
    session.PendingCompaction &&
    session.Messages.Count > 0)
{
    var outcome = await _compressionService.CompressAsync(session.Id, reason: "auto");
    await PublishCompactionEventsAsync(session.Id, outcome);
    session.PendingCompaction = false;
}
```

（**审查 M4（环内保护）**：原 `BudgetCheckHook` 在每次 LLM 调用前（`ChatBeforeStart`）检查，含单次执行的多轮工具循环。门控移到"新一轮执行开始"后，同一次 agent 循环内工具调用后可能中途溢出。取舍：本计划按 spec 决策接受此差异——将门控放在**每轮 Agent 循环开始前**（而非仅执行入口一次），尽量逼近原保护粒度。`PublishCompactionEventsAsync` 发布 `CompactionStartedEvent`/`CompactionCompletedEvent`/`CompactionFailedEvent`。）

- [ ] **Step 3: 命令链路填充 `NeedsRefresh`**

在命令处理分支（`ProcessCommandAsync` 内，`ExecutionJobService.cs` 约 929-939 行 `ShouldContinue==false` 分支），从 `CommandResult` 填充事件字段：

```csharp
// 现有：result.ShouldContinue == false 时构造 CommandResultEvent
return new CommandResultEvent
{
    SessionId = session.Id,
    CommandName = result.CommandName ?? context.Command,   // 以实际字段为准
    Success = result.Success,
    Message = result.Message ?? result.ErrorMessage,
    NavigationTarget = result.GetNavigationTarget(),      // CommandResultExtensions.cs:50
    NeedsRefresh = result.NeedsRefresh                    // CommandResult.NeedsRefresh，ICommand.cs:166
};
```

（`NeedsRefresh` 来自 `CommandResult.NeedsRefresh`（ICommand.cs:166），由 `/compact` 命令返回 `CommandResult.Ok(message, needsRefresh: true, shouldContinue: false)`（ICommand.cs:179）填充——见任务 8。`NavigationTarget` 来自 `WithNavigation` 元数据。）

- [ ] **Step 4: 测试 + 提交**

新建测试验证：
1. `PendingCompaction == true` 且 `TokenBudget.AutoCompactionEnabled == true` 时调用 `CompressionService.CompressAsync`。
2. 压缩完成发布 `CompactionCompletedEvent`。

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~ExecutionJobServiceCompactionTests"`
```bash
git add src/Seeing.Agent.App tests/Seeing.Agent.Tests/Execution
git commit -m "feat: ExecutionJobService 自动压缩门控 + 命令事件 NeedsRefresh 填充"
```

---

## 任务 8：`/compact` 命令修复（shouldContinue:false + 走主库 + 事件）

**Files:**
- Modify: `src/Seeing.Agent.App/Commands/BuiltIn/BuiltInCommands.cs`（`CompactHistory` 方法）
- Test: `tests/Seeing.Agent.Tests/Commands/CompactHistoryCommandTests.cs`（新建）

**现状核对（审查 M2）：** `CompactHistory` 是 `[Command]` 注解方法（`BuiltInCommands.cs:164-206`），返回 `CommandResult`。第 205 行已带 `needsRefresh: true` 但**未传 `shouldContinue: false`**——`CommandResult.Ok` 默认 `shouldContinue: true`（ICommand.cs:179），导致 `ProcessCommandAsync` 继续执行 Agent 且事件链路不通。`CommandContext` 无 `ShouldContinue` 属性（ICommand.cs:109-146），正确修法是在返回的 `CommandResult` 中设 `shouldContinue: false`。当前实现直改 `session.Messages.RemoveRange`，需改走主库 `CompressionService`。

- [ ] **Step 1: 写测试（TDD 红）**

```csharp
using FluentAssertions;
using Seeing.Agent.Compression;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Session.Core;
using Moq;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

public class CompactHistoryCommandTests
{
    [Fact]
    public async Task CompactHistory_ShouldReturnNeedsRefreshAndNotContinue()
    {
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("a"));
        session.Messages.Add(SessionMessage.AssistantMessage("b"));

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns(session);

        var compression = new Mock<CompressionService>(null, sessionManager.Object, new CompressionOptions());
        compression.Setup(c => c.CompressAsync(session.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompressionOutcome { Success = true, TokensBefore = 100, TokensAfter = 50, MessagesRemoved = 1 });

        var commands = new BuiltInCommands(compression.Object, sessionManager.Object);

        var result = await commands.CompactHistory(new CommandContext
        {
            SessionId = session.Id,
            Arguments = ""
        });

        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue();
        result.ShouldContinue.Should().BeFalse();
    }
}
```

（`BuiltInCommands` 构造函数签名以实际为准——执行时核对 `BuiltInCommands.cs` 构造注入，可能需注入 `CompressionService` 与 `ISessionManager`。若构造函数字段未暴露，改用反射或调整注入。**执行说明**：`BuiltInCommands` 已注入 `ISessionManager`（第 188 行使用 `_sessionManager`）；新增 `CompressionService` 注入。）

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CompactHistoryCommandTests"`
Expected: 失败（`ShouldContinue` 为 true）。

- [ ] **Step 2: 修复命令**

`BuiltInCommands.CompactHistory` 改为：

```csharp
public async Task<CommandResult> CompactHistory(CommandContext context, CancellationToken ct = default)
{
    if (string.IsNullOrEmpty(context.SessionId))
    {
        return CommandResult.Fail("No active session");
    }

    // 解析保留数量
    var keepCount = 10;
    if (!string.IsNullOrWhiteSpace(context.Arguments))
    {
        if (int.TryParse(context.Arguments.Trim(), out var parsed) && parsed > 0)
        {
            keepCount = parsed;
        }
    }

    var outcome = await _compressionService.CompressAsync(
        context.SessionId,
        reason: "manual",
        cancellationToken: ct);

    if (!outcome.Success)
    {
        return CommandResult.Fail(outcome.ErrorMessage ?? "压缩失败");
    }

    // 根因修复：shouldContinue:false 终止 Agent 循环（ICommand.cs:179 默认 true）
    return CommandResult.Ok(
        $"Compacted history: removed {outcome.MessagesRemoved} messages",
        needsRefresh: true,
        shouldContinue: false);
}
```

（压缩执行与事件发布由 `CompressionService` + `ExecutionJobService` 协作完成；命令只负责设 `shouldContinue: false` 与 `needsRefresh: true`。）

- [ ] **Step 3: 测试通过 + 提交**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~CompactHistoryCommandTests"`
```bash
git add src/Seeing.Agent.App/Commands/BuiltIn/BuiltInCommands.cs tests/Seeing.Agent.Tests/Commands
git commit -m "fix: /compact 命令设 shouldContinue:false + 走主库 CompressionService"
```

---

## 任务 9：主库 DI 注册清理（SeeingAgentOptions + ServiceCollectionExtensions）

**Files:**
- Modify: `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 删 `SeeingAgentOptions.CompactionStrategy`**

`src/Seeing.Agent/Configuration/SeeingAgentOptions.cs` 中 `CompactionStrategy` 属性删除。

- [ ] **Step 2: 清理压缩策略注册 + 注册压缩组件**

`ServiceCollectionExtensions.cs` 约第 401 行的压缩策略注册（`compressor: sp.GetService<ICompressionStrategy>()` 等）删除。新增（**M6：此处为主库唯一注册点，TokenBudget 扩展不重复注册**）：

```csharp
services.AddSingleton<ISummarizer, LlmSummarizer>();
services.AddSingleton<CompressionService>();
services.AddSingleton<CompressionOptions>();
```

（`LlmSummarizer` 依赖 `ITextCompletion`——已注册于 `ServiceCollectionExtensions.cs:690`，无需额外注册。`CompressionService` 依赖 `ISessionManager`（已注册）与 `ISummarizer`。）

- [ ] **Step 3: 构建 + 提交**

Run: `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
```bash
git add src/Seeing.Agent
git commit -m "refactor: 主库 DI 注册压缩组件，删旧策略注册"
```

---

## 任务 10：WebUI 清理 + 前端压缩事件处理

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`
- Modify: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/Token/ChatTokenStatusBar.razor`（**注意：在 `Components/Token/`，非 `Pages/`**，审查 M5）
- Delete: `samples/Seeing.Agent.WebUI/Services/SessionCompactionService.cs`
- Delete: `samples/Seeing.Agent.WebUI/Services/LlmSummarizer.cs`（旧 `ISummarizer` 实现）

- [ ] **Step 1: Program.cs 清理**

删除第 42-44 行（旧 `ISummarizer`/`SummarizingStrategy`/`HybridStrategy`）、第 71 行（`SessionCompactionService`）。压缩组件由主库 `AddSeeingAgent()` 注册（任务 9），WebUI 不重复注册。

- [ ] **Step 2: 删除旧服务文件**

删除 `SessionCompactionService.cs`、`LlmSummarizer.cs`（旧 `ISummarizer` 实现）。

- [ ] **Step 3: `EventStreamHandler` 处理压缩事件**

`HandleCompaction`（或现有 switch）新增分支处理字符串类型 `compaction.started/delta/completed/failed`：

```csharp
case CompactionEventTypes.Completed:
    // 时间线刷新：会话已变更，请求重新加载
    break;
case CompactionEventTypes.Failed:
    // 显示错误提示
    break;
```

（子项目①已确认 `EventStreamHandler` 有 `HandleCompaction`；现改为 switch 字符串分支。）

- [ ] **Step 4: `Session.razor` 清理旧服务引用 + 消费命令结果**

**4a. 删除旧服务引用**：
- 第 40 行 `@inject Services.SessionCompactionService CompactionService` 删除。
- `CompactSessionAsync`（:1082）、`FallbackCompactAsync`（:1144，用将被删的 `SessionCompressor`）方法删除。
- `OnCompactRequested`（:1983）改为发送 `/compact` 命令（注入输入框并提交，或直调 `ChatOrchestrator` 命令通道）。
- 若 `LlmSummarizer`/`ISummarizer` 被 `Session.razor` 引用，一并删除。

**4b. `ConsumeEventStreamAsync` 消费命令结果**（约 :1522，现无 `CommandResultEvent`/compaction 分支，落入 else 增量子同步）：
- `case CommandResultEvent` 且 `NeedsRefresh == true` → 调用会话重载（时间线刷新）。
- `case CommandResultEvent` 且 `NavigationTarget != null` → 导航。
- `case CompactionEventTypes.Completed` → 时间线刷新。

- [ ] **Step 5: `ChatTokenStatusBar.razor` 按钮改发 `/compact`**

压缩按钮改为发送 `/compact` 命令（而非直调旧压缩服务）：

```razor
<Button OnClick="() => OnCompactRequested()">压缩</Button>
// OnCompactRequested 向输入框注入 "/compact" 并提交
```

（该组件现有压缩按钮逻辑若引用 `SessionCompactionService` 或 `CompactionService` 注入，同步改为事件回调。）

- [ ] **Step 6: 构建 + 提交**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
```bash
git add -A samples/Seeing.Agent.WebUI
git commit -m "refactor: WebUI 清理旧压缩服务，前端消费 compaction 事件"
```

---

## 任务 11：全量验证

- [ ] **Step 1: 全量构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: BUILD SUCCEEDED。

- [ ] **Step 2: 全量测试**

Run: `dotnet test Seeing.Agent.slnx`
Expected: TEST PASS。

- [ ] **Step 3: 手动冒烟（可选）**

Run: `dotnet run --project samples/Seeing.Agent.Cli`
输入 `/compact`，验证：Agent 循环终止、`CommandResultEvent(NeedsRefresh=true)` 发出、时间线刷新。

- [ ] **Step 4: 提交**

```bash
git add -A
git commit -m "feat: 压缩系统收敛完成（子项目②）"
```

---

## 验收标准

- 全库仅一个压缩执行入口：主库 `CompressionService`；唯一公共抽象 `ISummarizer`。
- 删除：TokenBudget `ICompressionService`/`CompressionStrategyFactory`/`ICompressionStrategyFactory`/`BudgetCheckHook`/旧 `CompactionEvent`/压缩死代码（`ICompressionTrigger`/`DefaultCompressionTrigger`/`CompressionDecision`/`CompressionReason`/`CompactionFailedException`）；原语层 `ICompressionStrategy`/`SlidingWindowCompression`/`SlidingWindowTokenStrategy`/`SummarizingStrategy`/`HybridStrategy`/`SessionCompressor`/`TokenCounterHelper`/旧 `ISummarizer`；`CompactionStrategyType` 枚举；WebUI `SessionCompactionService`/WebUI 旧 `LlmSummarizer`。
- 保留（仅删 `CompactionStrategy` 属性，审查 M3/H1）：`TokenBudgetConfigResolver`、`GlobalTokenBudgetOptions`、`ITokenBudgetApi.GetBreakdownAsync`/`GetBudgetStatusAsync`/`GetEffectiveConfigAsync`。
- `ITokenBudgetApi.TriggerCompactionAsync` 改无参（删 `CompactionStrategyType?`），内部走主库 `CompressionService`。
- TokenBudget 仅 `PendingCompaction` 门控标记；`ExecutionJobService` 每轮循环开始前检查门控（`_seeingAgentOptions.CurrentValue.TokenBudget.AutoCompactionEnabled` + `PendingCompaction`），调主库压缩。
- `/compact`：走主库 `CompressionService` + `CommandResult.Ok(..., needsRefresh: true, shouldContinue: false)` + `CommandResultEvent(NeedsRefresh=true)` + `compaction.*` 事件；前端 `ConsumeEventStreamAsync` 消费并刷新时间线。
- 无 LLM 时压缩失败提示明确（"未配置摘要器"）。
- 全量 `dotnet build` 与 `dotnet test` 通过。
