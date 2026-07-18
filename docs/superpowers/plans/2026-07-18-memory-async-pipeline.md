# Memory Async Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Seeing.Agent.Memory 从「Hook 同步全文落盘 + 假 Embedding」改造为「异步管道：入队 → 启发式过滤 → LLM 抽取 → 轻量 session 索引 / daily 落盘 → 会话演化 → 可切换召回」，并提供 `memory.json` 配置与 WebUI 设置页。

**Architecture:** 对话热路径只 `TryEnqueue`；`MemoryPipelineWorker` 后台消费；未配置 Embedding 时禁用向量、仅 BM25；演化由 `MemoryEvolutionWorker` 在会话结束/空闲时触发。复用现有 FileStore / KeywordIndex / VectorIndex / Graph / 配额。

**Tech Stack:** .NET 10、xUnit + FluentAssertions、SQLite FTS5/sqlite-vec、`ILlmService`、Blazor + AntDesign、`UnifiedConfigManager`（`memory.json`）

**Spec:** `docs/superpowers/specs/2026-07-18-memory-pipeline-design.md`

## Global Constraints

- 记忆故障不得导致对话失败；Hook 不写盘、不调 LLM、不做 Embedding
- 禁止生产路径默认 `RandomEmbeddingService`；Embedding 未配置 → 禁用 VectorIndex
- `Extraction` 失败或关闭时不回退全文落盘
- 配置文件为 `.seeing/memory.json`（用户/项目双层级），与 `UnifiedConfigManager` 已注册的 `Memory` 节一致（规范中的 seeing.json 节名以本仓库既有 registry 为准）
- 测试命名：`{方法}_{场景}_Should{预期}`；私有字段 `_camelCase`
- 提交信息用英文 concise 风格；不 force-push；不改 git config

---

## File Structure

```
src/Seeing.Agent.Memory/
├── Configuration/
│   └── MemoryOptions.cs                 # 新建：完整 Options 树
├── Abstractions/
│   ├── IMemoryWorkQueue.cs              # 新建
│   ├── IMemoryHeuristicFilter.cs        # 新建
│   ├── IMemoryExtractor.cs              # 新建
│   ├── IMemoryPipeline.cs               # 新建
│   ├── IMemoryEvolutionService.cs       # 新建
│   ├── IMemoryRecallService.cs          # 新建
│   └── IEmbeddingStatus.cs              # 新建
├── Core/
│   ├── Models/MemoryCandidate.cs        # 新建
│   ├── Models/ExtractionResult.cs       # 新建
│   ├── Models/PipelineResult.cs         # 新建
│   ├── Queue/ChannelMemoryWorkQueue.cs  # 新建
│   ├── Filter/HeuristicMemoryFilter.cs  # 新建
│   ├── Evolution/LlmMemoryExtractor.cs  # 新建
│   ├── Evolution/LlmMemoryEvolution.cs  # 新建
│   ├── Evolution/PromptTemplates.cs     # 新建
│   ├── Pipeline/MemoryPipeline.cs       # 新建
│   ├── Recall/MemoryRecallService.cs    # 新建
│   ├── Embedding/NullEmbeddingService.cs# 新建（显式不可用）
│   ├── Embedding/ProviderEmbeddingService.cs # 新建（P4）
│   ├── Index/HybridMemoryIndex.cs       # 修改：Embedding 不可用时跳过向量
│   └── Embedding/RandomEmbeddingService.cs # 保留仅测试；DI 默认不再注册
├── Background/
│   ├── MemoryPipelineWorker.cs          # 新建
│   ├── MemoryEvolutionWorker.cs         # 新建
│   └── MemoryIndexingService.cs         # 修改：仅扫漏
├── Integration/
│   ├── MemoryHookHandler.cs             # 修改：只入队
│   ├── MemoryRecallHandler.cs           # 新建（P3）
│   └── MemoryTools.cs                   # 新建（P3）
└── Extensions/MemoryServiceExtensions.cs # 修改：注册全套服务

src/Seeing.Agent/Configuration/
└── UnifiedConfigManager.cs              # 修改：Memory 节 typeof(MemoryOptions) + Get/Set

samples/Seeing.Agent.WebUI/
├── Pages/MemorySettingsPage.razor       # 新建
├── Pages/MemoryPage.razor               # 修改：入口按钮
└── Program.cs                           # 按需微调注册

tests/Seeing.Agent.Memory.Tests/
├── Queue/ChannelMemoryWorkQueueTests.cs
├── Filter/HeuristicMemoryFilterTests.cs
├── Pipeline/MemoryPipelineTests.cs
├── Integration/ChatMemoryHandlerTests.cs
├── Evolution/LlmMemoryExtractorTests.cs
├── Evolution/MemoryEvolutionTests.cs
├── Recall/MemoryRecallServiceTests.cs
└── Embedding/EmbeddingStatusTests.cs
```

---

### Task 1: MemoryOptions + 配置接线

**Files:**
- Create: `src/Seeing.Agent.Memory/Configuration/MemoryOptions.cs`
- Modify: `src/Seeing.Agent/Configuration/UnifiedConfigManager.cs`（Memory 节类型与 Get/Set）
- Test: `tests/Seeing.Agent.Memory.Tests/Configuration/MemoryOptionsTests.cs`

**Interfaces:**
- Produces: `MemoryOptions` 及嵌套类；`IsEmbeddingConfigured` 属性

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Seeing.Agent.Memory.Configuration;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Configuration;

public class MemoryOptionsTests
{
    [Fact]
    public void IsEmbeddingConfigured_WhenProviderOrModelEmpty_ShouldBeFalse()
    {
        var options = new MemoryOptions();
        options.IsEmbeddingConfigured.Should().BeFalse();

        options.Embedding.Provider = "openai";
        options.IsEmbeddingConfigured.Should().BeFalse();

        options.Embedding.Model = "text-embedding-3-small";
        options.IsEmbeddingConfigured.Should().BeTrue();
    }

    [Fact]
    public void Defaults_ShouldMatchSpec()
    {
        var o = new MemoryOptions();
        o.Enabled.Should().BeTrue();
        o.Capture.AutoCapture.Should().BeTrue();
        o.Capture.MaxSnippetChars.Should().Be(4096);
        o.Capture.QueueCapacity.Should().Be(256);
        o.Extraction.MinImportance.Should().Be(0.5);
        o.Evolution.IdleMinutes.Should().Be(15);
        o.Retrieval.Mode.Should().Be(MemoryRetrievalMode.Both);
        o.Retrieval.InjectTimeoutMs.Should().Be(150);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Seeing.Agent.Memory.Tests --filter MemoryOptionsTests`
Expected: FAIL（类型不存在）

- [ ] **Step 3: Write MemoryOptions**

```csharp
namespace Seeing.Agent.Memory.Configuration;

public sealed class MemoryOptions
{
    public bool Enabled { get; set; } = true;
    public MemoryCaptureOptions Capture { get; set; } = new();
    public MemoryFilterOptions Filter { get; set; } = new();
    public MemoryExtractionOptions Extraction { get; set; } = new();
    public MemoryEvolutionOptions Evolution { get; set; } = new();
    public MemoryEmbeddingOptions Embedding { get; set; } = new();
    public MemoryRetrievalOptions Retrieval { get; set; } = new();
    public MemoryCostOptions Cost { get; set; } = new();

    public bool IsEmbeddingConfigured =>
        !string.IsNullOrWhiteSpace(Embedding.Provider)
        && !string.IsNullOrWhiteSpace(Embedding.Model);
}

public sealed class MemoryCaptureOptions
{
    public bool AutoCapture { get; set; } = true;
    public bool CaptureChat { get; set; } = true;
    public bool CaptureTools { get; set; } = true;
    public List<string> ToolAllowlist { get; set; } = new();
    public List<string> ToolBlocklist { get; set; } = new() { "list_dir", "glob", "grep" };
    public int MaxSnippetChars { get; set; } = 4096;
    public int QueueCapacity { get; set; } = 256;
}

public sealed class MemoryFilterOptions
{
    public int MinChars { get; set; } = 20;
    public List<string> AckPatterns { get; set; } = new()
    {
        @"^(好的|嗯|ok|okay|thanks|谢谢)[\s!。．.]*$"
    };
    public int NearDuplicateWindow { get; set; } = 32;
}

public sealed class MemoryExtractionOptions
{
    public bool Enabled { get; set; } = true;
    public double MinImportance { get; set; } = 0.5;
    public string? Model { get; set; }
    public int MaxCandidatesPerMinute { get; set; } = 30;
}

public sealed class MemoryEvolutionOptions
{
    public bool Enabled { get; set; } = true;
    public int IdleMinutes { get; set; } = 15;
    public bool OnSessionEnd { get; set; } = true;
    public double PromoteToDigestMinImportance { get; set; } = 0.8;
}

public sealed class MemoryEmbeddingOptions
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int? Dimensions { get; set; }
}

public enum MemoryRetrievalMode { AutoInject, ToolsOnly, Both }

public sealed class MemoryRetrievalOptions
{
    public MemoryRetrievalMode Mode { get; set; } = MemoryRetrievalMode.Both;
    public int TopK { get; set; } = 5;
    public int MaxInjectTokens { get; set; } = 800;
    public int InjectTimeoutMs { get; set; } = 150;
    public List<string> SearchTypes { get; set; } = new() { "daily", "digest" };
}

public sealed class MemoryCostOptions
{
    public long DailyTokenQuota { get; set; } = 1_000_000;
    public int RateLimitPerMinute { get; set; } = 60;
}
```

- [ ] **Step 4: Wire UnifiedConfigManager**

In `BuildSectionRegistry`, change Memory entry to `typeof(Seeing.Agent.Memory.Configuration.MemoryOptions)` **only if** core project can reference Memory — **若产生循环依赖则保持 `typeof(object)`，在 Memory 模块内用 `IOptionsMonitor<MemoryOptions>` + 自定义绑定，不改 core 类型注册。**

推荐避免循环依赖：

```csharp
// MemoryServiceExtensions.cs
services.Configure<MemoryOptions>(_ => { });
services.AddSingleton<IConfigureOptions<MemoryOptions>, MemoryOptionsConfigure>();
```

`MemoryOptionsConfigure` 从 `UnifiedConfigManager.GetSection<object>("Memory")` 或缓存反序列化 JSON 到 `MemoryOptions`。若 `GetSection` API 不便，在 Extensions 中：

```csharp
services.AddSingleton<IOptions<MemoryOptions>>(sp =>
{
    var mgr = sp.GetRequiredService<UnifiedConfigManager>();
    var raw = mgr.GetCachedSection("Memory"); // 若无此 API，用 LoadSection 现有方法
    // 实现：从 memory.json 反序列化；失败则 new MemoryOptions()
    return Options.Create(parsed);
});
```

实现时以仓库现有 `GetSection`/`SaveSectionAsync` 模式为准（对照 Scheduler 如何读 `scheduler.json`）。

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Seeing.Agent.Memory.Tests --filter MemoryOptionsTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Seeing.Agent.Memory/Configuration/MemoryOptions.cs tests/Seeing.Agent.Memory.Tests/Configuration/MemoryOptionsTests.cs src/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs
git commit -m "feat(memory): add MemoryOptions and defaults"
```

---

### Task 2: 有界工作队列

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IMemoryWorkQueue.cs`
- Create: `src/Seeing.Agent.Memory/Core/Models/MemoryCandidate.cs`
- Create: `src/Seeing.Agent.Memory/Core/Queue/ChannelMemoryWorkQueue.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Queue/ChannelMemoryWorkQueueTests.cs`

**Interfaces:**
- Produces:
  - `enum MemorySource { Chat, Tool }`
  - `record MemoryCandidate(string Id, string SessionId, string? AgentId, MemorySource Source, string? ToolId, string Snippet, DateTimeOffset CreatedAt)`
  - `IMemoryWorkQueue { bool TryEnqueue(MemoryCandidate); IAsyncEnumerable<MemoryCandidate> ReadAllAsync(CancellationToken); int Count { get; } }`

- [ ] **Step 1: Write failing tests**

```csharp
using FluentAssertions;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Queue;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Queue;

public class ChannelMemoryWorkQueueTests
{
    [Fact]
    public void TryEnqueue_WhenUnderCapacity_ShouldReturnTrueAndIncreaseCount()
    {
        var q = new ChannelMemoryWorkQueue(capacity: 2);
        var c = NewCandidate("a");
        q.TryEnqueue(c).Should().BeTrue();
        q.Count.Should().Be(1);
    }

    [Fact]
    public void TryEnqueue_WhenFull_ShouldReturnFalse()
    {
        var q = new ChannelMemoryWorkQueue(capacity: 1);
        q.TryEnqueue(NewCandidate("a")).Should().BeTrue();
        q.TryEnqueue(NewCandidate("b")).Should().BeFalse();
        q.Count.Should().Be(1);
    }

    private static MemoryCandidate NewCandidate(string id) =>
        new(id, "s1", null, MemorySource.Chat, null, "hello world content here", DateTimeOffset.UtcNow);
}
```

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test tests/Seeing.Agent.Memory.Tests --filter ChannelMemoryWorkQueueTests`

- [ ] **Step 3: Implement**

```csharp
// IMemoryWorkQueue.cs
namespace Seeing.Agent.Memory.Abstractions;

public interface IMemoryWorkQueue
{
    bool TryEnqueue(MemoryCandidate candidate);
    IAsyncEnumerable<MemoryCandidate> ReadAllAsync(CancellationToken ct);
    int Count { get; }
}
```

```csharp
// ChannelMemoryWorkQueue.cs
using System.Threading.Channels;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Core.Queue;

public sealed class ChannelMemoryWorkQueue : IMemoryWorkQueue
{
    private readonly Channel<MemoryCandidate> _channel;
    private int _count;

    public ChannelMemoryWorkQueue(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _channel = Channel.CreateBounded<MemoryCandidate>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite still returns false when full
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int Count => Volatile.Read(ref _count);

    public bool TryEnqueue(MemoryCandidate candidate)
    {
        if (!_channel.Writer.TryWrite(candidate))
            return false;
        Interlocked.Increment(ref _count);
        return true;
    }

    public async IAsyncEnumerable<MemoryCandidate> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _count);
            yield return item;
        }
    }
}
```

注：`BoundedChannelFullMode.Wait` 下 `TryWrite` 在满时返回 false（不阻塞）——实现后用测试确认；若行为不符，改为 `DropWrite` 或手动信号量计数。

- [ ] **Step 4: Run tests — expect PASS**

- [ ] **Step 5: Commit**

```bash
git commit -m "feat(memory): add bounded memory work queue"
```

---

### Task 3: 启发式过滤器

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IMemoryHeuristicFilter.cs`
- Create: `src/Seeing.Agent.Memory/Core/Filter/HeuristicMemoryFilter.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Filter/HeuristicMemoryFilterTests.cs`

**Interfaces:**
- Consumes: `MemoryOptions.Filter`, `MemoryCandidate`
- Produces: `record FilterDecision(bool Accepted, string? Reason)`；`IMemoryHeuristicFilter.Evaluate(...)`

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void Evaluate_TooShort_ShouldReject()
{
    var filter = new HeuristicMemoryFilter(Options.Create(new MemoryOptions
    {
        Filter = new MemoryFilterOptions { MinChars = 20 }
    }));
    var d = filter.Evaluate(new MemoryCandidate("1","s",null,MemorySource.Chat,null,"hi",DateTimeOffset.UtcNow));
    d.Accepted.Should().BeFalse();
    d.Reason.Should().Be("too_short");
}

[Fact]
public void Evaluate_AckOnly_ShouldReject()
{
    var filter = new HeuristicMemoryFilter(Options.Create(new MemoryOptions()));
    var d = filter.Evaluate(new MemoryCandidate("1","s",null,MemorySource.Chat,null,"好的",DateTimeOffset.UtcNow));
    d.Accepted.Should().BeFalse();
    d.Reason.Should().Be("ack");
}

[Fact]
public void Evaluate_SubstantialContent_ShouldAccept()
{
    var filter = new HeuristicMemoryFilter(Options.Create(new MemoryOptions()));
    var text = "用户偏好使用 PostgreSQL，并且要求所有 API 必须分页。";
    filter.Evaluate(new MemoryCandidate("1","s",null,MemorySource.Chat,null,text,DateTimeOffset.UtcNow))
        .Accepted.Should().BeTrue();
}
```

- [ ] **Step 2: Implement `HeuristicMemoryFilter`**

- 长度检查
- `AckPatterns` 用 `Regex`（Compiled | IgnoreCase | CultureInvariant）
- 近重复：维护最近 `NearDuplicateWindow` 条 snippet 的 FNV/哈希；相同则 `Reason=near_duplicate`
- Tool：若 `ToolId` 在 Blocklist（且 Allowlist 为空或未命中）→ `tool_blocked`

- [ ] **Step 3: Tests PASS + Commit**

```bash
git commit -m "feat(memory): add heuristic memory filter"
```

---

### Task 4: Hook 改为只入队

**Files:**
- Modify: `src/Seeing.Agent.Memory/Integration/MemoryHookHandler.cs`
- Modify: `src/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Integration/ChatMemoryHandlerEnqueueTests.cs`

**Interfaces:**
- Consumes: `IMemoryWorkQueue`, `IOptions<MemoryOptions>`
- Produces: Hook 行为变更（不再调用 `IMemoryService.SaveAsync`）

- [ ] **Step 1: Failing test with mocks**

```csharp
[Fact]
public async Task ExecuteAsync_WhenAutoCapture_ShouldEnqueueAndNotSave()
{
    var queue = new Mock<IMemoryWorkQueue>();
    queue.Setup(q => q.TryEnqueue(It.IsAny<MemoryCandidate>())).Returns(true);
    var memory = new Mock<IMemoryService>(MockBehavior.Strict); // SaveAsync 不得被调用

    var handler = new ChatMemoryHandler(queue.Object, Options.Create(new MemoryOptions()), memory.Object, NullLogger<ChatMemoryHandler>.Instance);

    var payload = /* 构造含 ChatAfterComplete Content 的 HookPayload */;
    var result = await handler.ExecuteAsync(payload);

    result.Should().Be(HookResult.Success);
    queue.Verify(q => q.TryEnqueue(It.Is<MemoryCandidate>(c =>
        c.Source == MemorySource.Chat && c.Snippet.Length > 0)), Times.Once);
}
```

构造 `HookPayload` 时对照现有 `HookDataContract.ChatAfterComplete` 与测试中其他 Hook 用法。

- [ ] **Step 2: Rewrite handlers**

```csharp
public async Task<HookResult> ExecuteAsync(HookPayload payload)
{
    try
    {
        var opts = _options.Value;
        if (!opts.Enabled || !opts.Capture.AutoCapture || !opts.Capture.CaptureChat)
            return HookResult.Success;

        var content = HookDataContract.ChatAfterComplete.Content.GetFrom(payload.Result);
        if (string.IsNullOrWhiteSpace(content))
            return HookResult.Success;

        var snippet = content.Length <= opts.Capture.MaxSnippetChars
            ? content
            : content[..opts.Capture.MaxSnippetChars];

        var candidate = new MemoryCandidate(
            Guid.NewGuid().ToString("N"),
            payload.SessionId ?? "unknown",
            payload.AgentId,
            MemorySource.Chat,
            null,
            snippet,
            DateTimeOffset.UtcNow);

        if (!_queue.TryEnqueue(candidate))
            _logger?.LogWarning("Memory queue full, dropping chat candidate Session={SessionId}", payload.SessionId);

        return HookResult.Success;
    }
    catch (Exception ex)
    {
        _logger?.LogError(ex, "ChatMemoryHandler failed");
        return HookResult.Success; // 仍不失败对话；或 FromError 但不抛
    }
}
```

`ToolMemoryHandler` 同样：检查 `CaptureTools`、Allow/Block list，入队 `MemorySource.Tool`。

规范：catch 后返回 `HookResult.Success`，保证对话不被记忆拖垮。

- [ ] **Step 3: DI — 注册 `IMemoryWorkQueue` 单例（容量来自 Options）**

- [ ] **Step 4: Tests PASS + Commit**

```bash
git commit -m "fix(memory): enqueue candidates from hooks instead of saving"
```

---

### Task 5: 禁用假 Embedding + Hybrid 门控

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IEmbeddingStatus.cs`
- Create: `src/Seeing.Agent.Memory/Core/Embedding/NullEmbeddingService.cs`
- Create: `src/Seeing.Agent.Memory/Core/Embedding/ConfigurableEmbeddingStatus.cs`
- Modify: `src/Seeing.Agent.Memory/Core/Index/HybridMemoryIndex.cs`
- Modify: `src/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Embedding/EmbeddingStatusTests.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Index/HybridMemoryIndexTests.cs`（增补）

**Interfaces:**
- Produces: `IEmbeddingStatus { bool IsAvailable; string? Reason; }`
- Hybrid：`IsAvailable==false` 时 Index/Search 跳过向量

- [ ] **Step 1: Tests**

```csharp
[Fact]
public void EmbeddingStatus_WhenNotConfigured_ShouldBeUnavailable()
{
    var status = new ConfigurableEmbeddingStatus(Options.Create(new MemoryOptions()));
    status.IsAvailable.Should().BeFalse();
    status.Reason.Should().Contain("not configured");
}

[Fact]
public async Task HybridIndex_WhenEmbeddingUnavailable_ShouldKeywordOnlyOnHybridSearch()
{
    // Arrange: mock vector never called; keyword returns 1 hit
    // Act: SearchAsync Hybrid
    // Assert: vector.Verify No; keyword Once
}
```

- [ ] **Step 2: Implement NullEmbeddingService + Status；DI 默认注册 Null，不再 Random**

```csharp
public sealed class NullEmbeddingService : IEmbeddingService
{
    public int Dimensions => 0;
    public string ProviderName => "null";
    public Task<EmbeddingResult> EmbedAsync(string text, CancellationToken ct = default)
        => throw new InvalidOperationException("Embedding is not configured");
    public Task<IReadOnlyList<EmbeddingResult>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        => throw new InvalidOperationException("Embedding is not configured");
}
```

`HybridMemoryIndex` 注入 `IEmbeddingStatus`：

```csharp
public async Task IndexAsync(FileNode node, CancellationToken ct = default)
{
    if (_embeddingStatus.IsAvailable)
        await _vectorIndex.IndexAsync(node, ct);
    await _keywordIndex.IndexAsync(node, ct);
}

public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default)
{
    if (query.Mode is SearchMode.Vector or SearchMode.Hybrid && !_embeddingStatus.IsAvailable)
        return await SearchKeywordAsync(query.Text, query.Limit, ct);
    // 原有分支...
}
```

- [ ] **Step 3: 更新 `VectorIndexTests`：继续使用 `MockEmbeddingService`；生产 DI 不用 Random**

- [ ] **Step 4: Commit**

```bash
git commit -m "fix(memory): disable vector search until embedding is configured"
```

---

### Task 6: LLM 抽取器 + 落盘格式

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IMemoryExtractor.cs`
- Create: `src/Seeing.Agent.Memory/Core/Models/ExtractionResult.cs`
- Create: `src/Seeing.Agent.Memory/Core/Evolution/PromptTemplates.cs`
- Create: `src/Seeing.Agent.Memory/Core/Evolution/LlmMemoryExtractor.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Evolution/LlmMemoryExtractorTests.cs`

**Interfaces:**
- Consumes: `ILlmService`, `IOptions<MemoryOptions>`, `IQuotaManager`（可选）
- Produces:
  - `record ExtractionResult(string Title, string Content, double Importance, IReadOnlyList<string> Tags, string Kind)`
  - `Task<ExtractionResult?> ExtractAsync(MemoryCandidate, CancellationToken)` — 失败/低于阈值返回 null

- [ ] **Step 1: Test with mocked ILlmService**

```csharp
[Fact]
public async Task ExtractAsync_WhenImportanceBelowThreshold_ShouldReturnNull()
{
    var llm = new Mock<ILlmService>();
    llm.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<ChatRequest>(), It.IsAny<CancellationToken>()))
       .ReturnsAsync(new ChatResponse { /* content JSON: importance 0.2 */ });

    var extractor = new LlmMemoryExtractor(llm.Object, Options.Create(new MemoryOptions
    {
        Extraction = new() { MinImportance = 0.5 }
    }), ...);

    var result = await extractor.ExtractAsync(candidate, CancellationToken.None);
    result.Should().BeNull();
}

[Fact]
public async Task ExtractAsync_WhenLlmThrows_ShouldReturnNull()
{
    var llm = new Mock<ILlmService>();
    llm.Setup(...).ThrowsAsync(new Exception("boom"));
    var result = await extractor.ExtractAsync(candidate, default);
    result.Should().BeNull();
}
```

- [ ] **Step 2: Implement JSON 抽取 prompt**

`PromptTemplates.ExtractionSystem` 要求模型只输出：

```json
{"title":"...","content":"...","importance":0.0,"tags":["..."],"kind":"fact|preference|decision|todo"}
```

解析失败 → null。`kind`/`content` 空 → null。

- [ ] **Step 3: Commit**

```bash
git commit -m "feat(memory): add LLM memory extractor"
```

---

### Task 7: MemoryPipeline + PipelineWorker

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IMemoryPipeline.cs`
- Create: `src/Seeing.Agent.Memory/Core/Models/PipelineResult.cs`
- Create: `src/Seeing.Agent.Memory/Core/Pipeline/MemoryPipeline.cs`
- Create: `src/Seeing.Agent.Memory/Background/MemoryPipelineWorker.cs`
- Modify: `src/Seeing.Agent.Memory/Extensions/MemoryServiceExtensions.cs`
- Modify: `src/Seeing.Agent.Memory/Background/MemoryIndexingService.cs`（注释/逻辑：主索引由 Pipeline 完成，本服务仅扫漏）
- Test: `tests/Seeing.Agent.Memory.Tests/Pipeline/MemoryPipelineTests.cs`

**Interfaces:**
- `Task<PipelineResult> ProcessAsync(MemoryCandidate, CancellationToken)`
- `record PipelineResult(bool Stored, string? DailyPath, string? Reason)`

- [ ] **Step 1: Integration-style test with temp FileStore**

```csharp
[Fact]
public async Task ProcessAsync_AcceptedExtraction_ShouldWriteDailyAndSessionIndex()
{
    // filter accept, extractor returns importance 0.9
    // assert file exists: daily/{date}/{id}.md
    // assert session/{sessionId}/index.md contains title/link
    // assert keyword index called (mock IMemoryIndex)
}
```

- [ ] **Step 2: Implement Pipeline**

顺序：
1. HeuristicFilter → reject 则 `Stored=false`
2. 若 `!Extraction.Enabled` → `Reason=extraction_disabled`，不落盘
3. Extract → null 则不落盘
4. 写 daily Markdown（YAML frontmatter）
5. Append session index 行（`SemaphoreSlim` per sessionId，字典 + 锁）
6. `IMemoryIndex.IndexAsync` + graph links（若 MemoryService 已封装则调用其内部或 FileStore+Index）

Daily 文件示例：

```markdown
---
id: {id}
type: daily
title: "{title}"
tags: [...]
importance: 0.9
source_session: {sessionId}
created_at: {iso}
---

{content}
```

- [ ] **Step 3: `MemoryPipelineWorker` : BackgroundService**

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await foreach (var candidate in _queue.ReadAllAsync(stoppingToken))
    {
        try { await _pipeline.ProcessAsync(candidate, stoppingToken); }
        catch (Exception ex) { _logger.LogError(ex, "Pipeline failed for {Id}", candidate.Id); }
    }
}
```

注册：`services.AddHostedService<MemoryPipelineWorker>();`

- [ ] **Step 4: Tests PASS + Commit**

```bash
git commit -m "feat(memory): add async memory pipeline worker"
```

---

### Task 8: 会话演化 Worker

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IMemoryEvolutionService.cs`
- Create: `src/Seeing.Agent.Memory/Core/Evolution/LlmMemoryEvolution.cs`
- Create: `src/Seeing.Agent.Memory/Background/MemoryEvolutionWorker.cs`
- Test: `tests/Seeing.Agent.Memory.Tests/Evolution/MemoryEvolutionTests.cs`

**Interfaces:**
- `Task EvolveSessionAsync(string sessionId, CancellationToken ct)`
- Worker：订阅会话结束事件（优先 `ISessionEventPublisher` / Session 生命周期）；并每分钟扫描「最后活动 + IdleMinutes」

- [ ] **Step 1: Test**

```csharp
[Fact]
public async Task EvolveSessionAsync_WhenImportanceHigh_ShouldWriteDigest()
{
    // seed daily files with importance 0.9
    // mock LLM merge response
    // assert digest/ file created when >= PromoteToDigestMinImportance
}
```

- [ ] **Step 2: Implement**

- 读 `session/{id}/index.md` 与关联 daily
- LLM 合并去重
- 更新 daily；`importance >= PromoteToDigestMinImportance` → 写 `digest/{id}.md`
- 重新索引

- [ ] **Step 3: EvolutionWorker**

- `OnSessionEnd` 配置为 true 时处理 SessionEnded
- 空闲：维护 `ConcurrentDictionary<string, DateTimeOffset> _lastActivity`；Capture 入队时更新；定时检查

Hook 入队成功时调用 `ISessionActivityTracker.Touch(sessionId)`（新建小接口）。

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(memory): add session evolution worker"
```

---

### Task 9: 召回服务 + Hook/Tools

**Files:**
- Create: `src/Seeing.Agent.Memory/Abstractions/IMemoryRecallService.cs`
- Create: `src/Seeing.Agent.Memory/Core/Recall/MemoryRecallService.cs`
- Create: `src/Seeing.Agent.Memory/Integration/MemoryRecallHandler.cs`
- Create: `src/Seeing.Agent.Memory/Integration/MemoryTools.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`（注册 Recall handler）
- Test: `tests/Seeing.Agent.Memory.Tests/Recall/MemoryRecallServiceTests.cs`

**Interfaces:**
- `Task<IReadOnlyList<SearchHit>> RecallAsync(string query, CancellationToken ct)`
- 内部：`SearchTypes` 过滤 daily/digest；`TopK`；用 `CancellationTokenSource.CancelAfter(InjectTimeoutMs)`

- [ ] **Step 1: Tests**

```csharp
[Fact]
public async Task RecallAsync_ShouldExcludeSessionPaths()
{
    // index returns session + daily; recall filters to daily/digest only
}

[Fact]
public async Task RecallAsync_WhenTimeout_ShouldReturnEmpty()
{
    // slow index mock; timeout 10ms; expect empty, no throw
}
```

- [ ] **Step 2: `MemoryRecallHandler` on `HookRegistry.ChatBeforeStart` 或 `LlmSystemPrompt`**

- Mode `ToolsOnly` → no-op
- Mode `AutoInject`/`Both` → Recall，将摘要追加到 system prompt / message（对照 TokenBudget hook 如何改 prompt）
- 超时/异常 → Success 且不注入

- [ ] **Step 3: MemoryTools**

```csharp
[Tool("搜索长期记忆", Name = "memory_search")]
public static async Task<string> Search(string query, [FromServices] IMemoryRecallService recall, ...)
```

按项目 Tool 注解约定实现（静态或实例 + DI）。

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(memory): add recall injection and memory tools"
```

---

### Task 10: 真实 Embedding Provider（P4）

**Files:**
- Create: `src/Seeing.Agent.Memory/Core/Embedding/ProviderEmbeddingService.cs`
- Modify: `MemoryServiceExtensions.cs` — 若 `IsEmbeddingConfigured` 则注册 Provider 实现并包装 `EmbeddingService` 缓存
- Test: `tests/Seeing.Agent.Memory.Tests/Embedding/ProviderEmbeddingServiceTests.cs`（HttpClient mock）

**Interfaces:**
- 从 `Memory.Embedding.Provider/Model` 解析到已有 Provider 的 base URL + key（经 `ProviderManager` / `UnifiedConfigManager.SeeingAgent.Providers`）
- 调用 OpenAI-compatible `/embeddings`（与项目内 OpenAI client 风格一致）

- [ ] **Step 1: Test mock HTTP 返回向量 → Dimensions 匹配**

- [ ] **Step 2: Implement + `IEmbeddingStatus.IsAvailable=true` when configured and last health ok**

- [ ] **Step 3: Commit**

```bash
git commit -m "feat(memory): wire real embedding provider"
```

---

### Task 11: WebUI 记忆设置页

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Pages/MemorySettingsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor` — Extra 增加「设置」按钮
- Modify: 配置保存服务（扩展 `SeeingConfigService` 或新建 `MemoryConfigService` 读写 `memory.json`）

**Interfaces:**
- 读写 `MemoryOptions` 全字段；展示 `IEmbeddingStatus`

- [ ] **Step 1: 页面骨架**

`@page "/memory/settings"`  
区块：总览、捕获、过滤与抽取、演化、Embedding（Provider 下拉来自 Providers + Model 输入 + 测试连接）、召回 Mode。

保存：调用 config API 写入项目级 `memory.json`，再 `ReloadAsync`。

- [ ] **Step 2: MemoryPage 增加导航按钮**

- [ ] **Step 3: 手动验证清单写在 PR 描述**（Blazor 无强制 UI 单测）

- [ ] **Step 4: Commit**

```bash
git commit -m "feat(webui): add memory settings page"
```

---

### Task 12: 回归与文档收尾

**Files:**
- Modify: `src/Seeing.Agent.Memory/README.md` — 更新流程说明（异步管道、配置、无假向量）
- Modify: spec 状态 → `已批准/已实现中`（可选）

- [ ] **Step 1: Run full memory tests**

Run: `dotnet test tests/Seeing.Agent.Memory.Tests`
Expected: all PASS

- [ ] **Step 2: Run solution build**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 errors

- [ ] **Step 3: Update README 数据流小节（与 spec §4 一致）**

- [ ] **Step 4: Commit**

```bash
git commit -m "docs(memory): update README for async pipeline"
```

---

## Spec Coverage Checklist

| Spec 要求 | Task |
|-----------|------|
| Hook 只入队、异步 | T2, T4, T7 |
| 启发式 → LLM 漏斗 | T3, T6, T7 |
| 轻量 session index + daily | T7 |
| 禁假 Embedding / 未配置禁向量 | T5, T10 |
| 会话结束/空闲演化 + digest 按 importance | T8 |
| memory.json + WebUI 设置 | T1, T11 |
| Recall Both/AutoInject/ToolsOnly | T9 |
| 故障不拖垮对话 | T4 catch→Success；T7/T9 隔离错误 |
| IndexingService 仅扫漏 | T7 |
| 非目标：分布式队列/自动清历史 | 不做 |

## Plan Self-Review Notes

- 配置文件明确为 **`memory.json`**（与 UnifiedConfigManager 既有注册一致），纠正规范正文中 seeing.json 表述在实现上的落点。
- 避免 Seeing.Agent ↔ Memory 循环依赖：Options 类型留在 Memory 程序集，core 继续 `typeof(object)` 或弱绑定。
- 类型名在各 Task 间一致：`MemoryCandidate`、`IMemoryWorkQueue.TryEnqueue`、`ReadAllAsync`、`IEmbeddingStatus`。
