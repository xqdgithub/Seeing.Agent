# P5-T11: Repair TaskTool / ExecutionJobService tests after Hosting/API split

**Branch:** `feature/modular-architecture`  
**Date:** 2026-09-08  
**Status:** Green

## Summary

11 failing tests were not caused by missing `IExecutionSubmitter` / `IExecutionStatusProvider` DI on TaskTool constructors (fixtures already passed `ExecutionJobService` for both). Root cause was `ProcessExecutionAsync` NRE on auto-compaction peek when tests used `Mock.Of<IConfigSectionStore>()`, plus a TaskStatus session-fallback state mapping bug.

## Root cause

1. **Primary — `IConfigSectionStore.GetSection` null → NRE**  
   Phase 4-5 added auto-compaction gating in `ExecutionJobService.ProcessExecutionAsync`:

   ```csharp
   _configStore.GetSection<TokenBudgetAutoCompactionPeek>("TokenBudget").AutoCompactionEnabled
   ```

   Contract (`T GetSection<T>() where T : class, new()`) always returns a value via `UnifiedConfigManager`. Test fixtures used `Mock.Of<IConfigSectionStore>()`, which returns `null` for `GetSection`. That NRE was caught, marked the execution **Failed**, published `ExecutionCompleteEvent`, and **skipped** command + agent paths.

   Symptoms matched all 11 failures:
   - Command short-circuit: command/agent never invoked, but Complete still arrived
   - TaskTool foreground: `completed` with empty assistant (“无输出内容”)
   - Background / Cancel / Running: never reached `Running` or synthetic notify (Complete fired before subscribe / WaitUntil)

2. **Secondary — `TaskStatusTool.FallbackFromSessionAsync`**  
   After fixing (1), completed child sessions that remain `SessionStatus.Active` were still reported as `state: running`. Aligned with `TaskTool` summary: no active execution + assistant content ⇒ `completed`.

## Fixes

| Area | Change |
|------|--------|
| Production | `ExecutionJobService`: null-conditional on GetSection peek |
| Production | `TaskStatusTool`: Active+result ⇒ completed; list fallback Active ⇒ completed when no exec |
| Tests | TaskTool / TaskStatus / Cancel / CommandShortCircuit / Instruction fixtures mock `GetSection` → `AutoCompactionEnabled = false` |

**Not reverted:** `AddSeeingCore` API.

## Verification

```text
Filter (TaskTool|TaskStatus|CommandShortCircuit|Cancel): 14 passed / 0 failed
Full Seeing.Agent.Tests: 839 passed / 0 failed / 0 skipped
```

## Commit

`fix(tests): repair TaskTool and ExecutionJobService tests after Hosting/API split`

## Concerns

- Other tests still constructing `ExecutionJobService` with bare `Mock.Of<IConfigSectionStore>()` (e.g. PermissionChannel unit-only ctor) are safe only if they never enter `ProcessExecutionAsync`; prefer explicit GetSection mock in any new execution-path fixture.
- `TokenBudgetAutoCompactionPeek.AutoCompactionEnabled` defaults to `true` when a real `new T()` is returned — production behavior unchanged; tests explicitly disable it.
- Background TaskTool still races Subscribe vs early Complete if executor finishes before `Task.Run` attaches; fixtures use delay to avoid flakiness.
