# 全项目问题修复设计（2026-09-25 架构审查后续）

**日期：** 2026-09-25
**来源：** [全项目架构审查报告](../../../.superpowers/sdd/reviews/2026-09-25-full-project-audit.md)（P0×4 / P1×28 / P2×30+ / P3 若干）
**决策方式：** 7 个批次逐项方案讨论，与维护者逐条确认（2026-09-25 会话）
**原则：** 重构优先于补丁；先止损（安全/泄漏）→ 再纠偏（正确性根因）→ 后清理（死代码/文档）；每批次附带回归测试与守门防回潮。

---

## 0. 审查结论修正记录

讨论过程中经维护者质疑并复验，修正两处审查结论：

| 原结论 | 修正 | 证据 |
|--------|------|------|
| A5：Skills `OnlineParsers/*` 为死代码 | **活代码**——WebUI 在线技能解析/下载功能（skills.sh / modelscope.cn）的依赖 | `SkillsModule.cs:70` 注册 → `SkillCreatePage.razor:13/:508/:539` 注入并调用 |
| MCP OAuth 密钥修复（批次 1 问题 5） | 保留但并入 OAuth 独立特性——按 MCP 规范完整重写后自然覆盖 | OAuth 决策见 §8 |

**流程规则（新增）：所有"死代码"结论在删除前必须做全仓（含 samples）消费者复验。**

---

## 1. 批次 1：安全止损

### 1.1 webfetch SSRF 重定向绕过（P0-1）
- **方案（A 手工跟随+逐跳校验）**：WebModule 改用命名 HttpClient（`AllowAutoRedirect=false`）；工具内循环跟随重定向 ≤5 跳，每跳经 `ValidateUrlSafetyAsync` 校验；Cloudflare 重试逻辑纳入同循环。
- **顺带修复**：`ResponseHeadersRead` + 流式限长读取（chunked 恶意流 OOM 面）；`_approvedHosts` 缓存语义在逐跳校验下复核。
- **位置**：`src/capabilities/Seeing.Agent.Tools.Web/WebFetchTool.cs`、`WebModule.cs`。
- **测试**：重定向到内网 IP 拒绝、重定向到公网放行、超 5 跳拒绝、chunked 超限截断（Web.Tests 补齐）。

### 1.2 单次批准写无 kind 目录白名单（P1-1）
- **方案（A1）**：决策链 7a 增加 `resolution.Scope != PermissionGrantScope.Once` 条件——仅 SessionDirectory 及以上 scope 写入白名单。
- **顺带修复**：步骤 1 边界预检 Allow 分支补 `RequireInteraction` 守卫（spec §5.1 契约要求）。
- **已知接受残留**：SessionDirectory 批准 `write` 后同目录 `delete` 仍免审（体验/安全折中，维护者知情接受；未来可升级 kind 维度方案，与本修复不冲突）。
- **位置**：`src/spine/Seeing.Agent.Core/Core/Permission/PermissionService.cs:250-257`。
- **测试**：Once 批准后同目录第二次访问仍询问（破坏性变更，同步修改锚定测试并记录 release notes）。

### 1.3 ACP 文件桥工作区前缀逃逸（P1-3）
- **方案（A 包内修复）**：`TryResolvePath` 补分隔符边界判定（`fullPath == root || StartsWith(root + SeparatorChar)`）。
- **决策记录**：放弃上提 PathSafety 到 Abstractions——避免契约层纯函数膨胀滑坡（维护者质疑采纳）；Abstractions 现有纯函数（ToolResultFormatting 等 4 处）在 06 文档记录为显式豁免清单。
- **位置**：`src/capabilities/Seeing.Agent.Acp/Filesystem/AcpFileSystemBridge.cs:113-115`。
- **测试**：兄弟目录前缀（`E:\ws\apple` vs root=`E:\ws\app`）拒绝。

### 1.4 MCP 工具零审批（P1-2）
- **方案（A server 粒度资源门）**：
  - MCP 工具以 `mcp.execute` kind、resource=server 名发起资源审批；
  - `PermissionService.IsResourceKind` 增加 `mcp.` 前缀（Allow 不短路）；
  - 审批记忆按 server 粒度（批一次=信任该 server 全部工具）；
  - `plan`/`explore` 的 AllowedTools 白名单继续兜底；
  - 实现路径（spec/plan 阶段定）：Composite `IToolPermissionPolicy`（Mcp 包提供）或工具标记机制。
- **位置**：`DefaultToolPermissionPolicy.cs`、`PermissionService.cs`、Mcp 包工具注册链。
- **测试**：MCP 工具首次调用询问、server 记忆命中后放行、plan agent 白名单兜底。

---

## 2. 批次 2：模块生命周期对称（方向 1 收编 + 守门）

**统一方向**：所有注册类资源（Hook/Agent/Skill/命令/ReloadHandler/MCP 连接）统一收编进所属 Module 的 `Activate/Deactivate`（`ISeeingModule.cs:25` 契约意图）；裸 `IHostedService`/Bootstrap 退役；架构测试守门防回潮。不做框架级 owner 化原语（契约变更面大，暂不需要）。

### 2.1 MCP 集成修复（P0-2 + P1-26）
- `McpModule.ActivateAsync`：初始化连接（`McpClientManager.InitializeAsync`）+ 登记动态工具贡献；`DeactivateAsync`：`ShutdownAsync` + 撤销贡献 + 卸载已注册工具。
- **动态工具结算（新契约）**：Abstractions 定义 `IDynamicToolContributor`（模块 Activate 时登记、工具集运行时可变）；`ExecutionJobService` 结算时把 enabled 模块的动态贡献并入 settledToolIds——修复 MCP 工具被 `settled.Contains` 滤出 schema 的功能回归（`ToolManager.cs:100-102` 已确认）。
- `ComponentManager.LoadAsync/LoadAllAsync` 加模块门控：`IComponentLoader` 增加 `ModuleId` 属性声明归属；Core 侧检查 `IModuleCatalog.IsEnabled`（含 reload 路径）。
- **一并修复**：`McpClientManager.DisposeAsync` 清空字典后空遍历 bug（:959-972）；Pause/Resume 4 处同步阻塞（:173-201）。
- **测试**：模块 disable 后子进程/工具/重连器全清；enable→disable→enable 循环幂等；MCP 工具出现在 settled schema。

### 2.2 SystemOne ReloadHandler 复活（P0-3）
- **方案（α 挂接式）**：`ReloadOrchestrator` 补 `Detach` API；`SystemOneModule.Activate` 登记 handler、`Deactivate` 撤销。
- **位置**：`SystemOneModule.cs:34`、`SystemOneReloadHandler.cs`、`ReloadOrchestrator.cs:330`。

### 2.3 AgentsBuiltIn Deactivate 空操作（P1-27）
- Deactivate 遍历 `BuiltInAgents` 逐个 `IAgentStore.Unregister`。

### 2.4 Memory Hook（P1-29）
- `MemoryBootstrapHostedService` 整体退役：工具注册已在 `MemoryModule.Activate`（去重复兜底）；4 个 Hook 移入 Module Activate/Deactivate（`HookManager.Remove` 已可用）；移除进程级 `MemoryHookRegistrationGate`。
- **移除 `MemoryOptions.Enabled` 第二套开关**：单一真相源为模块启停；`Enabled` 选项语义并入模块开关（文档同步 + 配置迁移说明）。

### 2.5 ACP Agent/命令（P1-30）
- `AcpAgentRegistrationHostedService` 退役 → `AcpModule.Activate/Deactivate` 挂接 `Registrar.RegisterAsync`/`UnregisterAgent`；`AcpReloadHandler` 挂接式（同 2.2 模式）；ACP 命令注册收编（命令注册表若无 Unregister API 则在 Abstractions 补契约）。

### 2.6 Skill/命令注册群（P2）
- `SystemOneSkillRegistrationHostedService`、`SchedulerSkillRegistrationHostedService`（裸）、`SchedulerCommandRegistrationHostedService` 统一收编进各自 Module；`SkillManager` 补 `Unregister` API。
- `_running` 非 volatile 问题随收编消除；Scheduler 配置节注册越界一并治理。

**守门**：架构测试扫描能力包内裸 `IHostedService` 注册资源模式（豁免执行型 `IModuleHostedService`）。

---

## 3. 批次 3：事件流重构（方案 A 最小重构）

- 删除 `ExecutionEventPublisher._channels` 主 channel 死代码（无消费者，事件无界滞留）。
- `Publish` 的 `buffer.Add` 移入 `_lock` 临界区（与订阅者扇出同锁）。
- `SubscribeAsync` 的"注册 + buffer 回放"收进同一 `_lock` 临界区（消除双份推送窗口，恰好一次语义）。
- `GetBufferedEvents`/`ClearBuffer` 与 buffer 操作同锁（CircularBuffer 线程安全性 spec 阶段确认）。
- Router 简化：删除手动 replay（`SessionEventStreamRouter.cs:107-111`）与 skipSet 机制（:123），统一依赖 publisher 回放——消除两层去重耦合（流尾弱化 P3 债根因）。
- **测试**：发布中订阅不丢不重（并发回归）；I1 重启逻辑保持。
- **边界**：`IExecutionEventPublisher` 契约零变更；exec 闪断的执行层根因由批次 4 A 组修复，两者正交。

---

## 4. 批次 4：执行管线正确性

### A 组：取消正确性（per-record CTS）
- `ExecutionRecord` 携带自己的 CTS（`SubmitAsync` 入槽时创建）；`ProcessExecutionAsync` 全程持有该 token，不再读 `queue.CurrentCancellationToken`。
- `CancelAsync` 按 executionId 找 record 的 CTS → `Cancel()`，**不立即 Dispose**（延迟至 record 终态后释放，规避 ODE）；队列推进保留（防卡死），换靶问题因 token 绑定 record 消失。
- `ExecutionJobService.cs:257/:1444` 同步阻塞补异步入口；`SessionWindow.razor:1409` 迁移到 `CancelBySessionAsync`。
- **测试**：窗口期取消 → 执行体完整观察取消、exec2 事件流不被 exec1 污染（闪断债关闭）。

### B 组：装饰器链序（维持现状语义 + 改文档）
- **决策**：保留 `Timeout(Retry(tool))` 语义（全局超时覆盖重试总时长，保守）；修正 `CreateDefault` 错误注释（:129"最后应用，最内层"→实际最外层）；AGENTS.md/04 文档链序描述更新为实际语义；补链序断言测试防未来误改。

### C 组：功能失效修复
- SnapshotStorage：删除 `GetMetadataPathById` 通配符路径；`DeleteSnapshotAsync` 直接构造真实路径；`ListSnapshotsAsync`/`CleanupAsync` 遍历中直接反序列化。
- LoopDetector 接线：`AgentExecutor` 工具执行点以 `(toolId, SHA256 参数哈希)` 记录，per-Loop 状态（挂 A 组状态对象）；3 次警告事件、5 次终止 Loop。

### D 组：并发加固
- X5：`_todoEmptyReminded` 等三字段移入 per-execution 状态类 `LoopExecutionState`（同时为 C 组 LoopDetector 状态挂点）。
- P1-9：ProviderManager 私有 `_sync` 锁包全部写路径（`RefreshConfiguredProviders`/`OnProvidersChanged`/Save/Delete）。
- P1-10：`SaveDisabledSetAsync` 集合快照移入 `_toolStateLock`；`:327` 同步包装删除，`RegisterTools`/`RegisterToolsFromType` async 化。

### E 组：ACP 执行器（①最小修复）
- `AcpPassthroughExecutor:116` ReadAllAsync 的 OCE 包 try/catch → `CancellationToken.None` 排空 → 产出 `LoopCancelledEvent` → 终止（对齐 Native）。
- Hook 面差异（ACP 不触发 chat.on_error）：**文档明示**（08 release notes + 02），不做行为增强。

### F 组：P2 全部纳入
- MergeDeep：数值 0 视为合法覆盖（Nullable 表达"未设置"）；null 分支返回深拷贝（消除用户级快照污染）。
- UnifiedConfigManager：统一"构建新快照后原子替换"；`SaveRawJsonAsync` 复用文件锁；`LoadAsync` 清缓存窗口消除。
- ToolDiscovery：拒绝 `async void`/out/ref/泛型方法；required 语义与 `ReflectedTool.IsRequiredParameter` 对齐。
- Failed 路径补 `IncompleteToolCallMarker.MarkCancelled`；`CleanupIdleSessions` 与 `SubmitAsync` 竞态加防护。

---

## 5. 批次 5：能力包功能修复

### 照单修复
| 组 | 修复 |
|----|------|
| Web | codesearch `parameters`→`params` + 请求体快照测试；webfetch 逐跳 SSRF 预校验（DNS rebinding TOCTOU 未消除，列入 06 接受残留） |
| FileSystem | rg 改 `--json` 输出（修 Windows 盘符解析全灭）+ 绝对路径测试用例；rg 子进程异步 + 超时；ReadTool 流式限长 + 删 dataUrl/base64 死代码 |
| MCP | 工具执行链加 CancellationToken 透传（`McpTool.cs:16` executor 委托签名 + coordinator 绑定）；删除/接线带 ct 死代码路径 |
| LLM | Anthropic 解析 `message_start` usage（修流式 InputTokens=0）；OpenAI `stream_options.include_usage`；Responses API 输入侧映射 `function_call`/`function_call_output`；客户端实现 IDisposable（修 HttpClient/SocketsHttpHandler 泄漏链）+ 构造函数不再篡改共享 HttpClient + response using |
| Memory | `SqliteTokenTracker`/`DailyQuotaManager` 注入并使用 `SqliteConnectionGate` |
| ACP | Terminal 按 sessionId 索引 + Exited/SessionDestroyed 延迟清理 + TTL；Kill 异常补日志；读文件流式限长；清理循环改单 reader |
| Hosting | TaskTool 后台监听改轮询 `WaitForExecutionAsync`（摆脱事件流回放依赖） |

### P2/P3 杂项（全部纳入）
bash 补 `TimeoutSkip` 能力声明；GitModule 改 `IOptionsMonitor`；session_trim 组锁/快照；SkillLoadCommand 修假成功（失败明确报错 + 走 SessionManager API）；SystemOne GetClient/Dispose 竞态加锁；BashTool 取消后事件收紧；GitLogTool maxCount 上限；IsDirectory 上移 FileSystemHelper（消 5 处复制）；GrepTool MaxLineLength 统一；用户正则 `MatchTimeout`（ReDoS 面）；McpTool 异常进 Error 字段对齐统一信封；ModelsDev 分页缓存 + 锁外 IO；Once 任务 misfire 改 `FireNow` 补跑；`IsDirectory` 等静默吞异常群补日志；`DateTime.Now` → `UtcNow` 统一；流式 EOF 补中断标记（FinishReason="aborted"）；OpenAI 流式工具参数 JSON 校验（对齐 Anthropic 回退 `{}`）。

### 文档声明项（归批次 7 落档）
- `DangerousCommandGuard` xargs 令牌化绕过：guard 语义自述"只拦截灾难性操作"——文档明示该边界，不做行为增强。
- Gateway 服务器生命周期与 gateway 模块脱钩：明示"网关为宿主级基础设施"设计意图。

### Skills 包
- **OnlineParsers 保留**（活功能，审查结论已修正）；`Pulling/BuiltinSkillLoader.cs`、`Pulling/SkillPermissionFilter.cs` 删除（全仓复验无消费者，后者挂旧权限体系）。

---

## 6. 批次 6：会话子系统

### P1-14 SessionData 线程安全（A 全快照）
- `Messages` getter 锁内返回快照 List；`Clone()` 消息快照入 `_messagesGate`；`ClearMessages` 补锁。
- spec 阶段扫描 getter 调用方，确认无"引用观察后续变化"依赖。

### P1-15 handoff 回滚（照单）
- `RollbackAsync` 内部改 `CancellationToken.None`；补回归测试（取消失败后组状态一致性）。

### P1-16 会话删除清理链路（A Hook 机制）
- Core 新增"会话清理订阅组件"：注册 `session.destroyed` Hook → `PermissionRequestManager` 收敛该会话在途（Deny Cancellation）+ `PermissionGrantStore.ClearSession`（记忆 + 白名单目录）。
- Memory `SessionEventPublisherAdapter` 对齐同一 Hook 机制。
- 覆盖所有 `SessionManager.Delete` 调用路径（含 WebUI 直调）。

### P2/P3（照单）
- FileSessionStore：备用替换改 Copy-then-delete、catch 不删 source；.tmp finally 清理。
- CreateChild/Fork/BackupFork 失败自清理（对齐 handoff 先例）；EnsureSessionAsync 原子化。
- Clone 的 Context 浅拷贝：**文档明示共享语义**（快照契约文档补声明 + 已知调用方审计）。
- TaskCardAggregator.Dispose 异步化；FailStep 区分 cancelled；DateTime 统一 UtcNow（JSON Kind 保真）；锁字典定期回收；`FindBySessionAsync` TTL 缓存。

---

## 7. 批次 7：守门与文档

### 死代码清理（删前全仓消费者复验）
Core `Tools/BuiltIn/Plan/` 整目录 + `PlanManagerTests` + 死 Helper（BinaryFileDetector/OutputTruncator）+ 空目录；`ChatExecutionQueue`/`ChatRunTracker`（复验后删）；Seeing.Session `Execution/` 目录（ExecutionStateManager/IExecutionState）；`PermissionService.GetPolicyAsync` 硬编码宽松策略；`CachedToolDecorator.ClearCache`；`UnitTest1.cs`；Seeing.Session csproj 死引用 TokenEstimation。

### 防回归扫描补盲（README 命令）
- `rg ": ITool\b|\[Tool\(" src/spine`（Core 零工具实现盲区——P0-4 教训）
- 能力包裸 IHostedService 注册资源模式扫描（批次 2 守门）

### 文档修复
- 根 AGENTS.md：删 `title` Agent；P0 摘要改"已关闭"；Helper 表路径更新；HookPoints 位置更正（Seeing.Session 原语层）。
- 02-modules.md：补 SystemOne/Tools.SystemOne/ModelCatalog×2/ModelCapabilities 共 5 包；§7 `s_fullModules` → `BuiltInCapabilitySets.Full`。
- 08/06 断链处置：权限 spec 文件失落的补救（补最小设计记录或改引 release notes）。
- 06 文档：新增本轮审查结论、Abstractions 纯函数豁免清单（4 处）、装饰器链实际语义（批次 4 决策）。
- **删除三份过时嵌套 AGENTS.md**（`Core/Tools/AGENTS.md`、`Core/Core/AGENTS.md`、`Seeing.Session/AGENTS.md`）——根 AGENTS.md + docs/architecture 为唯一真相源。

### 仓库卫生
- `samples/Seeing.Agent.WebUI/plugins/*.dll` 移除 + .gitignore；tests/ 顶层 16 文件清理（产物入 .gitignore、脚本移 tests/tools/）；spine 内 Gateway 测试移 tests/gateway。

### 测试补齐与守门
- `EventStreamPermissionChannel` 测试（对齐 Gateway 侧形态）；`RetryToolDecorator` 重试/退避/异常白名单直接测试；Web 包（SSRF 逐跳、请求体）；Mcp 包（Coordinator/Registry 基础）。
- full 场景守门反射化：测试改为反射发现全部 `ISeeingModule` 实现 id 与 `Full` 求差；`Full` 清单补 `systemone`/`systemone.tools`。

### 命名空间迁移（A 全量迁移）
- 9 个工具包 50+ 文件命名空间 `Seeing.Agent.Core.Tools.*` → `Seeing.Agent.Tools.*`（IDE 全局重构；工具发现按 Id 不受影响）。

### 显式接受残留清单（落档 06，避免下轮审查重复发现）
- 多通道 `TryAutoApprove` 进程级语义（WebUI+Gateway 同进程为默认示例形态；按来源隔离属后续演进）。
- `PermissionRuleEntry.PathMatches` 相对 glob 对绝对路径失效 + Windows 大小写语义不一致（`*.PEM` vs `key.pem` Deny 绕过面）。
- `PermissionGrantStore.Lookup` 恒 OrdinalIgnoreCase（Linux 大小写敏感文件系统跨文件命中）。
- Gateway 审批回传 scope 恒 Once（协议无法表达 Session/SessionDirectory 记忆）。
- `RetryMiddleware` 可重试集合缺 IOException（与 `RetryToolDecorator` 不一致）。
- ToolDrainTimeout 取消泄漏（AGENTS.md 已知边界）。

### Plan 阶段行动项（审查签字 advisory 采纳）
- §1.2 的"spec §5.1 契约要求"指向 P1-4 断链文件——批次 1 plan 内联 RequireInteraction 契约语义，或批次 7 断链补救先行，保证实施者可追溯。
- X6 同步包装反模式（报告 10+ 处）：plan 阶段以 `rg "GetAwaiter\(\)\.GetResult\(\)"` 生成全量清单逐项分派，防遗漏。

---

## 8. 后置独立特性：MCP OAuth 完整实现

- **范围**：按 MCP 授权规范（OAuth 2.1）重写——元数据发现/配置端点 + 授权码流程（浏览器跳转 + localhost 回调）+ token 交换 + 过期前主动 refresh + 401 触发 refresh 重试 + 存储加密（随机密钥文件模式，原批次 1 问题 5 并入）。
- **集成点**：`HttpWrapperFactory` HTTP 传输注入 Authorization 头；server 配置 client_id/授权端点。
- **时机**：独立特性开发（独立 spec → plan → 实施），不阻塞批次 1-7。
- **批次 5 对 OAuth 的动作**：仅确认现有半成品无意外触发路径（`AddMcpOAuth` 无宿主调用、`example.com` 授权 URL 不会被自动使用），不重写、不删除——`McpOAuthProvider`/`McpOAuthStorage` 保留至特性整体替换，避免同文件两度改动。

---

## 9. 实施顺序与依赖

```
批次 1（安全止损，独立小改动）
  └─ 1.4 MCP 审批依赖批次 2 的 MCP 工具标记 → 实现时序：批次 2 先行或同步
批次 2（生命周期收编）←— 批次 5 的 MCP 取消修复同文件，协调实施
批次 3（事件流）独立
批次 4（执行管线）独立（B 组文档项与批次 7 文档合并处理）
批次 5（能力包）依赖批次 2 的 MCP 变更落位
批次 6（会话）独立
批次 7（守门文档）最后收尾（总结各批次行为变更 + release notes）
```

- 每批次独立 PR/验证（`dotnet build Seeing.Agent.slnx` + `dotnet vstest` 各测试程序集）。
- 行为变更项（1.2 白名单收紧、2.4 Memory Enabled 移除、B 组链序文档化）须在批次 7 统一写 release notes。

## 10. 风险与回滚

| 风险 | 缓解 |
|------|------|
| 1.2 行为变更破坏既有测试 | 锚定测试同步修改属预期；逐项列出改动清单入 PR |
| 批次 2 收编引入启动顺序回归 | 每模块收编配 Activate/Deactivate 对称测试（Invariants 体系） |
| 命名空间迁移遗漏 | IDE 全局重构 + 全量构建守门；迁移后全仓 grep 旧命名空间应为零 |
| full 反射化测试对插件模块误报 | 反射范围限定已加载程序集的 ISeeingModule，plugs 按需豁免 |
| per-record CTS 延迟 Dispose 造成持有延长 | record 终态即释放（CompleteAsync/消费方 finally），单测覆盖 |
