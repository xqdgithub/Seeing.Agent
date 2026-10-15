# Model Capability Sources Implementation Plan

**Date:** 2026-09-12  
**Spec:** `docs/superpowers/specs/2026-09-12-model-capability-sources-design.md`  
**Approach:** Fast-track P0→P3; final review only.

## Files

### P0 Abstractions + Core
- `src/abstractions/.../Llm/ModelCapabilities/*.cs` — interfaces, DTOs, events, FillEmpty, ModelCapabilitiesChange
- `src/spine/.../Llm/NullModelCapabilityManager.cs`
- `src/spine/.../Llm/ModelCapabilityCatalogReloadHandler.cs`
- Wire in `ServiceCollectionExtensions.cs`; inject into `ProviderManager` / `ConfiguredLlmProvider`

### P1 Packages
- `src/capabilities/Seeing.Agent.Llm.ModelCapabilities/`
- `src/capabilities/Seeing.Agent.Llm.ModelCatalog.ModelsDev/` (+ embedded snapshot)

### P2 Plugs
- OpenCodeZen / DeepSeek / Configured enrich calls; trim redundant presets

### P3 WebUI
- Minimal settings page: list sources, order, enable, refresh/edit entry points

## Tasks
1. P0 abstractions + Core + tests
2. P1 Manager + ModelsDev
3. P2 provider wiring
4. P3 minimal UI
5. Final review
