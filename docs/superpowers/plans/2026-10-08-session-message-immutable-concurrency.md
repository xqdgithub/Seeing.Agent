# SessionMessage 内容不可变并发契约 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> 本计划按**可并行工作包（Work Packet）**组织：P0 为串行关键路径；P1.x / P2.x 文件互不重叠，可由并行子 Agent 同时实现；P3 为串行验证与文档。

**Goal:** 将 `SessionMessage`/`SessionToolCall` 的集合成员改为不可变集合，从类型系统上消除"后台写 + Dispatcher 读"的 `Collection was modified` 及由此引发的渲染树 NRE。

**Architecture:** `publish-on-write`——结构变更一律替换属性引用；读取方直接持引用枚举，无需锁。`SessionData.Messages` 的锁内快照契约保持不变。`System.Collections.Immutable` 属共享框架，无新依赖。

**Tech Stack:** .NET 10.0 / C# / System.Collections.Immutable / System.Text.Json / xUnit+FluentAssertions。

**设计规格:** `docs/superpowers/specs/2026-10-08-session-message-immutable-concurrency-design.md`

**执行约束:**
- P1.x/P2.x 子 Agent **只改自己名下的文件**，不要运行整解决方案构建（P0 后、未全部迁移前必然编译失败），只提交编辑并在回报中列出改动。
- 关键审查点：`[Checkpoint A]` P0+P1 完成后的整解决方案构建；`[Checkpoint B]` P2+P3 后的测试回归。

---

## 文件结构（改动地图）

| 包 | 文件 | 职责 | 所属包 |
|----|------|------|--------|
| Model | `src/primitives/Seeing.Session/Core/SessionMessage.cs` | 模型不可变化 + Clone/工厂 | **P0** |
| Writer | `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs` | 权威写入者 | **P1.1** |
| Writer | `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs` | UI 投影写入者 | **P1.2** |
| Writer | `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs` | TaskSteps 聚合 | **P1.3** |
| Writer | `samples/Seeing.Agent.WebUI/Services/SessionMessageConverter.cs` | ChatMessage→SessionMessage | **P1.4** |
| Test | `tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs` | 迁移断言构造 | **P2.1** |
| Test | `tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs` | 迁移断言构造 | **P2.2** |
| Test | `tests/primitives/Seeing.Session.Tests/SessionMessageImmutabilityTests.cs` | 新增并发契约 + STJ 往返 | **P3** |
| Doc | `docs/architecture/04-development-standards.md` + Release Notes | 契约与破坏性清单 | **P3** |

---

## P0（串行关键路径）：模型不可变化

**Files:**
- Modify: `src/primitives/Seeing.Session/Core/SessionMessage.cs`

- [ ] **Step 0.1: 顶部加 using**

```csharp
using System.Collections.Immutable;
using System.Text.Json.Serialization;
```

- [ ] **Step 0.2: `SessionToolCall` 集合属性改类型**

```csharp
// 原: public List<SessionTaskStep>? TaskSteps { get; set; }
public ImmutableList<SessionTaskStep>? TaskSteps { get; set; }

// 原: public Dictionary<string, object>? Metadata { get; set; }
public ImmutableDictionary<string, object>? Metadata { get; set; }
```

- [ ] **Step 0.3: `SessionToolCall.Clone()` 产出不可变集合**

```csharp
Metadata = Metadata == null ? null : ImmutableDictionary.CreateRange(Metadata),
TaskSteps = TaskSteps?.Select(s => new SessionTaskStep
{
    StepKind = s.StepKind,
    ToolCallId = s.ToolCallId,
    ToolName = s.ToolName,
    Status = s.Status,
    Preview = s.Preview,
    Timestamp = s.Timestamp
}).ToImmutableList()
```

- [ ] **Step 0.4: `SessionMessage` 集合属性改类型**

```csharp
// 原: public List<SessionContentPart>? Parts { get; set; }
public ImmutableList<SessionContentPart>? Parts { get; set; }

// 原: public List<SessionToolCall>? ToolCalls { get; set; }
public ImmutableList<SessionToolCall>? ToolCalls { get; set; }

// 原: public Dictionary<string, object>? Metadata { get; set; }
public ImmutableDictionary<string, object>? Metadata { get; set; }
```

- [ ] **Step 0.5: `SessionMessage.Clone()` 产出不可变集合**

```csharp
Parts = Parts?.Select(p => new SessionContentPart
{
    Type = p.Type, Text = p.Text, Url = p.Url, DataBase64 = p.DataBase64,
    MimeType = p.MimeType, FileName = p.FileName, FileId = p.FileId, ImageDetail = p.ImageDetail
}).ToImmutableList(),
ToolCalls = ToolCalls?.Select(t => t.Clone()).ToImmutableList(),
Metadata = Metadata == null ? null : DeepCloneMetadata(Metadata),
```

- [ ] **Step 0.6: `DeepCloneMetadata` 返回不可变字典**

```csharp
private static ImmutableDictionary<string, object> DeepCloneMetadata(
    IEnumerable<KeyValuePair<string, object>> source)
{
    var builder = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
    foreach (var (key, value) in source)
        builder[key] = DeepCloneMetadataValue(value);
    return builder.ToImmutable();
}
```

- [ ] **Step 0.7: 工厂方法适配**

```csharp
public static SessionMessage UserMessageWithParts(List<SessionContentPart> parts) =>
    new() { Id = NewId(), Role = MessageRole.User, Parts = parts.ToImmutableList() };

public static SessionMessage UserMessageWithImage(string text, string imageUrl) =>
    new()
    {
        Id = NewId(), Role = MessageRole.User,
        Parts = ImmutableList.Create(
            SessionContentPart.CreateText(text),
            SessionContentPart.CreateImageFromUrl(imageUrl))
    };

public static SessionMessage AssistantMessageWithToolCalls(
    List<SessionToolCall> toolCalls, string? content = null) =>
    new()
    {
        Id = NewId(), Role = MessageRole.Assistant,
        Content = content ?? string.Empty, ToolCalls = toolCalls.ToImmutableList()
    };

public SessionMessage WithMetadata(string key, object value)
{
    Metadata = (Metadata ?? ImmutableDictionary<string, object>.Empty).SetItem(key, value);
    return this;
}
```

- [ ] **Step 0.8: 自检（不构建）** — 确认文件内已无 `List<SessionToolCall>?`/`List<SessionContentPart>?`/`Dictionary<string, object>?` 作为这三个属性类型；`HasToolCalls`/`GetEffectiveParts` 无需改（`Count` 兼容）。

---

## P1.x（并行）：写入者迁移（依赖 P0）

### P1.1 `ChatEventTracker.cs`

**Files:** `src/hosting/Seeing.Agent.Hosting/Execution/ChatEventTracker.cs`
**Depends on:** P0

- [ ] 顶部加 `using System.Collections.Immutable;`
- [ ] `StreamComplete` 分支替换为（用 `ImmutableList.Add`，禁止原地 Add）：

```csharp
if (streamComplete.Message.ToolCalls is { Count: > 0 })
{
    var nextToolCalls = _currentAssistantMessage.ToolCalls
        ?? ImmutableList<SessionToolCall>.Empty;
    foreach (var tc in streamComplete.Message.ToolCalls)
    {
        if (string.IsNullOrEmpty(tc.Id)) continue;
        if (nextToolCalls.Any(t => t.Id == tc.Id)) continue;
        nextToolCalls = nextToolCalls.Add(new SessionToolCall
        {
            Id = tc.Id, Name = tc.Name,
            Arguments = string.IsNullOrWhiteSpace(tc.Function?.Arguments) ? "{}" : tc.Function!.Arguments,
            Status = "pending"
        });
    }
    _currentAssistantMessage.ToolCalls = nextToolCalls;
}
```

- [ ] `ToolCallEvent` 分支替换为：

```csharp
var currentToolCalls = _currentAssistantMessage.ToolCalls
    ?? ImmutableList<SessionToolCall>.Empty;
var toolCallId = toolCall.ToolCallId ?? Guid.NewGuid().ToString("N");
var existing = currentToolCalls.FirstOrDefault(t => t.Id == toolCallId);
if (existing == null)
{
    existing = new SessionToolCall
    {
        Id = toolCallId, Name = toolCall.ToolName ?? string.Empty,
        Arguments = FormatArguments(toolCall.Arguments)
    };
    _currentAssistantMessage.ToolCalls = currentToolCalls.Add(existing);
}
```

- [ ] `EnsureAssistantMessage` 中 Metadata 写入改为：

```csharp
_currentAssistantMessage.Metadata =
    (_currentAssistantMessage.Metadata ?? ImmutableDictionary<string, object>.Empty)
        .SetItem(SchemaSnapshotMetadataKey, DeepCloneObject(_pendingSchemaSnapshot));
```

- [ ] `AttachSchemaSnapshotMetadata` 中：`target.Metadata ??= new Dictionary<string, object>();` → 删除该行；写入改 `SetItem`；else 分支 `target.Metadata = new Dictionary<string, object>();` → `target.Metadata = ImmutableDictionary<string, object>.Empty;`：

```csharp
target.Metadata = (target.Metadata ?? ImmutableDictionary<string, object>.Empty)
    .SetItem(SchemaSnapshotMetadataKey, DeepCloneObject(payload));
```

### P1.2 `EventStreamHandler.cs`

**Files:** `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`
**Depends on:** P0

- [ ] 顶部加 `using System.Collections.Immutable;`
- [ ] `HandleToolCall` 结构变更段替换为：

```csharp
var toolCalls = _currentAssistantMessage.ToolCalls
    ?? ImmutableList<SessionToolCall>.Empty;

var toolCall = toolCalls.FirstOrDefault(t => t.Id == evt.ToolCallId);
if (toolCall == null)
{
    var contentPosition = _currentAssistantMessage.Content?.Length ?? 0;
    var toolCallId = evt.ToolCallId ?? Guid.NewGuid().ToString();
    toolCall = new SessionToolCall
    {
        Id = toolCallId, Name = evt.ToolName ?? string.Empty,
        Arguments = FormatArgumentsJson(evt.Arguments)
    };
    _currentAssistantMessage.ToolCalls = toolCalls.Add(toolCall);
    _toolCallPositions[toolCallId] = contentPosition;
}
```

- [ ] 其余仅读 `ToolCalls`（`MarkIncompleteTasksCancelledAsync`/`ReconcileOrphanTaskCardsAsync` 写元素标量）**不改**。

### P1.3 `TaskCardAggregator.cs`

**Files:** `samples/Seeing.Agent.WebUI/Services/TaskCardAggregator.cs`
**Depends on:** P0

- [ ] 顶部加 `using System.Collections.Immutable;`
- [ ] `MergeStep` 末尾赋值改为：

```csharp
// 原: toolCall.TaskSteps = steps;
toolCall.TaskSteps = steps.ToImmutableList();
```

### P1.4 `SessionMessageConverter.cs`

**Files:** `samples/Seeing.Agent.WebUI/Services/SessionMessageConverter.cs`
**Depends on:** P0

- [ ] 顶部加 `using System.Collections.Immutable;`
- [ ] `ToSessionMessage` 中：

```csharp
sessionMessage.Parts = chatMessage.Parts.Select(ToSessionContentPart).ToImmutableList();
sessionMessage.ToolCalls = chatMessage.ToolCalls.Select(ToSessionToolCall).ToImmutableList();
```

（`ToChatMessage` 侧目标是 `ChatMessage`，保持不变。）

---

## [Checkpoint A]（串行，审查点）

- [ ] `dotnet build Seeing.Agent.slnx -c Debug --nologo` → 0 error。
- [ ] 若报错，集中在两处：未迁移的 `= List<>` 赋值点、或 `List` 专有 API（`Find/Exists/Add`）。逐一按 P1 模式修。

---

## P2.x（并行）：既有测试迁移（依赖 P0）

### P2.1 `MessageTimelineStoreTests.cs`

**Files:** `tests/apps/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs`
**Depends on:** P0

- [ ] 顶部加 `using System.Collections.Immutable;`
- [ ] 所有 `SessionMessage.ToolCalls = <List/集合表达式>` 改 `= ImmutableList.Create(...)`；`Parts = <List>` 改 `.ToImmutableList()`。读断言（`.Should().ContainSingle()`、索引）不变。

### P2.2 `TaskCardAggregatorTests.cs`

**Files:** `tests/apps/Seeing.Agent.WebUI.Tests/Services/TaskCardAggregatorTests.cs`
**Depends on:** P0

- [ ] 顶部加 `using System.Collections.Immutable;`
- [ ] `msg.ToolCalls = new List<SessionToolCall> { ... };` → `msg.ToolCalls = ImmutableList.Create(...);`。读断言不变。

---

## P3（串行）：新增契约测试 + 文档 + 回归

### Task P3.1: 并发契约压力测试

**Files:** Create `tests/primitives/Seeing.Session.Tests/SessionMessageImmutabilityTests.cs`

```csharp
using System.Collections.Immutable;
using FluentAssertions;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests;

public class SessionMessageImmutabilityTests
{
    [Fact]
    public void ConcurrentPublishOnWrite_And_Enumerate_DoesNotThrow()
    {
        var msg = new SessionMessage { Role = MessageRole.Assistant, ToolCalls = ImmutableList<SessionToolCall>.Empty };
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
                msg.ToolCalls = msg.ToolCalls!.Add(new SessionToolCall { Id = $"t{i}", Name = "read" });
        });
        var reader = Task.Run(() =>
        {
            while (!stop)
            {
                var snapshot = msg.ToolCalls;
                _ = snapshot?.Count(s => s.Name == "read");
            }
        });
        writer.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        stop = true;
        reader.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        msg.ToolCalls!.Should().HaveCount(20_000);
    }
}
```

### Task P3.2: STJ 往返测试

**Files:** 追加至 `tests/primitives/Seeing.Session.Tests/SessionMessageImmutabilityTests.cs`

```csharp
[Fact]
public void JsonRoundTrip_PreservesCollections()
{
    var original = new SessionMessage
    {
        Id = "m1", Role = MessageRole.Assistant, Content = "hi",
        Parts = ImmutableList.Create(SessionContentPart.CreateText("hi")),
        Metadata = ImmutableDictionary<string, object>.Empty.SetItem("k", "v"),
        ToolCalls = ImmutableList.Create(new SessionToolCall
        {
            Id = "t1", Name = "read", Status = "success",
            TaskSteps = ImmutableList.Create(new SessionTaskStep { StepKind = "tool_complete", ToolName = "read" }),
            Metadata = ImmutableDictionary<string, object>.Empty.SetItem("x", "y")
        })
    };

    var json = System.Text.Json.JsonSerializer.Serialize(original);
    var back = System.Text.Json.JsonSerializer.Deserialize<SessionMessage>(json)!;

    back.ToolCalls!.Should().HaveCount(1);
    back.ToolCalls![0].TaskSteps!.Should().HaveCount(1);
    back.Parts!.Should().HaveCount(1);
}
```

### Task P3.3: 契约文档 + Release Notes

- [ ] `docs/architecture/04-development-standards.md`：新增"消息内容发布即不可变（publish-on-write）"契约 + 反模式条目（禁止对 `SessionMessage`/`SessionToolCall` 集合属性原地 `Add/Remove/索引写`）。
- [ ] 新增 `docs/architecture/12-session-message-immutability-release-notes.md`：破坏性清单（5 个属性类型）、迁移对照、JSON 兼容声明。

### Task P3.4: 回归

- [ ] `dotnet build Seeing.Agent.slnx -c Debug --nologo` → 0 error/0 warning（新增）。
- [ ] `dotnet build tests/apps/Seeing.Agent.WebUI.Tests && dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll`
- [ ] `dotnet build tests/primitives/Seeing.Session.Tests && dotnet vstest tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll`
- [ ] `dotnet build tests/spine/Seeing.Agent.Tests && dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`

---

## 并行执行编排

```
P0 (串行)  ──┬─► P1.1 ┐
             ├─► P1.2 ┤
             ├─► P1.3 ┼─► [Checkpoint A: 全量构建] ──┬─► P3.1 ┐
             ├─► P1.4 ┘                             ├─► P3.2 ┼─► [Checkpoint B: 回归]
             ├─► P2.1 ┐                             ├─► P3.3 ┘
             └─► P2.2 ┘                             └─► P3.4
```

- P1.1–P1.4、P2.1–P2.2 六包文件互不重叠，可同时分派 6 个子 Agent。
- 每个子 Agent 仅做所辖文件的精确替换，**不构建**（避免互相干扰）；回报改动清单。
- 编排者在 Checkpoint A 统一构建并修复遗漏点。

---

## Self-Review

- **Spec 覆盖**：契约（P3.3）、模型（P0）、写入者（P1.1–1.4）、读取者零改动（无需任务）、序列化（P3.2）、测试（P3.1/P3.2）、破坏性清单（P3.3）——均有对应任务。
- **Placeholder 扫描**：无 TBD/TODO；每步含具体代码。
- **类型一致性**：全篇统一 `ImmutableList<T>` / `ImmutableDictionary<K,V>`；空集合统一 `ImmutableList<T>.Empty` / `ImmutableDictionary<K,V>.Empty`；`SetItem`/`Add` API 名称一致。
- **已知遗漏处理**：`IncompleteToolCallMarker`、`ExecutionJobService`、`AgentExecutor`、`SessionReadTool`、`TuiViewState`、`ChatMessageHistoryBuilder`、`SessionToolBase` 均为只读，且 `IncompleteToolCallMarker` 仅写元素标量 → 无需任务（Checkpoint A 构建会兜底暴露任何遗漏）。
