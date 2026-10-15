# Message Timeline Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild WebUI message list around a stable `MessageTimelineStore` with revision-based render isolation, JS-owned scroll pinning (strict respect for scroll-up), and a sliding mount window for long sessions.

**Architecture:** `SessionMessage` stays persistence-only. `MessageTimelineStore` incrementally builds `TimelineItem[]` (stable `Key` + `Revision`). `MessageList` renders only a sliding window of items; `TimelineItemView` uses `ShouldRender` on `Revision`. `ScrollPolicy` in `app.js` owns pinned state and never drives Blazor list rebuilds via scroll-button toggles. Pipeline rebuilds content blocks only when a turn’s `Revision` changes.

**Tech Stack:** .NET 10 / Blazor Server, xUnit + FluentAssertions, existing `MessageRenderPipeline`, AntDesign (unchanged chrome).

**Spec:** [`docs/superpowers/specs/2026-08-04-message-timeline-rendering-design.md`](../specs/2026-08-04-message-timeline-rendering-design.md)

**Git policy:** User requested no auto-commits for this workstream. **Skip all Commit steps** unless the user explicitly asks to commit.

---

## File map

| File | Responsibility |
|------|----------------|
| `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItemKind.cs` | Enum of display kinds |
| `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItem.cs` | Stable Key + Revision + payload |
| `samples/Seeing.Agent.WebUI/Services/MessageViewModelFactory.cs` | `SessionMessage` → `MessageViewModel` (extracted from `Session.razor`) |
| `samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs` | Reset/Apply API + `Items` + `Changed` |
| `samples/Seeing.Agent.WebUI/Services/MessageListWindow.cs` | Pure window math: start index / load more / slide |
| `samples/Seeing.Agent.WebUI/Components/TimelineItemView.razor` | Kind dispatch + `ShouldRender` by Revision |
| `samples/Seeing.Agent.WebUI/Components/MessageList.razor` | Windowed list + JS scroll notify only |
| `samples/Seeing.Agent.WebUI/Pages/Session.razor` | Wire store; stop Clear+full refresh; reduce page-wide stream redraws |
| `samples/Seeing.Agent.WebUI/Program.cs` | `AddScoped<MessageTimelineStore>()` |
| `samples/Seeing.Agent.WebUI/wwwroot/js/app.js` | ScrollPolicy: no 3s auto-unpin; JS-only back-to-bottom button |
| `samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor` | Cache blocks by Revision |
| `samples/Seeing.Agent.WebUI/Rendering/MessageRenderPipeline.cs` | Optional helper to build blocks once per revision (if cleaner than component cache) |
| `tests/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs` | Store unit tests |
| `tests/Seeing.Agent.WebUI.Tests/Services/MessageListWindowTests.cs` | Window unit tests |

**Note on `role=tool` messages:** Timeline does **not** emit separate items for `role=tool`. Tools live on assistant `MessageViewModel.ToolCalls` / Loop content blocks (matches what `LoopGroupItem` actually shows). Orphan tool rows are skipped on Reset.

---

### Task 1: TimelineItem model

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItemKind.cs`
- Create: `samples/Seeing.Agent.WebUI/Models/Timeline/TimelineItem.cs`

- [ ] **Step 1: Add `TimelineItemKind`**

```csharp
namespace Seeing.Agent.WebUI.Models.Timeline;

public enum TimelineItemKind
{
    User,
    AssistantTurn,
    System,
    Compaction,
    Reminder,
    ProjectInstructions
}
```

- [ ] **Step 2: Add `TimelineItem`**

```csharp
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Models.Timeline;

public sealed class TimelineItem
{
    public required string Key { get; init; }
    public required TimelineItemKind Kind { get; init; }
    public int Revision { get; private set; }
    public MessageViewModel? Message { get; internal set; }
    public LoopGroupViewModel? Turn { get; internal set; }

    public void Touch() => Revision++;

    public static string AssistantKey(string? loopId, string messageId)
        => string.IsNullOrEmpty(loopId) ? $"single-{messageId}" : loopId;
}
```

- [ ] **Step 3: Build to verify compile**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj --no-restore 2>&1 | Select-Object -Last 30`  
(If restore needed: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`)  
Expected: build succeeds (or only pre-existing warnings).

- [ ] **Step 4: Commit** — **SKIP** unless user asks.

---

### Task 2: MessageListWindow (TDD)

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/MessageListWindow.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/MessageListWindowTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class MessageListWindowTests
{
    [Fact]
    public void InitialStart_WhenFewerThanWindow_ShouldBeZero()
    {
        MessageListWindow.ComputeInitialStart(total: 10, initialWindow: 40)
            .Should().Be(0);
    }

    [Fact]
    public void InitialStart_WhenMoreThanWindow_ShouldPinTail()
    {
        MessageListWindow.ComputeInitialStart(total: 100, initialWindow: 40)
            .Should().Be(60);
    }

    [Fact]
    public void SlideWhilePinned_ShouldEnforceMaxMounted()
    {
        // total 100, start 60 → 40 mounted; grow to 120 → start becomes 40 (120-80)
        var start = MessageListWindow.SlideWhilePinned(
            currentStart: 60, total: 120, maxMounted: 80);
        start.Should().Be(40);
    }

    [Fact]
    public void SlideWhilePinned_WhenUnderMax_ShouldKeepStart()
    {
        MessageListWindow.SlideWhilePinned(50, total: 100, maxMounted: 80)
            .Should().Be(50);
    }

    [Fact]
    public void LoadMore_ShouldDecreaseStartByBatch()
    {
        MessageListWindow.LoadMore(currentStart: 40, batch: 20)
            .Should().Be(20);
    }

    [Fact]
    public void LoadMore_ShouldNotGoBelowZero()
    {
        MessageListWindow.LoadMore(currentStart: 10, batch: 20)
            .Should().Be(0);
    }
}
```

- [ ] **Step 2: Run tests — expect FAIL**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests --filter "FullyQualifiedName~MessageListWindowTests" --no-restore`  
Expected: FAIL (type/method missing). If restore needed, drop `--no-restore`.

- [ ] **Step 3: Implement `MessageListWindow`**

```csharp
namespace Seeing.Agent.WebUI.Services;

public static class MessageListWindow
{
    public const int InitialWindowSize = 40;
    public const int LoadMoreBatch = 20;
    public const int MaxMounted = 80;

    public static int ComputeInitialStart(int total, int initialWindow = InitialWindowSize)
        => total <= initialWindow ? 0 : total - initialWindow;

    public static int SlideWhilePinned(int currentStart, int total, int maxMounted = MaxMounted)
    {
        var mounted = total - currentStart;
        if (mounted <= maxMounted)
            return currentStart;
        return Math.Max(0, total - maxMounted);
    }

    public static int LoadMore(int currentStart, int batch = LoadMoreBatch)
        => Math.Max(0, currentStart - batch);
}
```

- [ ] **Step 4: Run tests — expect PASS**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests --filter "FullyQualifiedName~MessageListWindowTests"`  
Expected: all PASS.

- [ ] **Step 5: Commit** — **SKIP** unless user asks.

---

### Task 3: MessageViewModelFactory extract

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/MessageViewModelFactory.cs`
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor` (later Task 7 will switch call sites; this task only adds factory)

- [ ] **Step 1: Move conversion logic**

Create `MessageViewModelFactory` with:

```csharp
public static class MessageViewModelFactory
{
    public static MessageViewModel FromSessionMessage(
        SessionMessage msg,
        string sessionId,
        bool isComplete = true)
    {
        // Move body from Session.razor ConvertToMessageViewModel exactly,
        // replacing SessionState.SessionId with sessionId parameter.
        // Keep SystemReminderRenderer / ProjectInstructionsRenderer / Metadata handling.
    }

    public static void MergeToolCall(MessageViewModel target, ToolCallViewModel incoming)
    {
        var existing = target.ToolCalls.FirstOrDefault(t => t.Id == incoming.Id);
        if (existing == null)
        {
            target.ToolCalls.Add(incoming);
            return;
        }
        var wasExpanded = existing.IsExpanded;
        existing.Name = incoming.Name;
        existing.Parameters = incoming.Parameters;
        existing.Result = incoming.Result;
        existing.Status = incoming.Status;
        existing.Error = incoming.Error;
        existing.TaskId = incoming.TaskId;
        existing.TaskAgent = incoming.TaskAgent;
        existing.TaskDescription = incoming.TaskDescription;
        existing.TaskBackground = incoming.TaskBackground;
        existing.TaskSteps = incoming.TaskSteps;
        existing.TodoList = incoming.TodoList;
        existing.IsExpanded = wasExpanded;
    }
}
```

Copy the full reminder/instructions/metadata branches from `Session.razor` `ConvertToMessageViewModel` (lines ~1261–1320+) unchanged aside from `sessionId`.

- [ ] **Step 2: Build WebUI**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`  
Expected: success.

- [ ] **Step 3: Commit** — **SKIP** unless user asks.

---

### Task 4: MessageTimelineStore (TDD)

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Services/MessageTimelineStore.cs`
- Test: `tests/Seeing.Agent.WebUI.Tests/Services/MessageTimelineStoreTests.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs` (register Scoped)

- [ ] **Step 1: Write failing store tests**

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Timeline;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class MessageTimelineStoreTests
{
    private static SessionMessage Msg(
        string id, string role, string content,
        string? loopId = null, int step = 0)
        => new()
        {
            Id = id,
            Role = role,
            Content = content,
            LoopId = loopId,
            Step = step,
            CreatedAt = DateTime.UtcNow
        };

    [Fact]
    public void ResetFromSession_ShouldGroupAssistantsByLoopId()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(new[]
        {
            Msg("u1", "user", "hi"),
            Msg("a1", "assistant", "one", loopId: "L1", step: 0),
            Msg("a2", "assistant", "two", loopId: "L1", step: 1),
        }, sessionId: "s1");

        store.Items.Should().HaveCount(2);
        store.Items[0].Kind.Should().Be(TimelineItemKind.User);
        store.Items[0].Key.Should().Be("u1");
        store.Items[1].Kind.Should().Be(TimelineItemKind.AssistantTurn);
        store.Items[1].Key.Should().Be("L1");
        store.Items[1].Turn!.Messages.Should().HaveCount(2);
    }

    [Fact]
    public void ResetFromSession_DuplicateId_ShouldKeepLast()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(new[]
        {
            Msg("u1", "user", "old"),
            Msg("u1", "user", "new"),
        }, "s1");

        store.Items.Should().ContainSingle();
        store.Items[0].Message!.Content.Should().Be("new");
    }

    [Fact]
    public void ResetFromSession_ShouldSkipToolRole()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(new[]
        {
            Msg("a1", "assistant", "x", loopId: "L1"),
            Msg("t1", "tool", "result", loopId: "L1"),
        }, "s1");

        store.Items.Should().ContainSingle(i => i.Kind == TimelineItemKind.AssistantTurn);
    }

    [Fact]
    public void PatchStream_ShouldIncrementRevision()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(new[] { Msg("a1", "assistant", "x", "L1") }, "s1");
        var rev = store.Items[0].Revision;

        store.PatchStream("L1", contentDelta: "y");

        store.Items[0].Revision.Should().Be(rev + 1);
        store.Items[0].Turn!.Messages[^1].Content.Should().Contain("y");
    }

    [Fact]
    public void UpsertTool_ShouldPreserveIsExpanded()
    {
        var store = new MessageTimelineStore();
        store.ResetFromSession(new[] { Msg("a1", "assistant", "x", "L1") }, "s1");
        store.UpsertTool("L1", new ToolCallViewModel { Id = "tc1", Name = "read", IsExpanded = true });
        store.UpsertTool("L1", new ToolCallViewModel { Id = "tc1", Name = "read", Result = "ok", IsExpanded = false });

        store.Items[0].Turn!.Messages[^1].ToolCalls.Single().IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Changed_ShouldFireOnResetAndPatch()
    {
        var store = new MessageTimelineStore();
        var n = 0;
        store.Changed += () => n++;
        store.ResetFromSession(new[] { Msg("a1", "assistant", "x", "L1") }, "s1");
        store.PatchStream("L1", "z");
        n.Should().Be(2);
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests --filter "FullyQualifiedName~MessageTimelineStoreTests"`  
Expected: FAIL (missing type).

- [ ] **Step 3: Implement `MessageTimelineStore`**

Core shape:

```csharp
public sealed class MessageTimelineStore
{
    private readonly List<TimelineItem> _items = new();
    private readonly Dictionary<string, TimelineItem> _byKey = new(StringComparer.Ordinal);
    private string _sessionId = "";

    public IReadOnlyList<TimelineItem> Items => _items;
    public event Action? Changed;

    public void ResetFromSession(IEnumerable<SessionMessage> messages, string sessionId)
    {
        _sessionId = sessionId ?? "";
        _items.Clear();
        _byKey.Clear();

        // 1) Dedupe by Id keeping last
        // 2) Walk in order:
        //    - skip role == "tool"
        //    - user: Reminder / ProjectInstructions / User via factory flags
        //    - assistant: merge into AssistantTurn by TimelineItem.AssistantKey
        //    - system / compaction metadata → System / Compaction
        // 3) For each AssistantTurn build LoopGroupViewModel (LoopIndex = order among turns)
        RaiseChanged();
    }

    public void AppendUser(MessageViewModel vm) { /* create item, Touch not needed on new; Revision starts 0; RaiseChanged */ }

    public void UpsertAssistantStep(string? loopId, int step, MessageViewModel vm)
    {
        var key = TimelineItem.AssistantKey(loopId, vm.Id);
        // find or create AssistantTurn; replace/add message by Id or step; Touch(); RaiseChanged
    }

    public void PatchStream(string? loopId, string? contentDelta = null, string? reasoningDelta = null)
    {
        // resolve active turn (by loopId or last AssistantTurn); append deltas to last incomplete assistant msg; Touch(); RaiseChanged
    }

    public void UpsertTool(string? loopId, ToolCallViewModel tool)
    {
        // resolve turn; MessageViewModelFactory.MergeToolCall on last assistant msg; Touch(); RaiseChanged
    }

    public void CompleteTurn(string? loopId)
    {
        // mark turn + messages IsComplete; Touch(); RaiseChanged
    }

    private void RaiseChanged() => Changed?.Invoke();
}
```

Implementation details that must match tests:

- `PatchStream` appends `contentDelta` to the **last** message in the turn’s `Messages` list (`Content += delta`).
- `UpsertTool` uses `MergeToolCall` preserving `IsExpanded`.
- New `TimelineItem` starts at `Revision = 0`; first `Touch()` after mutate makes it 1 for Patch test — either start at 0 and Touch on Patch, or set content then Touch once.

- [ ] **Step 4: Register DI**

In `Program.cs` near other Scoped UI services:

```csharp
builder.Services.AddScoped<MessageTimelineStore>();
```

- [ ] **Step 5: Run tests — expect PASS**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests --filter "FullyQualifiedName~MessageTimelineStoreTests"`  
Expected: all PASS.

- [ ] **Step 6: Commit** — **SKIP** unless user asks.

---

### Task 5: ScrollPolicy JS (strict pin)

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/js/app.js`
- Modify: `tests/Seeing.Agent.WebUI.Tests/Infrastructure/WebUiScriptInteropContractTests.cs` (if it asserts function names)

- [ ] **Step 1: Update `ScrollManager`**

Required behavior changes in `app.js`:

1. **Delete** `resetScrollTimeout` / 3-second auto `userScrolled = false`.
2. `handleScroll`: set `userScrolled = true` when not at bottom; set `userScrolled = false` only when `isAtBottom`.
3. Add `notifyContentGrew()` → `scrollToBottom(false)` (respects pin).
4. Add `preserveScrollOnPrepend(previousScrollHeight)`:

```javascript
preserveScrollOnPrepend(previousScrollHeight) {
    if (!this.container) return;
    const delta = this.container.scrollHeight - previousScrollHeight;
    this.container.scrollTop = this.lastScrollTop + delta;
}
```

5. Add `setBackToBottomVisible(visible)` that toggles a DOM element `#message-list-scroll-to-bottom` via `classList` / `hidden` — **no DotNet invoke**.
6. Keep exports: `initMessageListScroll`, `smartScrollToBottom`, `forceScrollToBottom`, `shouldAutoScroll`, plus new `notifyContentGrew`, `preserveScrollOnPrepend`.

Wire scroll listener to update button visibility in JS only:

```javascript
this.container.addEventListener('scroll', () => {
    this.handleScroll();
    this.setBackToBottomVisible(this.userScrolled);
});
```

- [ ] **Step 2: Align contract tests**

Open `WebUiScriptInteropContractTests.cs`. If it lists required global functions, add `notifyContentGrew` and `preserveScrollOnPrepend`; remove any assertion that depends on auto-resume timeout if present.

- [ ] **Step 3: Run contract tests**

Run: `dotnet test tests/Seeing.Agent.WebUI.Tests --filter "FullyQualifiedName~WebUiScriptInteropContractTests"`  
Expected: PASS.

- [ ] **Step 4: Commit** — **SKIP** unless user asks.

---

### Task 6: TimelineItemView + MessageList rewrite

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/TimelineItemView.razor`
- Modify: `samples/Seeing.Agent.WebUI/Components/MessageList.razor` (replace Loop HashSet path)
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/message-list.css` (only if scroll button markup moves)

- [ ] **Step 1: Add `TimelineItemView.razor`**

```razor
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.Models.Timeline

@if (Item.Kind == TimelineItemKind.AssistantTurn && Item.Turn != null)
{
    <LoopGroupItem @key="Item.Key" Loop="Item.Turn" OnToolClick="OnToolClick" />
}
else if (Item.Message != null)
{
    @if (Item.Kind == TimelineItemKind.Reminder)
    {
        <SystemReminderMessageItem @key="Item.Key" Message="Item.Message" />
    }
    else if (Item.Kind == TimelineItemKind.ProjectInstructions)
    {
        <ProjectInstructionsMessageItem @key="Item.Key" Message="Item.Message" />
    }
    else if (Item.Kind == TimelineItemKind.Compaction)
    {
        <div @key="Item.Key" class="compaction-summary-message" ...>
            @((MarkupString)Item.Message.Content.Replace("\n", "<br/>"))
        </div>
    }
    else
    {
        <ChatMessageItem @key="Item.Key" Message="Item.Message" OnToolClick="OnToolClick" />
    }
}

@code {
    [Parameter] public TimelineItem Item { get; set; } = null!;
    [Parameter] public EventCallback<ToolCallViewModel> OnToolClick { get; set; }
    private int _renderedRevision = -1;

    protected override bool ShouldRender()
        => _renderedRevision != Item.Revision;

    protected override void OnAfterRender(bool firstRender)
        => _renderedRevision = Item.Revision;
}
```

- [ ] **Step 2: Rewrite `MessageList` parameters & render**

Replace `List<MessageViewModel> Messages` with:

```csharp
[Parameter] public IReadOnlyList<TimelineItem> Items { get; set; } = Array.Empty<TimelineItem>();
[Parameter] public bool AutoScroll { get; set; } = true;
[Parameter] public EventCallback<ToolCallViewModel> OnToolClick { get; set; }
```

Markup:

```razor
<div class="message-list" @ref="MessageListElement" id="@ContainerId">
  @if (Items.Count == 0) { <Empty>...</Empty> }
  else {
    <div class="message-list-container">
      @if (_windowStart > 0) {
        <div id="message-list-top-sentinel" class="message-list-top-sentinel">以上还有 @_windowStart 条</div>
      }
      @for (var i = _windowStart; i < Items.Count; i++)
      {
        var item = Items[i];
        <TimelineItemView @key="item.Key" Item="item" OnToolClick="OnToolClickHandler" />
      }
      <div id="@ScrollAnchorId" class="scroll-anchor"></div>
      <div id="message-list-scroll-to-bottom" class="scroll-to-bottom-button" hidden
           onclick="forceScrollToBottom()">...</div>
    </div>
  }
</div>
```

Code-behind essentials:

- Remove `_currentRenderLoopIds`, `IsFirstAssistantInLoop`, `_loopCache`, `_loopMessages`, all Loop build helpers.
- Track `_windowStart`, `_lastItemCount`, `_lastTailKey`, `_scrollManagerInitialized`.
- `OnParametersSet`: if item count/first key changed → treat as reset → `_windowStart = MessageListWindow.ComputeInitialStart(Items.Count)`.
- If pinned path (we don’t know pin in C#): when `Items.Count` grows and `AutoScroll`, call `SlideWhilePinned` after render notify.
- `OnAfterRenderAsync`:
  - firstRender: `initMessageListScroll`
  - if content grew and AutoScroll: `notifyContentGrew` (**not** unconditional `smartScrollToBottom` every time)
  - **Do not** call `CheckScrollPositionAsync` / do not `StateHasChanged` for button
  - Detect near-top via JS `isNearTop` (add small helper) → `LoadMore` + `preserveScrollOnPrepend`

Add JS helper:

```javascript
function isNearTop(threshold = 80) {
  if (!messageListScrollManager?.container) return false;
  return messageListScrollManager.container.scrollTop < threshold;
}
function getScrollHeight() {
  return messageListScrollManager?.container?.scrollHeight ?? 0;
}
```

Load-more flow in `OnAfterRenderAsync` (throttle with `_loadMoreLock`):

```csharp
if (await JS.InvokeAsync<bool>("isNearTop") && _windowStart > 0)
{
    var prev = await JS.InvokeAsync<double>("getScrollHeight");
    _windowStart = MessageListWindow.LoadMore(_windowStart);
    await InvokeAsync(StateHasChanged);
    await JS.InvokeVoidAsync("preserveScrollOnPrepend", prev);
}
```

Prefer triggering load-more from a JS scroll callback via `DotNetObjectReference` **only for load-more**, never for button visibility. If using `DotNetObjectReference`, dispose it in `DisposeAsync`.

- [ ] **Step 3: Build WebUI**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`  
Expected: may fail until Task 7 updates `Session.razor` call site — if so, temporarily keep a thin adapter overload or proceed immediately to Task 7 in the same session.

- [ ] **Step 4: Commit** — **SKIP** unless user asks.

---

### Task 7: Session.razor integration

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/Session.razor`

- [ ] **Step 1: Inject store & subscribe**

```csharp
@inject MessageTimelineStore Timeline

protected override void OnInitialized()
{
    Timeline.Changed += OnTimelineChanged;
    // existing init...
}

private void OnTimelineChanged()
{
    // Only refresh message region — InvokeAsync StateHasChanged is OK but avoid extra work in header setters
    _ = InvokeAsync(StateHasChanged);
}

public void Dispose()
{
    Timeline.Changed -= OnTimelineChanged;
    // existing dispose...
}
```

- [ ] **Step 2: Replace message list binding**

```razor
<MessageList Items="@Timeline.Items"
             AutoScroll="true"
             OnToolClick="@OnToolClick" />
```

Remove empty-state branch that keys only on `_messageViewModels.Count` — use `Timeline.Items.Count` instead. Remove or stop updating `_messageViewModels` for display (can delete field once unused).

- [ ] **Step 3: Replace `RefreshMessageViewModels` call sites**

| Event path | Call |
|------------|------|
| Session load / switch / compaction success | `Timeline.ResetFromSession(SessionState.CurrentSession.Messages, SessionState.SessionId)` |
| Stream delta (throttled) | Prefer `Timeline.PatchStream(loopId, content, reasoning)` using ids from `EventStreamHandler` / delta event; if loopId unknown, patch last turn |
| Tool events | `Timeline.UpsertTool(...)` with VM from factory |
| Stream start / new assistant message | `Timeline.UpsertAssistantStep(...)` |
| Execution complete | `Timeline.CompleteTurn(...)` then optional light Reset if handler wrote many messages |
| Fallback when unsure | `Timeline.ResetFromSession(...)` (correctness over perf) |

Delete `RefreshMessageViewModels` Clear+rebuild once all paths migrated. Keep `MessageViewModelFactory` for any remaining conversions.

During stream throttle loop in `ConsumeEventStreamAsync`, **do not** call page-level refresh for every delta if `Timeline.Changed` already notifies — either:

- remove `await InvokeAsync(StateHasChanged)` after Patch when store raises `Changed`, or
- keep one path only (prefer store `Changed` → `OnTimelineChanged`).

- [ ] **Step 4: Build + existing WebUI tests**

Run:

```bash
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj
dotnet test tests/Seeing.Agent.WebUI.Tests
```

Expected: build OK; all tests PASS.

- [ ] **Step 5: Commit** — **SKIP** unless user asks.

---

### Task 8: Pipeline / LoopGroupItem revision cache

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/LoopGroupItem.razor`
- Modify: `samples/Seeing.Agent.WebUI/Models/LoopGroupViewModel.cs` (add `ContentRevision` mirroring item, or read from parameter)

- [ ] **Step 1: Pass revision into LoopGroupItem**

```razor
[Parameter] public int ContentRevision { get; set; }
```

From `TimelineItemView`:

```razor
<LoopGroupItem Loop="Item.Turn" ContentRevision="Item.Revision" OnToolClick="OnToolClick" />
```

- [ ] **Step 2: Cache RenderFragment / avoid rebuild**

In `LoopGroupItem`:

```csharp
private int _blocksRevision = -1;
private RenderFragment? _body;

protected override void OnParametersSet()
{
    if (_blocksRevision == ContentRevision && _body != null)
        return;
    _blocksRevision = ContentRevision;
    _body = Pipeline.RenderLoop(Loop, GetRenderOptions(), null, OnToolClick);
}

protected override bool ShouldRender()
    => true; // parent TimelineItemView already gates; or also gate on ContentRevision
```

Markup: `@_body` instead of calling `Pipeline.RenderLoop` inline every time.

- [ ] **Step 3: Build**

Run: `dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`  
Expected: success.

- [ ] **Step 4: Commit** — **SKIP** unless user asks.

---

### Task 9: Manual verification checklist

- [ ] **Step 1: Run WebUI**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

- [ ] **Step 2: Verify scenarios**

1. Start a long reply; expand/collapse a tool mid-stream → assistant stays visible; no jump to top.
2. Scroll up mid-stream → stays put while tokens continue; no auto return after 3s.
3. Click back-to-bottom → pins and follows again.
4. Session with 100+ turns (or generate) → initial DOM ~40; scroll up loads earlier; near-bottom slide unloads oldest while pinned.
5. Compact / switch session → list resets; scrolled to bottom; no duplicate keys in console.
6. Browser console: no repeated Blazor circuit errors / JSDisconnectedException from scroll helpers.

- [ ] **Step 3: Commit** — **SKIP** unless user asks.

---

## Spec coverage self-check

| Spec requirement | Task |
|------------------|------|
| TimelineItem Key/Revision/Kinds | Task 1 |
| MessageTimelineStore Reset/Apply APIs | Task 4 |
| Skip render-time Loop HashSet | Task 6 |
| Strict scroll-up / no 3s resume | Task 5 |
| Sliding window 40/20/80 | Task 2 + 6 |
| Session incremental + reduce page redraw | Task 7 |
| Pipeline blocks by Revision | Task 8 |
| No Virtualize / no persistence change | Honored throughout |
| Unit tests Store + Window | Task 2, 4 |
| Manual UX acceptance | Task 9 |
| `role=tool` not separate timeline rows | Task 4 note |

## Placeholder / consistency check

- Types: `TimelineItem`, `TimelineItemKind`, `MessageTimelineStore`, `MessageListWindow`, `MessageViewModelFactory` — names consistent across tasks.
- Window constants: `40` / `20` / `80` only in `MessageListWindow`.
- Scroll API names: `notifyContentGrew`, `preserveScrollOnPrepend`, `forceScrollToBottom` — Task 5 defines, Task 6 consumes.
- No TBD left in steps.

---

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-04-message-timeline-rendering.md` (not git-committed).

**Two execution options:**

1. **Subagent-Driven (recommended)** — fresh subagent per task, review between tasks  
2. **Inline Execution** — execute tasks in this session with checkpoints  

Which approach?
