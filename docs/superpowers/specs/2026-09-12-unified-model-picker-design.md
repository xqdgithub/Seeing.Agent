# Unified Model Picker Design

**Date:** 2026-09-12  
**Status:** Implemented  
**Tracked doc:** [`docs/architecture/07-webui-model-picker.md`](../../architecture/07-webui-model-picker.md)

## Goal

Unify all catalog-based model selection in WebUI behind two dedicated components so Session and config pages stay consistent, while matching page density (Modal+Badge vs compact dropdown).

## Non-goals

- ACP free-text model / mode inputs remain as-is.
- Models page table CRUD UI unchanged (only the “test model” picker switches).
- No new DI service / `IModelPickerSource` abstraction.
- No new TTL cache in UI; `ModelConfigManager._modelCache` is the sole catalog source of truth.

## Components

Location: `samples/Seeing.Agent.WebUI/Components/Models/`

| Piece | Role |
|-------|------|
| `ModelPickerItem` (+ static build helper) | Map `ModelConfig` → UI row; filter by `ModelType?` / `providerId?` |
| `ModelSelectDropdown.razor` | Compact AntDesign Select for forms |
| `ModelSelectModal.razor` | Searchable grouped modal with richer metadata |
| `ModelSelectBadge.razor` | Toolbar button; click opens Modal |

Shared parameters (Dropdown + Badge):

- `Value` / `ValueChanged` — catalog key `provider/model`
- `FilterType` — `ModelType?`, default `Text`
- `ProviderId` — optional provider scope
- `AllowClear`, `Disabled`, `Placeholder`, `Style` / `Class`
- Orphan values: still shown + “不可用” marker
- Inject `IModelConfigManager`; subscribe `ModelConfigChanged` on init, unsubscribe on dispose; re-read on open

### Badge (Session)

- Height ~24px (align Agent `InputSize.Small`); width ~160px with ellipsis
- Primary: model display name (prefer full); secondary: gray small `/ provider` (truncates first)
- Tooltip for full catalog key (do not use AntDesign Button `Title`)
- Click opens Modal; optional clear when `AllowClear`
- `IsInvalid` / orphan danger styling

### Modal

- Search over id / name / provider
- Group by Provider
- Row: display name, `provider/id`, types, free flag, context/output, thinking if present
- Click row → select and close
- Optional “刷新目录” → `RefreshCatalogAsync()`
- Wire AntDesign Modal `Visible` + `VisibleChanged` (required for open/close)

### Dropdown (config pages)

- Search + group by Provider
- Item: model name + small Provider tag
- `AllowClear` + orphan handling

## Call-site mapping

| Site | Component |
|------|-----------|
| Session toolbar (Native) | `ModelSelectBadge` |
| Settings default model | `ModelSelectDropdown` |
| Agent config default model | `ModelSelectDropdown` |
| Cron job model | `ModelSelectDropdown` |
| Memory embedding / extraction | `ModelSelectDropdown` (`Embedding` / `Text`) |
| Models page test model | `ModelSelectDropdown` (+ `ProviderId`) |

Removed: `SessionToolbar` static `s_optionCache`, unused `ModelSelector.razor`.

## ModelConfigManager

Existing in-memory cache + refresh queue + `ModelConfigChanged` is correct. UI must subscribe to the event and must not add a second TTL cache.

## Testing

- `ModelPickerItemTests` for filter / free / badge fields
- Manual: Models page refresh → Session Badge Modal shows new Text models immediately
