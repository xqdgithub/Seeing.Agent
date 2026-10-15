# Seeing.Agent CLI 一次性提示词执行设计（`-p` / `--yolo`）

**日期**：2026-10-12
**分支**：master（以当前分支为准）
**状态**：已实现（`-p`/`--yolo`/`--stream` 已落地，CLI 测试全绿）

**依据**
- `docs/architecture/03-configuration-lifecycle.md`、`04-development-standards.md`、`08-permission-authorization-release-notes.md`
- `docs/architecture/14-session-inbox-runtime-release-notes.md`
- `docs/superpowers/specs/2026-07-30-seeing-cli-design.md`
- `docs/superpowers/specs/2026-09-17-permission-authorization-subsystem-design.md`
- 代码基线：`samples/Seeing.Agent.Cli/{Program.cs,Infrastructure/CliServiceBootstrap.cs,Commands/*}`、`src/abstractions/Seeing.Agent.Abstractions/Runtime/{ISessionRuntime.cs,InboxMessage.cs,InboxSender.cs,ISessionDirectory.cs}`、`src/abstractions/Seeing.Agent.Abstractions/Events/{MessageEventTypes.cs,HostingLifecycleEvents.cs}`、`src/hosting/Seeing.Agent.Hosting/Execution/SessionRuntime.cs`、`src/hosting/Seeing.Agent.Hosting.Headless/HeadlessHostingServiceCollectionExtensions.cs`、`src/spine/Seeing.Agent.Core/Core/Permission/{EffectivePermissionPolicy.cs,PermissionService.cs}`、`src/primitives/Seeing.Session/Core/{SessionData.cs,ISessionManager.cs}`

---

## 1. 问题与动机

当前 `seeing-cli` 只能做管理类操作（服务启停、配置、代理/任务/会话查询），唯一的代理交互入口是 `seeing-cli`（无参）或 `seeing-cli tui` 拉起的全屏 TUI。这带来两个缺口：

1. **无法脚本化**：CI/脚本无法"给一句提示词、拿到结果、按退出码判断成败"。TUI 需要交互终端，重定向输入/输出即被 `TuiLaunch.CanRunInteractive` 拒绝。
2. **无头执行缺审批策略**：Headless/CLI 宿主未注册交互审批通道，落到默认 `DenyAllPermissionChannel` → 需要审批的操作立即 `Deny(NoChannel)`，无法在无人值守脚本中完成写文件/执行命令类任务。

**目标**：为 `seeing-cli` 增加一次性（one-shot）提示词执行能力，支持：

- `-p/--prompt`：投递一句提示词、执行、返回结果，可加入脚本工作流；
- `--yolo`：本次会话自动批准全部审批，使无头脚本可完成需审批的操作；
- `--stream`：以 JSONL 事件流输出，便于程序化消费。

**非目标**（YAGNI）：双向 JSON-RPC/ACP 交互、多轮会话续接（`--continue`/`--resume`）、交互式控制台审批通道、CLI 内持久化审批记忆、`--json` 单对象输出。这些留待后续演进。

---

## 2. 命令形态

在 `Program.cs` 的 `rootCommand` 上直接挂选项与根 action。保持既有语义：**无参数**仍进 TUI；带子命令仍走子命令；**带 `-p`** 走一次性执行。

```
seeing-cli -p "把 README 里的 TODO 都列出来" --yolo
seeing-cli -p "总结这个仓库" --stream | jq -c 'select(.type=="stream.delta")'
echo "解释这段代码" | seeing-cli -p -
```

### 2.1 选项表

| 选项 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `-p, --prompt <text>` | string | 无 | 提示词正文；`-` 表示从 stdin 读取全部内容。指定即触发一次性模式 |
| `--yolo` | bool | false | 本次会话 `SessionAutoApprove.Enabled`（自动批准全部审批） |
| `--stream` | bool | false | 以 JSONL 事件流写 stdout（见 §4） |
| `--agent <id>` | string? | null | 覆盖默认 Agent（透传 `InboxMessage.AgentId`） |
| `--model <id>` | string? | null | 覆盖默认模型（透传 `InboxMessage.ModelId`） |
| `--timeout <sec>` | int? | null | 整体超时（秒）；超时取消执行并返回退出码 1 |
| `--log-level <level>` | string? | null | 日志级别（Debug/Information/Warning/Error）；**默认不输出任何日志**，指定后才写 stderr |

> 兼容性：`-p` 为根命令选项。System.CommandLine 下根命令带选项后，`args.Length == 0` 的短路分支（`Program.cs:12`）保持不变；显式子命令（`start`/`config`/...）优先级不受影响。

### 2.2 参数校验

- 未提供 `-p` 且提供了 `--yolo`/`--stream` 等一次性专属选项 → 报错退出码 2，提示"一次性选项需与 -p 同用"。
- `-p` 值为空字符串 → 退出码 2。
- `--timeout <= 0` → 退出码 2。

---

## 3. 执行流程

```
解析选项
  → 若 -p == "-"：从 stdin 读全文作为 prompt
  → 构建 Host（复用 CliServiceBootstrap.BuildHostAsync）
  → ISessionDirectory.CreateAsync(title=prompt 截断)   // 每次新建会话
  → 若 --yolo：ISessionManager.SetAutoApproveAsync(sessionId, Enabled)
  → 订阅 ISessionRuntime.SubscribeEvents(sessionId)（后台泵事件 → Channel）
  → ISessionRuntime.DeliverAsync(Wake, Presentation=User, AgentId/ModelId)
  → 消费事件直到 ExecutionCompleteEvent（或 LoopComplete + idle）
  → WaitForIdleAsync 兜底
  → 输出结果（默认文本 / --stream JSONL）
  → 退出码
```

### 3.1 会话生命周期

- **每次新建会话**（用户确认）：不提供 `--continue`/`--resume`，行为确定、无隐式状态。
- 会话**保留**（用户确认）：执行后可经 `seeing-cli session list` 查到，便于事后审计；标题取提示词前若干字符（复用现有标题生成/截断策略）。

### 3.2 事件订阅与终态判定

- 订阅必须在 `DeliverAsync` **之前**建立，避免漏掉首批事件（`SubscribeEvents` 订阅时自动回放环形缓冲，天然幂等，双保险）。
- 终态：收到 `ExecutionCompleteEvent`（`Type == "loop.complete"`，带 `ExecutionId`/`Status`）。收到后：
  - 取消订阅；
  - `await WaitForIdleAsync(sessionId, ct)` 兜底（防仍有未读 Wake 残留）；
  - 结束。
- 失败信号：`ExecutionCompleteEvent.Status == Failed` 或收到 `ErrorEvent` → 记录失败，退出码 1（仍输出已产生的最终文本/事件）。

### 3.3 事件消费实现

采用与 TUI `TuiEventPump` 同构的通道泵：后台任务 `await foreach (SubscribeEvents)` → `Channel<IMessageEvent>`，主协程从通道读并逐条投影/写出。通道无界，由消费端持续排空。

---

## 4. 输出契约

### 4.1 默认模式（无 `--stream`）

- **stdout**：仅最终 assistant 文本 = **最后一条 `StreamCompleteEvent.Message.Content`**（多步工具轮次下取最后一步的最终回复）。不额外加换行包装（保留内容原样；若内容非空则以换行结尾）。
- **stderr**：默认不输出任何工具进度等噪音；仅在失败时输出错误/权限拒绝诊断（用于解释非零退出码）。
- 若执行失败且无最终文本，stdout 为空、stderr 有错误、退出码 1。

### 4.2 `--stream` 模式

- **stdout**：每行一个 JSON 对象（JSONL），**直接序列化具体的 `IMessageEvent` 实例**（用户确认）。因事件类型为开放字符串标识，`data` 不另包信封，字段随事件模型走。示例：

```jsonl
{"sessionId":"ses_...","loopId":"...","timestamp":"2026-10-12T10:00:00","type":"stream.start","step":0,"attempt":1}
{"sessionId":"ses_...","loopId":"...","timestamp":"2026-10-12T10:00:01","type":"stream.delta","contentDelta":"好的，"}
{"sessionId":"ses_...","loopId":"...","timestamp":"2026-10-12T10:00:02","type":"stream.complete","message":{...},"usage":{...}}
{"sessionId":"ses_...","loopId":"...","timestamp":"2026-10-12T10:00:03","type":"loop.complete","executionId":"exec_...","status":"Completed"}
```

- 序列化：`JsonSerializer.Serialize(evt, evt.GetType(), options)`，`options` 用 `PropertyNamingPolicy = CamelCase` + `DefaultIgnoreCondition = WhenWritingNull`；事件/DTO 上已有的 `[JsonPropertyName]`（如 `ChatMessage` 的 snake_case）优先。
- **stderr**：与默认模式相同的诊断（`--stream` 下 stdout 已被 JSONL 占用）。
- 每行 `WriteLine` + `Flush`，保证管道实时性。

### 4.3 stdout 纯净性（关键约束）

`Host.CreateApplicationBuilder` 默认 Console 日志写 **stdout**，会污染结果/JSONL。一次性模式必须：

1. 将 Console logger 的 `LogToStandardErrorThreshold` 设为 `LogLevel.Trace`，使全部日志写 stderr；或
2. 配置 `Logging:Console:LogToStandardErrorThreshold=Trace`（等价）。

实现优先在 `CliServiceBootstrap` 增加一次性模式的重载/开关，把日志重定向到 stderr；`--log-level` 控制最低级别。

> **默认级别**：一次性模式下日志默认**完全静默**（`None`），只有显式 `--log-level <level>` 才输出日志。
> 不能依赖 CLI 自带的 `appsettings.json`（其 `Default: Warning`）——该文件从**当前目录**加载，
> 脚本在其它目录运行时不会命中，会退回到 `Information` 造成刷屏；故必须由代码显式设置默认级别。

### 4.4 退出码

| 码 | 含义 |
|----|------|
| 0 | 成功（`ExecutionCompleteEvent.Status == Completed`） |
| 1 | 执行失败（`Failed` / `ErrorEvent` / 超时） |
| 2 | 参数错误（缺 `-p`、空 prompt、非法 timeout、误用一次性选项） |
| 130 | 用户 Ctrl+C 取消 |

沿用 `Program.cs` 现有约定：命令 action 经 `Environment.ExitCode` 表达失败，`Main` 不吞掉。

---

## 5. `--yolo` 与审批语义

### 5.1 `--yolo`

- 仅对**本次新建会话**调用 `ISessionManager.SetAutoApproveAsync(sessionId, SessionAutoApprove.Enabled)`。
- 生效链（`EffectivePermissionPolicy.Resolve`）：会话三态 `Enabled` → `PermissionEffect.Allow`，短路所有需审批的操作。作用域限于本会话，**不**修改全局 `Permission.AutoApproveAll`，不污染同进程其它会话。

### 5.2 不加 `--yolo`

- CLI/Headless 宿主注册的是 `DenyAllPermissionChannel`（`TryAutoApprove` 恒 null）；无 `IQuestionSurfaceRegistry`/呈现端 → `CanSurface == false`。
- 授权引擎在步骤 6 返回 `Deny(NoChannel)`，操作被拒。stderr 输出明确提示：`需 --yolo 或改用 TUI 以进行交互审批`。
- 执行不会挂起等待（现状"无 Presence → 立即 Deny"），符合脚本预期。

### 5.3 安全说明

`--yolo` 等同 `AutoApproveAll` 的会话级等价物，属危险开关；文档与 `--help` 中标注安全警告。工作区硬边界（`Workspace.RestrictToWorkspace`）等**规则级 Deny 不受 `--yolo` 影响**（Deny 规则在步骤 3 短路，先于生效开关）。

---

## 6. 代码结构

新增（不改动既有命令行为）：

| 文件 | 职责 |
|------|------|
| `samples/Seeing.Agent.Cli/Commands/PromptCommand.cs` | 定义 `-p/--yolo/--stream/--agent/--model/--timeout/--log-level` 选项与根 action；参数校验与退出码映射 |
| `samples/Seeing.Agent.Cli/Services/OneShotOptions.cs` | 一次性执行选项记录（`Prompt/Yolo/Stream/Agent/Model/Timeout/LogLevel`） |
| `samples/Seeing.Agent.Cli/Services/OneShotRunner.cs` | 纯编排：建会话 → 设 yolo → 订阅 → 投递 → 收终态 → WaitForIdle → 返回结果；可单测 |
| `samples/Seeing.Agent.Cli/Services/OneShotOutput.cs` | 输出策略：默认文本 vs JSONL 序列化；stdout/stderr 分离 |
| `samples/Seeing.Agent.Cli/Services/StdinReader.cs` | `-p -` 时读取 stdin 全文 |

改动：

- `Program.cs`：注册根命令的一次性选项与 action（保留无参进 TUI 与各子命令）。
- `Infrastructure/CliServiceBootstrap.cs`：新增一次性模式重载/参数，将 Console 日志阈值改写到 stderr。

**依赖方向**：仅在 sample 内新增，复用 `ISessionRuntime`/`ISessionDirectory`/`ISessionManager`（Abstractions/原语端口），不引入新的跨层依赖，不触碰 Core/能力包。

---

## 7. 边界与错误处理

| 场景 | 行为 |
|------|------|
| 未配置 LLM Provider | 执行失败，stderr 提示配置 `seeing.json`，退出码 1 |
| prompt 为 `-` 但 stdin 为空 | 退出码 2（空提示词） |
| 需审批且未 `--yolo` | 操作被拒，执行可能以失败终态结束；stderr 提示 `--yolo` |
| `--timeout` 触发 | `ISessionRuntime.StopAsync(sessionId)` 取消，退出码 1 |
| Ctrl+C | 注册 `Console.CancelKeyPress`/`PosixSignal`，调用 `StopAsync`，退出码 130 |
| 执行中产生子会话/Task | 由运行时消费循环管理；一次性模式只关心根会话终态，子会话终态前根 `ExecutionComplete` 已发出 |
| 未知事件类型 | JSONL 原样序列化；默认模式忽略 |

---

## 8. 测试计划

`tests/apps/Seeing.Agent.Cli.Tests`（xUnit + FluentAssertions）：

1. **选项解析**：`PromptCommand.Create()` 暴露 `prompt/yolo/stream/agent/model/timeout/log-level`；`-p` 别名。
2. **参数校验**：缺 `-p` 却给 `--yolo` → 错误；空 prompt → 退出码 2；`--timeout 0` → 退出码 2。
3. **JSONL 序列化**：给定 `StreamDeltaEvent`/`StreamCompleteEvent`/`ExecutionCompleteEvent` 样本，`OneShotOutput` 输出为单行合法 JSON，含 `type` 与具体字段（camelCase）。
4. **默认文本提取**：多步事件序列 → 取最后 `StreamCompleteEvent.Message.Content`。
5. **退出码映射**：`Completed`→0，`Failed`→1，取消→130。
6. **yolo 调用**：mock `ISessionManager`，`--yolo` 时断言 `SetAutoApproveAsync(sessionId, Enabled)` 被调用；无 `--yolo` 时不调用。
7. **`-p -` stdin**：`StdinReader` 注入 `TextReader` 读取全文。
8. **OneShotRunner 编排**：以 fake `ISessionRuntime`（可发事件流）断言投递参数（Presentation=User、AgentId/ModelId 透传）与终态退出。

（遵循 AGENTS.md：CLI 测试用 VSTest 直接跑已构建程序集。）

---

## 9. 文档与帮助

- `--help` 中 `-p`/`--yolo`/`--stream` 描述清晰，`--yolo` 标注安全警告。
- 更新 `docs/superpowers/specs/2026-07-30-seeing-cli-design.md` 命令总览，补一行：
  `seeing-cli -p "<prompt>" [--yolo] [--stream]   一次性执行提示词并返回结果`。
