# 全项目问题修复实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.
> **执行模式（维护者指令）：** 子Agent 并行优先；**非每步审查**——仅在 Wave 边界（关键步骤）审查；快速实现。

**Goal:** 落地 [2026-09-25 全项目修复设计 spec](../specs/2026-09-25-full-project-remediation-design.md) 的 7 个批次（P0×4 / P1×28 / P2/P3 全量）。

**Architecture:** 3 个并行波次——Wave 1（互不冲突的独立修复，最大并行）→ Wave 2（模块生命周期收编及其依赖项）→ Wave 3（文档收尾 + 命名空间迁移 + 最终验收）。波间为审查检查点。

**Tech Stack:** .NET 10 / xUnit 2.9 + FluentAssertions；验证命令：`dotnet build Seeing.Agent.slnx` + `dotnet vstest <dll>`（MTP 不可用）。

---

## 并行分组依据（冲突分析）

| 冲突域 | 处置 |
|--------|------|
| `PermissionService.cs` | 批次 1.2 + 死代码 `GetPolicyAsync` 删除 + P1-16 清理组件注册 → **合并进 Task 6** |
| `AgentExecutor.cs` | 批次 4 X5 字段 + C 组 LoopDetector 接线同文件 → **合并进 Task 3b** |
| `ExecutionJobService.cs` | 批次 4 A（CTS）与批次 2 动态结算同文件 → **分波**（Wave 1 / Wave 2） |
| Acp 包（4 个任务触及） | 桥（Wave 1）/ 模块收编（Wave 2）/ Terminal + Executor（Wave 2）分波 |
| 命名空间迁移（50+ 文件全包） | **必须最后**（Wave 3），与一切代码修改互斥 |
| 工具包测试程序集 | 各任务只加自己包的测试，无冲突 |

## 执行规则（派发子Agent 时逐条携带）

1. 每个 Task 一个子Agent，Wave 内任务**同时派发**（单消息多 Task 调用）。
2. 子Agent 必读：spec 对应章节（路径见各任务卡）+ 审查报告对应条目（`.superpowers/sdd/reviews/2026-09-25-full-project-audit.md`）。
3. 每个 Task 完成即 `git add <本任务文件> && git commit`（独立提交，消息见任务卡）；**禁止** 越界修改其他任务文件。
4. 测试失败先自查（读 spec 的方案要点再修），不可为过测弱化断言。
5. **死代码删除前必须全仓消费者复验**（`rg` 含 samples）。
6. 构建验证：`dotnet build Seeing.Agent.slnx`；测试：`dotnet build <test-project>` 后 `dotnet vstest <测试dll> --TestCaseFilter:"FullyQualifiedName~<关键词>"`。
7. Wave 完成后由主会话统一审查（见 §关键审查点），**任务间不互相审查**。

---

## Wave 1（最大并行：8 个任务）

### Task 1: 事件流重构（批次 3）
**Spec:** §3 | **Files:** `src/spine/Seeing.Agent.Core/Execution/ExecutionEventPublisher.cs`、`samples/Seeing.Agent.WebUI/Services/SessionEventStreamRouter.cs`
**方案:** 删 `_channels` 主 channel 死代码；`Publish` 的 `buffer.Add` 移入 `_lock`；`SubscribeAsync` 注册+回放收进同一临界区（恰一次语义）；`GetBufferedEvents`/`ClearBuffer` 同锁；Router 删手动 replay（:107-111）与 skipSet（:123），统一依赖 publisher 回放。
**测试:** 并发发布中订阅——不丢不重（逐事件计数）；I1 重启逻辑既有测试保持绿。
**验证:** build + `tests/spine/Seeing.Agent.Tests`（Events 相关）+ `tests/apps/Seeing.Agent.WebUI.Tests`（Router 测试）。
**提交:** `refactor(events): unify publish/subscribe critical sections, remove dead main channel`

### Task 2: 会话子系统（批次 6）
**Spec:** §6 | **Files:** `src/primitives/Seeing.Session/Core/SessionData.cs`、`Management/SessionManager.cs`、`Storage/FileSessionStore.cs`、`Management/SessionGroupManager.cs`、`src/capabilities/Seeing.Agent.Tools.Session/SessionHandoffTool.cs`、`samples/.../TaskCardAggregator.cs`
**方案:** SessionData 全快照（getter 锁内快照 + Clone/ClearMessages 入锁；spec 阶段扫描 getter 调用方确认无引用观察依赖）；handoff `RollbackAsync` 用 `CancellationToken.None`；P1-16：Core 新建会话清理订阅组件（注册 `session.destroyed` Hook → `PermissionRequestManager` 收敛 + `PermissionGrantStore.ClearSession`）——**本任务只建组件+注册，不动 PermissionService**；FileSessionStore Copy-then-delete + .tmp finally；CreateChild/Fork 失败自清理；EnsureSessionAsync 原子化；P3 群（Dispose 异步化/FailStep cancelled/UtcNow/锁字典回收/TTL 缓存）。
**测试:** Clone/枚举 × 并发 AddMessage；handoff 取消回滚一致性；FileSessionStore 备用失败不丢源；清理组件触发收敛+ClearSession。
**验证:** build + `tests/primitives/Seeing.Session.Tests` + `tests/capabilities/Seeing.Agent.Tools.Session.Tests`。
**提交:** `fix(session): full snapshot thread safety, handoff rollback ct, destroyed cleanup chain`

### Task 3a: 执行取消 per-record CTS（批次 4 A）
**Spec:** §4 A | **Files:** `src/hosting/Seeing.Agent.Hosting/Execution/SessionExecutionQueue.cs`、`ExecutionJobService.cs`、`samples/.../SessionWindow.razor`
**方案:** ExecutionRecord 携带 CTS（SubmitAsync 入槽创建）；ProcessExecutionAsync 全程持 record token，不读 `queue.CurrentCancellationToken`；CancelAsync 按 record 找 CTS → Cancel 不立即 Dispose（终态后释放）；`:257/:1444` 补异步入口；`SessionWindow.razor:1409` 迁移 `CancelBySessionAsync`。
**测试:** 窗口期取消→执行体完整观察取消；exec2 事件不被 exec1 污染；Cancel→Complete 无 ODE。
**验证:** build + `tests/hosting/Seeing.Agent.Hosting.Tests` + `tests/spine/Seeing.Agent.Tests`（Execution 相关）。
**提交:** `fix(execution): bind cancellation token to execution record (exec flash-break root cause)`

### Task 3b: AgentExecutor 状态与 LoopDetector（批次 4 C+D/X5）
**Spec:** §4 C、D | **Files:** `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs`、`Core/Detection/LoopDetector.cs`
**方案:** `_todoEmptyReminded` 等三字段移入 per-execution `LoopExecutionState`；工具执行点接线 LoopDetector（`(toolId, SHA256 参数哈希)`、per-Loop 状态、3 警告事件 / 5 终止 Loop）。注意 LoopDetector 双入口计数问题（A3 P3）。
**测试:** 并发双 Loop 互不踩踏提醒标志；同参数 5 次调用终止 Loop。
**验证:** build + `tests/spine/Seeing.Agent.Tests`（Loop/Detection 相关）。
**提交:** `fix(executor): per-execution loop state + wire loop detector`

### Task 3c: ToolManager/ProviderManager 并发加固（批次 4 D）
**Spec:** §4 D | **Files:** `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs`、`Llm/ProviderManager.cs`
**方案:** SaveDisabledSetAsync 集合快照入 `_toolStateLock`；`:327` 同步包装删除、RegisterTools/FromType async 化（调用方同步迁移）；ProviderManager 私有 `_sync` 锁包全部写路径。
**测试:** 并发 SaveProvider × RefreshConfiguredProviders 不抛；并发注册+禁用无异常。
**验证:** build + `tests/spine/Seeing.Agent.Tests`（Tools/Llm 相关）。
**提交:** `fix(core): concurrency hardening for ToolManager and ProviderManager`

### Task 4: 装饰器链注释 + 配置基础设施（批次 4 B+F）
**Spec:** §4 B、F | **Files:** `src/spine/Seeing.Agent.Core/Decorators/ToolDecoratorRegistry.cs`、`Extensions/ServiceCollectionExtensions.cs`（注释）、`Configuration/MergeDeep.cs`、`Configuration/UnifiedConfigManager.cs`、`Tools/Discovery/ToolDiscovery.cs`、`Hosting/Execution/ExecutionJobService.cs`（仅 IncompleteToolCallMarker 段）
**方案:** **链序语义维持现状**（Timeout 覆盖重试总时长）——修正 `CreateDefault:129` 错误注释、ServiceCollectionExtensions 注释矛盾；补链序断言测试（OutputLimiter 最外、Retry 最内）；MergeDeep 数值 0 合法覆盖 + null 分支深拷贝；UnifiedConfigManager 快照原子替换 + SaveRawJsonAsync 复用文件锁 + LoadAsync 窗口消除；ToolDiscovery 拒绝 async void/out/ref/泛型 + required 对齐；Failed 路径补 IncompleteToolCallMarker；CleanupIdleSessions 竞态防护。**文档（AGENTS.md/04 链序描述）归 Task 14，本任务只改代码注释。**
**注意:** ExecutionJobService 仅允许改 IncompleteToolCallMarker/Cleanup 段（Task 3a 在并行改同文件其他段——若实际冲突，本任务该项挪至 Wave 2 补做）。
**测试:** 链序断言（Decorator 层次验证）；MergeDeep 数值 0/null 隔离；ToolDiscovery 拒绝非法签名。
**验证:** build + `tests/spine/Seeing.Agent.Tests`（Decorators/Configuration）。
**提交:** `fix(core): decorator chain docs, MergeDeep/UnifiedConfigManager semantics, ToolDiscovery validation`

### Task 5: SSRF + ACP 前缀（批次 1.1 + 1.3）
**Spec:** §1.1、§1.3 | **Files:** `src/capabilities/Seeing.Agent.Tools.Web/WebFetchTool.cs`、`WebModule.cs`、`src/capabilities/Seeing.Agent.Acp/Filesystem/AcpFileSystemBridge.cs`
**方案:** WebModule 命名 client（`AllowAutoRedirect=false`）；工具内 ≤5 跳逐跳 `ValidateUrlSafetyAsync`（Cloudflare 重试同循环）；`ResponseHeadersRead` + 流式限长；DNS rebinding 连接期 IP 固化（按解析 IP 直连 + Host 头，或 ConnectCallback）；ACP 桥分隔符边界（`fullPath == root || StartsWith(root + SeparatorChar)`）。
**测试:** 302→内网 IP 拒绝；302→公网放行；>5 跳拒绝；chunked 超限截断；`E:\ws\apple` vs `E:\ws\app` 拒绝。
**验证:** build + 新建 `tests/capabilities/Seeing.Agent.Tools.Web.Tests` 测试类 + `tests/capabilities/Seeing.Agent.Acp.Tests`。
**提交:** `fix(security): SSRF per-hop validation, ACP workspace prefix boundary`

### Task 6: 权限白名单收紧 + 权限死代码（批次 1.2 + 批次 7 权限项）
**Spec:** §1.2 + §7 死代码清单 | **Files:** `src/spine/Seeing.Agent.Core/Core/Permission/PermissionService.cs`（7a 条件 + RequireInteraction 守卫 + 删 `GetPolicyAsync`/`MergePolicies` 死代码）、`tests/spine/Seeing.Agent.Tests/Permission/`
**方案:** 7a 补 `resolution.Scope != PermissionGrantScope.Once`；步骤 1 gate Allow 分支补 `!normalized.RequireInteraction`；删除 `GetPolicyAsync` 硬编码宽松策略（死代码，误用即安全洞）与 `MergePolicies`。
**测试:** Once 批准后同目录再询问（**同步修改锚定测试**）；RequireInteraction+白名单不被静默放行。
**验证:** build + `dotnet vstest .../Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Permission"`。
**提交:** `fix(permission): Once scope no longer writes session whitelist; require-interaction guard`

### Task 7: 死代码清理 + 仓库卫生（批次 7 部分）
**Spec:** §7 死代码/仓库卫生 | **Files:** 删 `src/spine/Seeing.Agent.Core/Tools/BuiltIn/Plan/`、死 Helper（BinaryFileDetector/OutputTruncator）、空目录（Shell/Web/Time）、`tests/.../Plan/PlanManagerTests.cs`、`Hosting/Internal/ChatExecutionQueue.cs`+`ChatRunTracker.cs`（ChatOrchestrator 注入清理）、`Seeing.Session/Execution/`（ExecutionStateManager/IExecutionState，**删前 rg 复验**）、`CachedToolDecorator.ClearCache`、`tests/apps/.../UnitTest1.cs`、Seeing.Session csproj 死引用；`git rm samples/Seeing.Agent.WebUI/plugins/*.dll` + .gitignore；tests/ 顶层产物清理 + .gitignore + 脚本移 `tests/tools/`
**方案:** 纯删除类；每项删除前 `rg "<TypeName>" src samples tests` 确认零消费者（测试自身除外）。
**验证:** build 全绿（删除后无悬空引用）。
**提交:** `chore: remove dead code (Plan tools, ExecutionStateManager, ChatExecutionQueue) and repo hygiene`

### Task 8: 独立能力包修复（批次 5 无依赖项）
**Spec:** §5 照单修复表 LLM/Memory/FileSystem/Hosting 行 + P2/P3 杂项 | **Files:** `Llm.Anthropic/Clients/AnthropicClient.cs`、`Llm.OpenAI/Clients/OpenAiChatClient.cs`+`OpenAiResponsesClient.cs`+工厂、`Memory/Core/CostControl/*`、`Tools.FileSystem/RipgrepSearch.cs`+`ReadTool.cs`、`Hosting/Tools/TaskTool.cs`、`Tools.Basic/BashTool.cs`、`Tools.Git/GitModule.cs`、`Tools.SystemOne`/`Scheduler` P2 项、`Skills/Commands/SkillCommands.cs`
**方案:** 按 spec §5 逐项：message_start usage / include_usage / function_call 映射 / IDisposable 链 / SqliteConnectionGate 注入 / rg --json + 异步超时 / TaskTool 轮询 / bash TimeoutSkip / GitModule IOptionsMonitor / session_trim 锁 / SkillLoadCommand 真实报错 / SystemOne GetClient 锁 / ModelsDev 缓存 / Once FireNow / P3 杂项（UtcNow 统一、MatchTimeout、异常日志群、IsDirectory 上移、常量统一）。Skills 死代码（BuiltinSkillLoader/SkillPermissionFilter）删除（**rg 复验**）。
**测试:** Anthropic 流式 usage 断言；OpenAI 请求体含 stream_options；Responses 二轮工具调用往返；CostControl 经 gate；rg 绝对路径解析（**Windows 盘符形态**）；TaskTool 轮询路径。
**验证:** build + 各包测试（Llm.Anthropic/OpenAI/Memory/Tools.FileSystem/Tools.Basic/Tools.Git/Skills）。
**提交:** 按包分组提交：`fix(llm): ...`、`fix(memory): ...`、`fix(filesystem): ripgrep json parsing`、`fix(tools): assorted P2/P3`

---

## ⛔ Wave 1 审查检查点（关键步骤）

主会话执行（不派子Agent）：
- [ ] `dotnet build Seeing.Agent.slnx` 全绿
- [ ] 全量测试（各测试 dll vstest）
- [ ] 行为变更抽查：白名单 Once 收紧、链序注释一致性、事件恰一次
- [ ] `git log --oneline` 提交粒度合理、无越界文件
- [ ] 更新本计划 checkbox

---

## Wave 2（批次 2 生命周期 + 依赖项：5 个任务）

### Task 9: MCP 集成修复（批次 2.1）
**Spec:** §2.1 | **Files:** `src/abstractions/.../Modules/` 或 `Tools/`（新增 `IDynamicToolContributor` 契约）、`src/capabilities/Seeing.Agent.Mcp/McpModule.cs`、`McpClientManager.cs`（DisposeAsync/Pause 修复）、`Management/McpToolRegistry.cs`、`src/spine/Seeing.Agent.Core/Core/ComponentManager.cs`、`Abstractions/Components/IComponentLoader.cs`（加 ModuleId）、`Hosting/Execution/ExecutionJobService.cs`（结算合并）
**方案:** McpModule.Activate 挂 Initialize + 登记动态贡献 / Deactivate 挂 Shutdown + 卸载工具；IDynamicToolContributor 契约 + ExecutionJobService 结算合并（settledToolIds ∪ enabled 模块动态贡献）；IComponentLoader.ModuleId + ComponentManager 双方法门控（含 reload 路径）；DisposeAsync 空遍历修复；Pause/Resume 4 处异步化。
**测试:** disable mcp 后子进程/工具/重连器清空；enable→disable→enable 幂等；MCP 工具出现在 settled schema；ComponentManager 在模块禁用时跳过加载。
**验证:** build + `tests/capabilities/Seeing.Agent.Mcp.Tests`（补 Coordinator/Registry 基础测试）+ Invariants。
**提交:** `fix(mcp): module-scoped lifecycle, dynamic tool settlement, component gating`

### Task 10: SystemOne/AgentsBuiltIn/Memory 收编（批次 2.2-2.4）
**Spec:** §2.2-2.4 | **Files:** `SystemOne/SystemOneModule.cs`、`SystemOneReloadHandler.cs`、`Core/Configuration/ReloadOrchestrator.cs`（补 Detach）、`Agents.BuiltIn/AgentsBuiltInModule.cs`、`Memory/Integration/Hosting/MemoryBootstrapHostedService.cs`（退役）、`MemoryModule.cs`、`ChatMemoryHandler.cs`（移除 Enabled 检查）
**方案:** ReloadOrchestrator 补 Attach/Detach；SystemOneModule Activate 登记/Deactivate 撤销 handler；AgentsBuiltIn Deactivate 逐个 Unregister；MemoryBootstrap 退役（Hook 移入 Module Activate/Deactivate、删进程级 gate、删 Enabled 第二套开关）。
**测试:** systemone 停用后热重载不复活 provider；agents.builtin 停用后内置 Agent 不可选；memory 停用后 Hook 不再写入。
**验证:** build + `tests/capabilities/Seeing.Agent.SystemOne.Tests`、`Seeing.Agent.Agents.BuiltIn.Tests`、`Seeing.Agent.Memory.Tests`。
**提交:** `refactor(modules): systemone/agents-builtin/memory lifecycle symmetry`

### Task 11: ACP + Skill/命令注册群收编（批次 2.5-2.6）
**Spec:** §2.5-2.6 | **Files:** `Acp/Extensions/AcpAgentRegistrationHostedService.cs`（退役）、`AcpModule.cs`、`Hosting/AcpReloadHandler.cs`、`AcpServiceCollectionExtensions.cs`（命令）、`Tools.SystemOne/Skills/SystemOneSkillRegistrationHostedService.cs`（退役）、`Scheduler/Skills/SchedulerSkillRegistrationHostedService.cs`（退役）、`Scheduler/.../SchedulerCommandRegistrationHostedService.cs`（退役）、各 Module、`Skills/SkillManager.cs`（补 Unregister）、Abstractions 命令注册契约（若无 Unregister）
**方案:** 全部注册逻辑移入所属 Module Activate/Deactivate；AcpReloadHandler 挂接式；命令注册表补注销；SkillManager.Unregister API。
**测试:** acp 停用后 agent 注销 + 配置重载不复活；scheduler 停用后 skill/命令不可见。
**验证:** build + `tests/capabilities/Seeing.Agent.Acp.Tests`、`Seeing.Agent.Scheduler.Tests`、`Seeing.Agent.Tools.SystemOne.Tests`。
**提交:** `refactor(modules): acp/scheduler/skill registration consolidated into module lifecycle`

### Task 12: MCP 工具审批（批次 1.4，依赖 Task 9）
**Spec:** §1.4 | **Files:** `Core/Permission/PermissionService.cs`（IsResourceKind 加 `mcp.`）、`DefaultToolPermissionPolicy.cs` 或 Mcp 包 Composite policy、Mcp 工具注册链
**方案:** MCP 工具以 `mcp.execute` kind、resource=server 名发起审批；server 粒度记忆；plan/explore AllowedTools 兜底验证。
**测试:** MCP 工具首调询问；server 记忆命中放行；build agent 的 `Allow(Tool,"*")` 不再短路 MCP。
**验证:** build + Permission 测试 + Mcp 测试。
**提交:** `feat(permission): server-scoped MCP tool authorization gate`

### Task 13: ACP 执行器 + Terminal（批次 4E + 批次 5 ACP 行）
**Spec:** §4 E、§5 ACP 行 | **Files:** `Acp/Execution/AcpPassthroughExecutor.cs`、`Acp/Terminal/AcpTerminalBridge.cs`、`AcpSessionLifecycleHook.cs`、`AcpConnectionIdleCleanupHostedService.cs`、`Acp/McpTool 取消`（McpTool.cs executor 签名 + coordinator——与 Task 9 同包不同文件，Task 9 完成后执行）
**方案:** ReadAllAsync OCE catch → 排空 → LoopCancelledEvent；Terminal sessionId 索引 + 延迟清理 + TTL；Kill 日志；读文件流式限长；清理循环单 reader；MCP 工具 ct 透传。
**测试:** ACP 取消产出 LoopCancelledEvent；Terminal 退出进程句柄回收。
**验证:** build + `tests/capabilities/Seeing.Agent.Acp.Tests`。
**提交:** `fix(acp): cancellation event, terminal lifecycle, mcp tool ct passthrough`

---

## ⛔ Wave 2 审查检查点（生命周期专项）

- [ ] build + 全量测试
- [ ] 架构守门：新增裸 IHostedService 扫描（能力包内应为零——Bootstrap 全退役）
- [ ] Invariants：Activate/Deactivate 对称测试全绿
- [ ] 行为变更：Memory.Enabled 移除的配置迁移说明已记录（归 Task 14 release notes）
- [ ] MCP 端到端：settled schema 含 MCP 工具 + 首调审批

---

## Wave 3（收尾：3 个任务，串行）

### Task 14: 文档全量修复（批次 7 文档）
**Spec:** §7 文档修复 + 显式接受残留清单 + release notes | **Files:** 根 `AGENTS.md`、`docs/architecture/02/04/06/08`、删三份嵌套 AGENTS.md、README 扫描命令
**方案:** 按 spec §7 逐项（title 删除、P0 摘要、02 补 5 包、断链补救、06 审查结论 + 豁免清单 + 链序语义、接受残留清单、删除嵌套 AGENTS.md、扫描命令补盲）；**统一 release notes**（行为变更：白名单 Once 收紧 / Memory.Enabled 移除 / MCP 审批 / 链序文档化）。
**验证:** `rg` 验证扫描命令全绿；文档内链批量检查。
**提交:** `docs: architecture drift fixes, accepted-residuals registry, release notes`

### Task 15: 命名空间迁移（批次 7）
**Spec:** §7 命名空间迁移 | **Files:** 9 个工具包 50+ 文件
**方案:** IDE 全局重构 `Seeing.Agent.Core.Tools.*` → `Seeing.Agent.Tools.*`；迁移后全仓 `rg "^namespace Seeing\.Agent\.Core\.Tools"` 应为零；csproj RootNamespace 已正确无需改。
**验证:** build 全绿 + rg 零残留 + 工具 schema 冒烟（ToolManager 注册不受影响）。
**提交:** `refactor(tools): migrate namespaces to Seeing.Agent.Tools.*`

### Task 16: 守门测试 + 最终验收
**Spec:** §7 测试补齐 | **Files:** `tests/.../BuiltInScenariosTests.cs`（反射化）、`tests/spine/Seeing.Agent.Invariants.Tests/`（裸 IHostedService 扫描）、新增 EventStreamPermissionChannel/RetryToolDecorator/Web 包测试
**方案:** full 场景反射化（反射发现 ISeeingModule id 与 Full 求差）+ Full 补 systemone；Invariants 增架构扫描测试；三组测试补齐。
**最终验收（主会话）:**
- [ ] `dotnet build Seeing.Agent.slnx` 全绿
- [ ] 全部测试程序集 vstest 全绿
- [ ] README 全部防回归扫描（含新增两条）全绿
- [ ] `git log --oneline` 对照计划任务卡齐全
**提交:** `test: reflective full-scenario gate, hosted-service architecture invariant, test gaps`

---

## 风险与回退

| 风险 | 缓解 |
|------|------|
| Wave 1 并行任务意外越界改同文件 | 任务卡明确"禁止越界"；审查点 `git log --stat` 核对 |
| Task 3a/4 同文件（ExecutionJobService）分段冲突 | 已限定各改各段；冲突则 Task 4 的 Marker 项挪 Wave 2 |
| 锚定测试破坏（Task 6 白名单收紧） | spec 已声明破坏性变更，同步修改属预期 |
| 命名空间迁移遗漏 | rg 零残留验证 + 全量构建 |
