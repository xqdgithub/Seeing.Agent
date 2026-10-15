# 工作区文件硬边界（本机 coding 防护）设计

> **日期：** 2026-09-11  
> **状态：** 已拍板（审查修订后；待实现计划）  
> **威胁模型：** 本机 coding agent——降低误改/误删盘外与系统路径的风险；非多租户逃逸、非不可信代码执行。

## 1. 背景与目标

Seeing.Agent 已具备：

- 执行世界接缝：`IExecutionWorld` / `IFileSystem` / `ISubprocess`（「换沙箱不改工具」）
- 仅 `io.local` 实现（宿主直连 `File.*` + `Process.Start`）
- 会话白名单：`IWorkspaceWhitelist` + `add_workspace_path`
- 权限通道：工作区内（或白名单内）filesystem 操作自动允许，否则 Ask（`ToolManager` → `IPermissionChannel`）

缺口：默认无「文件路径硬边界」；Ask Allow 今日**不**写入 whitelist；Web Host Shape 构造 `SerializingPermissionChannel` 时可能未注入 whitelist；Grep/Glob 常走 `rg` 子进程绕过 `IFileSystem`。

**目标（MVP）：**

1. 可选开启：文件工具的**目标路径**必须 ∈ `WorkspaceRoot ∪ SessionWhitelist` 才执行（与底层 FS 或 `rg` 无关）。
2. **单一 Ask 所有者**：越界只由权限通道 Ask 一次；Allow（含记忆命中）时写入会话白名单；工具侧门闸只做成员检查。
3. **默认关闭**，与今日行为一致。
4. Shell **不**做路径硬狱（继续 Guard + Permission）；UI/文档须明示本开关不含 bash。
5. 工作区外偶发编辑靠**扩白名单**，不靠切换工作区；切换工作区仅用于换主项目。

**非目标（本阶段不做）：**

- Docker / MicroVM / 托管沙箱（E2B 等）
- 会话级整箱 / 每会话独立 `IExecutionWorld`
- Shell 路径硬狱
- 默认开启硬边界
- 用 `Scenario.Seams` 驱动进程 IO（进程 BoundSeams 规则不变）
- 强对抗隔离（TOCTOU / 父目录扩大面在本威胁模型下可接受，须在文档一句说明）

## 2. 已拍板决策

| # | 议题 | 选择 |
|---|------|------|
| D1 | 威胁模型 | 本机 coding，防搞坏盘/系统 |
| D2 | 工作区内文件 | 直接改真实文件（非副本/合并流） |
| D3 | 工作区外文件 | 扩会话白名单（Ask / `add_workspace_path`），不靠切工作区 |
| D4 | 切工作区 | 换主项目；**切根时 `IWorkspaceWhitelist.ClearAll()`**；**同时清空全部话 `IPermissionMemory`**（防 Restrict=false 时旧记忆自动放行盘外） |
| D5 | 文件工具 | Restrict=true 时强制允许集；越界仅通道 Ask；Allow → whitelist.Add → 门闸放行 |
| D6 | Shell | 软控制 only（`DangerousCommandGuard` + Permission） |
| D7 | 默认 | `RestrictToWorkspace = false`（仍 `io.local` 直通） |
| D8 | 开启方式 | 设置 / `seeing.json` 开关，而非默认换 `executionWorld` 模块 |
| D9 | Ask 所有权 | **仅** `IPermissionChannel` / `SerializingPermissionChannel`（经 ToolManager 既有路径）；门闸**禁止**再 Ask |
| D10 | 路径规范化基 | 相对路径解析基 = `IExecutionWorld.Cwd`（与现工具/进程 cwd 对齐；不以 session.WorkingDirectory 另开一套，除非工具显式传入已解析绝对路径） |

## 3. 行为规格

### 3.1 开关关闭（默认）

与当前行为一致：无硬边界门闸失败；`SerializingPermissionChannel` 仍可对盘外 filesystem 操作 Ask；Allow **不强制**写 whitelist（保持今日语义）。

### 3.2 开关开启：单一 Ask + 门闸校验

允许集：

```
allowed(sessionId) = WorkspaceRoot ∪ { dirs in IWorkspaceWhitelist for sessionId }
```

路径规范化：

- 使用与 `PathSafety` 一致的绝对路径 + 实路径解析（symlink/junction 对齐 `PathSafety.ResolveRealPath` / `IsPathWithinDirectory`）。
- 相对路径：相对 **`IExecutionWorld.Cwd`**（D10）。
- **`SessionId` 为空 + `RestrictToWorkspace=true`（钉死归属）：**
  1. **第一道（必须，主落点）：** `SerializingPermissionChannel.RequestAsync` 入口：对 `filesystem.*` 直接 Deny/短路，**禁止**调内部 UI Ask、禁止记忆短路放行。`ToolManager` 侧不另开平行逻辑（避免双处重复）；若未来无 Serializing 包装的通道，再在 ToolManager 补同等短路。
  2. **第二道（防御）：** `IWorkspacePathGate.EnsureAllowed` 若仍见到空 SessionId → Failure，不 IO。
  3. 失败文案见 §5。不得出现「先弹 Ask 再因无 SessionId 失败」。

**适用工具（按目标路径，不按 IO 后端）：**

`read` / `write` / `edit` / `delete` / `glob` / `grep` / `add_workspace_path`（见 §3.6）以及其它声明为 filesystem 类、带路径参数的内置文件工具。

> Grep/Glob 即使走 `rg`（`ISubprocess`）也必须先对**搜索根 / 目标路径**做允许集检查；硬边界与是否调用 `IFileSystem` **无关**。

**禁止双重 Ask 的调用序列：**

```
ToolManager
  → IPermissionChannel.RequestAsync(filesystem.*, resource=path)   ← 唯一 Ask
       · 根内或 whitelist 内 → Allow（可无 UI）
       · 否则 Ask 用户 / 查记忆
       · Restrict=true 且最终 Allow → whitelist.Add(目录边界)     ← 强制
  → 工具 ExecuteAsync
       → IWorkspacePathGate.EnsureAllowed(sessionId, path)         ← 只校验，不 Ask
            · ∈ allowed → 继续
            · ∉ allowed → Failure（防御性；正常路径不应发生）
       → IFileSystem 或 rg / 其它后端
```

门闸职责：**只读校验**允许集。不得调用 `IPermissionChannel`。若校验失败，返回明确 Failure（提示配置/白名单异常），不二次弹窗。

Shell（`bash`）与 git 等非文件工具目标路径检查：

- **不**应用本硬边界。
- 继续既有 Guard + 权限规则。
- 文档与设置文案须写明：「工作区文件硬边界不约束 bash」。

### 3.3 白名单写入粒度与记忆同步

与 `IWorkspaceWhitelist.Add(sessionId, directoryPath)` 一致：加入的是**目录**。

- 越界目标为文件：Allow 后加入其**父目录**。
- 越界目标为目录：加入该目录。
- `add_workspace_path`：显式申请目录进白名单（可仍走权限 Ask，见 §3.6）。

**记忆与硬检（钉死）：**

当 `RestrictToWorkspace=true` 时，凡 filesystem 操作最终判定为 Allow（含 `IPermissionMemory` 命中短路），**必须**在返回 Allow 前对 resource 执行 `whitelist.Add`（目录粒度同上）。禁止「记忆 Allow 但路径不在 whitelist」导致门闸失败或静默绕过。

当 `RestrictToWorkspace=false` 时，不强制因 Allow 写 whitelist（与今日一致）。

### 3.4 切换工作区

`IWorkspaceProvider` 工作区根变更（`WorkspaceRootChanged`）时：

1. 调用 `IWorkspaceWhitelist.ClearAll()`（Abstractions **新增** API；不得假设可枚举全部 sessionId 再 `ClearSession`）。
2. 清空全部话权限记忆（`IPermissionMemory` 提供或扩展全清能力；若今日仅有按 session 清理，则实现须能清全部话）。
3. 理由：避免切根后旧白名单/旧记忆继续放行与新项目无关的盘外路径。

### 3.5 Web / MCP / Memory / Skills

- Skills / Memory / Scheduler 等宿主元数据读写（不经文件工具目标路径门闸）**不受**本开关约束。
- MCP / Web 网络能力不在本设计范围。

### 3.6 `add_workspace_path` 与 Exists 探测

- 该工具的目的是扩权：目标目录可先做只读 `Exists`/是目录探测（允许在未入允许集时探测），再 Ask / `whitelist.Add`。
- 不得在未批准前对该路径做写/删。

## 4. 架构与组件

### 4.1 选型：通道扩权 + 工具侧只读门闸（非新 `executionWorld`）

```
ToolManager ──filesystem.*──► SerializingPermissionChannel
                                 │ Restrict=true ∧ Allow → whitelist.Add
                                 ▼
FileSystem 工具 ──EnsureAllowed──► IWorkspacePathGate（只校验）
                                 ▼
                    IFileSystem 和/或 ISubprocess(rg)
bash / git ──► ISubprocess（无本硬边界）
```

端口命名：`IWorkspacePathGate`（或 `IWorkspaceBoundedFileAccess` 的 Ensure 子集）；MVP 以「路径是否允许」为唯一操作，不要求门闸包装全部 IO API。

### 4.2 实现落点（钉死）

| 工件 | 位置 |
|------|------|
| `IWorkspacePathGate`、`IWorkspaceWhitelist.ClearAll` | `Seeing.Agent.Abstractions` |
| `WorkspaceOptions.RestrictToWorkspace` | 既有 `WorkspaceOptions`（Abstractions） |
| Gate 实现、Channel 写 whitelist、ClearAll 实现、切根订阅 | `Seeing.Agent.Core`（`Core.Permission` / 配置初始化），复用 `PathSafety` |
| DI 注册 | `AddSeeingCore`（及各 Host Shape 对 `SerializingPermissionChannel` 的构造） |
| 文件工具 | `Seeing.Agent.Tools.FileSystem`：仅注入 Abstractions 端口；**禁止**引用 Core |

### 4.3 Host Shape：必须注入 whitelist

凡构造 `SerializingPermissionChannel` 处（至少：Core 默认、`Hosting.Web`、以及其它 Shape 若有同等包装）**必须**注入 `IWorkspaceWhitelist`。**禁止**省略该参数（不得依赖默认 `null`）。

今日缺口（须在 P0 修复）：`WebHostingServiceCollectionExtensions` 仅传入 `workspace`，`whitelist` 缺省为 null → 通道侧白名单无效，同目录无法免 Ask。

验收：WebUI 主路径下，whitelist.Add 后同目录 filesystem 操作自动 Allow（Restrict 开或关均应看到通道尊重 whitelist）。

### 4.4 配置与热读

```json
{
  "SeeingAgent": {
    "Workspace": {
      "UseGlobal": false,
      "CustomPath": null,
      "RestrictToWorkspace": false
    }
  }
}
```

- `WorkspaceOptions.RestrictToWorkspace`（`bool`，默认 `false`）。
- **Gate 与 Channel 必须**通过 `IOptionsMonitor<SeeingAgentOptions>.CurrentValue.Workspace.RestrictToWorkspace` 读取，**禁止**依赖 `WorkspaceProvider` 内可能过期的 options 缓存。
- 热切换：中途改为 true/false，后续工具调用立即按新值执行（须测）。
- UI：设置 → 工作区 →「工作区文件硬边界」；文案注明不含 bash。P0 可仅配置项，UI 可同 PR 或紧随，不堵门闸逻辑。

### 4.5 Session 上下文

Gate / Channel 从 `ToolContext.SessionId` / `PermissionRequest.SessionId` 取会话；禁止 AsyncLocal 作为唯一来源。

## 5. 错误与可观察性

- 通道 Deny：工具层既有失败语义。
- 门闸 Failure（防御性）：说明路径不在允许集；提示检查开关/白名单或使用 `add_workspace_path`。
- 空 SessionId + Restrict=true：由 Channel/ToolManager 短路；文案「缺少会话 ID，无法在硬边界模式下访问文件」；**不得**先展示权限 UI。
- 日志：`SessionId`、`Path`、`Restrict`、`InAllowSet`、`Action=Ask|Allow|Deny|GateReject`；扩权成功可打 `workspace.boundary.expanded`。

## 6. 测试计划（规格级）

| 用例 | 期望 |
|------|------|
| Restrict=false，盘外 write | 与现网一致；无新增硬失败；Allow 不强制写 whitelist |
| Restrict=true，根内 read/write | 成功；无 Ask |
| Restrict=true，盘外 write，Ask Allow | **仅一次** Ask；whitelist 含父目录；文件已写；再次同目录无 Ask |
| Restrict=true，盘外 write，记忆命中 Allow | 无 UI Ask；仍执行 whitelist.Add；门闸通过 |
| Restrict=true，盘外 write，Ask Deny | 未写盘；whitelist 未增加 |
| Restrict=true，`add_workspace_path` 后再 write | 成功 |
| Restrict=true，grep/glob（rg 可用）盘外根 | 执行前被拒或先 Ask；不得在未允许时跑 rg 扫盘外 |
| Restrict=true，空 SessionId | Channel/ToolManager 短路 Deny；**无权限 UI**；无 IO；Gate 防御性 Failure 可选覆盖 |
| Web Shape：whitelist 注入 | 通道尊重 whitelist；同目录免 Ask |
| 切工作区根 | `ClearAll` + 权限记忆全清；旧盘外路径需重新 Ask |
| Restrict 热切换 false→true | 后续调用立即 enforce |
| 相对路径 / `..` / symlink | 规范化/实路径后判允许集 |
| bash 盘外 | 不因本开关被路径硬拒 |
| 无双重 Ask | 单次盘外操作用户只看到一轮权限 UI |

不变量：`Tools.FileSystem` 不引用 Core；文件工具只依赖 Abstractions 端口。

## 7. 落地阶段

| 阶段 | 内容 |
|------|------|
| P0 | `RestrictToWorkspace`；`IWorkspaceWhitelist.ClearAll`；Web 等 Shape **注入 whitelist**；Channel：Restrict=true 时 Allow→`whitelist.Add`；空 SessionId 在 Channel/ToolManager 短路 Deny；切根订阅 ClearAll + 清记忆。**说明：P0 单独打开开关尚不构成完整硬边界**（缺 P1 门闸时，未走通道的路径仍可能直达 IO） |
| P1 | `IWorkspacePathGate` + Core 实现；FileSystem 工具（含 grep/glob 目标路径）接入 EnsureAllowed → **此时硬边界才完整** |
| P2 | 测试（含双重 Ask、Web+whitelist、rg、空 SessionId 不弹 Ask、热切换、symlink）；设置文案 |
| P3 | architecture / 扩展指南短文；与未来 `io.*` 沙箱关系 |

## 8. 仍开放（不阻塞本规格）

1. 设置 UI 是否与 P0 同 PR（逻辑与配置优先）。
2. 父目录以外的更粗白名单策略——**不做**；保持父目录默认。

## 9. 成功标准

- 默认关闭时，既有测试与行为无回归。
- 开启后，未经用户同意（通道 Allow / 预置白名单）无法用**文件工具**改或扫盘外真实路径。
- 单次越界仅一轮 Ask；同意后同会话同目录可继续；切根后旧扩权与旧记忆失效。
- Shell 不因本功能被路径硬拒；文档/UI 明示范围。
- 无双重 Ask；Web 主路径 whitelist 生效。

---

## 修订记录

| 日期 | 说明 |
|------|------|
| 2026-09-11 | 初稿：brainstorm 拍板 D1–D8 落档 |
| 2026-09-11 | 自检：D4/切根与门闸方案表述对齐 |
| 2026-09-11 | 设计审查修订：单一 Ask（D9）、Web 注入 whitelist、ClearAll、grep/glob 按目标路径、记忆同步、D10 cwd、切根清记忆、实现落点钉死、热读 OptionsMonitor、测试补强 |
| 2026-09-11 | 复审修订：空 SessionId 短路归属 Channel/ToolManager（禁止先 Ask）；P0 非完整硬边界说明；Shape 禁止省略 whitelist |
| 2026-09-11 | 签核后微调：空 SessionId 主落点定为 SerializingPermissionChannel 入口（避免与 ToolManager 双实现） |
