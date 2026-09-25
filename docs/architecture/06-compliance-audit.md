# 06 合规审查

**审查日期：** 2026-09-08（残留清理后复扫）；2026-09-17（权限授权子系统复扫，见下文专节）；2026-09-18（会话工具包复扫，见下文专节）；2026-09-25（全项目架构审查复扫，见下文专节）  
**对照：** `docs/superpowers/specs/2026-09-08-modular-architecture-design.md` + 本目录 01–05  
**先前签字：** `.superpowers/sdd/reviews/compliance-remediation-final-r3.md` → Ready to merge: Yes  
**深化说明：** `docs/superpowers/specs/2026-09-08-architecture-docs-deepen-design.md`

本文反映**当前工作树**扫描结果，供后续开发对照，防止回潮。

---

## 总评

| 维度 | 状态 | 证据摘要 |
|------|------|----------|
| Core 零能力包引用 / AddSeeingCore 零工具 | ✅ | 能力包 csproj 无 Core；无 `AddSeeingAgent`；Core 内无 `[Tool(` 实现（死代码 `Tools/BuiltIn/Plan/` 已于 2026-09-25 删除，`rg ": ITool\b"` 仅剩装饰器/反射基础设施） |
| 能力包无 Core ProjectReference | ✅ | `rg Core.csproj` **仅** `src/hosting/*`（6 个 Shape/Hosting） |
| 工具 Activate 挂载 | ✅ | `*Module*` 无 `AddSingleton<ITool>` |
| Hosting ↛ Skills / ACP | ✅ | hosting csproj 无 Skills/Acp |
| Gateway ↛ Core / Hosting | ✅ | gateway 下无 Core/Hosting ProjectReference |
| Hosting.Gateway ↛ Agent.Gateway | ✅ | hosting 下无 Agent.Gateway.csproj |
| HostedService 复活 | ✅ | `IModuleHostedService` + Acp/Memory/Scheduler 实现 |
| 连接自管 | ✅ | Acp/Memory Owner；Memory 消费者经 `RequireOpen` 每次取连接 |
| Blazor 全贡献路由 | ✅ | 壳页无 `@page`；ModuleRouter |
| Embed Host Shape | ✅ | `src/hosting/...Embed` + `samples/...Embed.Demo` |
| Core 命名空间 `Seeing.Agent.Core.*` | ✅ | spine 内已迁 |
| 磁盘分类目录 | ✅ | `src/` 顶层仅六类；`tests/` 含镜像 + apps/plugs |
| 能力包互硬引用（Skills/Mcp） | ✅ | Scheduler/Acp 等无 Skills/Mcp ProjectReference |
| schema 单点（执行路径） | ✅ | Native `AgentExecutor` 只读 `context.ToolSchemas`；计算在 `ExecutionJobService` |
| 无阻塞式 GetToolSchemas 同步包装 | ✅ | 已删除 `Task.Run().GetResult()` 同步 API |
| CapabilitySet / Boot 契约落点 | ✅（目标态） | `CapabilitySetDefinition` / `ICapabilitySetCatalog` **仅** Abstractions + Core；能力包不得自实现目录或引用 Core 结算类型；会话滤镜不外键绑 CapabilitySet |

**结论：** **架构方向性债已关闭，本轮无需为合规做强制重构。**  
后续风险主要在**回潮**（平铺新项目、ConfigureServices 挂 ITool、能力包再引 Core、配置旁路、用 Scenario/`Modules.Enabled` 当启动天花板）。用 [README 扫描命令](README.md) 守门。

---

## 可接受残留 / 技术债（非阻断）

| 项 | 说明 | 建议 |
|----|------|------|
| Hosting 仍引用 Tools.Support | Task/Todo 继承 `ToolBase`；Support 仅 Abstractions，无具体 Tools.* | **保持**：勿上提实现到 Abstractions，勿再引 Tools.FileSystem 等 |
| `GetToolSchemasAsync()` 无参 / ForMode / ForAgent | 诊断与测试枚举注册表；**不得**写回 ChatRequest | 执行路径只用 settled ids 重载 |

### 本轮已清理（2026-09-08）

| 项 | 处理 |
|----|------|
| Memory 构造时 `EnsureInstance` 捕获连接 | 消费者经 `SqliteConnectionSource` → `RequireOpen()`；DI 注入 Owner |
| Quota/TokenTracker `Dispose` 共享连接 | 已移除错误 Dispose |
| `GetToolSchemas*` 同步 + `Task.Run().GetResult()` | 已删除；测试改为 async |

---

## 本次复扫命令与结果

```text
rg "Seeing\.Agent\.Core\.csproj" src -g "*.csproj"
→ 仅 hosting/Hosting{,.Web,.Headless,.Embed,.Gateway,.Tui}

rg "AddSingleton<\s*ITool" src -g "*Module*.cs"
→ 空

rg "Seeing\.Agent\.(Skills|Mcp)\.csproj" src -g "*.csproj"
→ 空

rg "Seeing\.Agent\.Gateway\.csproj" src/hosting -g "*.csproj"
→ 空

rg "Seeing\.Agent\.(Core|Hosting)\.csproj" src/gateway -g "*.csproj"
→ 空

rg "AddSeeingAgent\b" src samples -g "*.cs"
→ 空

src/ 顶层目录
→ primitives, abstractions, spine, hosting, capabilities, gateway

GetToolSchemas 调用（执行路径）
→ ExecutionJobService 计算；AgentExecutor 读 context.ToolSchemas
→ ToolsCommands 诊断查询 ToolManager（可接受）
```

---

## 权限授权子系统复扫（2026-09-17）

**背景：** `feature/permission-authorization-refactor` 全量重构（原设计规格文件已失落；行为契约以 [`08 权限授权子系统 Release Notes`](08-permission-authorization-release-notes.md) 为准）。

| 维度 | 状态 | 证据 |
|------|------|------|
| 旧权限类型归零 | ✅ | `SerializingPermissionChannel` / `DynamicPermissionChannel` / `DefaultPermissionChannel` / `IPermissionMemory` / `SessionPermissionMemory` / `IWorkspaceWhitelist` / `SessionWorkspaceWhitelist` / `IPermissionCache` / `PermissionCacheKey` / `PermissionMiddleware` / `IPermissionEventSink` / `PermissionChannelResult` / `BlazorPermissionChannel` / `PermissionRunContext` / `SetRunContext` / `PermissionResponseEvent` 在 `src samples tests` 无**代码**命中 |
| 旧通道残留文本 | ⚠️ 非阻断 | `Abstractions/Permissions/IPermissionGrantStore.cs:13` 注释历史提及 `IWorkspaceWhitelist`（允许的历史说明）；`samples/Seeing.Agent.WebUI/plugins/*.dll` 为已提交的旧构建产物（二进制内嵌旧符号，非代码引用，建议重建/移除） |
| 依赖方向 | ✅ | 能力包无 Core ProjectReference（仅 `src/hosting/*`）；`src/gateway` 无 Core/Hosting ProjectReference |
| 权限契约分层 | ✅ | 新契约集中在 `Abstractions/Permissions`（零实现）；实现落 Core / Hosting.Web / Gateway；UI 投影在 sample |
| DI 环修复 | ✅ | `EffectivePermissionPolicy` 单点解析生效开关、`PermissionRequestManager` 不依赖 `IPermissionService`；Core-only 解析 Manager 由 `NullExecutionEventPublisher` 兜底，WebUI 宿主 2.9s 内启动 |
| 单一真相源 | ✅ | 在途 → `IPermissionRequestManager`；记忆/白名单 → `IPermissionGrantStore`；可交互性 → `IPermissionPresentationStore`；审批事件 → `IExecutionEventPublisher` |
| seam | ✅ | `SettlementEngine.ExclusiveSeams` 仅 `executionWorld`；`"permissionChannel"` seam 已移除 |

复扫命令（`rg` 不可用时用 `Select-String` 等价实现，排除 `obj/bin`）见 [README 防回归扫描](README.md) 的权限契约分层规则。

---

## 会话工具包复扫（2026-09-18）

**背景：** 新增能力包 `Seeing.Agent.Tools.Session`（模块 `session.tools`，分支 `feature/session-tools-and-groups`）。

| 维度 | 状态 | 证据 |
|------|------|------|
| 能力包无 Core ProjectReference | ✅ | csproj 仅引用 Abstractions + `Seeing.Session`（原语）+ `Tools.Support` |
| 工具 Activate 挂载 | ✅ | `SessionToolsModule.ConfigureServices` 无 `AddSingleton<ITool>`；Activate/Deactivate 成对 |
| full/code 能力集与测试同步 | ✅ | `BuiltInCapabilitySets` + `BuiltInScenariosTests` 含 `session.tools` |

复扫命令：

```text
rg "Seeing\.Agent\.Core\.csproj" src/capabilities/Seeing.Agent.Tools.Session -g "*.csproj"
→ 空

rg "AddSingleton<\s*ITool" src/capabilities/Seeing.Agent.Tools.Session -g "*Module*.cs"
→ 空
```

---

## 全项目审查复扫（2026-09-25）

**背景：** 9 域并行只读审查（报告 `.superpowers/sdd/reviews/2026-09-25-full-project-audit.md`，P0×4 / P1×28 / P2×30+），修复设计 `docs/superpowers/specs/2026-09-25-full-project-remediation-design.md`；整改见 [`11 全项目整改 Release Notes`](11-remediation-release-notes.md)。本轮完成批次 1–7（安全止损、生命周期对称、事件流、执行管线、能力包、会话、守门文档）。

| 维度 | 状态 | 证据 |
|------|------|------|
| Core 零工具实现 | ✅ | `Tools/BuiltIn/Plan/` 死代码删除；`rg "\[Tool\(" src/spine` 为空 |
| 能力包无 Core ProjectReference | ✅ | 仅 `src/hosting/*` 命中（维持 2026-09-08 结论） |
| 模块生命周期对称 | ✅ | MCP/SystemOne/Memory/Agents/ACP/Scheduler/Skill 注册收编进 `Activate/Deactivate`；Bootstrap 退役 |
| 事件流恰一次 | ✅ | `ExecutionEventPublisher` 发布/订阅同锁，Router 移除 skipSet 手动回放 |
| 取消正确性 | ✅ | CTS 绑定 `ExecutionRecord`（per-execution），窗口期取消不再偷换 |
| 装饰器链序 | ✅ | 实际 `OutputLimiter(Cached(Timeout(Retry(tool))))`（补齐链序断言测试） |
| schema 单点 | ✅ | MCP 动态工具经 `IDynamicToolContributor` 并入 settledToolIds |
| full 场景守门 | ✅ 完成 | `Full` 清单补 `systemone`/`systemone.tools`；反射守门（`BuiltInScenariosTests` 反射发现全部 `ISeeingModule` 求差）+ `FullExcludedModules` 显式豁免在线源 `llm.modelcatalog.modelsdev`（`2c934db` + `ead35e3`） |

### 2026-09-25 整改覆盖矩阵（P0/P1 逐条落档）

> 审查报告去重计 **P0×4 / P1×28**；交叉印证项折叠为：X1→P1-12、X2→P1-13、X3→P0-2、X5→并发（P1 组）、X7→P1-19。下表按报告枚举编号逐条落档（P0-1..4、P1-1..31 + X5/X6），**全部已闭环**；提交为 `master` 上对应修复。

| 报告项 | 状态 | 提交 | 备注 |
|--------|------|------|------|
| P0-1 webfetch 重定向 SSRF | ✅ | `c5898fc` | 禁用自动重定向，逐跳校验 + 流式限长 |
| P0-2 MCP 模块生命周期脱钩 | ✅ | `eb54f90` | Activate/Deactivate 对称；`LoadAllAsync` 加模块门控 |
| P0-3 SystemOne 停用后复活 | ✅ | `179d351` | ReloadHandler 随模块激活挂接 |
| P0-4 Core 残留工具死代码 | ✅ | `49014dc` | 删除 `Tools/BuiltIn/Plan/` + 扫描命令补盲区 |
| P1-1 单次批准写目录白名单 | ✅ | `2168eaf` | 仅 `Scope != Once` 写白名单 |
| P1-2 MCP 工具零审批 | ✅ | `212518f`+`8515cfb` | server 粒度授权门；`mcp.execute`→`McpTool` 映射、Authorizer 透传 |
| P1-3 ACP 工作区前缀逃逸 | ✅ | `c5898fc` | `root + SeparatorChar` 边界 |
| P1-4 权限文档断链 | ✅ | `21810b3` | 08/06 显式标注规格失落，契约以 08 为准 |
| P1-5 取消令牌槽位偷换 | ✅ | `8f80fff`+`82baaf5` | CTS 绑定 `ExecutionRecord`；窗口重复终态/延迟释放 |
| P1-6 装饰器链序与注释相反 | ✅ | `867f1bf` | 保留 `Timeout(Retry)` 语义，修正相反注释（链序未变） |
| P1-7 SnapshotStorage 通配符路径 | ✅ | `995828d` | 删除零消费者死代码（后补） |
| P1-8 LoopDetector 未接线 | ✅ | `6b6644c`+`5ba2d54` | per-execution 状态接线（3 警告 5 终止）；空参数不抛异常 |
| P1-9 ProviderManager Dictionary 竞态 | ✅ | `03fb960`+`599716b` | 快照入锁；注册移出锁避免事件重入 |
| P1-10 ToolManager 同步阻塞 + 锁外遍历 | ✅ | `03fb960` | `RegisterToolAsync`；快照入锁 |
| P1-11 ACP 取消事件不可达 | ✅ | `751cd50`+`a500846` | OCE 排空对齐 Native（`LoopCancelledEvent`）；排空有界化 |
| P1-12 X1 主 channel 无消费者 | ✅ | `1b495a5`+`e766dc7` | 移除无消费者主通道；CompleteSession 清缓冲 |
| P1-13 X2 订阅/回放竞态 | ✅ | `1b495a5` | 发布/订阅同锁（恰一次） |
| P1-14 SessionData 半线程安全 | ✅ | `4451b53` | `Messages`/`Clone`/`ClearMessages` 入锁全快照 |
| P1-15 handoff 回滚复用已取消 ct | ✅ | `4451b53` | 回滚用 `CancellationToken.None` |
| P1-16 Destroyed 事件未发布 | ✅ | `4451b53` | `session.destroyed` → 收敛在途 + `ClearSession` |
| P1-17 Windows grep 匹配丢弃 | ✅ | `19140ee` | ripgrep `--json` 解析 + 异步超时 |
| P1-18 codesearch 参数名错误 | ✅ | `7ade54e` | `parameters`→`params`（后补） |
| P1-19 MCP 工具执行不可取消 | ✅ | `751cd50` | executor 委托接入 `CancellationToken` |
| P1-20 Anthropic 流式 input_tokens=0 | ✅ | `38f17ea` | 解析 `message_start` usage |
| P1-21 OpenAI 流式缺 include_usage | ✅ | `38f17ea` | 声明 `stream_options.include_usage` |
| P1-22 Responses 多轮工具调用损坏 | ✅ | `38f17ea` | 输入侧补 `function_call`/`function_call_output` |
| P1-23 LLM 客户端泄漏链 | ✅ | `38f17ea` | `IDisposable` 释放链 |
| P1-24 Memory CostControl 绕过 gate | ✅ | `c544804` | 经 `SqliteConnectionGate` |
| P1-25 ACP Terminal 句柄泄漏 | ✅ | `751cd50`+`c44ff3e` | 终端级清理；读取限制负值钳制 |
| P1-26 MCP 动态工具 schema 断裂 | ✅ | `eb54f90` | `IDynamicToolContributor` 并入 settled ids |
| P1-27 AgentsBuiltIn Deactivate 空操作 | ✅ | `179d351`+`fe86f1d` | 按来源注销，不误删用户同名 |
| P1-28 full 场景守门失守 | ✅ | `2c934db`+`ead35e3` | 反射守门 + `FullExcludedModules`（modelsdev 显式豁免） |
| P1-29 Memory Bootstrap Hook 不注销 | ✅ | `179d351`+`a7579b0` | Hook 随模块 Activate/Deactivate；删除顶层 `Enabled` |
| P1-30 ACP passthrough 注册脱钩 | ✅ | `e2eb171`+`2c934db` | 注册/命令随模块生命周期；ACP Hook 收编 |
| P1-31 Hosting.Web 权限通道零测试 | ✅ | `2c934db` | 补 `EventStreamPermissionChannelTests` |
| X5 AgentExecutor 实例字段竞态 | ✅ | `6b6644c` | per-execution 循环状态 |
| X6 同步包装 async 残留 | ✅ | `4a12e5e` | 消除系统性 `Task.Run().GetResult()`/裸阻断 |

**矩阵生成后补记（审查 B / 后续修复）：** 并发 I1–I5 → `03fb960`（ToolManager/ProviderManager 快照入锁、注册异步化）、`82baaf5`（取消/启动窗口重复终态、队列丢项、CTS 延迟释放）、`599716b`（ProviderManager 注册移出锁避免事件重入）；MergeDeep 数值 0 分层语义 → `5ba2d54`（撤销「数值 0 始终覆盖」，恢复 null 分支深拷贝）；MCP 审批安全 → `8515cfb`；ACP/Agents 生命周期 → `179d351`、`e2eb171`、`fe86f1d`、`a500846`、`2c934db`；`RetryMiddleware` 补 `IOException` + 指数退避 + 退避响应取消 → `8ae5f3f`（已从接受残留清单移出）；`RetryToolDecorator` 重试次数语义（审查 B I6）→ 根 `AGENTS.md`「工具装饰器链」已校正（`maxRetries=3` 为总尝试次数含首次）。

### Abstractions 纯函数豁免清单（显式）

`Abstractions` 允许保留的 4 处无状态纯函数/静态解析（不属「零实现纪律」违规；「上提 PathSafety 到 Abstractions」已决策放弃，避免契约层纯函数膨胀）：

| 类型 | 位置 | 说明 |
|------|------|------|
| `ToolResultFormatting` | `Abstractions/Tools/ToolResultFormatting.cs` | 统一工具结果回传格式化（纯函数） |
| `ModelCapabilityFillEmpty` | `Abstractions/Llm/ModelCapabilities/ModelCapabilityFillEmpty.cs` | 能力缺省填充（纯函数） |
| `SystemOneClientFactoryResolver` | `Abstractions/SystemOne/SystemOneClientFactoryResolver.cs` | 工厂解析（无状态） |
| `LlmClientFactoryResolver` | `Abstractions/Llm/LlmClientFactoryResolver.cs` | 工厂解析（无状态） |

> `PermissionRuleEntry` 匹配算法（~140 行）与 `ConfigSectionOptionsMonitor` 为已知契约层实现，维持现状（下沉需单独评估，不在本轮范围）。

### 装饰器链实际语义（批次 4 B 组决策）

保留 `Timeout(Retry(tool))` 语义（全局超时覆盖重试总时长，保守）；链序为
`ToolOutputLimiter(Cached(Timeout(Retry(tool))))`——注册顺序「重试→超时→缓存→输出限长」，后注册者居外层，重试在最内层。修正了 `ToolDecoratorRegistry.CreateDefault` 的相反注释。详见 [`04 §3`](04-development-standards.md) 与根 `AGENTS.md`「工具装饰器链」。

### 显式接受残留清单（避免下轮审查重复发现）

| 残留 | 说明 / 边界 |
|------|-------------|
| 多通道 `TryAutoApprove` 进程级语义 | 同进程 WebUI + Gateway 为默认示例形态；Gateway `auto_approve` 影响同进程 WebUI 会话。按来源隔离属后续演进 |
| `PermissionRuleEntry.PathMatches` 相对 glob | 相对 glob 对绝对路径失效；Windows 大小写语义不一致（`*.PEM` vs `key.pem` Deny 存在绕过面） |
| `PermissionGrantStore.Lookup` 恒 `OrdinalIgnoreCase` | Linux 大小写敏感文件系统上跨大小写文件可命中 |
| Gateway 审批回传 scope 恒 `Once` | 协议层无法表达 Session/SessionDirectory 记忆 |
| webfetch DNS rebinding TOCTOU | 每跳仅做一次 DNS 预校验，实际连接由 `HttpClient` 重新解析（校验与连接两次解析）；恶意 DNS 可在两次解析间切换至内网 IP。**连接期 IP 固化未实现**（`SocketsHttpHandler.ConnectCallback` 校验/按 IP 直连），属后续演进 |
| `ToolDrainTimeout` 取消泄漏 | 不响应取消的工具可能拖满排空窗口（10s）后跳过终态，导致任务/进程泄漏（已知边界，见根 `AGENTS.md`） |
| `DangerousCommandGuard` 令牌化绕过 | guard 语义自述「只拦截灾难性操作」；`xargs` 等令牌化包装可绕过，为设计边界，不做行为增强（批次 5 文档声明项） |
| Gateway 服务器生命周期与 `gateway` 模块脱钩 | 网关为**宿主级基础设施**（`AddSeeingGatewayServer` 由 sample 组合），不随模块 Activate/Deactivate（设计意图，非债） |

### 本轮新增防回归扫描命令

```text
# Core 零工具实现（[Tool( 应无命中；: ITool\b 仅剩基础设施）
rg ": ITool\b|\[Tool\(" src/spine
→ [Tool( 空；: ITool\b 命中 ToolDecorator / ReflectedTool 及泛型约束（基础设施）

# 能力包内裸 IHostedService 注册（应为零：注册型资源随 Module Activate/Deactivate 收编）
rg "AddHostedService<" src/capabilities -g "*.cs"
→ 无命中；AcpHookRegistrationHostedService 已退役，Hook 注册移入 AcpModule.ActivateAsync/DeactivateAsync
```

---

## 是否需要重构？

| 类别 | 判定 |
|------|------|
| 分层 / 模块生命周期 / schema 单点 / 目录分类 | **否**（已合规） |
| Memory 连接经 Owner 每次取、同步 GetToolSchemas 清理 | **已完成**（本轮） |
| Hosting→Tools.Support | **保持**（有意依赖，非债） |
| 文档与入口过时表述 | **已随 architecture 文档集处理**（2026-09-08）；**2026-09-25 再次全量校正**（title Agent、Helper 表、HookPoints 位置、02 补 5+1 包、装饰器链语义、断链） |

若出现下表现象，再开重构 PR，不要用补丁旁路「先顶住」。

---

## 若出现下列现象 → 立即停并对照本文

| 现象 | 可能根因 |
|------|----------|
| 配置禁用模块但 schema 仍有工具 | 又在 ConfigureServices 挂了 ITool，或 Activate 未 Unregister |
| 热重载后工具还在 / 后台还在跑 | Deactivate 未对称；HostedService 未实现复活协议 |
| 改了 seeing.json 节不生效 | 未 `IConfigSectionRegistry.Register` 或旁路读文件 |
| 能力包编译依赖 Core | 违反分层；上提端口到 Abstractions |
| 新项目出现在 `src/Seeing.Agent.Xxx` 根下 | 违反目录分类；移入对应子目录并改 slnx |
| ChatRequest.Tools 与 SchemaSnapshot 不一致 | 第二处计算 schema 写入请求 |
| 能力包实现 / 引用 CapabilitySet 目录或 SettlementEngine | 违反「目录仅 Abstractions/Core」；Boot 结算属脊柱 |
| `Tools.Disabled` 进模块层求交 / 用 Scenario 驱动 Activate | 两层/两轴回潮；对照 01 §3、03 §3–§4 |

---

## 相关报告

- 全项目审查（2026-09-25）：`.superpowers/sdd/reviews/2026-09-25-full-project-audit.md`  
- 修复设计（2026-09-25）：`docs/superpowers/specs/2026-09-25-full-project-remediation-design.md`  
- 整改 release notes： [`11 全项目整改`](11-remediation-release-notes.md)  
- 合规整改计划：`docs/superpowers/plans/2026-09-08-modular-arch-compliance-remediation.md`  
- 终审 r3：`.superpowers/sdd/reviews/compliance-remediation-final-r3.md`  
- W3 依赖盘点（历史）：`docs/superpowers/plans/2026-09-08-w3-core-dependency-inventory.md`  
- 文档深化设计：`docs/superpowers/specs/2026-09-08-architecture-docs-deepen-design.md`
