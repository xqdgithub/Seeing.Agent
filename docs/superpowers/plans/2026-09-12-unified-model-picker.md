# Unified Model Picker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two shared WebUI model pickers (Dropdown + Badge/Modal) backed directly by `IModelConfigManager`, replacing inconsistent Select usages.

**Architecture:** Helper maps catalog → `ModelPickerItem`. Components inject Manager, subscribe to `ModelConfigChanged`, no UI TTL cache. Session uses Badge+Modal; config pages use Dropdown.

**Tech Stack:** Blazor Server, AntDesign, `IModelConfigManager`, xUnit/FluentAssertions for helper tests.

## Global Constraints

- No `IModelPickerSource` / extra DI catalog service.
- No new UI static TTL cache; remove `SessionToolbar.s_optionCache`.
- Catalog key format remains `provider/model`.
- ACP free-text inputs untouched.
- Do not commit unless the user explicitly asks (repo rule).
- Prefer Chinese UI copy consistent with existing pages.
- Target `net10.0`; tests via VSTest on built DLLs when MTP finds zero tests.

## File map

| File | Responsibility |
|------|----------------|
| `samples/.../Components/Models/ModelPickerItem.cs` | Item DTO + `Build` filter helper |
| `samples/.../Components/Models/ModelSelectDropdown.razor` | Form dropdown |
| `samples/.../Components/Models/ModelSelectModal.razor` | Selection modal |
| `samples/.../Components/Models/ModelSelectBadge.razor` | Badge + opens modal |
| `samples/.../wwwroot/css/` (or component styles) | Badge sizing, modal row layout |
| `tests/apps/Seeing.Agent.WebUI.Tests/.../ModelPickerItemTests.cs` | Helper unit tests |
| Call sites: SessionToolbar, Settings, AgentConfigForm, JobEditorModal, MemorySettingsPage, ModelsPage | Wire components |
| `ModelSelector.razor` | Delete or redirect to Dropdown |

---

### Task 1: ModelPickerItem helper + tests

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/Models/ModelPickerItem.cs`
- Create: `tests/apps/Seeing.Agent.WebUI.Tests/ModelPickerItemTests.cs` (or Helpers folder matching existing tests)

- [ ] **Step 1:** Add failing tests for `Build`: filter by `ModelType.Text` / `Embedding`, optional `providerId`, orphan detection helper if exposed, free flag from metadata (same rules as ModelsPage if shared).
- [ ] **Step 2:** Implement `ModelPickerItem` with fields: `Key`, `Provider`, `ModelId`, `DisplayName`, `Types`, `Context`, `Output`, `IsFree`, `SupportsThinking`, and static `Build(IReadOnlyDictionary<string,ModelConfig> models, ModelType? filter, string? providerId)`.
- [ ] **Step 3:** Run WebUI.Tests for this class; fix until green.

---

### Task 2: ModelSelectDropdown + ModelSelectModal + ModelSelectBadge

**Depends on:** Task 1

**Files:**
- Create the three `.razor` components under `Components/Models/`
- Add minimal CSS for readable Badge (not chip-sized) and modal rows

- [ ] **Step 1:** Implement Dropdown: inject Manager; load via `GetModelsByType` / `GetModels` + `ModelPickerItem.Build`; subscribe `ModelConfigChanged`; AntDesign Select grouped by Provider; Provider tag on items; AllowClear; orphan option.
- [ ] **Step 2:** Implement Modal: search, group by provider, rich row metadata, click-to-select-and-close, optional refresh calling `RefreshCatalogAsync`.
- [ ] **Step 3:** Implement Badge: large enough for toolbar; shows `Provider · DisplayName`; opens Modal; AllowClear; invalid/orphan visual states via parameters (`IsInvalid` optional).
- [ ] **Step 4:** Smoke-build WebUI project.

---

### Task 3: Wire SessionToolbar (Badge) + remove option cache

**Depends on:** Task 2

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionToolbar.razor`
- Possibly: `SessionWindow.razor` / context if `ModelInvalid` plumbing needs a parameter

- [ ] Replace Native model `<Select>` with `ModelSelectBadge` bound to `Context.SelectedModel` / `OnModelChanged`.
- [ ] Delete `s_optionCache`, `OptionCacheTtl`, and model-list loading used only for that Select (keep Agent select loading as needed).
- [ ] Pass invalid state from `Context.ModelInvalid`.
- [ ] Build WebUI.

---

### Task 4: Wire config pages to Dropdown (parallelizable)

**Depends on:** Task 2

**Files:**
- `Pages/Settings.razor`
- `Components/Agents/AgentConfigForm.razor` (+ parent model keys if redundant)
- `Components/CronJobs/JobEditorModal.razor`
- `Pages/MemorySettingsPage.razor`
- `Pages/ModelsPage.razor` (test model picker only)
- Delete or thin-wrap `Components/ModelSelector.razor` if unused

- [ ] Replace each catalog Select with `ModelSelectDropdown` and correct `FilterType` / `ProviderId` / `AllowClear`.
- [ ] Remove local `_models` / `_modelKeys` lists that become redundant.
- [ ] Build WebUI + relevant tests.

---

### Task 5: Verification + final review

- [ ] `dotnet build` WebUI + `Seeing.Agent.WebUI.Tests`
- [ ] Run `ModelPickerItemTests` via vstest
- [ ] Manual checklist: Models refresh → Session Badge shows new Text models; Settings/Agent/Cron/Memory/Models test pickers show Provider; orphan + clear behaviors
- [ ] Whole-change review against `docs/superpowers/specs/2026-09-12-unified-model-picker-design.md`
