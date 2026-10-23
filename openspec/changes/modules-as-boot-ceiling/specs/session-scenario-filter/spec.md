## ADDED Requirements

### Requirement: Scenario is a work mode with its own Modules list

A scenario definition SHALL carry its own `Modules` list (module ids) for session filtering and work-mode fields (`DefaultAgent`, optional `Tools.Disabled`, `Seams`). Scenario MUST NOT reference a capability set by foreign key. Scenario MUST NOT be used as the process Activate ceiling or process seam binding source. The `Modules` list (module layer) and `Tools.Disabled` (tool layer) operate at different layers and MUST NOT be mixed in one formula.

#### Scenario: Built-in scenario uses its own Modules

- **WHEN** the built-in `code` scenario defines `Modules` containing filesystem-related module ids
- **THEN** session settlement MUST use that scenario's `Modules` (after ∩ bootEnabled) as the module basis, independent of any capability set

#### Scenario: Two scenarios may overlap Modules

- **WHEN** two scenarios define overlapping but not identical `Modules` lists with different `DefaultAgent` values
- **THEN** each MUST resolve independently against bootEnabled, differing in both module basis and work-mode fields

### Requirement: Session effective modules intersect bootEnabled (module layer only)

The system SHALL compute session effective modules (module layer) as:

`sessionModules = scenario.Modules ∩ bootEnabled` (then ∩ `session.ScenarioOverride.Modules.Enabled` if present, still ∩ bootEnabled)

This computation MUST operate on module ids only and MUST NOT apply tool-level filters. Session settlement MUST resolve scenario definitions via `IScenarioCatalog` (configured ∪ built-in), not built-in-only lookup.

#### Scenario: Escalate session mode under wide Boot

- **WHEN** `Boot` is `*` (so filesystem is boot-enabled) and process default `Scenario` is `minimal` while the session `Scenario` is `full` (whose `Modules` includes filesystem)
- **THEN** session effective modules MUST include filesystem (it is in both scenario.Modules and bootEnabled)

#### Scenario: Session cannot exceed Boot

- **WHEN** `Boot` is `minimal` (filesystem not in bootEnabled) and session `Scenario` is `full` (Modules include filesystem)
- **THEN** session effective modules MUST NOT include filesystem

#### Scenario: Override cannot widen past boot

- **WHEN** `session.ScenarioOverride.Modules.Enabled` lists a module id not in bootEnabled
- **THEN** that module id MUST be excluded from session effective modules

#### Scenario: Custom scenario via catalog

- **WHEN** a custom scenario under `Scenarios` defines its own `Modules` and the session uses that scenario name
- **THEN** settlement MUST use that definition's Modules and MUST NOT treat the scenario as unknown empty

### Requirement: Settled tool ids expand modules then apply tool filters (tool layer)

After session effective modules are computed (module layer), the system SHALL derive settled tool ids (tool layer) as:

`settledToolIds = ⋃ sessionModules.ProvidedTools − scenario.Tools.Disabled − session.ScenarioOverride.Tools.Disabled − Modules.Tools.Disabled`

Tool-level filters (`Tools.Disabled`) MUST apply only after `ProvidedTools` expansion and MUST NOT affect module-layer membership or Activate decisions. Schema is then `ToolManager registry ∩ settledToolIds ∩ Agent AllowedTools − DeniedTools`.

#### Scenario: Tools.Disabled removes tool not module

- **WHEN** session modules include `shell` (boot-enabled) and `scenario.Tools.Disabled` contains `bash`
- **THEN** `bash` MUST be absent from settled tool ids while the `shell` module remains in session effective modules and remains Activated

#### Scenario: Module not boot-enabled yields no tools

- **WHEN** a module is not in bootEnabled but appears in scenario.Modules
- **THEN** its ProvidedTools MUST NOT appear in settled tool ids (module-layer intersection already excluded it)

### Requirement: Process Scenario is default work mode name only

The process `Scenario` field SHALL set the default work mode for new sessions and the fallback when `session.Scenario` is null. It MUST NOT drive Activate or process seam binding.

#### Scenario: New session materializes default work mode

- **WHEN** a new root session is created and process `Scenario` is `code`
- **THEN** the session MUST persist `Scenario = code` (across all root session create paths: SessionManager, WebUI, Gateway)

### Requirement: Process chrome follows bootEnabled not scenario name

Process navigation and similar chrome MUST gate on boot-enabled modules (`IModuleCatalog.IsEnabled` / Requires) and MUST NOT hide contributions solely because the process default `Scenario` name mismatches a contribution's scenario whitelist when Requires are satisfied by bootEnabled. `UiContributionVisibility.MatchesProcessScenario` (or equivalent) MUST NOT be used as a hard hide for boot-enabled modules.

#### Scenario: Nav visible with Boot star and minimal default scenario

- **WHEN** `Boot` is `*` so filesystem is enabled and process `Scenario` is `minimal`
- **THEN** nav contributions requiring filesystem MUST remain visible (subject to Requires), not hidden by process scenario name mismatch

#### Scenario: Nav hidden when module not boot-enabled

- **WHEN** `Boot` is `minimal` so `mcp` is not boot-enabled and a nav contribution Requires `mcp`
- **THEN** that contribution MUST be hidden regardless of process scenario name
