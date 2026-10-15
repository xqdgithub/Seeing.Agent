# 工作区文件硬边界 Implementation Plan

> **For agentic workers:** 按任务顺序实现；规格见 `docs/superpowers/specs/2026-09-11-workspace-file-boundary-design.md`。优先快速打通 P0+P1，UI/长文档可后补。

**Goal:** 可选开启「工作区文件硬边界」：文件工具目标路径须 ∈ WorkspaceRoot∪Whitelist；单一 Ask；默认关闭。

**Architecture:** `SerializingPermissionChannel` 在 Restrict=true 时 Allow→whitelist.Add，空 SessionId 短路；`IWorkspacePathGate` 只校验；FileSystem 工具（含 grep/glob）调用 Gate；Web Shape 注入 whitelist；切根 ClearAll+清记忆。

**Tech Stack:** .NET 10 / xUnit / Abstractions + Core.Permission + Tools.FileSystem

## Global Constraints

- Tools.FileSystem 禁止引用 Core
- 默认 RestrictToWorkspace=false 零行为变化
- 门闸禁止 Ask；Shell 不做路径硬狱

---

### Task 1: 配置 + ClearAll + 记忆全清 API

- [ ] `WorkspaceOptions.RestrictToWorkspace`
- [ ] `IWorkspaceWhitelist.ClearAll()` + 实现
- [ ] `IPermissionMemory.ClearAll()`（若无则加）+ 实现
- [ ] 单测 ClearAll

### Task 2: Channel 行为（P0 核心）

- [ ] Restrict=true ∧ Allow → whitelist.Add（含记忆命中）
- [ ] Restrict=true ∧ 空 SessionId → Deny，无 Ask
- [ ] 读 `IOptionsMonitor<SeeingAgentOptions>`
- [ ] Web Shape 注入 whitelist
- [ ] 切根订阅 ClearAll + ClearAll memory
- [ ] 单测

### Task 3: IWorkspacePathGate + 工具接入（P1）

- [ ] Abstractions `IWorkspacePathGate`
- [ ] Core 实现 + DI
- [ ] read/write/edit/delete/glob/grep 目标路径 EnsureAllowed
- [ ] 单测（含盘外 Deny 后不 IO）

### Task 4: 回归验证

- [ ] `dotnet build` + 相关 vstest
- [ ] Restrict=false 既有行为不破
