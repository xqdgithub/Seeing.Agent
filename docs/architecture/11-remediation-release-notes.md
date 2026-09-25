# 11 全项目整改 Release Notes

**日期：** 2026-09-25
**来源：** 全项目架构审查报告 `.superpowers/sdd/reviews/2026-09-25-full-project-audit.md`（P0×4 / P1×28 / P2×30+）
**设计规格：** [`2026-09-25-full-project-remediation-design.md`](../superpowers/specs/2026-09-25-full-project-remediation-design.md)
**实施计划：** [`2026-09-25-full-project-remediation.md`](../superpowers/plans/2026-09-25-full-project-remediation.md)
**合规记录：** [`06-compliance-audit.md` §全项目审查复扫（2026-09-25）](06-compliance-audit.md)
**相关：** [`08 权限授权子系统 Release Notes`](08-permission-authorization-release-notes.md)（§7 增量）、[`09`](09-session-tools-and-groups-release-notes.md)、[`10`](10-session-persistence-release-notes.md)

---

## 1. 概要

本轮按「先止损（安全/泄漏）→ 再纠偏（正确性根因）→ 后清理（死代码/文档）」分 7 批次完成：

- **批次 1 安全止损**：webfetch 重定向逐跳 SSRF 校验；Once 批准不再写白名单；ACP 文件桥分隔符边界；MCP 工具零审批修复。
- **批次 2 生命周期对称**：MCP/SystemOne/Memory/AgentsBuiltIn/ACP/Scheduler/Skill 的注册类资源统一收编进所属模块 `Activate/Deactivate`；裸 Bootstrap 退役；组件加载加模块门控。
- **批次 3 事件流**：`ExecutionEventPublisher` 发布/订阅同锁（恰一次），移除 Router 手动回放与 skipSet。
- **批次 4 执行管线**：per-execution CTS（取消不再偷换）；装饰器链注释校正；SnapshotStorage/LoopDetector/并发加固；ACP 取消可达。
- **批次 5 能力包**：LLM usage/Responses/释放链、FileSystem rg `--json`、Memory 连接门、MCP 取消透传、TaskTool 轮询等。
- **批次 6 会话子系统**：SessionData 全快照、handoff 回滚取消隔离、`session.destroyed` 清理链路。
- **批次 7 守门文档**：死代码清理、扫描命令补盲、文档漂移修复、本 release notes。

---

## 2. 破坏性变更

### 2.1 契约变更

| 类型 / 成员 | 位置 | 处置 | 替代 |
|-------------|------|------|------|
| `IToolManager.RegisterTool` | `Abstractions/Tools/IToolManager.cs` | **删除（同步）** | `RegisterToolAsync(ITool, CancellationToken)`；`RegisterTools` / `RegisterToolsFromType` 同步包装一并移除 |
| `IMcpController.Pause/ResumeServer(s)` | `Abstractions/Mcp/IMcpController.cs` | **异步化** | `PauseServerAsync` / `ResumeServerAsync` / `PauseAllServersAsync` / `ResumeAllServersAsync`（均接受 `CancellationToken`） |
| `IDynamicToolContributor`（新增） | `Abstractions/Tools/` | **新增契约** | 模块 Activate 登记、工具集运行时可变；`ExecutionJobService` 结算时并入 enabled 模块的动态贡献 |

### 2.2 行为变更（同输入不同结果）

1. **Once 批准不再写入会话目录白名单**：仅 `Scope != Once`（SessionDirectory 及以上）写白名单；「本次允许」后同目录再次访问仍询问。详见 [`08 §7.1`](08-permission-authorization-release-notes.md)。
2. **`RequireInteraction=true` 绕过工作区白名单**：边界预检 Allow 分支补 `!RequireInteraction` 守卫。
3. **MCP 工具 server 粒度审批**：MCP 工具以 `mcp.execute` kind、resource=server 名发起审批；Agent 的 `Allow(Tool,"*")` 不再短路；批准记忆按 server 粒度。
4. **Memory 第二套开关收尾**：Hook 层与提示注入不再读取 `MemoryOptions.Enabled`（模块启停为单一真相源）；退役 `MemoryBootstrapHostedService` 与进程级 `MemoryHookRegistrationGate`，4 个 Hook 随模块 `Activate/Deactivate` 登记/撤销。**配置迁移**：以模块 enabled 控制记忆 Hook/召回；`MemoryOptions.Enabled` 顶层字段**已删除**（含后台 worker 读取与 WebUI 总开关），记忆启停单一真相源归模块生命周期（`a7579b0`）。
5. **`/skill` 假成功改明确失败**：当会话末条消息非 user 时，`/skill` 返回 `CommandResult.Fail`（此前静默丢弃并报成功）；并通过 `SessionManager.UpdateSessionAsync` 持久化，避免刷新丢失。
6. **ACP 命令/skill 注册依赖模块激活**：ACP 命令、动态 skill 命令随 `AcpModule.Activate` 注册、`Deactivate` 注销（此前宿主启动即注册，模块停用后仍可见）。
7. **TaskCard `FailStep` 取消不写 `Error`**：区分取消与错误——`cancelled=true` 时不写 `ToolCall.Error`（父工具状态由事件流权威置为 cancelled），避免卡片误渲染为错误。

---

## 3. 非破坏性修复要点

| 领域 | 修复 |
|------|------|
| 安全 | webfetch 逐跳 SSRF 预校验 + 流式限长；ACP 工作区前缀分隔符边界；用户正则 `MatchTimeout`（ReDoS 面）。**DNS rebinding TOCTOU 未消除**（每跳仅做一次 DNS 预校验，实际连接由 HttpClient 重新解析），已列入 [`06`](06-compliance-audit.md) 接受残留 |
| 事件流 | `ExecutionEventPublisher` 发布、订阅注册+回放同锁（恰一次）；Router 简化 |
| 执行取消 | CTS 绑定 `ExecutionRecord`（per-execution），`CancelAsync` 按 id 取消且延迟 Dispose；exec1/exec2 闪断根因关闭 |
| 循环检测 | `LoopDetector` 接线生产路径（per-execution 状态，3 警告 5 终止）；单例字段竞态消除 |
| LLM | Anthropic `message_start` usage；OpenAI `stream_options.include_usage`；Responses 输入侧 `function_call`/`function_call_output`；客户端 `IDisposable` 释放链；流式工具参数 JSON 校验 |
| FileSystem | ripgrep 改 `--json`（修 Windows 盘符解析）+ 异步超时；ReadTool 流式限长 |
| MCP | 模块生命周期对称（子进程/工具/重连器全清）；动态工具结算契约；`DisposeAsync` 空遍历修复；Pause/Resume 异步化；工具执行链 `CancellationToken` 透传 |
| Memory | CostControl 经 `SqliteConnectionGate`；Hook 生命周期收编 |
| Scheduler | Skill/命令注册收编；Once 任务停机补跑（`FireNow`）；配置节注册越界治理 |
| 会话 | SessionData 全快照（`Messages`/`Clone`/`ClearMessages` 入锁）；handoff `RollbackAsync` 用 `CancellationToken.None`；`session.destroyed` → 收敛在途 + `ClearSession`；写回原子化 |
| 配置 | `MergeDeep` 数值 0 视为合法覆盖 + null 分支深拷贝；`UnifiedConfigManager` 原子替换 + 文件锁 |
| 工具发现 | 拒绝 `async void`/`out`/`ref`/泛型；required 语义对齐 |
| 杂项 | `DateTime.Now` → `UtcNow`；`IsDirectory` 上移 `FileSystemHelper`（消 5 处复制）；静默吞异常补日志；流式 EOF 补中断标记 |
| 重试中间件 | `RetryMiddleware` 可重试集合补 `IOException`，改为指数退避（`1s×2^attempt`，上限 10s）且退避等待响应取消（`8ae5f3f`） |
| 守门/文档 | `Full` 清单补 `systemone`/`systemone.tools` 并反射化守门（`2c934db` + `ead35e3`）；命名空间迁移 `Seeing.Agent.Core.Tools.*` → `Seeing.Agent.Tools.*`（`e1c23eb`，零残留） |

---

## 4. 已知差异 / 边界

| 项 | 说明 |
|----|------|
| ACP 不触发 chat 级 Hook | `AcpPassthroughExecutor` 不触发 `chat.on_error` 等；取消路径已对齐 Native（`LoopCancelledEvent`）。已知差异，不做增强；详见 [`08 §7.3`](08-permission-authorization-release-notes.md) |
| MCP server 粒度记忆 | 单 server 批准=信任其全部工具（含后续新增）；按工具隔离属后续演进 |
| SessionDirectory kind 维度 | 批准 `write` 后同目录 `delete` 仍免审（体验/安全折中） |
| 接受残留清单 | 6 项（TryAutoApprove 进程级、PathMatches glob、GrantStore 大小写、Gateway scope 恒 Once、ToolDrainTimeout、webfetch DNS rebinding TOCTOU）+ 2 项文档声明（DangerousCommandGuard、Gateway 生命周期）见 [`06`](06-compliance-audit.md)；原「`RetryMiddleware` 缺 `IOException`」已由 `8ae5f3f` 修复并移出 |

---

## 5. 迁移指引

- **工具注册**：`RegisterTool(...)` → `await RegisterToolAsync(...)`；`RegisterTools`/`RegisterToolsFromType` 同步包装已删除。
- **MCP 控制**：`Pause/ResumeServer(s)` → `...Async` 版本（`IMcpController`）。
- **记忆开关**：`MemoryOptions.Enabled` 顶层字段已删除（`a7579b0`）；记忆启停单一真相源为模块启停（`seeing.json` 模块 enabled）。
- **MCP 审批**：MCP 工具首调会以 server 粒度发起审批；如需放行可批准一次（记忆整 server）或用 Agent `AllowedTools` 白名单（`plan`/`explore` 默认兜底）。
- **Once 审批**：如依赖「一次批准整目录免审」的旧行为，需改为 SessionDirectory 及以上 scope。

---

## 6. 相关文档

- [`06 合规审查`](06-compliance-audit.md)：本轮结论、Abstractions 纯函数豁免清单、接受残留清单、防回归扫描命令。
- [`08 权限授权子系统`](08-permission-authorization-release-notes.md)：权限增量变更与边界。
- 根 `AGENTS.md`：Helper 表、HookPoints 位置、装饰器链实际语义已同步校正。
