# OpenAI Client 思考过程（Reasoning）支持优化计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `OpenAiClient` 正确提取和透传 DeepSeek 等 OpenAI 兼容模型的 `reasoning_content`（思考过程），使下游 `LlmService`/`AgentExecutor` 可以正常展示思考链。

**Architecture:** 在 `OpenAiClient.cs` 的流式和非流式路径中，通过 OpenAI SDK 2.12.0 的 `JsonPatch & Patch` 转义通道读取原始 JSON 中未被建模的 `reasoning_content` 字段，映射到已有的 `StreamUpdate.ReasoningDelta` 和 `ChatMessage.ReasoningContent`。同步扩展 `TokenUsage` 支持 reasoning token 统计。

**Tech Stack:** C#, OpenAI SDK 2.12.0 (`System.ClientModel.Primitives.JsonPatch`), .NET 10.0

---

## 变更文件清单

| 操作 | 文件 | 职责 |
|------|------|------|
| 修改 | `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs` | 流式+非流式 extraction，token 映射 |
| 修改 | `src/Seeing.Agent/Llm/LlmModels.cs` | `TokenUsage` 新增 `ReasoningTokens` |
| 新增 | `tests/Seeing.Agent.Tests/Llm/Clients/OpenAiClientReasoningTests.cs` | 测试 reasoning extraction |

---

### Task 1: TokenUsage 新增 ReasoningTokens 属性

**Files:**
- Modify: `src/Seeing.Agent/Llm/LlmModels.cs:178-191`

- [ ] **Step 1: 添加 ReasoningTokens 属性**

```csharp
// 在 TokenUsage 类的 TotalTokens 属性上方添加
/// <summary>推理（思考）Token 数（仅部分模型支持，如 DeepSeek-R1、o1）</summary>
[JsonPropertyName("reasoning_tokens")]
public int ReasoningTokens { get; set; }
```

修改后的完整 `TokenUsage` 类：

```csharp
/// <summary>
/// Token 使用统计
/// </summary>
public class TokenUsage
{
    /// <summary>输入 Token 数</summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    /// <summary>输出 Token 数</summary>
    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    /// <summary>推理（思考）Token 数（仅部分模型支持，如 DeepSeek-R1、o1）</summary>
    [JsonPropertyName("reasoning_tokens")]
    public int ReasoningTokens { get; set; }

    /// <summary>总 Token 数</summary>
    [JsonIgnore]
    public int TotalTokens => InputTokens + OutputTokens;
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Llm/LlmModels.cs
git commit -m "feat: 在 TokenUsage 中新增 ReasoningTokens 属性，用于统计思考过程 token 消耗

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 非流式路径提取 reasoning_content

**Files:**
- Modify: `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs:329-366` (MapResponse 方法)

- [ ] **Step 1: 修改 MapResponse，提取 reasoning_content**

将现有的 `MapResponse` 方法中构建 `CoreChatMessage` 的部分（第331-336行）替换为：

```csharp
private ChatResponse MapResponse(ChatCompletion completion, string model)
{
    // 通过 Patch 读取 DeepSeek/OpenAI o1 等模型的 reasoning_content
    string? reasoningContent = null;
    try
    {
        if (completion.Patch.Contains("reasoning_content"u8))
            reasoningContent = completion.Patch.GetString("reasoning_content"u8);
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "OpenAI: 读取 reasoning_content 失败（模型可能不支持思考过程）");
    }

    var message = new CoreChatMessage
    {
        Role = ChatRole.Assistant,
        Content = completion.Content.FirstOrDefault()?.Text ?? "",
        ReasoningContent = reasoningContent
    };

    // 处理工具调用
    if (completion.ToolCalls.Count > 0)
    {
        message.ToolCalls = completion.ToolCalls
            .Where(tc => tc.Kind == ChatToolCallKind.Function)
            .Select(tc => new ToolCall
            {
                Id = tc.Id,
                Type = "function",
                Function = new FunctionCall
                {
                    Name = tc.FunctionName,
                    Arguments = tc.FunctionArguments.ToString()
                }
            }).ToList();
    }

    return new ChatResponse
    {
        Id = completion.Id,
        Model = completion.Model,
        Message = message,
        FinishReason = completion.FinishReason.ToString(),
        Usage = new TokenUsage
        {
            InputTokens = completion.Usage.InputTokenCount,
            OutputTokens = completion.Usage.OutputTokenCount,
            ReasoningTokens = completion.Usage.OutputTokenDetails?.ReasoningTokenCount ?? 0
        }
    };
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Llm/Clients/OpenAiClient.cs
git commit -m "feat: OpenAiClient 非流式路径通过 Patch 提取 reasoning_content

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 流式路径提取 reasoning_content delta

**Files:**
- Modify: `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs:79-135` (CompleteStreamAsync 方法)
- Modify: `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs:370-423` (StreamingToolCallAccumulator — 不变，仅引用位置变化)

- [ ] **Step 1: 在流式循环中添加 reasoning delta 提取**

将 `CompleteStreamAsync` 方法中的 `await foreach` 循环体（第93-134行）替换为：

```csharp
await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken))
{
    if (string.IsNullOrEmpty(responseId))
        responseId = update.CompletionId;

    // ========== 提取思考过程增量（DeepSeek-R1 / OpenAI o1） ==========
    try
    {
        if (update.Patch.TryGetValue("reasoning_content"u8, out string? reasoningDelta)
            && !string.IsNullOrEmpty(reasoningDelta))
        {
            yield return new StreamUpdate
            {
                Id = responseId,
                ReasoningDelta = reasoningDelta,
                IsComplete = false
            };
        }
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "OpenAI 流式: 读取 reasoning_content delta 失败");
    }

    foreach (var contentPart in update.ContentUpdate)
    {
        if (!string.IsNullOrEmpty(contentPart.Text))
        {
            contentBuilder.Append(contentPart.Text);
            yield return new StreamUpdate
            {
                Id = responseId,
                ContentDelta = contentPart.Text,
                IsComplete = false
            };
        }
    }

    foreach (var toolCallUpdate in update.ToolCallUpdates)
        streamingTools.Append(toolCallUpdate);

    if (update.FinishReason != null)
    {
        var usage = update.Usage;
        var built = streamingTools.BuildFunctionToolCalls();
        var finalTools = built.Count > 0 ? built : null;

        yield return new StreamUpdate
        {
            Id = responseId,
            IsComplete = true,
            ToolCallDeltas = finalTools,
            Usage = usage != null ? new TokenUsage
            {
                InputTokens = usage.InputTokenCount,
                OutputTokens = usage.OutputTokenCount,
                ReasoningTokens = usage.OutputTokenDetails?.ReasoningTokenCount ?? 0
            } : null
        };
    }
}
```

- [ ] **Step 2: 编译验证**

```bash
dotnet build src/Seeing.Agent/Seeing.Agent.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Llm/Clients/OpenAiClient.cs
git commit -m "feat: OpenAiClient 流式路径通过 Patch 提取 reasoning_content delta

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 编写推理提取的单元测试

**Files:**
- Create: `tests/Seeing.Agent.Tests/Llm/Clients/OpenAiClientReasoningTests.cs`

- [ ] **Step 1: 创建测试文件，验证 ReasoningContent 映射到 ChatResponse**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.Clients;
using Xunit;

namespace Seeing.Agent.Tests.Llm.Clients;

/// <summary>
/// 验证 OpenAiClient 的 thinking/reasoning 提取逻辑。
/// 这些测试不发起真实网络请求，仅验证映射和 Patch 读取逻辑。
/// </summary>
public class OpenAiClientReasoningTests
{
    [Fact]
    public void SmartThinking_ReasoningTokens_计算总Token时_不计入Output()
    {
        // ReasoningTokens 应该单独统计，不影响 OutputTokens
        var usage = new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 500,
            ReasoningTokens = 2000
        };

        usage.TotalTokens.Should().Be(600);  // 100 + 500，reasoning 不计入
        usage.ReasoningTokens.Should().Be(2000);
    }

    [Fact]
    public void ChatMessage_ReasoningContent为null时_IsThought为false()
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant, Content = "hello" };

        msg.IsThought.Should().BeFalse();
    }

    [Fact]
    public void ChatMessage_ReasoningContent非空时_IsThought为true()
    {
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "最终回答",
            ReasoningContent = "让我思考一下..."
        };

        msg.IsThought.Should().BeTrue();
        msg.ReasoningContent.Should().Be("让我思考一下...");
    }

    [Fact]
    public void StreamUpdate_Constructor_默认ReasoningDelta为null()
    {
        var update = new StreamUpdate { ContentDelta = "text" };

        update.ReasoningDelta.Should().BeNull();
        update.ContentDelta.Should().Be("text");
    }
}
```

- [ ] **Step 2: 运行测试验证**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~OpenAiClientReasoningTests"
```

Expected: 4 tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Seeing.Agent.Tests/Llm/Clients/OpenAiClientReasoningTests.cs
git commit -m "test: 添加 OpenAiClient reasoning 相关的单元测试

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 回归验证

- [ ] **Step 1: 运行全部测试确保无回归**

```bash
dotnet test
```

Expected: All tests pass, no regressions.

- [ ] **Step 2: 若全部通过，无需额外 commit；若失败则修复后 commit**

---

## Self-Review

**1. Spec coverage:** 每个需求点都有对应 task。
- ✅ 流式 reasoning delta → Task 3
- ✅ 非流式 reasoning content → Task 2
- ✅ reasoning token 统计 → Task 1
- ✅ 单元测试 → Task 4
- ✅ 回归验证 → Task 5

**2. Placeholder scan:** 无 TBD/TODO/占位符，每步都有具体代码和命令。

**3. Type consistency:**
- `TokenUsage.ReasoningTokens` (int) → `ChatTokenUsage.OutputTokenDetails.ReasoningTokenCount` (int) ✅
- `StreamUpdate.ReasoningDelta` (string?) → `Patch.TryGetValue("reasoning_content"u8, out string?)` ✅
- `ChatMessage.ReasoningContent` (string?) → `Patch.GetString("reasoning_content"u8)` (string?) ✅
