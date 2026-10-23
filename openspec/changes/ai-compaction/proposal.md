## Why

The current `/compact` command simply truncates old messages, losing valuable context about user goals, decisions, and progress. This leads to degraded conversation quality after compression. Users need an AI-powered summarization that preserves key information while reducing token usage.

## What Changes

- **New**: AI-powered session compaction service that generates structured summaries
- **New**: Fallback mechanism using existing `SessionCompressor` when AI summarization fails
- **New**: Special UI rendering for compaction summary messages
- **Modified**: `/compact` command behavior from simple truncation to AI summarization

## Capabilities

### New Capabilities

- `session-compaction`: AI-powered session history summarization that preserves key context (goals, decisions, progress, files) while reducing message count

### Modified Capabilities

- None (this is a new feature, no existing spec-level behavior changes)

## Impact

**Files Added:**
- `samples/Seeing.Agent.WebUI/Services/SessionCompactionService.cs` (~95 lines)

**Files Modified:**
- `samples/Seeing.Agent.WebUI/Pages/Session.razor` - Update `CompactSessionAsync`, add `FallbackCompactAsync`
- `samples/Seeing.Agent.WebUI/State/SessionState.cs` - Add `IsCompacting` property
- `samples/Seeing.Agent.WebUI/Models/MessageViewModel.cs` - Add `IsCompactionSummary` property
- `samples/Seeing.Agent.WebUI/Components/MessageList.razor` - Add compaction summary rendering
- `samples/Seeing.Agent.WebUI/Program.cs` - Register service

**Dependencies:**
- Existing `ILlmService` for AI summarization
- Existing `SessionCompressor` for fallback
- Existing `SessionMessage.Metadata` for storing compaction markers

**Total Code Impact:** ~95 lines new + ~50 lines modifications
