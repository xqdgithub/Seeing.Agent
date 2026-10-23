## Context

The current `/compact` command in Session.razor uses simple truncation (removes oldest messages, keeps last 10). This loses valuable context about user goals, decisions made, files involved, and progress status. The user experience degrades after compression because the AI agent no longer remembers what was discussed.

**Current State:**
- `SessionCompressor` class exists in `Seeing.Session/Compression/` - implements sliding window truncation
- `ILlmService` provides chat completion API
- `SessionMessage.Metadata` dictionary can store arbitrary markers
- `SessionState` manages UI state including `IsExecuting`

**Constraints:**
- Must not modify core `Seeing.Session` package (ICompressionStrategy interface is synchronous)
- Must have fallback when AI summarization fails
- Should use cost-effective models (gpt-4o-mini preferred)
- Total new code should be < 200 lines

## Goals / Non-Goals

**Goals:**
- Generate structured AI summaries preserving: goals, decisions, progress, files, blockers
- Graceful fallback to existing `SessionCompressor` on failure
- Visual distinction for summary messages in UI
- Minimal dependencies and code footprint

**Non-Goals:**
- Modifying `ICompressionStrategy` interface (would require async changes)
- Supporting multi-turn summarization (v2 feature)
- Persisting summaries separately from session messages
- Auto-compaction triggers (manual `/compact` only for v1)

## Decisions

### 1. Separate Service vs Strategy Implementation

**Decision:** Create `SessionCompactionService` as independent service, NOT implementing `ICompressionStrategy`

**Rationale:**
- `ICompressionStrategy.Compress()` is synchronous (`IReadOnlyList<SessionMessage> Compress(...)`)
- AI summarization requires async LLM calls
- Modifying the interface would break existing implementations
- Service approach allows clean separation of concerns

**Alternatives Considered:**
- Extend interface with `CompressAsync()` → Rejected: breaking change, affects multiple packages
- Block on async call → Rejected: would freeze UI thread

### 2. Model Selection Strategy

**Decision:** Hardcoded preference list with fallback to first available model

**Rationale:**
- Compaction is low-stakes operation (fallback exists)
- Cost optimization matters (runs frequently)
- Simplicity over configurability for v1

**Model Priority:** `gpt-4o-mini` > `gpt-3.5-turbo` > `claude-3-haiku` > `deepseek-chat` > first available

### 3. Summary Format

**Decision:** Markdown-formatted structured summary with emoji markers

**Rationale:**
- Matches existing message format in codebase
- Readable in both raw and rendered forms
- Emoji markers provide quick visual scanning

**Template Sections:**
1. User goals (what they want to achieve)
2. Key decisions (what was decided)
3. Work in progress (current tasks)
4. Open issues (blockers, questions)
5. Relevant files (paths with context)

### 4. Fallback Mechanism

**Decision:** Use existing `SessionCompressor` with system notice message

**Rationale:**
- Proven implementation already exists
- No new code paths to test
- User gets clear feedback that fallback occurred

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| LLM call fails | Automatic fallback to `SessionCompressor` with user notification |
| Summary loses critical info | Prompt engineering + preserve file paths explicitly |
| High token cost on large sessions | Cap history at 500 chars per message before summarization |
| Concurrent compaction attempts | `SessionState.IsCompacting` flag prevents re-entry |
| Model not configured | Constructor throws clear error message |
| Summary message confused with system message | `is_compaction_summary` metadata marker + special UI rendering |

## Migration Plan

**Deployment:** Zero-downtime, additive change only

1. Deploy new service and UI changes
2. `/compact` command automatically uses new behavior
3. No data migration needed (sessions unchanged)
4. No rollback needed (fallback always available)

**Rollback:** Remove service registration, revert UI changes - simple code revert

## Open Questions

- Should we persist compaction history for debugging? (Deferred to v2)
- Should user be able to configure compaction model? (Deferred to v2)
- Should we support partial compaction (summarize only user messages)? (Deferred to v2)
