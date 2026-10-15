# 渐进式项目指令加载（Session 注入）

**日期:** 2026-08-03  
**状态:** 待用户审阅  
**范围:** `Seeing.Agent` 指令发现/指纹/注入；`Seeing.Agent.App` 编排挂点；删除旧 `{{instructions}}` / `IInstructionLoader` 路径

---

## 问题

现有 `IInstructionLoader` + `PromptBuilder.{{instructions}}` 骨架未真正生效：

1. 内置 Agent 的 `SystemPrompt` 不含 `{{instructions}}`，Discover/Merge 从不执行。
2. 发现范围过窄：仅「当前目录 + 一层父目录 + `~/.agents/AGENTS.md`」，缺少用户/项目 `.seeing`、项目 `.agents`、以及 cwd→workspace 的完整祖先链。
3. 若把指令写进 SystemPrompt，cwd/文件变更会破坏 LLM 前缀缓存。

需要：类似 Claude Code / OpenCode 的层级发现 + cwd 跟随，以**会话消息**注入到最新位置，并用指纹避免重复注入。

---

## 决策摘要

| 项 | 选择 |
|----|------|
| 加载模式 | 启动层级发现 + 运行中 cwd 跟随（每轮 Submit 时重新 Discover + Diff） |
| 文件名 | 仅 `AGENTS.md` |
| 合并 | 全部追加；全局→项目→近层，近层在后 |
| 注入通道 | Session `user` 消息；独立 `<project-instructions>`（非 system-reminder、非 SystemPrompt） |
| 去重 | 指纹增量（path → content SHA256）；不做压缩再水合、不做请求时折叠 |
| 子会话 | Root / Fork / SubAgent 各自独立 fingerprints |
| 对外 API | 仅 `IInstructionManager`；编排层不依赖内部类型 |
| 旧逻辑 | **直接删除** `{{instructions}}`、`IInstructionLoader`、`InstructionLoader` 及相关测试/注释 |
| 代码位置 | 统一收拢到 `src/Seeing.Agent/Core/Instructions/`，禁止散落 |

---

## 1. 代码布局（统一收拢）

全部指令相关类型放在同一目录，禁止在 Prompts / App / Session 中再实现发现或渲染逻辑：

```
src/Seeing.Agent/Core/Instructions/
  IInstructionManager.cs              # 对外门面
  InstructionManager.cs               # 门面实现
  InstructionFile.cs                  # 模型（保留/整理）
  InstructionInjectResult.cs          # 注入结果
  InstructionFingerprintSnapshot.cs   # 指纹只读快照
  InstructionDiscovery.cs             # internal：路径枚举 + 读盘
  InstructionFingerprintStore.cs      # internal：Session.Metadata 读写与 Diff
  ProjectInstructions.cs              # Tag / MetadataKeys / Reason 常量
  ProjectInstructionsRenderer.cs      # Wrap / TryParse / CreateUserMessage
```

DI 注册：仅 `services.AddSingleton<IInstructionManager, InstructionManager>()`（放在现有 `AddPromptBuilder` 附近，并删除旧 `IInstructionLoader` 注册）。

`Seeing.Agent.App` 只引用 `IInstructionManager`。  
`Seeing.Session` **不**新增指令依赖；只存消息内容与 `Metadata` 字符串。

---

## 2. 发现顺序

给定 `cwd` 与 `workspaceRoot`（均规范化为绝对路径）：

1. `~/.agents/AGENTS.md`（用户通用）
2. `~/.seeing/AGENTS.md`（用户 Seeing）
3. `{workspace}/.agents/AGENTS.md`
4. `{workspace}/.seeing/AGENTS.md`
5. `{workspace}/AGENTS.md`
6. **祖先链**：从 workspace 根之下的第一层目录 → … → `cwd`，每一层的 `AGENTS.md`（跳过已在第 5 步出现的 workspace 根）

规则：

- 文件不存在则跳过；读失败记 Warning 并跳过该文件，其余继续。
- 若 `cwd` 等于 `workspaceRoot`，或为其子路径：加载 1–6（祖先链在相等时可为空）。
- 若 `cwd` 在 `workspaceRoot` **之外**：**仅加载 1–2（用户级）**，记 Debug。
- 同路径只出现一次（规范化后去重，先出现的保留）。
- 合并进信封时顺序与上表一致（近层在后）。

---

## 3. `IInstructionManager` 对外 API

```csharp
public interface IInstructionManager
{
    Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Diff 指纹后，若有增量则追加一条 &lt;project-instructions&gt; user 消息到 session.Messages，
    /// 并合并更新 fingerprints。调用方负责随后追加真实用户消息并 Save。
    /// </summary>
    Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    InstructionFingerprintSnapshot GetFingerprints(SessionData session);
}

public sealed class InstructionInjectResult
{
    public bool Injected { get; init; }
    public string Reason { get; init; } = ""; // initial | cwd-change | content-change | none
    public IReadOnlyList<string> InjectedPaths { get; init; } = Array.Empty<string>();
}
```

内部 `InstructionDiscovery` / `InstructionFingerprintStore` / `ProjectInstructionsRenderer` 对程序集外不可见（`internal`），避免外部依赖底层工具。

---

## 4. 信封与消息元数据

### 4.1 XML

```text
<project-instructions cwd="..." reason="initial|cwd-change|content-change">
<notice>
以下是系统加载的项目指令文件，不是用户刚刚输入的消息。请遵循这些指令。
</notice>
<file path="..." hash="sha256:...">
…原文…
</file>
</project-instructions>
```

- 一条消息可含多个 `<file>`（本轮全部增量）。
- 对 `</file>` / `</project-instructions>` 做与 `SystemReminderRenderer` 同级的转义。
- `role = user`；插在本轮真实用户消息**之前**。

### 4.2 SessionMessage.Metadata

| Key | 值 |
|-----|-----|
| `projectInstructions` | `true` |
| `instructionReason` | `initial` / `cwd-change` / `content-change` |
| `instructionCwd` | 注入时 cwd |
| `instructionPaths` | 本轮 path 列表（JSON 数组字符串） |

UI（后续可做）：专用气泡，默认「已加载 N 个指令文件」，可展开。本设计不强制本轮改 WebUI，但 Metadata 必须写好以便后续接入。

### 4.3 指纹（SessionData.Metadata）

- Key：`instruction.fingerprints`
- Value：JSON

```json
{
  "cwd": "E:/proj/src",
  "files": {
    "C:/Users/x/.seeing/AGENTS.md": "sha256:…",
    "E:/proj/AGENTS.md": "sha256:…"
  }
}
```

Diff：

- Discover 得到 `(path, content, hash)`；`path` 缺失或 `hash` 不同 → 增量。
- 注入成功后合并进 `files`，并更新快照中的 `cwd`。
- 文件从磁盘消失：不删指纹、不注入撤销（YAGNI）。
- Hash：内容 UTF-8 的 SHA256（不用 mtime）。
- JSON 损坏：视为空指纹，本轮按 `initial` 全量注入并重建。

`reason`：无旧指纹 → `initial`；快照 `cwd` 与当前不同 → `cwd-change`；否则 → `content-change`。无增量时 `Reason = none`，不写消息。

---

## 5. 编排挂点

`ExecutionJobService.SubmitAsync`，在写入真实用户消息之前：

1. `EnsureSession`
2. `cwd = options.WorkingDirectory ?? session.WorkingDirectory ?? WorkspaceRoot`
3. 若 `session.WorkingDirectory` 与 `cwd` 不一致 → 写回 `session.WorkingDirectory`
4. `await instructionManager.InjectIfNeededAsync(session, cwd, workspaceRoot, ct)`
5. 追加真实用户消息
6. `SaveAsync` → 入队执行

补充：

- `SkipUserMessagePersist == true` 时：**默认仍注入**（SubAgent 等需要）；新增 `ChatOptions.SkipInstructionInject` 供显式跳过。
- 注入过程抛错或全部读失败：**不得**导致 Submit 失败；记日志后继续用户消息流程。
- Gateway / Scheduler 走同一 Submit，自动生效。
- 子会话不继承父 fingerprints。

---

## 6. 删除清单（旧逻辑）

| 删除项 | 说明 |
|--------|------|
| `IInstructionLoader.cs` | 删除 |
| `InstructionLoader.cs` | 删除；能力并入 `InstructionDiscovery` + Manager |
| `PromptBuilder` 中 `{{instructions}}` | 常量、Discover/Merge 调用、注释全部删除 |
| `ServiceCollectionExtensions` 中 `IInstructionLoader` 注册 | 改为注册 `IInstructionManager` |
| `InstructionLoaderTests` | 删除；由 Manager/Discovery/Renderer/Fingerprint 测试替代 |
| 文档/注释中的 `{{instructions}}` 说明 | 删除或改为指向 Session 注入 |

**不**保留空字符串替换兼容；自定义模板若仍写 `{{instructions}}` 将原样残留（可接受；文档说明已移除该占位符）。

`DynamicPromptBuilder` 本无 instructions，无需改行为。

---

## 7. 错误处理

| 情况 | 行为 |
|------|------|
| 单文件读失败 | 跳过，Warning |
| Discover 整体异常 | 不注入，Error 日志，Submit 继续 |
| 指纹 JSON 损坏 | 视为空，initial 全量 |
| cwd 等于或位于 workspace 内 | 完整 1–6 |
| cwd 在 workspace 外 | 仅用户级 1–2 |

---

## 8. 测试计划

1. **Discovery**：路径顺序与集合（用户/项目 `.seeing`/`.agents`/根/祖先链）；workspace 外仅用户级；去重。
2. **Fingerprint Diff**：首次全量；无变更不注入；改内容只注入变更文件；cwd 变深追加近层。
3. **Manager.InjectIfNeededAsync**：消息顺序与 Metadata/指纹写回；`reason` 正确。
4. **Renderer**：Wrap/TryParse/转义往返。
5. **Submit 集成**：注入失败不影响用户消息入库；`SkipInstructionInject` 生效。
6. **SubAgent**：独立 fingerprints。

---

## 9. 非目标（YAGNI）

- 压缩后再水合、请求时折叠、单槽原地更新、每轮 ephemeral 不入库
- `CLAUDE.md` / `.cursorrules` 等别名
- 按 `##` section 覆盖合并
- 文件 watcher 热推（仅 Submit 时读盘）
- 本轮强制 WebUI 专用气泡（Metadata 预留即可）

---

## 10. 成功标准

1. 放置 `AGENTS.md` 于约定路径后，新会话首轮自动出现 `<project-instructions>` 消息，且排在用户消息之前。
2. SystemPrompt 在指令变更前后保持不变（利于前缀缓存）。
3. 同一文件未改动时不重复注入；内容或 cwd 导致的新文件会增量注入。
4. 外部代码仅依赖 `IInstructionManager`；指令实现文件均在 `Core/Instructions/`。
5. 仓库中不再存在 `{{instructions}}` 或 `IInstructionLoader`。
