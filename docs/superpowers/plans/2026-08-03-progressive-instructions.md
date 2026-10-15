# Progressive Project Instructions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hierarchical `AGENTS.md` discovery with fingerprint-based `<project-instructions>` injection into Session (not SystemPrompt), exposed only via `IInstructionManager`.

**Architecture:** All instruction code lives in `src/Seeing.Agent/Core/Instructions/`. `InstructionManager` is the sole public facade. `ExecutionJobService.SubmitAsync` calls `InjectIfNeededAsync` before persisting the real user message. Delete `IInstructionLoader`, `InstructionLoader`, and all `{{instructions}}` PromptBuilder paths.

**Tech Stack:** .NET 10, xUnit, FluentAssertions, Moq, Seeing.Session `SessionData`/`SessionMessage`

## Global Constraints

- **NO GIT COMMITS** — User mandate for this execution. Never run `git commit`, `git add` for committing, or amend. Leave all changes in the working tree.
- Spec source of truth: `docs/superpowers/specs/2026-08-03-progressive-instructions-design.md`
- Only file name: `AGENTS.md`
- Merge: append all layers; global → project → closer; closer last
- Injection: Session `user` message with `<project-instructions>`; never SystemPrompt
- Dedup: path → SHA256 content hash fingerprints in `session.Metadata["instruction.fingerprints"]`
- Child sessions: independent fingerprints (no inheritance)
- Public API: only `IInstructionManager`; discovery/fingerprint/renderer are `internal`
- Code location: unify under `src/Seeing.Agent/Core/Instructions/` — do not scatter
- Delete old: `IInstructionLoader`, `InstructionLoader`, `{{instructions}}` entirely (no empty-string shim)
- `Seeing.Agent.Tests` already has `InternalsVisibleTo`
- Target TFM: `net10.0`

## File map

| Path | Role |
|------|------|
| `Core/Instructions/IInstructionManager.cs` | Public facade |
| `Core/Instructions/InstructionManager.cs` | Facade impl |
| `Core/Instructions/InstructionFile.cs` | Model (keep/extend with Hash) |
| `Core/Instructions/InstructionInjectResult.cs` | Inject result |
| `Core/Instructions/InstructionFingerprintSnapshot.cs` | Read-only fingerprint view |
| `Core/Instructions/InstructionDiscovery.cs` | internal discovery |
| `Core/Instructions/InstructionFingerprintStore.cs` | internal metadata JSON + diff |
| `Core/Instructions/ProjectInstructions.cs` | Tag/keys/reasons |
| `Core/Instructions/ProjectInstructionsRenderer.cs` | Wrap/TryParse/CreateUserMessage |
| Delete `IInstructionLoader.cs`, `InstructionLoader.cs` | — |
| Modify `PromptBuilder.cs` | Remove instruction loader dependency |
| Modify `ServiceCollectionExtensions.cs` | Register `IInstructionManager` |
| Modify `ChatOptions.cs` | Add `SkipInstructionInject` |
| Modify `ExecutionJobService.cs` | Inject before user message |
| Tests under `tests/Seeing.Agent.Tests/Instructions/` | Replace InstructionLoaderTests |

---

### Task 1: Discovery + models + unit tests

**Files:**
- Create/Modify: `src/Seeing.Agent/Core/Instructions/InstructionFile.cs`
- Create: `src/Seeing.Agent/Core/Instructions/InstructionDiscovery.cs`
- Create: `tests/Seeing.Agent.Tests/Instructions/InstructionDiscoveryTests.cs`
- Delete when obsolete later: keep `InstructionLoader*` until Task 3/4 removes them — for Task 1 only add new types alongside

**Interfaces:**
- Produces: `internal sealed class InstructionDiscovery` with `Task<IReadOnlyList<InstructionFile>> DiscoverAsync(string cwd, string workspaceRoot, CancellationToken ct = default)`
- `InstructionFile`: `Path`, `Content`, `LastModified`, `Hash` (sha256 hex prefixed `sha256:`)

Discovery order (spec §2):
1. `~/.agents/AGENTS.md`
2. `~/.seeing/AGENTS.md`
3. `{workspace}/.agents/AGENTS.md`
4. `{workspace}/.seeing/AGENTS.md`
5. `{workspace}/AGENTS.md`
6. Ancestor chain from first child under workspace → cwd

Rules: skip missing; skip read errors (log Warning); if cwd outside workspace → only 1–2; normalize absolute paths; dedupe by path (first wins).

- [ ] **Step 1:** Write failing tests for order, workspace-outside, dedupe, ancestor chain
- [ ] **Step 2:** Run `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~InstructionDiscoveryTests"` — expect FAIL
- [ ] **Step 3:** Implement `InstructionDiscovery` + extend `InstructionFile`
- [ ] **Step 4:** Re-run tests — PASS
- [ ] **Step 5:** Do NOT commit

---

### Task 2: Fingerprint store + ProjectInstructions renderer

**Files:**
- Create: `ProjectInstructions.cs`, `ProjectInstructionsRenderer.cs`, `InstructionFingerprintStore.cs`, `InstructionFingerprintSnapshot.cs`, `InstructionInjectResult.cs`
- Test: `ProjectInstructionsRendererTests.cs`, `InstructionFingerprintStoreTests.cs`

**Interfaces:**
- `ProjectInstructions.Tag = "project-instructions"`
- MetadataKeys: `projectInstructions`, `instructionReason`, `instructionCwd`, `instructionPaths`
- Reasons: `initial`, `cwd-change`, `content-change`, `none`
- Fingerprint metadata key: `instruction.fingerprints`
- Store: `Load`, `Diff(discovered)`, `MergeAndSave(session, cwd, injectedFiles)`
- Renderer: `Wrap`, `TryParse`, `CreateUserMessage(cwd, reason, files)` → `SessionMessage` with metadata

Mirror escape style from `SystemReminderRenderer` (`<\/` style for closers in body).

- [ ] **Step 1:** Failing tests for Wrap/TryParse roundtrip, Diff first/no-change/content-change, corrupt JSON → empty
- [ ] **Step 2:** Run filtered tests — FAIL
- [ ] **Step 3:** Implement
- [ ] **Step 4:** Tests PASS
- [ ] **Step 5:** Do NOT commit

---

### Task 3: InstructionManager + DI; remove IInstructionLoader from PromptBuilder

**Files:**
- Create: `IInstructionManager.cs`, `InstructionManager.cs`
- Modify: `PromptBuilder.cs` — remove `_instructionLoader`, `{{instructions}}` branch, constructor deps
- Modify: `ServiceCollectionExtensions.AddPromptBuilder` — register `IInstructionManager` only
- Delete: `IInstructionLoader.cs`, `InstructionLoader.cs`, `InstructionLoaderTests.cs`
- Test: `InstructionManagerTests.cs`
- Fix any compile breaks from PromptBuilder ctor changes

`IInstructionManager`:
```csharp
Task<IReadOnlyList<InstructionFile>> DiscoverAsync(string cwd, string workspaceRoot, CancellationToken ct = default);
Task<InstructionInjectResult> InjectIfNeededAsync(SessionData session, string cwd, string workspaceRoot, CancellationToken ct = default);
InstructionFingerprintSnapshot GetFingerprints(SessionData session);
```

InjectIfNeededAsync: discover → diff → if empty return none; else CreateUserMessage, `session.Messages.Add`, merge fingerprints; determine reason per spec.

- [ ] **Step 1–4:** TDD Manager inject/no-op; delete old loader; update DI/PromptBuilder; tests PASS
- [ ] **Step 5:** Do NOT commit

---

### Task 4: ExecutionJobService hook + ChatOptions.SkipInstructionInject

**Files:**
- Modify: `src/Seeing.Agent.App/Models/ChatOptions.cs` — add `bool SkipInstructionInject`
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs` — before user message persist, resolve cwd, update `session.WorkingDirectory`, call `InjectIfNeededAsync` unless skipped; swallow inject exceptions (log Error); still inject when `SkipUserMessagePersist` unless `SkipInstructionInject`
- Ensure `Seeing.Agent.App` can resolve `IInstructionManager` from DI (already references Seeing.Agent)

- [ ] **Step 1:** Implement hook + option
- [ ] **Step 2:** `dotnet build` solution — PASS
- [ ] **Step 3:** Do NOT commit

---

### Task 5: Cleanup verification + residual tests

**Files:**
- Grep repo for `IInstructionLoader`, `{{instructions}}`, `InstructionLoader` — must be gone from src/tests (except historical docs if any)
- Add/adjust Submit-level test if feasible in App tests; otherwise Manager+Discovery coverage is enough + build
- Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~Instruction"`
- Run: `dotnet build`

- [ ] **Step 1:** Grep cleanup
- [ ] **Step 2:** Full instruction test filter + build PASS
- [ ] **Step 3:** Do NOT commit

---

## Self-review notes

- Spec §1–10 covered by Tasks 1–5
- WebUI bubble explicitly out of scope
- Compaction rehydrate out of scope
- No commit steps per user mandate
