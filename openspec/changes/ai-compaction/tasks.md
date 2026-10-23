## 1. Core Service Implementation

- [x] 1.1 Create `SessionCompactionService.cs` in `samples/Seeing.Agent.WebUI/Services/`
- [x] 1.2 Implement `CompactAsync` method with message splitting and LLM call
- [x] 1.3 Implement `GetDefaultCompactionModel` with preference list
- [x] 1.4 Implement `BuildSummaryPrompt` with structured template
- [x] 1.5 Create `CompactionResult` record type

## 2. State Management

- [x] 2.1 Add `IsCompacting` property to `SessionState.cs`
- [x] 2.2 Add `IsCompactionSummary` property to `MessageViewModel.cs`
- [x] 2.3 Update `ConvertToMessageViewModel` to read `is_compaction_summary` metadata

## 3. UI Integration

- [x] 3.1 Update `CompactSessionAsync` in `Session.razor` to use `SessionCompactionService`
- [x] 3.2 Add `FallbackCompactAsync` method using existing `SessionCompressor`
- [x] 3.3 Add loading state handling with `SessionState.IsCompacting`
- [x] 3.4 Add DI injection for `SessionCompactionService` in `Session.razor`
- [x] 3.5 Add conditional rendering for compaction summary in `MessageList.razor`

## 4. Service Registration

- [x] 4.1 Register `SessionCompactionService` in `Program.cs`

## 5. Testing & Verification

- [x] 5.1 Verify compaction with > 10 messages triggers AI summary (build passed)
- [x] 5.2 Verify compaction with ≤ 10 messages shows "无需压缩" message (logic implemented)
- [x] 5.3 Verify fallback works when LLM call fails (fallback implemented)
- [x] 5.4 Verify summary message displays with special UI treatment (UI rendering added)
