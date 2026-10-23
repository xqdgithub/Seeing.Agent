## ADDED Requirements

### Requirement: Capability sets are module-only named packs

The system SHALL provide named capability sets whose definition contains a module id list (`Modules`) and an optional module id exclusion list (`Disabled`). Capability sets MUST NOT contain work-mode fields (DefaultAgent, Tools, Seams). Built-in sets MUST be mergeable with configured `CapabilitySets` (same name: config overrides built-in). `Modules` MAY be `["*"]` meaning all available; this token MUST be expanded at the resolution layer and MUST NOT enter literal `IntersectWithAvailable` as a module id. Built-in sets MUST include `minimal`, `code`, `work`, `research`, `full`, `secure` (`Modules=["*"]` with `Disabled` containing shell/mcp/acp/gateway), and `dev` (same module list reference as `full`).

#### Scenario: List and resolve a set

- **WHEN** a built-in or configured capability set name exists
- **THEN** `ICapabilitySetCatalog.Get` MUST return its `Modules` and `Disabled` for settlement

#### Scenario: Config overrides built-in

- **WHEN** `CapabilitySets` in seeing.json defines the same name as a built-in set with a different `Modules` list
- **THEN** resolution MUST use the configured definition

#### Scenario: Star modules expands to all available

- **WHEN** a capability set has `Modules: ["*"]` and no `Disabled`
- **THEN** its resolved module basis MUST equal the host `Available` set, and the `"*"` token MUST NOT be passed to `IntersectWithAvailable` as a literal module id

#### Scenario: Empty modules yields empty basis

- **WHEN** a capability set has `Modules: []` (empty, not `["*"]`)
- **THEN** its resolved module basis MUST be empty

### Requirement: Boot pointer alone drives Activate membership

The system SHALL compute `bootEnabled` (module layer only) as:

- if `Boot` is `*`: `Available − Modules.Disabled`
- else: `(CapabilitySets[Boot].Modules ∩ Available) − CapabilitySets[Boot].Disabled − Modules.Disabled`

`ModuleCatalog.Enabled` MUST equal `bootEnabled`. Activate/Deactivate MUST follow diffs of `bootEnabled` only (module layer). Process default `Scenario` MUST NOT determine Activate membership. `Modules.Enabled` MUST NOT participate in settlement; if present, it MUST be ignored and the system SHOULD emit a warning.

#### Scenario: Boot star mounts available

- **WHEN** `Boot` is `*` and `Modules.Disabled` is empty
- **THEN** boot-enabled MUST equal the host `Available` set

#### Scenario: Boot named set with Disabled

- **WHEN** `Boot` is `secure`, `CapabilitySets.secure.Modules` is `["*"]` and `Disabled` is `["shell","mcp"]`
- **THEN** boot-enabled MUST equal `Available` minus `shell` and `mcp` (and any `Modules.Disabled`)

#### Scenario: Boot named set whitelist

- **WHEN** `Boot` is `minimal` and that capability set lists an explicit subset of `Available`
- **THEN** only modules in that intersection (minus set `Disabled` and `Modules.Disabled`) MUST be Activated

#### Scenario: Default scenario name does not shrink boot

- **WHEN** `Boot` is `*` and process `Scenario` is `minimal`
- **THEN** boot-enabled MUST remain `Available` (minus `Modules.Disabled`), not the minimal capability set

#### Scenario: Unknown Boot fails settlement

- **WHEN** `Boot` is a non-`*` name not found in the capability set catalog
- **THEN** settlement MUST fail with `SettlementException` and MUST NOT apply a new enabled set

#### Scenario: Modules.Enabled ignored

- **WHEN** configuration still contains `Modules.Enabled`
- **THEN** settlement MUST ignore it for bootEnabled and SHOULD emit a warning directing migration to CapabilitySets + Boot

### Requirement: Boot override from CLI and environment

The system SHALL resolve Boot override with priority: CLI `--boot` argument > environment variable `SEEING_BOOT` > file `Boot` > `HostDefaultBoot` > `*`. Host samples (WebUI, Gateway, CLI bootstrap) MUST apply `BootOverrideSource` after Host Shape registration. CLI `start` / `web` / `gateway` MUST accept `--boot` and pass it to child processes via `SEEING_BOOT`.

#### Scenario: CLI boot overrides file

- **WHEN** seeing.json has `Boot=*` and the host supplies `BootOverride` `secure` (via `--boot secure` or `SEEING_BOOT`)
- **THEN** bootEnabled MUST be computed from the `secure` capability set

#### Scenario: HostDefaultBoot used when file absent

- **WHEN** seeing.json has no `Boot` and `HostDefaultBoot` is `minimal` and no BootOverride is set
- **THEN** effective Boot MUST be `minimal`

### Requirement: Process BoundSeams bind from Seams and host default only

The system SHALL compute process-level BoundSeams from `SeeingAgent.Seams` (user override) merged with `HostDefaultSeams` (host default). Scenario.`Seams` MUST NOT drive process-level seam binding or Activate membership. `ValidateAndBindExclusiveSeams` MUST validate that seam providers are in `bootEnabled`.

#### Scenario: Seams override host default

- **WHEN** `SeeingAgent.Seams` sets `executionWorld=io.local` and `HostDefaultSeams` also sets `executionWorld=io.local`
- **THEN** BoundSeams MUST bind `executionWorld` to `io.local` (user wins)

#### Scenario: Scenario seams do not affect process binding

- **WHEN** process default `Scenario` is `code` whose `Seams` sets `executionWorld=io.local`, but `SeeingAgent.Seams` is empty and `HostDefaultSeams` is empty
- **THEN** BoundSeams MUST NOT bind `executionWorld` from the scenario; the seam remains unbound (and settlement fails if a consumer requires it)

#### Scenario: Seam provider must be boot-enabled

- **WHEN** BoundSeams binds `executionWorld` to `io.local` but `io.local` is not in `bootEnabled`
- **THEN** settlement MUST fail with `SettlementException`

### Requirement: Hard dependencies validated on boot set

The system SHALL reject settlement with `SettlementException` when any module in `bootEnabled` has a `DependsOn` target not in `bootEnabled`.

#### Scenario: Missing dependency refuses boot

- **WHEN** `bootEnabled` would include `shell` but not `io.local` and `shell` declares `DependsOn: io.local`
- **THEN** settlement MUST fail and MUST NOT replace `ModuleCatalog.Enabled`

### Requirement: Lifecycle follows Boot and capability set edits only

Changing `Boot`, a referenced capability set's `Modules`/`Disabled`, `Modules.Disabled`, `Seams`, or host `Available` MUST resettle and Activate/Deactivate accordingly (module layer). Changing only `Scenarios` entries or the process default `Scenario` string MUST NOT trigger boot Activate/Deactivate diffs.

#### Scenario: Editing capability set unloads modules

- **WHEN** `Boot` is `dev` and `CapabilitySets.dev.Modules` is edited to remove `mcp` while `mcp` was Activated
- **THEN** reload MUST Deactivate `mcp` (subject to in-flight deferral)

#### Scenario: Scenario default rename keeps mount

- **WHEN** only `Scenario` changes from `code` to `research` and Boot/capability sets are unchanged
- **THEN** Activated modules in `bootEnabled` MUST remain Activated; no Activate/Deactivate diff runs

#### Scenario: Editing Scenarios modules does not trigger boot diff

- **WHEN** `Scenarios.code.Modules` is edited to add `web` while `Boot` and `CapabilitySets` are unchanged
- **THEN** the reload handler MUST NOT run a boot Activate/Deactivate diff (only `IScenarioCatalog`/OptionsMonitor refresh for next session)

#### Scenario: Scenarios-only reload refreshes catalog for next session

- **WHEN** a custom scenario `my-review` is added under `Scenarios` (Boot/CapabilitySets unchanged) and a session subsequently uses `Scenario = my-review`
- **THEN** `IScenarioCatalog.Get("my-review")` MUST return the new definition on the next `SubmitAsync` without requiring a process restart or boot resettle

### Requirement: Built-in capability sets enumerated

The system SHALL ship built-in capability sets whose names and module lists are the single source of truth for boot module ids. Built-in scenarios MUST reference these sets' module constants at compile time (no array copy). Built-in sets MUST include: `minimal`, `code`, `work`, `research`, `full`, `secure`, and `dev`.

#### Scenario: Built-in set names resolve

- **WHEN** `Boot` is `code` and no configured `CapabilitySets.code` exists
- **THEN** `ICapabilitySetCatalog.Get("code")` MUST return the built-in `code` module list

#### Scenario: Secure and dev built-ins

- **WHEN** `ICapabilitySetCatalog.Get("secure")` and `Get("dev")` are called with no config overrides
- **THEN** `secure` MUST resolve to `Modules=["*"]` with Disabled containing `shell`, `mcp`, `acp`, `gateway`; and `dev` MUST share the same module list reference as `full`

#### Scenario: Built-in scenario references capability set constants

- **WHEN** `BuiltInScenarios.Code.Modules` is inspected
- **THEN** it MUST be the same reference as `BuiltInCapabilitySets.Code` (compile-time constant sharing, not a copied array)

### Requirement: Host default seams and boot per Host Shape

Each Host Shape (Web, Headless, Embed, Gateway) SHALL register `HostDefaultSeams` and MAY register `HostDefaultBoot` via `ProcessSettlementOptions`. `HostDefaultSeams` MUST at least bind `executionWorld` to a default provider (e.g. `io.local`) so settlement does not fail when user `Seams` is absent.

#### Scenario: Web host default seams

- **WHEN** the Web Host Shape initializes and user `SeeingAgent.Seams` is empty
- **THEN** `HostDefaultSeams` MUST provide `executionWorld = io.local` so a consumer of `executionWorld` does not cause SettlementException

#### Scenario: Embed host default boot

- **WHEN** the Embed Host Shape initializes and no file `Boot` and no `BootOverride` are set
- **THEN** `HostDefaultBoot` MAY be `minimal` (or another narrow set) to limit resource usage on embedded hosts

#### Scenario: Headless host default seams

- **WHEN** the Headless Host Shape initializes and user `SeeingAgent.Seams` is empty
- **THEN** `HostDefaultSeams` MUST provide `executionWorld = io.local` so settlement does not fail

#### Scenario: Gateway host default seams

- **WHEN** the Gateway Host Shape initializes and user `SeeingAgent.Seams` is empty
- **THEN** `HostDefaultSeams` MUST provide `executionWorld = io.local` so settlement does not fail

### Requirement: Settlement result exposes Boot

`SettlementResult` SHALL include a `Boot` field reflecting the effective boot pointer resolved (after override priority). `SettlementResult.Scenario` SHALL be retained and documented as "process default work mode name for diagnostics only"; it MUST NOT be used by settlement consumers as the Activate source.

#### Scenario: Boot field populated

- **WHEN** settlement completes with effective Boot `secure`
- **THEN** `SettlementResult.Boot` MUST equal `secure` regardless of the process default `Scenario` value

### Requirement: Capability set catalog registered in DI

`ICapabilitySetCatalog` MUST be registered in DI (e.g. `ServiceCollectionExtensions` TryAddSingleton) and `ConfigSectionRegistry` MUST register `Boot` and `CapabilitySets` spine sections so save/load and hot-reload routing work.

#### Scenario: New sections registered

- **WHEN** the host initializes
- **THEN** `ConfigSectionRegistry` MUST contain `Boot` and `CapabilitySets` registrations and `ICapabilitySetCatalog` MUST be resolvable from DI
