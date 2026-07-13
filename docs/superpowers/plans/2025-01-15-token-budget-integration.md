# Token 预算管理集成实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将已实现的 Token 预算管理模块集成到 AgentExecutor 执行流程中，实现自动预算检查、多策略压缩和 UI 实时更新。

**Architecture:** 通过 Hook 机制集成（BudgetCheckHook + BudgetUpdateHook），使用现有的 IMessageEvent 事件流通知 UI，配置通过 UnifiedConfigManager 统一管理。新增 SummarizingStrategy 和 HybridStrategy 压缩策略。

**Tech Stack:** C# .NET 10.0, ASP.NET Core, Blazor WebUI, xUnit, FluentAssertions, Moq

---

## File Structure

### 新增文件

```
src/
├── Seeing.Session/Compression/Strategies/
│   ├── SummarizingStrategy.cs              # LLM 摘要压缩策略
│   └── HybridStrategy.cs                   # 混合压缩策略
│
├── Seeing.TokenBudget/
│   ├── Services/
│   │   ├── ICompressionService.cs          # 压缩服务接口
│   │   ├── CompressionService.cs           # 压缩服务实现
│   │   └── ICompressionStrategyFactory.cs  # 策略工厂接口
│   │   └── CompressionStrategyFactory.cs   # 策略工厂实现
│   └── Hooks/
│       ├── BudgetCheckHook.cs              # 请求前检查 Hook
│       └── BudgetUpdateHook.cs             # 响应后更新 Hook
│
└── samples/Seeing.Agent.WebUI/Components/Token/
    └── ChatTokenStatusBar.razor            # 扩展：事件订阅
```

### 修改文件

```
src/Seeing.Agent/Core/Events/MessageEventTypes.cs    # 添加事件类型枚举
src/Seeing.Agent/Core/Events/BudgetEvents.cs         # 新增：预算相关事件
src/Seeing.Agent/Configuration/SeeingAgentOptions.cs # 添加 TokenBudgetOptions
src/Seeing.Agent/Configuration/UnifiedConfigManager.cs # 注册 TokenBudget 配置节
src/Seeing.TokenBudget/Extensions/TokenBudgetServiceExtensions.cs # 扩展 DI 注册
```

---

## Phase 1: 配置模型扩展

### Task 1.1: 添加 TokenBudgetOptions 配置类

**Files:**
- Modify: `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`

- [ ] **Step 1: 添加 TokenBudgetOptions 类**

在 `SeeingAgentOptions.cs` 文件末尾添加：

```csharp
/// <summary>
/// Token 预算配置选项
/// </summary>
public class TokenBudgetOptions
{
    /// <summary>默认最大上下文 Token 数</summary>
    public int? MaxContextTokens { get; set; }
    
    /// <summary>警告阈值</summary>
    public ThresholdOptions WarningThreshold { get; set; } = new() { Percentage = 80 };
    
    /// <summary>压缩阈值</summary>
    public ThresholdOptions CompactionThreshold { get; set; } = new() { Percentage = 90 };
    
    /// <summary>压缩策略类型</summary>
    public CompactionStrategyType CompactionStrategy { get; set; } = CompactionStrategyType.SlidingWindow;
    
    /// <summary>滑动窗口保留 Token 数</summary>
    public int SlidingWindowKeepTokens { get; set; } = 20000;
    
    /// <summary>摘要目标 Token 数</summary>
    public int SummaryTargetTokens { get; set; } = 4000;
    
    /// <summary>是否启用自动压缩</summary>
    public bool AutoCompactionEnabled { get; set; } = true;
}

/// <summary>
/// 阈值配置选项
/// </summary>
public class ThresholdOptions
{
    public int? Percentage { get; set; }
    public int? AbsoluteTokens { get; set; }
}
```

- [ ] **Step 2: 在 SeeingAgentOptions 中添加 TokenBudget 属性**

在 `SeeingAgentOptions` 类中添加：

```csharp
/// <summary>Token 预算全局配置</summary>
public TokenBudgetOptions TokenBudget { get; set; } = new();
```

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Configuration/SeeingAgentOptions.cs
git commit -m "feat(config): add TokenBudgetOptions to SeeingAgentOptions"
```

---

### Task 1.2: 注册 TokenBudget 配置节

**Files:**
- Modify: `src/Seeing.Agent/Configuration/UnifiedConfigManager.cs`

- [ ] **Step 1: 添加 using 语句**

在文件顶部添加：

```csharp
using Seeing.TokenBudget.Core;
```

- [ ] **Step 2: 在 BuildSectionRegistry 中添加配置节注册**

在 `BuildSectionRegistry()` 方法的字典初始化中添加：

```csharp
["TokenBudget"] = new("TokenBudget", "seeing.json", ConfigScope.Both, 
    typeof(TokenBudgetOptions), displayName: "Token 预算配置", displayOrder: 16),
```

- [ ] **Step 3: 在 GetFromSeeingAgent 方法中添加映射**

在 `GetFromSeeingAgent<T>` 方法的 switch 表达式中添加：

```csharp
case "TokenBudget" => SeeingAgent.TokenBudget as T,
```

- [ ] **Step 4: 在 UpdateSeeingAgentProperty 方法中添加更新逻辑**

在 `UpdateSeeingAgentProperty` 方法的 switch 语句中添加：

```csharp
case "TokenBudget":
    if (value is TokenBudgetOptions tokenBudget)
        SeeingAgent.TokenBudget = tokenBudget;
    break;
```

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Agent/Configuration/UnifiedConfigManager.cs
git commit -m "feat(config): register TokenBudget section in UnifiedConfigManager"
```

---

## Phase 2: 事件类型定义

### Task 2.1: 添加预算事件类型枚举

**Files:**
- Modify: `src/Seeing.Agent/Core/Events/MessageEventTypes.cs`

- [ ] **Step 1: 在 MessageEventType 枚举中添加新类型**

在 `MessageEventType` 枚举中添加：

```csharp
/// <summary>预算状态更新</summary>
BudgetStatus,

/// <summary>压缩执行</summary>
Compaction,

/// <summary>预算警告</summary>
BudgetWarning,
```

- [ ] **Step 2: 提交**

```bash
git add src/Seeing.Agent/Core/Events/MessageEventTypes.cs
git commit -m "feat(events): add BudgetStatus, Compaction, BudgetWarning event types"
```

---

### Task 2.2: 创建预算事件类

**Files:**
- Create: `src/Seeing.Agent/Core/Events/BudgetEvents.cs`

- [ ] **Step 1: 创建事件类文件**

```csharp
using Seeing.TokenBudget.Core;
using Seeing.TokenBudget.Api.Responses;

namespace Seeing.Agent.Core.Events;

/// <summary>
/// 预算状态更新事件 - 通知 UI 更新进度条
/// </summary>
public record BudgetStatusEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.BudgetStatus;

    /// <summary>当前 Token 数</summary>
    public int CurrentTokens { get; init; }

    /// <summary>最大 Token 数</summary>
    public int MaxTokens { get; init; }

    /// <summary>使用百分比</summary>
    public double UsagePercentage { get; init; }

    /// <summary>预算级别</summary>
    public BudgetLevel Level { get; init; }

    /// <summary>Token 分布详情</summary>
    public TokenBreakdownResponse? Breakdown { get; init; }
}

/// <summary>
/// 压缩执行事件 - 通知 UI 压缩结果
/// </summary>
public record CompactionEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.Compaction;

    /// <summary>使用的压缩策略</summary>
    public string Strategy { get; init; } = string.Empty;

    /// <summary>压缩前 Token 数</summary>
    public int TokensBefore { get; init; }

    /// <summary>压缩后 Token 数</summary>
    public int TokensAfter { get; init; }

    /// <summary>移除的消息数</summary>
    public int MessagesRemoved { get; init; }

    /// <summary>是否成功</summary>
    public bool Success { get; init; }

    /// <summary>错误信息（失败时）</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 预算警告事件 - 通知 UI 显示警告
/// </summary>
public record BudgetWarningEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public string? LoopId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public MessageEventType Type => MessageEventType.BudgetWarning;

    /// <summary>警告消息</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>预算级别</summary>
    public BudgetLevel Level { get; init; }
}
```

- [ ] **Step 2: 提交**

```bash
git add src/Seeing.Agent/Core/Events/BudgetEvents.cs
git commit -m "feat(events): add BudgetStatusEvent, CompactionEvent, BudgetWarningEvent"
```

---

## Phase 3: 压缩策略实现

### Task 3.1: 实现 SummarizingStrategy

**Files:**
- Create: `src/Seeing.Session/Compression/Strategies/SummarizingStrategy.cs`
- Test: `tests/Seeing.Session.Tests/Compression/SummarizingStrategyTests.cs`

- [ ] **Step 1: 创建测试文件**

```csharp
// tests/Seeing.Session.Tests/Compression/SummarizingStrategyTests.cs
using FluentAssertions;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Seeing.TokenBudget.Core;
using Xunit;

namespace Seeing.Session.Tests.Compression;

public class SummarizingStrategyTests
{
    private readonly Mock<ILlmService> _llmMock;
    private readonly Mock<ITokenCounter> _tokenCounterMock;
    private readonly SummarizingStrategy _strategy;

    public SummarizingStrategyTests()
    {
        _llmMock = new Mock<ILlmService>();
        _tokenCounterMock = new Mock<ITokenCounter>();
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(10);
        _strategy = new SummarizingStrategy(_llmMock.Object);
    }

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        _strategy.Name.Should().Be("summarizing");
    }

    [Fact]
    public void Compress_EmptyMessages_ReturnsEmptyResult()
    {
        var messages = Array.Empty<SessionMessage>();
        var config = new TokenBudgetConfig { SummaryTargetTokens = 1000 };

        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
    }

    [Fact]
    public void CompressByTokenBudget_PreservesFirstAndRecentMessages()
    {
        // Arrange
        var messages = new List<SessionMessage>
        {
            SessionMessage.SystemMessage("System prompt"),
            SessionMessage.UserMessage("User 1"),
            SessionMessage.AssistantMessage("Assistant 1"),
            SessionMessage.UserMessage("User 2"),
            SessionMessage.AssistantMessage("Assistant 2"),
        };
        var config = new TokenBudgetConfig { SummaryTargetTokens = 100 };

        // Setup LLM to return a summary
        SetupLlmSummary("Summary of conversation");

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().BeGreaterThan(0);
    }

    private void SetupLlmSummary(string summary)
    {
        // Setup mock to return summary
        _llmMock.Setup(x => x.CompleteAsync(
            It.IsAny<string>(),
            It.IsAny<ChatRequest>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse
            {
                Message = new ChatMessage { Content = summary }
            });
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.Session.Tests --filter "SummarizingStrategyTests"`
Expected: FAIL - SummarizingStrategy not found

- [ ] **Step 3: 实现 SummarizingStrategy**

```csharp
// src/Seeing.Session/Compression/Strategies/SummarizingStrategy.cs
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Seeing.TokenBudget.Core;

namespace Seeing.Session.Compression.Strategies;

/// <summary>
/// LLM 摘要压缩策略 - 使用 LLM 生成历史摘要
/// </summary>
public class SummarizingStrategy : ICompressionStrategy
{
    private readonly ILlmService _llmService;
    private readonly int _keepRecentMessages;

    /// <summary>
    /// 策略名称
    /// </summary>
    public string Name => "summarizing";

    /// <summary>
    /// 摘要提示词模板
    /// </summary>
    private const string SummaryPromptTemplate = @"请将以下对话历史压缩为简洁的摘要，保留关键信息：
- 用户的主要请求
- 助手完成的关键操作
- 重要的上下文信息（文件路径、决策结果等）

对话历史：
{messages}

摘要格式：
【用户请求】...
【已完成的操作】...
【重要上下文】...";

    /// <summary>
    /// 创建 SummarizingStrategy 实例
    /// </summary>
    /// <param name="llmService">LLM 服务</param>
    /// <param name="keepRecentMessages">保留最近 N 条消息（默认 4）</param>
    public SummarizingStrategy(ILlmService llmService, int keepRecentMessages = 4)
    {
        _llmService = llmService;
        _keepRecentMessages = keepRecentMessages;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
    {
        // 简单实现：保留第一条和最后 N 条
        if (messages.Count <= _keepRecentMessages + 1)
            return messages;

        var result = new List<SessionMessage> { messages[0] };
        var startIndex = messages.Count - _keepRecentMessages;
        for (var i = startIndex; i < messages.Count; i++)
        {
            result.Add(messages[i]);
        }
        return result;
    }

    /// <inheritdoc />
    public int EstimateRetainedCount(int messageCount)
    {
        if (messageCount <= _keepRecentMessages + 1)
            return messageCount;
        return 1 + _keepRecentMessages;
    }

    /// <inheritdoc />
    public CompressionResult CompressByTokenBudget(
        IReadOnlyList<SessionMessage> messages,
        TokenBudgetConfig config,
        ITokenCounter tokenCounter)
    {
        // 空消息处理
        if (messages.Count == 0)
        {
            return CompressionResult.Succeeded(0, 0, 0, Array.Empty<SessionMessage>());
        }

        var tokensBefore = CountTokens(messages, tokenCounter);
        var targetTokens = config.SummaryTargetTokens;

        // 无需压缩
        if (tokensBefore <= targetTokens)
        {
            return CompressionResult.Succeeded(tokensBefore, tokensBefore, 0, messages);
        }

        // 消息太少，无法压缩
        if (messages.Count <= _keepRecentMessages + 1)
        {
            return CompressionResult.Succeeded(tokensBefore, tokensBefore, 0, messages);
        }

        try
        {
            // 构建摘要请求
            var toSummarize = messages.Skip(1).Take(messages.Count - _keepRecentMessages - 1).ToList();
            var summaryContent = GenerateSummaryAsync(toSummarize).GetAwaiter().GetResult();

            // 构建压缩后的消息列表
            var result = new List<SessionMessage> { messages[0] }; // 保留系统提示

            // 添加摘要消息
            result.Add(SessionMessage.SystemMessage($"[对话摘要]\n{summaryContent}"));

            // 添加最近的消息
            for (var i = messages.Count - _keepRecentMessages; i < messages.Count; i++)
            {
                result.Add(messages[i]);
            }

            var tokensAfter = CountTokens(result, tokenCounter);

            return CompressionResult.Succeeded(
                tokensBefore,
                tokensAfter,
                messages.Count - result.Count,
                result);
        }
        catch (Exception ex)
        {
            return CompressionResult.Failed(tokensBefore, ex.Message);
        }
    }

    /// <summary>
    /// 使用 LLM 生成摘要
    /// </summary>
    private async Task<string> GenerateSummaryAsync(IReadOnlyList<SessionMessage> messages)
    {
        var historyText = string.Join("\n\n", messages.Select(m =>
            $"[{m.Role}]: {m.Content}"));

        var prompt = SummaryPromptTemplate.Replace("{messages}", historyText);

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                new() { Role = ChatRole.User, Content = prompt }
            },
            MaxTokens = 1000
        };

        var response = await _llmService.CompleteAsync(
            "default",
            request,
            "summary-generation",
            CancellationToken.None);

        return response.Message?.Content ?? "无法生成摘要";
    }

    private int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter counter)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += counter.Estimate(message.Content ?? string.Empty);
            if (!string.IsNullOrEmpty(message.ReasoningContent))
                total += counter.Estimate(message.ReasoningContent);
        }
        return total;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Session.Tests --filter "SummarizingStrategyTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Session/Compression/Strategies/SummarizingStrategy.cs tests/Seeing.Session.Tests/Compression/SummarizingStrategyTests.cs
git commit -m "feat(compression): add SummarizingStrategy for LLM-based compression"
```

---

### Task 3.2: 实现 HybridStrategy

**Files:**
- Create: `src/Seeing.Session/Compression/Strategies/HybridStrategy.cs`
- Test: `tests/Seeing.Session.Tests/Compression/HybridStrategyTests.cs`

- [ ] **Step 1: 创建测试文件**

```csharp
// tests/Seeing.Session.Tests/Compression/HybridStrategyTests.cs
using FluentAssertions;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Session.Compression.Strategies;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Seeing.TokenBudget.Core;
using Xunit;

namespace Seeing.Session.Tests.Compression;

public class HybridStrategyTests
{
    private readonly Mock<ITokenCounter> _tokenCounterMock;
    private readonly HybridStrategy _strategy;

    public HybridStrategyTests()
    {
        _tokenCounterMock = new Mock<ITokenCounter>();
        _tokenCounterMock.Setup(x => x.Estimate(It.IsAny<string>())).Returns(10);
        
        var slidingWindow = new SlidingWindowTokenStrategy(keepLastN: 2);
        var llmMock = new Mock<ILlmService>();
        var summarizing = new SummarizingStrategy(llmMock.Object, keepRecentMessages: 2);
        
        _strategy = new HybridStrategy(slidingWindow, summarizing);
    }

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        _strategy.Name.Should().Be("hybrid");
    }

    [Fact]
    public void CompressByTokenBudget_TriesSlidingWindowFirst()
    {
        // Arrange
        var messages = CreateMessages(10);
        var config = new TokenBudgetConfig
        {
            SlidingWindowKeepTokens = 100,
            SummaryTargetTokens = 50
        };

        // Act
        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        // Assert
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void CompressByTokenBudget_ReturnsEmptyResultForEmptyMessages()
    {
        var messages = Array.Empty<SessionMessage>();
        var config = new TokenBudgetConfig();

        var result = _strategy.CompressByTokenBudget(messages, config, _tokenCounterMock.Object);

        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
    }

    private static List<SessionMessage> CreateMessages(int count)
    {
        var messages = new List<SessionMessage> { SessionMessage.SystemMessage("System") };
        for (var i = 0; i < count - 1; i++)
        {
            messages.Add(i % 2 == 0
                ? SessionMessage.UserMessage($"User message {i}")
                : SessionMessage.AssistantMessage($"Assistant message {i}"));
        }
        return messages;
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.Session.Tests --filter "HybridStrategyTests"`
Expected: FAIL - HybridStrategy not found

- [ ] **Step 3: 实现 HybridStrategy**

```csharp
// src/Seeing.Session/Compression/Strategies/HybridStrategy.cs
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Seeing.TokenBudget.Core;

namespace Seeing.Session.Compression.Strategies;

/// <summary>
/// 混合压缩策略 - 组合滑动窗口和摘要策略
/// </summary>
public class HybridStrategy : ICompressionStrategy
{
    private readonly SlidingWindowTokenStrategy _slidingWindowStrategy;
    private readonly SummarizingStrategy _summarizingStrategy;

    /// <summary>
    /// 策略名称
    /// </summary>
    public string Name => "hybrid";

    /// <summary>
    /// 创建 HybridStrategy 实例
    /// </summary>
    /// <param name="slidingWindowStrategy">滑动窗口策略</param>
    /// <param name="summarizingStrategy">摘要策略</param>
    public HybridStrategy(
        SlidingWindowTokenStrategy slidingWindowStrategy,
        SummarizingStrategy summarizingStrategy)
    {
        _slidingWindowStrategy = slidingWindowStrategy;
        _summarizingStrategy = summarizingStrategy;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionMessage> Compress(IReadOnlyList<SessionMessage> messages)
    {
        // 默认使用滑动窗口
        return _slidingWindowStrategy.Compress(messages);
    }

    /// <inheritdoc />
    public int EstimateRetainedCount(int messageCount)
    {
        return _slidingWindowStrategy.EstimateRetainedCount(messageCount);
    }

    /// <inheritdoc />
    public CompressionResult CompressByTokenBudget(
        IReadOnlyList<SessionMessage> messages,
        TokenBudgetConfig config,
        ITokenCounter tokenCounter)
    {
        // 空消息处理
        if (messages.Count == 0)
        {
            return CompressionResult.Succeeded(0, 0, 0, Array.Empty<SessionMessage>());
        }

        var targetTokens = config.SlidingWindowKeepTokens;

        // 第一步：尝试滑动窗口压缩
        var slidingResult = _slidingWindowStrategy.CompressByTokenBudget(messages, config, tokenCounter);

        // 如果滑动窗口成功且达到目标，直接返回
        if (slidingResult.Success && slidingResult.CompressedMessages != null)
        {
            var tokensAfter = CountTokens(slidingResult.CompressedMessages, tokenCounter);
            if (tokensAfter <= targetTokens)
            {
                return slidingResult;
            }
        }

        // 第二步：尝试摘要压缩
        var summaryConfig = new TokenBudgetConfig
        {
            SummaryTargetTokens = config.SummaryTargetTokens,
            MaxContextTokens = config.MaxContextTokens
        };

        var summaryResult = _summarizingStrategy.CompressByTokenBudget(messages, summaryConfig, tokenCounter);

        // 如果摘要成功，返回摘要结果
        if (summaryResult.Success)
        {
            return summaryResult;
        }

        // 都失败了，返回滑动窗口结果（即使是部分成功）
        return slidingResult;
    }

    private int CountTokens(IReadOnlyList<SessionMessage> messages, ITokenCounter counter)
    {
        if (messages == null) return 0;
        var total = 0;
        foreach (var message in messages)
        {
            total += counter.Estimate(message.Content ?? string.Empty);
            if (!string.IsNullOrEmpty(message.ReasoningContent))
                total += counter.Estimate(message.ReasoningContent);
        }
        return total;
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/Seeing.Session.Tests --filter "HybridStrategyTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.Session/Compression/Strategies/HybridStrategy.cs tests/Seeing.Session.Tests/Compression/HybridStrategyTests.cs
git commit -m "feat(compression): add HybridStrategy combining sliding window and summarizing"
```

---

## Phase 4: 压缩服务和策略工厂

### Task 4.1: 创建 ICompressionService 和实现

**Files:**
- Create: `src/Seeing.TokenBudget/Services/ICompressionService.cs`
- Create: `src/Seeing.TokenBudget/Services/CompressionService.cs`
- Test: `tests/Seeing.TokenBudget.Tests/CompressionServiceTests.cs`

- [ ] **Step 1: 创建接口**

```csharp
// src/Seeing.TokenBudget/Services/ICompressionService.cs
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;

namespace Seeing.TokenBudget;

/// <summary>
/// 压缩服务接口 - 执行消息压缩
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// 执行压缩
    /// </summary>
    /// <param name="session">会话数据</param>
    /// <param name="agent">Agent 定义</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>压缩结果</returns>
    Task<CompressionResult> CompressAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: 创建测试文件**

```csharp
// tests/Seeing.TokenBudget.Tests/CompressionServiceTests.cs
using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Models;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.TokenBudget.Core;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class CompressionServiceTests
{
    private readonly Mock<ICompressionStrategyFactory> _factoryMock;
    private readonly Mock<ITokenBudgetConfigResolver> _resolverMock;
    private readonly Mock<ICompressionStrategy> _strategyMock;

    public CompressionServiceTests()
    {
        _factoryMock = new Mock<ICompressionStrategyFactory>();
        _resolverMock = new Mock<ITokenBudgetConfigResolver>();
        _strategyMock = new Mock<ICompressionStrategy>();
    }

    [Fact]
    public async Task CompressAsync_WithAutoCompactionDisabled_ReturnsNoCompression()
    {
        // Arrange
        var config = new TokenBudgetConfig { AutoCompactionEnabled = false };
        _resolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        var service = new CompressionService(_factoryMock.Object, _resolverMock.Object, null!);
        var session = new SessionData();
        var agent = new AgentDefinition();

        // Act
        var result = await service.CompressAsync(session, agent);

        // Assert
        result.Success.Should().BeTrue();
        result.MessagesRemoved.Should().Be(0);
    }
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "CompressionServiceTests"`
Expected: FAIL

- [ ] **Step 4: 创建策略工厂接口**

```csharp
// src/Seeing.TokenBudget/Services/ICompressionStrategyFactory.cs
using Seeing.Session.Compression;
using Seeing.TokenBudget.Core;

namespace Seeing.TokenBudget;

/// <summary>
/// 压缩策略工厂接口
/// </summary>
public interface ICompressionStrategyFactory
{
    /// <summary>
    /// 获取指定类型的压缩策略
    /// </summary>
    ICompressionStrategy GetStrategy(CompactionStrategyType type);
}
```

- [ ] **Step 5: 实现压缩服务**

```csharp
// src/Seeing.TokenBudget/Services/CompressionService.cs
using Seeing.Agent.Core.Models;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.TokenBudget.Core;
using Seeing.TokenEstimation;

namespace Seeing.TokenBudget;

/// <summary>
/// 压缩服务实现
/// </summary>
public class CompressionService : ICompressionService
{
    private readonly ICompressionStrategyFactory _strategyFactory;
    private readonly ITokenBudgetConfigResolver _configResolver;
    private readonly ITokenCounter _tokenCounter;

    public CompressionService(
        ICompressionStrategyFactory strategyFactory,
        ITokenBudgetConfigResolver configResolver,
        ITokenCounter tokenCounter)
    {
        _strategyFactory = strategyFactory;
        _configResolver = configResolver;
        _tokenCounter = tokenCounter;
    }

    public async Task<CompressionResult> CompressAsync(
        SessionData session,
        AgentDefinition agent,
        CancellationToken cancellationToken = default)
    {
        // 获取有效配置
        var config = _configResolver.Resolve(
            session.BudgetConfig,
            agent.BudgetConfig,
            null); // 全局配置由 resolver 内部处理

        // 检查是否启用自动压缩
        if (!config.AutoCompactionEnabled)
        {
            return CompressionResult.Succeeded(0, 0, 0, session.Messages.ToList());
        }

        // 获取策略
        var strategy = _strategyFactory.GetStrategy(config.CompactionStrategy);

        // 执行压缩
        var result = strategy.CompressByTokenBudget(session.Messages, config, _tokenCounter);

        // 如果压缩成功，更新会话消息
        if (result.Success && result.CompressedMessages != null)
        {
            session.Messages.Clear();
            foreach (var message in result.CompressedMessages)
            {
                session.Messages.Add(message);
            }
        }

        return result;
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "CompressionServiceTests"`
Expected: PASS

- [ ] **Step 7: 提交**

```bash
git add src/Seeing.TokenBudget/Services/ICompressionService.cs src/Seeing.TokenBudget/Services/CompressionService.cs src/Seeing.TokenBudget/Services/ICompressionStrategyFactory.cs tests/Seeing.TokenBudget.Tests/CompressionServiceTests.cs
git commit -m "feat(token-budget): add ICompressionService and CompressionService"
```

---

### Task 4.2: 实现策略工厂

**Files:**
- Create: `src/Seeing.TokenBudget/Services/CompressionStrategyFactory.cs`
- Test: `tests/Seeing.TokenBudget.Tests/CompressionStrategyFactoryTests.cs`

- [ ] **Step 1: 创建测试文件**

```csharp
// tests/Seeing.TokenBudget.Tests/CompressionStrategyFactoryTests.cs
using FluentAssertions;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Session.Compression.Strategies;
using Seeing.TokenBudget.Core;
using Xunit;

namespace Seeing.TokenBudget.Tests;

public class CompressionStrategyFactoryTests
{
    private readonly Mock<ILlmService> _llmMock = new();

    [Fact]
    public void GetStrategy_SlidingWindow_ReturnsSlidingWindowTokenStrategy()
    {
        var factory = CreateFactory();

        var strategy = factory.GetStrategy(CompactionStrategyType.SlidingWindow);

        strategy.Should().BeOfType<SlidingWindowTokenStrategy>();
    }

    [Fact]
    public void GetStrategy_Summarizing_ReturnsSummarizingStrategy()
    {
        var factory = CreateFactory();

        var strategy = factory.GetStrategy(CompactionStrategyType.Summarizing);

        strategy.Should().BeOfType<SummarizingStrategy>();
    }

    [Fact]
    public void GetStrategy_Hybrid_ReturnsHybridStrategy()
    {
        var factory = CreateFactory();

        var strategy = factory.GetStrategy(CompactionStrategyType.Hybrid);

        strategy.Should().BeOfType<HybridStrategy>();
    }

    private CompressionStrategyFactory CreateFactory()
    {
        var slidingWindow = new SlidingWindowTokenStrategy();
        var summarizing = new SummarizingStrategy(_llmMock.Object);
        var hybrid = new HybridStrategy(slidingWindow, summarizing);

        return new CompressionStrategyFactory(slidingWindow, summarizing, hybrid);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "CompressionStrategyFactoryTests"`
Expected: FAIL

- [ ] **Step 3: 实现策略工厂**

```csharp
// src/Seeing.TokenBudget/Services/CompressionStrategyFactory.cs
using Seeing.Session.Compression;
using Seeing.Session.Compression.Strategies;
using Seeing.TokenBudget.Core;

namespace Seeing.TokenBudget;

/// <summary>
/// 压缩策略工厂实现
/// </summary>
public class CompressionStrategyFactory : ICompressionStrategyFactory
{
    private readonly SlidingWindowTokenStrategy _slidingWindowStrategy;
    private readonly SummarizingStrategy _summarizingStrategy;
    private readonly HybridStrategy _hybridStrategy;

    public CompressionStrategyFactory(
        SlidingWindowTokenStrategy slidingWindowStrategy,
        SummarizingStrategy summarizingStrategy,
        HybridStrategy hybridStrategy)
    {
        _slidingWindowStrategy = slidingWindowStrategy;
        _summarizingStrategy = summarizingStrategy;
        _hybridStrategy = hybridStrategy;
    }

    public ICompressionStrategy GetStrategy(CompactionStrategyType type)
    {
        return type switch
        {
            CompactionStrategyType.SlidingWindow => _slidingWindowStrategy,
            CompactionStrategyType.Summarizing => _summarizingStrategy,
            CompactionStrategyType.Hybrid => _hybridStrategy,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown compression strategy type")
        };
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "CompressionStrategyFactoryTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.TokenBudget/Services/CompressionStrategyFactory.cs tests/Seeing.TokenBudget.Tests/CompressionStrategyFactoryTests.cs
git commit -m "feat(token-budget): add CompressionStrategyFactory"
```

---

## Phase 5: Hook 实现

### Task 5.1: 实现 BudgetCheckHook

**Files:**
- Create: `src/Seeing.TokenBudget/Hooks/BudgetCheckHook.cs`
- Test: `tests/Seeing.TokenBudget.Tests/Hooks/BudgetCheckHookTests.cs`

- [ ] **Step 1: 创建测试文件**

```csharp
// tests/Seeing.TokenBudget.Tests/Hooks/BudgetCheckHookTests.cs
using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.TokenBudget.Core;
using Xunit;

namespace Seeing.TokenBudget.Tests.Hooks;

public class BudgetCheckHookTests
{
    private readonly Mock<ICompressionService> _compressionServiceMock = new();
    private readonly Mock<ITokenBudgetConfigResolver> _configResolverMock = new();

    [Fact]
    public async Task ExecuteAsync_WhenPendingCompactionTrue_TriggersCompression()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(new TokenBudgetConfig { AutoCompactionEnabled = true });

        _compressionServiceMock.Setup(x => x.CompressAsync(It.IsAny<SessionData>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompressionResult.Succeeded(1000, 500, 5, new List<SessionMessage>()));

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();
        _compressionServiceMock.Verify(x => x.CompressAsync(It.IsAny<SessionData>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPendingCompactionFalse_SkipsCompression()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = false };
        var agent = new AgentDefinition();
        var payload = CreatePayload(session, agent);

        var hook = new BudgetCheckHook(_compressionServiceMock.Object, _configResolverMock.Object);

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        _compressionServiceMock.Verify(x => x.CompressAsync(It.IsAny<SessionData>(), It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static HookPayload CreatePayload(SessionData session, AgentDefinition agent)
    {
        return HookPayload.Blocking(
            HookRegistry.ChatBeforeStart,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "BudgetCheckHookTests"`
Expected: FAIL

- [ ] **Step 3: 实现 BudgetCheckHook**

```csharp
// src/Seeing.TokenBudget/Hooks/BudgetCheckHook.cs
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.TokenBudget.Core;

namespace Seeing.TokenBudget.Hooks;

/// <summary>
/// 预算检查 Hook - 在 LLM 请求前检查并执行待处理的压缩
/// </summary>
public class BudgetCheckHook : IHookHandler
{
    private readonly ICompressionService _compressionService;
    private readonly ITokenBudgetConfigResolver _configResolver;

    /// <summary>
    /// Hook 规格 - Chat 开始前
    /// </summary>
    public HookSpec Spec => HookRegistry.ChatBeforeStart;

    /// <summary>
    /// 优先级 - 高优先级，最先执行
    /// </summary>
    public int Priority => 100;

    public BudgetCheckHook(
        ICompressionService compressionService,
        ITokenBudgetConfigResolver configResolver)
    {
        _compressionService = compressionService;
        _configResolver = configResolver;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        var session = payload.GetInput<SessionData>("session");
        var agent = payload.GetInput<AgentDefinition>("agent");

        if (session == null || agent == null)
        {
            return HookResult.Success;
        }

        // 检查是否需要压缩
        if (!session.PendingCompaction)
        {
            return HookResult.Success;
        }

        // 检查是否启用自动压缩
        var config = _configResolver.Resolve(
            session.BudgetConfig,
            agent.BudgetConfig,
            null);

        if (!config.AutoCompactionEnabled)
        {
            session.PendingCompaction = false;
            return HookResult.Success;
        }

        try
        {
            // 执行压缩
            var result = await _compressionService.CompressAsync(session, agent, payload.CancellationToken);

            // 清除标记
            session.PendingCompaction = false;

            // 将压缩结果存入 Mutable，供后续事件使用
            payload.SetMutable("compactionResult", result);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            // 压缩失败不阻止执行
            payload.SetMutable("compactionError", ex.Message);
            return HookResult.Success;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "BudgetCheckHookTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.TokenBudget/Hooks/BudgetCheckHook.cs tests/Seeing.TokenBudget.Tests/Hooks/BudgetCheckHookTests.cs
git commit -m "feat(token-budget): add BudgetCheckHook for pre-request compression"
```

---

### Task 5.2: 实现 BudgetUpdateHook

**Files:**
- Create: `src/Seeing.TokenBudget/Hooks/BudgetUpdateHook.cs`
- Test: `tests/Seeing.TokenBudget.Tests/Hooks/BudgetUpdateHookTests.cs`

- [ ] **Step 1: 创建测试文件**

```csharp
// tests/Seeing.TokenBudget.Tests/Hooks/BudgetUpdateHookTests.cs
using FluentAssertions;
using Moq;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.TokenBudget.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.TokenBudget.Tests.Hooks;

public class BudgetUpdateHookTests
{
    private readonly Mock<ITokenBudgetManager> _budgetManagerMock = new();
    private readonly Mock<ITokenBudgetConfigResolver> _configResolverMock = new();

    [Fact]
    public async Task ExecuteAsync_WhenOverThreshold_SetsPendingCompaction()
    {
        // Arrange
        var session = new SessionData();
        session.Messages.Add(SessionMessage.UserMessage("Test message"));
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig
        {
            MaxContextTokens = 1000,
            WarningThreshold = new ThresholdConfig { Percentage = 80 },
            CompactionThreshold = new ThresholdConfig { Percentage = 90 }
        };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown { BySource = new SourceBreakdown { UserMessages = 500 } });

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Critical, CurrentTokens = 950, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, new CharBasedTokenCounter());

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNormal_DoesNotSetPendingCompaction()
    {
        // Arrange
        var session = new SessionData();
        var agent = new AgentDefinition();

        var config = new TokenBudgetConfig { MaxContextTokens = 1000 };

        _configResolverMock.Setup(x => x.Resolve(It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>(), It.IsAny<TokenBudgetConfig?>()))
            .Returns(config);

        _budgetManagerMock.Setup(x => x.CalculateBreakdown(It.IsAny<SessionData>(), It.IsAny<string?>(), It.IsAny<int?>()))
            .Returns(new TokenBreakdown());

        _budgetManagerMock.Setup(x => x.CheckBudget(It.IsAny<SessionData>(), It.IsAny<TokenBudgetConfig>(), It.IsAny<int>()))
            .Returns(new BudgetStatus { Level = BudgetLevel.Normal, CurrentTokens = 100, MaxTokens = 1000 });

        var payload = HookPayload.FireAndForget(
            HookRegistry.ChatAfterComplete,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        var hook = new BudgetUpdateHook(_budgetManagerMock.Object, _configResolverMock.Object, new CharBasedTokenCounter());

        // Act
        var result = await hook.ExecuteAsync(payload);

        // Assert
        result.Continue.Should().BeTrue();
        session.PendingCompaction.Should().BeFalse();
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "BudgetUpdateHookTests"`
Expected: FAIL

- [ ] **Step 3: 实现 BudgetUpdateHook**

```csharp
// src/Seeing.TokenBudget/Hooks/BudgetUpdateHook.cs
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.TokenBudget.Api.Responses;
using Seeing.TokenBudget.Core;
using Seeing.TokenEstimation;

namespace Seeing.TokenBudget.Hooks;

/// <summary>
/// 预算更新 Hook - 在 LLM 响应后更新预算状态
/// </summary>
public class BudgetUpdateHook : IHookHandler
{
    private readonly ITokenBudgetManager _budgetManager;
    private readonly ITokenBudgetConfigResolver _configResolver;
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    /// Hook 规格 - Chat 完成后
    /// </summary>
    public HookSpec Spec => HookRegistry.ChatAfterComplete;

    /// <summary>
    /// 优先级 - 默认
    /// </summary>
    public int Priority => 50;

    public BudgetUpdateHook(
        ITokenBudgetManager budgetManager,
        ITokenBudgetConfigResolver configResolver,
        ITokenCounter tokenCounter)
    {
        _budgetManager = budgetManager;
        _configResolver = configResolver;
        _tokenCounter = tokenCounter;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        var session = payload.GetInput<SessionData>("session");
        var agent = payload.GetInput<AgentDefinition>("agent");
        var systemPrompt = payload.GetInput<string>("systemPrompt");
        var toolTokens = payload.GetInput<int?>("toolTokens");

        if (session == null || agent == null)
        {
            return HookResult.Success;
        }

        try
        {
            // 计算当前 token 分布
            var breakdown = _budgetManager.CalculateBreakdown(session, systemPrompt, toolTokens);

            // 获取有效配置
            var config = _configResolver.Resolve(
                session.BudgetConfig,
                agent.BudgetConfig,
                null);

            // 检查预算状态
            var status = _budgetManager.CheckBudget(session, config, breakdown.Total);

            // 根据状态设置 PendingCompaction
            if (status.Level >= BudgetLevel.Critical)
            {
                session.PendingCompaction = true;
            }

            // 构建事件并存入 Mutable，供外部事件发射器使用
            var budgetEvent = new BudgetStatusEvent
            {
                SessionId = payload.SessionId,
                CurrentTokens = status.CurrentTokens,
                MaxTokens = status.MaxTokens,
                UsagePercentage = status.UsagePercentage,
                Level = status.Level,
                Breakdown = MapToResponse(breakdown)
            };

            payload.SetMutable("budgetStatusEvent", budgetEvent);

            // 如果有压缩结果，构建压缩事件
            if (payload.Mutable.TryGetValue("compactionResult", out var compactionResultObj) &&
                compactionResultObj is CompressionResult compactionResult)
            {
                var compactionEvent = new CompactionEvent
                {
                    SessionId = payload.SessionId,
                    Strategy = config.CompactionStrategy.ToString(),
                    TokensBefore = compactionResult.TokensBefore,
                    TokensAfter = compactionResult.TokensAfter,
                    MessagesRemoved = compactionResult.MessagesRemoved,
                    Success = compactionResult.Success
                };
                payload.SetMutable("compactionEvent", compactionEvent);
            }

            return HookResult.Success;
        }
        catch (Exception)
        {
            // 预算检查失败不阻止执行
            return HookResult.Success;
        }
    }

    private static TokenBreakdownResponse? MapToResponse(TokenBreakdown? breakdown)
    {
        if (breakdown == null) return null;

        return new TokenBreakdownResponse
        {
            TotalTokens = breakdown.Total,
            BySource = new SourceBreakdownData
            {
                SystemPrompt = new CategoryInfo { Tokens = breakdown.BySource.SystemPrompt },
                ToolDefinitions = new CategoryInfo { Tokens = breakdown.BySource.ToolDefinitions },
                UserMessages = new CategoryInfo { Tokens = breakdown.BySource.UserMessages },
                AssistantMessages = new CategoryInfo { Tokens = breakdown.BySource.AssistantMessages },
                ToolResults = new CategoryInfo { Tokens = breakdown.BySource.ToolResults }
            },
            ByRole = new RoleBreakdownData
            {
                System = new CategoryInfo { Tokens = breakdown.ByRole.System },
                User = new CategoryInfo { Tokens = breakdown.ByRole.User },
                Assistant = new CategoryInfo { Tokens = breakdown.ByRole.Assistant },
                Tool = new CategoryInfo { Tokens = breakdown.ByRole.Tool }
            }
        };
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "BudgetUpdateHookTests"`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/Seeing.TokenBudget/Hooks/BudgetUpdateHook.cs tests/Seeing.TokenBudget.Tests/Hooks/BudgetUpdateHookTests.cs
git commit -m "feat(token-budget): add BudgetUpdateHook for post-response budget tracking"
```

---

## Phase 6: DI 扩展

### Task 6.1: 扩展 TokenBudgetServiceExtensions

**Files:**
- Modify: `src/Seeing.TokenBudget/Extensions/TokenBudgetServiceExtensions.cs`

- [ ] **Step 1: 添加新服务注册**

在 `AddTokenBudgetManagement` 方法中添加：

```csharp
// 注册压缩策略
services.AddSingleton<SlidingWindowTokenStrategy>();
services.AddSingleton<SummarizingStrategy>();
services.AddSingleton<HybridStrategy>();
services.AddSingleton<ICompressionStrategyFactory, CompressionStrategyFactory>();

// 注册压缩服务
services.AddSingleton<ICompressionService, CompressionService>();

// 注册 Hooks
services.AddSingleton<BudgetCheckHook>();
services.AddSingleton<BudgetUpdateHook>();
```

- [ ] **Step 2: 添加 Hook 注册扩展方法**

```csharp
/// <summary>
/// 注册 Token 预算相关 Hook 到 IHookManager
/// </summary>
public static IServiceProvider UseTokenBudgetHooks(this IServiceProvider services)
{
    var hookManager = services.GetRequiredService<IHookManager>();
    
    var checkHook = services.GetRequiredService<BudgetCheckHook>();
    var updateHook = services.GetRequiredService<BudgetUpdateHook>();
    
    hookManager.Register(checkHook);
    hookManager.Register(updateHook);
    
    return services;
}
```

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.TokenBudget/Extensions/TokenBudgetServiceExtensions.cs
git commit -m "feat(token-budget): extend DI registration with strategies and hooks"
```

---

## Phase 7: 集成测试

### Task 7.1: 端到端集成测试

**Files:**
- Create: `tests/Seeing.TokenBudget.Tests/Integration/TokenBudgetHookIntegrationTests.cs`

- [ ] **Step 1: 创建集成测试**

```csharp
// tests/Seeing.TokenBudget.Tests/Integration/TokenBudgetHookIntegrationTests.cs
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.Session.Core.Budget;
using Seeing.TokenBudget;
using Seeing.TokenBudget.Core;
using Xunit;

namespace Seeing.TokenBudget.Tests.Integration;

public class TokenBudgetHookIntegrationTests : IClassFixture<TokenBudgetTestFixture>
{
    private readonly TokenBudgetTestFixture _fixture;

    public TokenBudgetHookIntegrationTests(TokenBudgetTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void AllServices_CanBeResolved()
    {
        var services = _fixture.Services;

        services.GetRequiredService<ICompressionService>().Should().NotBeNull();
        services.GetRequiredService<ICompressionStrategyFactory>().Should().NotBeNull();
        services.GetRequiredService<BudgetCheckHook>().Should().NotBeNull();
        services.GetRequiredService<BudgetUpdateHook>().Should().NotBeNull();
    }

    [Fact]
    public async Task FullFlow_PendingCompaction_TriggeredCorrectly()
    {
        // Arrange
        var session = new SessionData { PendingCompaction = true };
        var agent = new AgentDefinition();
        var hookManager = _fixture.Services.GetRequiredService<IHookManager>();

        // Act - 触发 before_start hook
        await hookManager.TriggerBlockingAsync(
            HookRegistry.ChatBeforeStart,
            "test-session",
            new Dictionary<string, object?>
            {
                ["session"] = session,
                ["agent"] = agent
            });

        // Assert
        session.PendingCompaction.Should().BeFalse();
    }
}

public class TokenBudgetTestFixture : IDisposable
{
    public IServiceProvider Services { get; }

    public TokenBudgetTestFixture()
    {
        var services = new ServiceCollection();
        
        // 注册必要的 mock
        services.AddSingleton<ITokenCounter, CharBasedTokenCounter>();
        services.AddSingleton<ITokenBudgetManager, TokenBudgetManager>();
        services.AddSingleton<ITokenBudgetConfigResolver, TokenBudgetConfigResolver>();
        
        // 注册压缩策略
        services.AddSingleton<SlidingWindowTokenStrategy>();
        services.AddSingleton<SummarizingStrategy>();
        services.AddSingleton<HybridStrategy>();
        services.AddSingleton<ICompressionStrategyFactory, CompressionStrategyFactory>();
        
        // 注册服务
        services.AddSingleton<ICompressionService, CompressionService>();
        
        // 注册 Hooks
        services.AddSingleton<BudgetCheckHook>();
        services.AddSingleton<BudgetUpdateHook>();
        
        // 注册 HookManager
        services.AddSingleton<IHookManager, HookManager>();
        
        Services = services.BuildServiceProvider();
    }

    public void Dispose() { }
}
```

- [ ] **Step 2: 运行集成测试**

Run: `dotnet test tests/Seeing.TokenBudget.Tests --filter "TokenBudgetHookIntegrationTests"`
Expected: PASS

- [ ] **Step 3: 提交**

```bash
git add tests/Seeing.TokenBudget.Tests/Integration/TokenBudgetHookIntegrationTests.cs
git commit -m "test(token-budget): add hook integration tests"
```

---

## Phase 8: UI 扩展（可选）

### Task 8.1: 扩展 ChatTokenStatusBar 事件订阅

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/Token/ChatTokenStatusBar.razor`

- [ ] **Step 1: 添加事件订阅参数**

```razor
@implements IAsyncDisposable

@code {
    // 现有代码...
    
    [Parameter] public IObservable<IMessageEvent>? EventStream { get; set; }
    
    private IDisposable? _eventSubscription;
    
    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        if (EventStream != null)
        {
            _eventSubscription = EventStream
                .Where(e => e.SessionId == SessionId)
                .Subscribe(OnEventReceived);
        }
    }
    
    private void OnEventReceived(IMessageEvent evt)
    {
        switch (evt)
        {
            case BudgetStatusEvent status:
                _currentTokens = status.CurrentTokens;
                _maxTokens = status.MaxTokens;
                _level = status.Level;
                InvokeAsync(StateHasChanged);
                break;
                
            case CompactionEvent compaction:
                ShowNotification($"压缩完成：移除 {compaction.MessagesRemoved} 条消息，节省 {compaction.TokensBefore - compaction.TokensAfter} tokens");
                break;
                
            case BudgetWarningEvent warning:
                ShowWarning(warning.Message);
                break;
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        await Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/Token/ChatTokenStatusBar.razor
git commit -m "feat(ui): add event stream subscription to ChatTokenStatusBar"
```

---

## 验收清单

- [ ] 所有单元测试通过：`dotnet test`
- [ ] 配置可通过 `seeing.json` 配置
- [ ] Hook 自动注册并可触发
- [ ] 压缩策略正确选择和执行
- [ ] 事件正确发射和接收
