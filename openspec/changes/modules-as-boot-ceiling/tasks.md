## 1. Domain model, options, and registry

- [x] 1.1 Add `CapabilitySetDefinition` (`Modules` + optional `Disabled`, module-only) and `ICapabilitySetCatalog` (built-in ∪ configured overrides)
- [x] 1.2 Extract `BuiltInCapabilitySets` from current BuiltInScenarios module lists (boot module SSOT); enumerate built-in set names: minimal/code/work/research/full (+ optional secure/dev)
- [x] 1.3 Make `BuiltInScenarios.Modules` reference `BuiltInCapabilitySets` constants at compile time (no array copy, 防漂移)
- [x] 1.4 Keep `ScenarioDefinition`/`ScenarioConfig` with own `Modules` (no capabilitySet foreign key); deprecate flat `ToolsDisabled`, unify to `Tools.Disabled`; finalize `SettlementResult.Scenario` as diagnostics-only (add `Boot` field, document `Scenario` = default work mode name)
- [x] 1.5 Extend `SeeingAgentOptions` with `Boot` (string) + `CapabilitySets` (dict); document `Modules.Enabled` ignored; keep `Modules.Disabled`
- [x] 1.6 `ConfigSectionRegistry.RegisterSpineSections`: register `Boot` + `CapabilitySets` (save/load/reload routing)
- [x] 1.7 Register `ICapabilitySetCatalog` in DI (`ServiceCollectionExtensions` TryAddSingleton, like `IScenarioCatalog`)
- [x] 1.8 Add `HostDefaultBoot` / `HostDefaultSeams` / `BootOverride` on `ProcessSettlementOptions`; priority BootOverride > file Boot > HostDefaultBoot
- [x] 1.9 `SettlementInput`: add `ConfiguredBoot` / `BootOverride` / `HostDefaultBoot` / `HostDefaultSeams`; `SettlementResult`: add `Boot` field; **保留** `Scenario` 字段，语义重定义为「进程默认工作模式名（仅诊断/日志）」，不被 Activate 消费

## 2. Boot settlement and lifecycle (module layer)

- [x] 2.1 Rewrite `SettlementEngine`: boot base = resolve(Boot) ∩ Available − Disabled(set) − Modules.Disabled; `["*"]` expanded at resolution layer (not literal IntersectWithAvailable); unknown Boot → SettlementException
- [x] 2.2 Stop using Scenario.Modules and Modules.Enabled as boot base
- [x] 2.3 Rewrite `ResolveSeamsMap` to read `Seams` + `HostDefaultSeams` only (not process Scenario name); `ValidateAndBindExclusiveSeams` validates providers ∈ bootEnabled
- [x] 2.4 `ModuleSettlementReloadHandler.s_relevantSections`: split — boot path listens to `Boot`/`CapabilitySets`/`Modules.Disabled`/`Seams`/`Workspace`; remove `Scenarios`/`Scenario` from boot Activate diff trigger
- [x] 2.5 `InitializeSeeingAsync` / `SettleAndActivateModulesAsync`: pass new SettlementInput (Boot, not Scenario base); inject `ICapabilitySetCatalog` into reload handler and initialize path (same pattern as `IScenarioCatalog`)
- [x] 2.6 Tests: Boot star; Boot named with Disabled; Boot minimal whitelist; `["*"]` not literal; empty Modules→empty; Scenario default ≠ shrink boot; unknown Boot throws; Modules.Enabled ignored+warn; capability set edit deactivates; DependsOn on boot set; BoundSeams from Seams+HostDefault; Scenario.Seams not binding; **Scenarios-only reload refreshes catalog** (no boot diff, IScenarioCatalog.Get hits next Submit)

## 3. Session filter path (two layers, separated)

- [x] 3.1 `SessionSettlement` module layer: `sessionModules = scenario.Modules ∩ catalog.Enabled` (then ∩ override.Modules.Enabled), module ids only, no tool filters (verify unchanged shape)
- [x] 3.2 `SessionSettlement` tool layer: `settledToolIds = ⋃ ProvidedTools − Tools.Disabled*`; verify Tools.Disabled never enters module formula
- [x] 3.3 `ExecutionJobService.SettleSessionAndSchemasAsync`: inject and pass `IScenarioCatalog` (not BuiltIn-only)
- [x] 3.4 Tests: Boot=* + session full gets wide tools; Boot=minimal + session full no filesystem tools; Tools.Disabled drops tool not module; custom scenario + own Modules works; override cannot widen past boot
- [x] 3.5 Materialize default `session.Scenario` on ALL root session create paths (SessionManager, WebUI, Gateway); MUST persist

## 4. UI, host, migration surface

- [x] 4.1 Settings UI: Boot + CapabilitySets editor vs Scenario work mode; warn on Modules.Enabled; deprecate flat ToolsDisabled in UI
- [x] 4.2 `UiContributionVisibility`: remove/adjust `MatchesProcessScenario` hard hide for boot-enabled modules; nav = Requires + bootEnabled only; Scenario whitelist only for session shell/slots
- [x] 4.3 `ScenariosSettingsCard` copy: 场景=工作模式；启动能力=Boot+能力集
- [x] 4.4 CLI/env `--boot` → BootOverride：`BootOverrideSource` 解析 `--boot`/`SEEING_BOOT`；CLI `start`/`web`/`gateway` 传子进程 env；WebUI/Gateway/Cli/Host Shape 接线
- [x] 4.5 Host Shape defaults: `HostDefaultSeams` (all shapes: at least `executionWorld=io.local`) + `HostDefaultBoot` where needed (e.g. Embed→minimal); document per-shape default table in design/docs

## 5. Save/Load and config sections

- [x] 5.1 `SaveLevel` / `RemoveSeeingAgentKeys`: support null-delete for `Boot` and `CapabilitySets` keys
- [x] 5.2 Config load/save round-trip tests for Boot + CapabilitySets (PascalCase JSON keys)
- [x] 5.3 Migration: detect `Modules.Enabled` → warn + ignore; detect flat `ToolsDisabled` → merge into `Tools.Disabled`

## 6. Documentation

- [x] 6.1 Rewrite `docs/architecture/03` §3 (process formula: bootEnabled), §4 (session: two-layer, fix existing tools-in-module-layer error), §5 (reload table: split boot vs Scenarios), §8 (forbid Modules.Enabled), §9 (troubleshoot Boot), §10 (add ICapabilitySetCatalog)
- [x] 6.2 Update `docs/architecture/01` §3 (two-axis: Boot/CapabilitySet vs Scenario), §4 (execution path formulas), §5 (consistency table bootEnabled), §7 (lifecycle input Boot)
- [x] 6.3 Update `docs/architecture/05` §4 JSON example: add Boot + CapabilitySets (PascalCase)
- [x] 6.4 Optional `docs/architecture/06` compliance row for CapabilitySet (Abstractions/Core only)
- [x] 6.5 Migration notes: Scenario=minimal sandbox → Boot=minimal; Modules.Enabled → CapabilitySet; ToolsDisabled flat → Tools.Disabled

## 7. Verification

- [x] 7.1 VSTest: settlement, reload (boot vs Scenarios split + catalog refresh on Scenarios-only change), session settlement (module + tool layers), visibility, config round-trip
- [x] 7.2 Update existing fixtures that assume processScenario=boot (VisibilityTests, SettlementEngineTests, SessionSettlementTests)
- [x] 7.3 Smoke: Boot=* Scenario default minimal, session switch to full → tools escalate; Boot=minimal → session full does not; Tools.Disabled drops tool but keeps module; BoundSeams from Seams not Scenario
