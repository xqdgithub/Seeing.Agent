# 宿主请求机制（Host Request）与编辑器命令工具设计

**日期**：2026-10-15
**分支**：master
**状态**：设计定稿 v7（六轮对抗性审查 + 架构澄清后定案；待终审签字）
**路径约定**：文中 `Core/…` = `src/spine/Seeing.Agent.Core/…`，`Abstractions/…` = `src/abstractions/Seeing.Agent.Abstractions/…`（按 AGENTS.md 路径映射）
**依据**
- `docs/architecture/01-overview.md`、`02-modules.md`、`04-development-standards.md`、`05-extension-guide.md`、`06-compliance-audit.md`
- `docs/architecture/16-session-file-explorer-release-notes.md`（Monaco 文件面板）
- `docs/superpowers/specs/2026-09-20-question-tool-and-interaction-infrastructure-design.md`（在途请求 + 呈现端 + 收件箱投影 · **同骨架蓝本**）
- `docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md`（权限在途 Manager · **同骨架蓝本**）
- 代码基线：`Core/Interactions/PendingRequestManager.cs`、`Core/Questions/QuestionRequestManager.cs`、`Core/Permission/PermissionRequestManager.cs`、`Core/Interactions/SurfaceRegistry.cs`、`Tools.Question/QuestionTool.cs`、`WebUI/Services/QuestionInbox.cs`、`WebUI/Services/WebUiSurfacePresenter.cs`、`WebUI/Components/SessionFilePanel.razor`、`WebUI/Components/SessionWindow.razor`、`WebUI.Files/Components/FilePanelHost.razor`、`WebUI.Files/Components/FileEditor.razor`

---

## 1. 问题与动机

### 1.1 需求

Monaco 编辑器（WebUI 文件面板）暴露大量命令/动作（`editor.action.*`）与状态查询 API。希望让 agent 能够：

1. **操作用户的实时编辑器视图**（呈现/辅助类动作）：折叠、格式化、跳转符号、选中所有匹配、多光标等。
2. **查询编辑器状态**并把结果回传给 agent：当前选区文本、光标位置、诊断（markers）、可见范围等。

并配合一个**内置 SKILL**，让 agent 知道有哪些命令、怎么用。

### 1.2 关键架构洞察：这不是「编辑器专属」，而是「宿主能力调用」

把 Question / Permission / Editor 三者的领域细节剥掉，剩下的是**同一个传输骨架**：

> **服务端发起请求 → 以 session 关联 → 交付给该会话对应的宿主 → 宿主侧执行并返回结构化结果 → 服务端 `await` 拿到结果。**
> 门闸：无宿主呈现（presence=false）即快速失败；兜底：超时 / 取消 / 释放 / 呈现端注销收敛；完成：幂等。

「谁产生响应」有两类，但**传输机制完全相同**：

| 维度 | Question | Permission | Editor（本设计） | 未来（剪贴板/通知/终端…） |
|---|---|---|---|---|
| 谁产生响应 | **人** | **人** | **宿主运行时** | **宿主运行时** |
| 领域语义 | 答案格式 | 授权决策 + 策略重评估 | 命令/查询载荷 | 各自 |
| 兜底语义 | Unavailable | **Deny（fail-closed）** | Unavailable | 各自 |
| 传输机制 | 在途 Manager + 呈现端 + **收件箱投影** | 同 | **同** | **同** |

**结论**：现有 `PendingRequestManager<,>`（Core）**已经**是通用并发内核，Question/Permission 都只是它的薄子类。本设计不再为 Editor 抄第三套 **Abstractions 表面 + 宿主交付**，而是泛化为**一个通用机制：Host Request（宿主能力调用）**——与「工具」（agent 调服务端能力）正好对称。

### 1.3 现状缺口

| # | 事实 | 证据 |
|---|------|------|
| G1 | 编辑器交互工具**仅有** `open_file`（只读定位） | `Tools.FileSystem/OpenFileTool.cs` |
| G2 | Monaco 的 C# API 已具备读值/写值/定位/选区/诊断等能力（BlazorMonaco 3.5.0） | `WebUI.Files/Components/FileEditor.razor`、`BlazorMonaco.dll` |
| G3 | 无「服务端工具 → 宿主执行 → 结果回传」的通用通道 | 全仓 `editor_command\|editor_query` 零命中 |
| G4 | Question 已趟通「在途 Manager + 呈现端 + **收件箱投影** + 回传 + 无 UI 快速失败」整条路径 | `QuestionTool.cs`、`QuestionRequestManager.cs`、`QuestionInbox.cs`、`ISurfaceRegistry.CanSurface` |
| G5 | Question/Permission 两个 Manager **各自拷贝**了一遍「呈现端注销 → 宽限计时 → 兜底收敛」逻辑 | 两 Manager 内 `_convergence*` 字段 + `OnProviderUnregistered/OnConvergenceTimer` |
| G6 | 若按「Editor 专用纵切面」实现，Abstractions 表面与宿主交付将出现**第三份拷贝** | 本设计要消除的重复 |
| G7 | **事件流在订阅建立时回放环形缓冲**；Question **刻意不消费事件**、改用收件箱拉取投影，以规避回放副作用 | `ExecutionEventPublisher.cs:73-83`；`QuestionInbox.cs:24-29`；WebUI 全仓无 `QuestionRequestEvent` 消费 |
| G8 | 宿主交付是**执行器**（非只读投影）：必须回到 Blazor 同步上下文（JS interop / `StateHasChanged`） | `FileEditor.razor`（JS interop）、`FilePanelHost.razor:231` |
| G9 | 同一 session 在**多个 circuit**（多标签）可同时可见，且各窗口各有一个 `SessionFilePanel` 实例 | `SessionWindow.razor:223`；`SessionFilePanel.razor:14-16` |

### 1.4 核心约束（本设计的重点）

- **复用而非新造**：Editor 必须踩在既有 `IPendingRequestManager<,>` + `ISurfaceRegistry` 基础设施上；**不得**再拷贝在途/收敛机制。
- **一次泛化，消灭重复**：把「传输表面 + 宿主交付」泛化为通用 **Host Request**；Editor 是第一个使用者。未来新增宿主能力**零 Core/Abstractions 改动**（机制层）。
- **交付 = 拉取投影 + 活跃面板定位 + 同步上下文编组**（G7/G8/G9）：带副作用的宿主请求**禁止**经会回放的会话事件流触发；采用 Question 已验证的收件箱投影。
- **尽力而为（架构决策，见 §2.3）**：`editor_command` 仅作用于**用户当前激活的文件面板**；同一会话被多标签同时打开且都开着文件面板时**可能重复执行**——接受为已知边界（与项目其他 P3 边界同级），**不引入选主/认领/执行闸等分布式协调**。
- **不打补丁**：G5 的既有重复先抽取到基类，再让 Host Request 以薄子类接入。
- **分层合规**：能力包只依赖 Abstractions + 原语；Manager 实现放 Core。

---

## 2. 目标与非目标

### 2.1 目标

1. **Step 1（重构，行为不变）**：把「呈现端注销 → 宽限计时 → 对不可呈现会话兜底收敛」逻辑上提到 `PendingRequestManager<TRequest,TResponse>` 基类；Question/Permission 删除各自拷贝，由现有测试锁定行为等价。
2. **Step 2（泛化机制）**：新增通用 **Host Request** 纵切面（Abstractions 契约/DTO/呈现端；Core 薄子类 Manager + DI；WebUI 收件箱投影 + 活跃面板定位 + 同步编组派发）。
3. **Step 3（首个使用者）**：能力包 `Seeing.Agent.Tools.Editor` 三工具 + 内嵌 SKILL + WebUI 编辑器面板目标。
4. 命令/查询经 `ISurfaceRegistry.CanSurface` 快速失败。
5. `editor_command` 走现有资源级权限审批链。

### 2.2 非目标

- **不限制 commandId**（透传任意 Monaco action id）；安全边界靠「要求宿主在线 + 权限审批」。
- **不做服务端降级**：无宿主时快速失败，不退回磁盘文件操作。
- **不自动保存**：命令改内容后由现有 `OnDidChangeModelContent → Tabs.MarkDirty` 标记 dirty。
- **不引入 Monaco 之外的编辑器**；命名用「editor」中性抽象，当前仅 Monaco 实现。
- **不迁移 Question/Permission 到 Host Request**（边界 A，见 §4.2）。
- **不引入远程宿主事件通道**（YAGNI）：当前宿主为进程内 WebUI，用拉取投影即可；远程宿主另立非回放通道。
- **不作用于非激活面板 / 非 active 子代理会话**：editor 命令只作用于**用户当前激活的文件面板**；无激活面板 → 快速失败。
- 不改动 Gateway/CLI 的交互协议。

### 2.3 架构决策：尽力而为交付（本设计的核心取舍）

**背景**：同一会话可被多个浏览器标签（circuit）同时打开，各 circuit 各有一个文件面板实例。严格保证「带副作用的 `editor_command` 恰好执行一次」需要「选主 + 一次性执行闸 + 所有权迁移 + 惰性清理」等分布式协调，复杂度高且持续引入竞态窗口（经六轮对抗审查反复暴露）。

**决策**（与用户澄清确认）：

1. **作用范围 = 用户当前激活的文件面板**：`FilePanelState` 为 circuit 维度（`IsOpen` + `SessionId` 唯一），故「激活面板」天然定义明确；agent 的 `path` 参数在当前面板内切换文件，不跨面板。
2. **尽力而为**：只要**任一** circuit 持有该会话的激活文件面板，请求即交付给**该（些）** circuit 执行。
   - **单标签（绝大多数情况）**：恰好一次。
   - **多标签且都开着同一会话的文件面板**：可能重复执行 → **已知边界**（见 §13），不保证恰好一次。
3. **不引入**选主、认领注册表、一次性执行闸、所有权迁移、惰性清理。

**理由**：多标签打开同一会话属边缘场景；项目已有大量「已知边界」先例（如「权限多通道为进程级语义」）。以边缘场景的弱保证换取交付机制的显著简化，符合 YAGNI。

---

## 3. 术语

| 术语 | 含义 |
|------|------|
| 在途请求 | 已发起、尚未被决议（完成/取消/超时/不可呈现）的交互请求 |
| 呈现端（Presenter） | 宿主侧声明「哪些会话可在本上下文呈现」的对象（`ISurfaceProvider`） |
| 可呈现性（Presence） | `CanSurface(sessionId)`：是否存在能展示该会话交互的宿主 |
| 宿主能力（Host Capability） | 宿主侧可被服务端调用的能力（编辑器命令、剪贴板、通知…） |
| Host Request | 服务端调用宿主能力的通用请求/响应机制 |
| 收件箱（Inbox） | 从 `IHostRequestManager.GetAllPending()` + `PendingChanged` 派生的只读投影（**非事件流**） |
| 激活面板（Active Panel） | 某 circuit 中 `FilePanelState.IsOpen && SessionId` 匹配的文件面板；每 circuit 至多一个 |
| 目标（Target） | 组件注册的「可编组执行委托」，按 `sessionId` 键；供 Dispatcher 定位活跃面板 |
| 命令（Command/Action） | Monaco `editor.trigger(source, commandId, payload)` 可触发的具名动作 |
| 查询（Query） | 读取编辑器状态的只读操作 |

---

## 4. 复用与泛化策略

### 4.1 重复定位（审查结论）

| 层 | 现状 | 判定 |
|---|---|---|
| 并发 rendezvous（Begin/Wait/TryResolve/超时/取消/释放） | `PendingRequestManager<,>` 单一基类 | ✅ 已复用 |
| 呈现端注销收敛 | 当前 Question/Permission 各一份；Step 1 上提基类 | ✅ 将复用 |
| **Abstractions 契约/DTO/呈现端接口** | Question、Permission 各一套；Editor 将成第三套 | ❌ **重复 → 泛化** |
| **宿主侧交付（投影 + 定位 + 回传）** | Question/Permission 各一套；Editor 将成第三套 | ❌ **重复 → 泛化** |
| **DI 扩展** | 各一套；Editor 将成第三套 | ❌ **重复 → 泛化** |

### 4.2 边界决策（边界 A）

| 域 | 归属 | 理由 |
|---|---|---|
| **程序化宿主能力**（editor、剪贴板、通知…） | **Host Request** | 响应由**宿主运行时**产生；无专用 UI 卡片；无 fail-closed/策略语义 |
| **人机交互**（question、permission） | **保持独立** | 响应由**人**产生；有专用 UI 卡片、审批语义、fail-closed、策略重评估 |

两者共享同一 `PendingRequestManager<,>` 基类；Abstractions 表面各自独立（可辩护的边界）。

### 4.3 复用/泛化清单

| 机制 | 决策 | 产物 | 落点 |
|------|------|------|------|
| 在途请求管理 | **已存在，直接复用** | `IPendingRequestManager<,>` + `PendingRequestManager<,>` | Abstractions 接口；Core 实现 |
| 可呈现性注册 | **已存在，直接复用** | `ISurfaceProvider` + `ISurfaceRegistry` + `SurfaceRegistry` | Abstractions 接口；Core 实现 |
| 呈现端注销收敛（宽限计时 + 兜底） | **抽（Step 1）** | 上提到 `PendingRequestManager` 基类，构造可选注入 `ISurfaceRegistry` | Core 基类 |
| **Host Request 传输表面** | **新增（一次，通用）** | `HostRequest/Response`、`IHostRequestManager`、`IHostSurfaceRegistry` | Abstractions |
| **Host Request Manager** | **薄子类（一次）** | `HostRequestManager : PendingRequestManager<HostRequest, HostResponse>` | Core |
| **宿主交付（投影 + 定位 + 编组）** | **一次，通用** | `HostRequestInbox`（Singleton 投影）+ `HostRequestActivePanelRegistry`（Singleton 活跃面板登记）+ `HostRequestDispatcher`（Scoped 派发） | WebUI |
| **Editor 能力** | **首个使用者** | 三工具 + 载荷助手 + 内嵌 SKILL + `SessionFilePanel` 面板登记 | 能力包 + WebUI |
| 命令白名单 / 服务端降级 / 远程事件通道 / 选主认领 | **不抽/不做** | 明确不做（见 §2.2、§2.3） | — |
| question/permission 收编 | **不做（边界 A）** | 保持独立 | — |

**不新增程序集**：`PendingRequestManager` 基类与 `HostRequestManager` 都放 Core；能力包只依赖 `IHostRequestManager` 抽象。

---

## 5. 交付机制（核心）

> 交付**不**经会话事件流（回放会重复执行副作用，G7），采用 Question 已验证的「收件箱投影」；**定位 = 用户当前激活的文件面板**（§2.3），并补齐 **Blazor 同步上下文编组**（G8）。

**结构**：`HostRequestActivePanelRegistry`（Singleton）登记「哪些 `sessionId` 在某 circuit 有激活文件面板」。Dispatcher 只执行**本 circuit** 拥有激活面板的请求；无任何激活面板时由某 circuit 快速返回 `Unavailable`。

```
agent 调 editor_command / editor_query / editor_list_commands
  → 工具取 IHostRequestManager + IHostSurfaceRegistry
      CanSurface(sessionId) == false → 立即 Failure("无可用宿主连接")        [快速失败]
  → manager.BeginAsync(new HostRequest { Kind="editor.command", SessionId, Payload=... })
      → 登记在途 → 触发 PendingChanged（进程内活事件，非事件流）
  → Singleton HostRequestInbox：PendingChanged → 投影 GetAllPending() → 触发 Changed
  → Scoped HostRequestDispatcher（circuit，单飞；构造时初始派发）：
      for each 在途 request:
        if ActivePanelRegistry.TryGetLocal(request.SessionId, myCircuitId, out var localTarget):
            try:    TryResolve(request.RequestId, await localTarget.InvokeAsync(request, ct))   [目标内部已编组]
            catch:  TryResolve(request.RequestId, Failed(reason))
        else if !ActivePanelRegistry.HasAny(request.SessionId):
            TryResolve(request.RequestId, Unavailable)      [全局无激活面板：快速失败，不挂起]
        else:
            continue                                        [他 circuit 持有激活面板：跳过]
  → 工具 WaitAsync(ticket) 返回 → 格式化为 ToolResult
```

**为何不用事件流（G7）**：`SubscribeEvents` 会回放缓冲；刷新/新 circuit 会重放陈旧请求，对带副作用命令重复执行；`TryResolve` 幂等无法撤销已发生的副作用。收件箱投影基于**活 C# 事件 `PendingChanged`**（不回放）+ 构造时拉取，天然规避。

**交付语义（§2.3）**：
- **单标签**：唯一 circuit 持有激活面板 → 唯一执行 → **恰好一次**。
- **多标签且都开同一会话面板**：多个 circuit 各执行一次 → **可能重复**（已知边界）。
- **无激活面板**：任一 circuit 的 Dispatcher 见 `HasAny=false` → `Unavailable`（快速失败）。
- **本 circuit 无面板但他 circuit 有**：本 circuit 跳过，他 circuit 执行。

**同步上下文编组（G8）**：Dispatcher 运行在后台线程（`PendingChanged` 触发）。**面板登记存「可编组委托」** `Func<HostRequest, CancellationToken, Task<HostResponse>>`，由持有组件（`SessionFilePanel`）在内部用 `InvokeAsync` 包裹对 `FilePanelHost`/`FileEditor` 的调用。Dispatcher **不引用任何 Blazor 类型**。

**单飞**：Dispatcher 单飞（**严格镜像 `QuestionInboxView.cs:118-150` 的 `Interlocked` 单飞 + `while(Exchange(dirty,0)==1)` dirty 重检循环**），覆盖完整派发轮次（含目标 `await`）；确保 `await` 期间到达的 `Inbox.Changed` 不被丢失（否则该请求退化为 30s 超时），且避免并发重入导致同 circuit 内重复派发。

**初始派发**：Dispatcher 构造时主动派发一次，覆盖「请求先 Begin、Dispatcher 后建立」的场景。

内容变更旁路（无需额外逻辑）：命令触发 `editor.trigger` → Monaco 触发 `OnDidChangeModelContent` → 现有 `FileEditor.OnContentChanged → SyncFromEditorAsync → Tabs.MarkDirty` 自动标记 dirty。

---

## 6. 分层落点

### 6.1 Abstractions（通用 Host Request）

| 文件 | 内容 |
|------|------|
| `Interactions/HostRequest.cs` | `sealed class`（Manager 约束 `TRequest: class`）：`RequestId`、`required SessionId`、`CallId`、`Kind`（string，如 `editor.command`）、`Payload`（`JsonElement?`，见 §13） |
| `Interactions/HostResponse.cs` | `sealed class`：`RequestId`、`SessionId`、`Status`、`Payload`（`JsonElement?`）、`Reason` |
| `Interactions/HostResponseStatus.cs` | 枚举：`Completed`、`Unavailable`、`Timeout`、`Cancelled`、`Failed` |
| `Interactions/IHostRequestManager.cs` | `: IPendingRequestManager<HostRequest, HostResponse>`（空接口，同 `IQuestionRequestManager`） |
| `Interactions/IHostSurfaceRegistry.cs` | `: ISurfaceRegistry`（空接口，同 `IQuestionSurfaceRegistry`） |
| `Permissions/PermissionKind.cs`（改） | 增 `Editor = 8` |

> **不新增事件类型**、**不新增 `IHostRequestHandler`**：交付走收件箱投影；宿主请求带副作用，不进事件流（§5、§13）。面板委托即处理逻辑。

### 6.2 Core（通用实现）

| 文件 | 内容 |
|------|------|
| `Core/Interactions/PendingRequestManager.cs`（**改**） | Step 1：构造增可选 `ISurfaceRegistry?` 与 `TimeSpan? convergenceGrace`（**均置末位**，权威来源为构造参数，**不新增虚属性**）；上提收敛逻辑（`_convergenceGate/_convergenceCandidates/_convergenceArmed/_convergenceTimer` + `OnProviderUnregistered/OnConvergenceTimer`）；**基类构造不得读取虚属性 `Timeout`**（派生字段未初始化）。`OnDisposing` 退订 + 释放 Timer（幂等，吞 `ObjectDisposedException`）。**派生 `OnDisposing` 必须链式 `base.OnDisposing()`** |
| `Core/Host/HostRequestManager.cs`（新） | 薄子类：`GetRequestId/GetSessionId/AssignRequestId`、`CreateFallback`（→Unavailable/Timeout/Cancelled）、`CreateMissingResponse`、`OnRequestBegan/OnResolved`（no-op 或日志）。覆盖 `Timeout`（默认 30s）；收敛宽限沿用基类默认 1s |
| `Core/Host/HostServiceExtensions.cs`（新） | `AddSeeingHostRequests()`：注册 `IHostSurfaceRegistry`（**独立** `SurfaceRegistry` 实例）+ `IHostRequestManager`（Singleton） |
| `Core/Interactions/SurfaceRegistry.cs`（改） | 增实现 `IHostSurfaceRegistry`（单实现多角色） |
| `Core/Questions/QuestionRequestManager.cs`（改） | 删收敛拷贝；构造把 `surfaceRegistry` 传基类；`OnDisposing` 加 `base.OnDisposing()` |
| `Core/Permission/PermissionRequestManager.cs`（改） | 同上（保留 session/options 退订） |
| `Core/Extensions/ServiceCollectionExtensions.cs`（改） | 调用 `AddSeeingHostRequests()`（**唯一定义处**） |
| `Core/Tools/BuiltIn/DefaultToolPermissionPolicy.cs`（改） | 增 `editor_command` → `("editor.execute", ArgKey: "commandId")` |
| `Core/Permission/PermissionKindMapper.cs`（改） | `editor.*` 前缀 → `PermissionKind.Editor`（**审批成立的前提**，见 §9） |

### 6.3 能力包 `Seeing.Agent.Tools.Editor`（新 · 首个使用者）

| 文件 | 内容 |
|------|------|
| `EditorHostRequests.cs` | 类型化助手：构造 `HostRequest`（Kind/载荷）与解析 `HostResponse.Payload`；**载荷 schema 单一来源** |
| `EditorCommandTool.cs` / `EditorQueryTool.cs` / `EditorListCommandsTool.cs` | 三工具 |
| `EditorToolsModule.cs` | `ISeeingModule`：注册工具 + 内嵌技能注册/注销；`ProvidedTools` |
| `Skills/editor-commands/SKILL.md` | 内嵌技能（`EmbeddedResource`） |
| `Skills/EditorSkillRegistrar.cs` | 扫 manifest 资源 → 注册内嵌技能（镜像 `SchedulerSkillRegistrar`；去 `HostedService` 误导命名） |
| `Seeing.Agent.Tools.Editor.csproj` | 依赖 Abstractions + Tools.Support；`<EmbeddedResource Include="Skills\**\SKILL.md" />`（资源名须含 `.Skills.`） |

> 工具只依赖 `IHostRequestManager` + `IHostSurfaceRegistry`（Abstractions），**不接触 Core**。**没有** `EditorRequestManager`/`EditorEvents`/`IEditorSurfaceRegistry`/`AddSeeingEditor`。

### 6.4 WebUI（通用交付 + Editor 面板）

| 文件 | 内容 |
|------|------|
| `Services/HostRequestInbox.cs`（新） | Singleton，`IDisposable`：订阅 `IHostRequestManager.PendingChanged` + 构造拉取 `GetAllPending()` → `IReadOnlyList<HostRequest>` 快照 + `Changed`（镜像 `QuestionInbox`） |
| `Services/HostRequestActivePanelRegistry.cs`（新） | Singleton：`Register(sessionId, circuitId, Func<HostRequest,CT,Task<HostResponse>>)` / `Unregister(sessionId, circuitId)` / **原子 `TryGetLocal(sessionId, circuitId, out Func<…>?)`** / `bool HasAny(sessionId)`；并发字典保护。委托按 `Kind` 内部分派（单个复用委托） |
| `Services/HostRequestDispatcher.cs`（新） | **Scoped**，`IDisposable`：注入 `HostRequestInbox` + `HostRequestActivePanelRegistry` + `IHostRequestManager` + `CircuitContext`；订阅 `Inbox.Changed`；**构造时初始派发**；**prerender（空 `CircuitContext.Id`）守卫**：不订阅；§5 派发逻辑（**单飞覆盖完整轮次**）；`Dispose` 退订 |
| `Components/SessionFilePanel.razor`（改） | **活跃面板登记**：`ShouldServe = PanelState.IsOpen && PanelState.SessionId == SessionId && _host != null`；`OnAfterRenderAsync`/`OnPanelChanged`（幂等）`Register`/`Unregister`（3 个 editor kind 委托；委托内部 `InvokeAsync` 包裹 `_host` 调用）；不渲染时 `_host = null`；`Dispose` 注销。**不再**用单实例 `Rebind` 桥 |
| `WebUI.Files/Components/FilePanelHost.razor`（改） | 新增 **public 转发方法**供 `SessionFilePanel` 委托调用：`OpenOrActivateAsync`（现 private → public 包装）、`ExecuteCommandAsync`/`QueryAsync`/`ListCommandsAsync`（转发到 `_editor`）；无打开文件/只读/二进制 → Unavailable |
| `WebUI.Files/Components/FileEditor.razor`（改） | 增 `ExecuteCommandAsync`/`QueryAsync`/`ListCommandsAsync`；`Trigger`（`Editor`）、`GetSelection`/`GetPosition`（`Editor`）、`GetValue`/`GetVisibleRanges`（`CodeEditor`）、`GetModelMarkers`（**静态 `BlazorMonaco.Editor.Global`**） |
| `WebUI.Files/wwwroot/files-panel.js`（改） | 新增 `window.seeingEditor.getActions(editorId)`（读 `window.blazorMonaco.editors[].editor.getSupportedActions()`） |
| `Services/WebUiSurfacePresenter.cs`（改） | **接入 `IHostSurfaceRegistry`**（B1）：构造注入并 `Register(this)`、`Dispose` 时 `Unregister`（与 permission/question 并列） |
| `Program.cs`（改） | `AddSeeingModule<EditorToolsModule>`；`AddSingleton<HostRequestInbox>`；`AddSingleton<HostRequestActivePanelRegistry>`；`AddScoped<HostRequestDispatcher>` |
| `Shared/MainLayout.razor`（改） | **急切解析 `HostRequestDispatcher`**（注入以触发构造/订阅；`MainLayout` 是全路由布局，始终渲染） |

---

## 7. 工具契约（Host Request 载荷）

### 7.1 `editor_command`（`Kind = "editor.command"`）

- 参数：`commandId`（必需）、`args`（可选，透传 payload）、`path`（可选，指定则在**当前激活面板内**切换/打开该文件）。
- 行为：`path` 有值时先经 `FilePanelHost` 的 **public** 打开/切换入口（当前面板内）；再 `editor.Trigger("seeing-agent", commandId, args)`。
- 返回：`{ status:"completed", changed:bool }`；命令不存在 → `{ status:"failed", reason:"未知命令" }`。
- 权限：资源级 `editor.execute`（resource=commandId）→ 走审批链（§9）。

### 7.2 `editor_query`（`Kind = "editor.query"`）

- 参数：`kind`（enum，必需）、`path`（可选）。
- `kind`：`selection`、`cursor`、`diagnostics`（`Global.GetModelMarkers`）、`visibleRanges`、`value`（带长度上限与截断标记）。
- 返回：结构化 JSON，**定界包裹**（`<<<EDITOR_DATA_BEGIN>>>...<<<EDITOR_DATA_END>>>`）防提示注入，同 `QuestionTool.BuildCompletedOutput`。
- 权限：只读，不做资源级检查。

### 7.3 `editor_list_commands`（`Kind = "editor.list_commands"`）

- 参数：无。
- 返回：`getSupportedActions()` 的 `[{ id, label, keybinding? }]`。
- 权限：只读，不做资源级检查。

---

## 8. 交付细节（WebUI）

- **定位 = 用户当前激活的文件面板**：`SessionFilePanel` 在 `ShouldServe` 时向 `HostRequestActivePanelRegistry`（Singleton）登记 `(sessionId, circuitId, 委托)`。
- **Dispatcher 路由**：本 circuit 有该 session 激活面板 → 执行；全局无任何激活面板 → `Unavailable`（快速失败）；他 circuit 持有 → 跳过。
- **多窗口**：每 circuit 至多一个激活面板（`FilePanelState` Scoped 唯一）；同 circuit 多窗口不同 session 各自独立。
- **编组**：面板委托内部 `InvokeAsync` 到 UI 线程；Dispatcher 不引用 Blazor 类型。
- **Unavailable 判定**（立即 `TryResolve`，不挂起）：全局无激活面板 / 面板无打开文件 / 当前标签为图片/二进制/只读 / 编辑器未就绪（`_editor is null`，由 `FilePanelHost` 转发方法内判定）。
- **超时**：`HostRequestManager` 覆盖 `Timeout`（默认 30s）。
- **DI/接线**：`HostRequestDispatcher` 为 Scoped，注入 `CircuitContext`；prerender（空 `CircuitContext.Id`）守卫：不订阅；由 `Shared/MainLayout.razor` 急切解析以建立订阅；`Dispose` 退订 `Inbox.Changed`。**面板登记所有者是 `SessionFilePanel`**（`FilePanelHost` 在 `WebUI.Files`，只引用 Abstractions，**无法**注入 WebUI 侧 registry——B2）。

---

## 9. 权限（走审批链）

- 新增 `PermissionKind.Editor = 8`；`PermissionKindMapper` 映射 `editor.*` → Editor。
- `DefaultToolPermissionPolicy` 增 `editor_command` → `("editor.execute", ArgKey:"commandId")`；查询/列举不入表。
- **审批成立的前提**：工具级检查（`AgentExecutor.EvaluatePermissionAsync`）对 build 的 `Allow(Tool,"*")` 会先放行非资源类 kind；资源级检查（`ToolManager`）以 `editor.execute` 进 `AuthorizeCoreAsync`，**只有当 mapper 把 `editor.execute` 映射为 `Editor`**（`Allow(Tool,"*")` 因 `PermissionRuleEntry.Matches` 限定 `{Tool,McpTool,Skill}` 不匹配 Editor）→ 无匹配 → `DefaultEffect=Ask` → 进入审批。**漏改 mapper 会静默回退 `Tool` 并被 build 短路为 Allow**。
- 措辞修正：build 下 `editor_command` **默认 Ask**；**作用域由用户选择**（选 `Session` 会记忆、同 commandId 后续免审；`Once` 才每次确认）。
- `plan`：`AllowedTools` 不含 → 工具级即 Deny。`explore`：无匹配规则 → `DefaultEffect=Deny`。
- **Host Request 机制本身不做授权**（与 question 一致）：授权是工具层职责。

---

## 10. SKILL 配合

内嵌 `editor-commands` SKILL.md：

1. 三工具用法与选择指引。
2. **精选高频命令目录**（id + 用途 + 参数 + 是否改内容）：视图/导航、折叠、选区/多光标、编辑、查找（示例见附录 A）。
3. `editor_query` 的 `kind` 说明与返回结构。
4. **目录查不到时用 `editor_list_commands` 动态探测**（并说明命令 id 随 Monaco 版本漂移）。
5. 无宿主/无激活面板时快速失败的语义（提示：需在 WebUI 打开文件面板）。

---

## 11. 测试

| 层 | 测试 | 说明 |
|----|------|------|
| Step 1 回归 | 现有 `QuestionRequestManagerTests`、`PermissionRequestManagerTests` 全绿 | 锁定收敛/超时/取消/幂等行为等价（internal 构造保留 `convergenceGrace` 参数即可零改动） |
| Step 1 新增 | `PendingRequestManagerConvergenceTests` | 基类：宽限期内重注册不收敛、跨会话不收敛、SurfaceShrink 不收敛 |
| Core | `HostRequestManagerTests` | 镜像 `QuestionRequestManagerTests`：Begin/Resolve 幂等、超时、取消、未知票据、注销收敛、Dispose 收敛 |
| 能力包 | `EditorToolTests` | 三工具参数校验、CanSurface=false 快速失败、载荷构造/解析、定界 |
| 权限 | `DefaultToolPermissionPolicyTests` 增 `editor_command`；`PermissionKindMapperTests` 增 `editor.*` | |
| WebUI | `HostRequestInboxTests` | 投影 = `GetAllPending` 快照；`PendingChanged` 触发 Changed |
| WebUI | `HostRequestActivePanelRegistryTests` | Register/Unregister；`TryGetLocal`/`HasAny`；多 circuit 同 session 各自登记；`TryGetLocal` 原子性（无 HasLocal/GetLocal 二步窗口） |
| WebUI | `HostRequestDispatcherTests` | 本 circuit 有激活面板 → 执行并 TryResolve；他 circuit 持有 → 跳过；全局无面板 → Unavailable；prerender 守卫；单飞（覆盖 await）；初始派发；Dispose 退订 |
| WebUI | `EditorPanelTargetTests`（可测部分） | `ShouldServe` 条件（session 匹配/面板开/`_host` 非空）；命令/查询分支（JS 交互 mock 隔离） |

测试命令（见 `AGENTS.md`；MTP 本环境无法发现测试，用 VSTest）：
```
dotnet build tests/spine/Seeing.Agent.Tests
dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
```

---

## 12. 实施顺序

1. **Step 1 重构**：上提收敛到基类 → 调整 Question/Permission（含 `base.OnDisposing()`）→ 现有测试全绿（独立可回滚提交）。
2. **Step 2 Abstractions（通用）**：Host DTO/契约 + `PermissionKind.Editor`。
3. **Step 2 Core（通用）**：`HostRequestManager` + `HostServiceExtensions` + `SurfaceRegistry` 多角色 + DI + **`WebUiSurfacePresenter` 接入 `IHostSurfaceRegistry`**。
4. **Step 2 WebUI（通用）**：`HostRequestInbox` + `HostRequestActivePanelRegistry` + `HostRequestDispatcher` + `Shared/MainLayout.razor` 急切解析。
5. **Step 3 能力包**：`Tools.Editor` 三工具 + 载荷助手 + 模块 + 内嵌 SKILL。
6. **Step 3 WebUI（Editor）**：`FileEditor` 命令/查询 + 枚举 JS + `FilePanelHost` public 转发 + `SessionFilePanel` 面板登记。
7. 权限：`PermissionKindMapper` + `DefaultToolPermissionPolicy`。
8. 测试补齐 + 全量回归。

---

## 13. 风险与注意事项

| 风险 | 缓解 |
|------|------|
| Step 1 改变 Permission 收敛语义 | 收敛仅调 `CreateFallback(req, Unavailable)`；Permission `Unavailable` 落 `_`→`NoChannel Deny`（逐字段与现路径一致）；现有测试锁定 |
| 基类 `Dispose` 顺序 | 基类 `Dispose()` 先 `OnDisposing()` 再收敛（收敛不依赖 Timer/订阅）；派生 **必须** `base.OnDisposing()`；基类构造不得读取虚属性 `Timeout` |
| `PendingRequestManager` 构造签名变更 | 新参数（`ISurfaceRegistry?`、`TimeSpan? convergenceGrace`）置末位；测试 internal 构造保留 `convergenceGrace` 参数；**不新增虚属性**（避免 ctor 参数与虚属性冗余） |
| **陈旧请求重复执行** | **不用事件流**（回放）；收件箱投影（活 `PendingChanged` + 构造拉取） |
| **多标签同会话同面板可能重复执行** | **已知边界**（架构决策 §2.3）：单标签恰好一次；多标签「尽力而为」。与项目其他 P3 边界同级 |
| **presenter 未接入 host registry（B1）** | `WebUiSurfacePresenter` 构造注入 `IHostSurfaceRegistry` 并 `Register/Unregister`；否则 `CanSurface` 恒 false |
| **面板登记所有者/程序集约束（B2）** | 所有者 = `SessionFilePanel`（`FilePanelHost` 在 `WebUI.Files`，只依赖 Abstractions，无法注入 WebUI 侧 registry） |
| **登记条件（B6）** | 含 `PanelState.SessionId == SessionId`；不渲染时 `_host = null`；`OnPanelChanged`（含 `SetSide/SetTreeVisible`）幂等重算 |
| **执行器线程编组（G8）** | 面板登记存**可编组委托**，组件层 `InvokeAsync` 包裹；Dispatcher 不引用 Blazor |
| **circuit 接线/急切构造** | Dispatcher 注入 `CircuitContext`；prerender 守卫；`Shared/MainLayout.razor` 急切解析；`IDisposable` 退订 |
| **同 circuit 重复派发 / 丢失派发** | Dispatcher 单飞**严格镜像 `QuestionInboxView` 的 dirty 重检循环**（覆盖完整轮次含 await），既不重复也不丢失 `Inbox.Changed` |
| **回落/新 circuit 漏派发** | Dispatcher **构造时初始派发**；无激活面板时任一 circuit 快速 `Unavailable` |
| Registry 并发 | `ActivePanelRegistry`（Singleton）并发字典保护 |
| 子代理会话 | 非 active 子会话无文件面板 → 无激活面板 → Unavailable（§2.2 明确范围） |
| `Payload` 序列化 | 一律 `JsonSerializer.SerializeToElement`（自持 `JsonDocument`），**禁止**传入已释放的 `JsonDocument.RootElement`；生命周期由在途字典界定 |
| `HostResponseStatus` 与 `QuestionResultStatus` 重叠 | 语义域不同（宿主 vs 人机），独立；`Failed` 为宿主执行失败专用。将来统一再评估 |
| `GetModelMarkers` 位置 | 在静态 `BlazorMonaco.Editor.Global`，非编辑器实例 |
| BlazorMonaco 未暴露 action 枚举 | 自定义 JS 读 `window.blazorMonaco.editors[].editor.getSupportedActions()` |
| 命令在只读/截断文件执行 | `FilePanelHost` 转发方法内预判（`IsReadOnly`/`IsDeleted`/二进制）→ Unavailable |
| 命令 id 版本漂移 | SKILL 标注「可能漂移」+ `editor_list_commands` 兜底 |
| 无宿主（CLI/Gateway） | `CanSurface` 快速失败 |
| 命令改内容未保存 | 明确「标记 dirty 不自动保存」 |
| Dispatcher 未退订 | 实现 `IDisposable`，circuit 关闭时退订 `Inbox.Changed` |

---

## 14. 未来演进（非本次范围）

- 新宿主能力（剪贴板、通知、终端）只需：新增工具 + 载荷助手 + 组件登记面板委托；**机制层 Core/Abstractions 零改动**（`Kind` 为 string、DTO 泛型、Registry 通用；委托按 `Kind` 内部分派）。变更类工具若需独立审批语义，按需新增 `PermissionKind`（如 Editor 即如此），属 Core 改动。
- **若未来不同能力由不同组件登记同 `(sessionId,circuitId)`**：需将 `ActivePanelRegistry` 键扩展为 `(sessionId, circuitId, capabilityId/kind)`，避免互相覆盖（当前 editor 单组件单委托，无此问题）。
- 远程宿主（Gateway）若需交付，另立**非回放**通道（点对点或单调序号去重），不复用会话事件流。
- 若将来确需「多标签严格恰好一次」，再引入选主 + 一次性执行闸（本次明确不做，见 §2.3）。
- `HostRequestInbox` 与 `QuestionInbox` 当前各自独立（投影模型与消费语义不同：只读 vs 可执行派发）；若出现第三处「Manager.PendingChanged → 投影」，再评估抽薄基类。
- question/permission 若将来统一到 Host Request（边界 A → B），单独评估迁移。

---

## 附录 A：SKILL 命令目录示例

- 视图/导航：`editor.action.revealDefinition`、`editor.action.goToLocations`、`editor.action.showHover`、`editor.action.marker.next`
- 折叠：`editor.foldAll`、`editor.unfoldAll`、`editor.foldLevel1`
- 选区/多光标：`editor.action.selectHighlights`、`editor.action.insertCursorAbove`/`Below`、`editor.action.addSelectionToNextFindMatch`
- 编辑（改内容）：`editor.action.formatDocument`、`editor.action.commentLine`、`editor.action.moveLinesUpAction`/`DownAction`、`editor.action.indentLines`/`outdentLines`
- 查找：`actions.find`、`editor.action.startFindReplaceAction`
