# Seeing.Agent 公共接口全量审计表

**日期:** 2026-08-18
**来源:** Phase 0 接口审查（`src` 全量扫描 `public interface I\w+`，去重后共 **135** 个）
**对照规范:** 设计文档 §5.1 命名规范（后缀即职责：Store/Registry/Manager/Service/Provider/Executor/Channel/Loader/Sink；接口名 = 实现类名去 I；层级 Store < Registry < Manager）
**约定:** 「处置」= 保留/删除/拆分/审查中/改名/重新设计；「审查中」附理由

---

## 一、主库 Seeing.Agent（70 个，核心审查对象）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IActivityTracer | 活动跟踪（IExecutionContext.cs:41 内嵌） | 合规（Tracer） | **保留** |
| IAgent | Agent 契约（17 可变属性 + 运行时状态） | 名不副实 | **删除**（§6.1 纯数据化，AgentDefinition 替代） |
| IAgentConfigManager | Agent MD 配置编辑（**零实现类、零消费方**，能力由 AgentManager 承担） | 合规（Manager） | **删除**（Phase 0 审查结论：孤儿接口；能力并入 AgentManager 内部，见设计文档 Phase 0 结论 2） |
| IAgentExecutionRouter | 执行入口（名为路由实为执行） | 语义误导 | **删除**（改名 IAgentExecutor，§6.1） |
| IAgentGenerator | Agent 生成器（Core/Generation/AgentGenerator.cs:11 实现） | Generator 不在 §5.1 | **审查中**（有实现有消费，建议保留；命名规范补充 Generator=生成器后缀） |
| IAgentLoopScheduler | 循环调度器（Core/Scheduling/） | Scheduler 不在 §5.1，与 Scheduler 包重名歧义 | **审查中**（保留现状；命名规范补充 Scheduler=调度器后缀，纳入后续排期） |
| IAgentManager | 26 方法 Agent 管理 | ISP 违反 | **删除**（§5.2；职责由 IAgentRegistry/IAgentRuntimeManager 承接） |
| IAgentRegistry | Agent 注册/查询/注销/权限筛选 | 合规（Registry） | **保留**（§6.1 拆分 15 方法 → 注册/查询/注销/权限筛选） |
| IAgentRuntimeManager | 默认 Agent/模型绑定/会话级模型覆盖 | 合规（Manager） | **保留**（§6.1；默认 Agent 方法去重后仅留此处） |
| IAgentStore | Agent 纯存取（5 方法） | 合规（Store） | **保留**（§6.1；Registry 底层存储，进 Abstractions） |
| IBackgroundTaskManager | 后台任务管理 | 合规（Manager） | **保留** |
| IBackgroundTaskProgress | 后台任务进度 | 合规（Progress） | **保留** |
| ICommand | 命令契约（Commands/ICommand.cs:203） | 无后缀 | **保留**（进 Abstractions Commands/） |
| ICommandDiscoveryInitializer | 命令发现初始化（CommandSystemOptions.cs:36） | Initializer 不在 §5.1 | **审查中**（建议保留；命名规范补充 Initializer=初始化器后缀） |
| ICommandRegistry | 命令注册表 | 合规（Registry） | **保留**（进 Abstractions Commands/） |
| ICommandService | 命令服务 | 合规（Service） | **保留** |
| IComponentLoader | 组件加载器 | 合规（Loader） | **保留**（§6.5 Type 开放化：枚举 → string） |
| IComponentManager | 组件管理器 | 合规（Manager） | **保留**（§6.5 签名变更） |
| IConfigSectionStore | 配置节存储 | 合规（Store） | **保留** |
| IConfigurableLlmProvider | 可配置 LLM Provider | 合规（Provider） | **保留**（进 Abstractions Llm/） |
| IExecutionContext | 执行上下文 | 合规（Context） | **保留**（ActiveAgent → AgentDefinition?，§6.1） |
| IExecutionMiddleware | 执行中间件 | 合规（Middleware） | **保留**（随 IExecutionPipeline） |
| IExecutionPipeline | 中间件链管道（有实现 ExecutionPipeline + DI 注册，非孤儿） | Pipeline 不在 §5.1 | **审查中**（保留现状；命名规范补充 Pipeline=管道后缀，与 Executor 语义边界待 Phase 2 确认） |
| IExtension | 扩展插件契约 | 命名合规 | **重新设计**（§7.1 拆分为 7 个小接口） |
| IGitService | Git 服务 | 合规（Service） | **保留** |
| IHookHandler | Hook 处理器 | 合规（Handler） | **保留**（进 Abstractions Hooks/） |
| IHookManager | Hook 管理器 | 合规（Manager） | **保留**（进 Abstractions Hooks/） |
| IInstructionManager | 指令管理器 | 合规（Manager） | **保留** |
| ILlmClient | LLM 客户端 | 无后缀 | **保留** |
| ILlmClientFactory | 客户端工厂 | 合规（Factory） | **保留** |
| ILlmProvider | LLM Provider | 合规（Provider） | **保留**（进 Abstractions Llm/） |
| ILlmService | LLM 服务 | 合规（Service） | **保留** |
| IMcpClientWrapper | MCP 客户端包装（McpClientManager.cs:947 内嵌） | 合规（Wrapper） | **保留** |
| IMcpClientWrapperFactory | MCP 包装工厂 | 合规（Factory） | **保留** |
| IMcpConfigManager | MCP 配置管理 | 合规（Manager） | **保留**（进 Abstractions Mcp/） |
| IMcpConfigPersistence | MCP 配置持久化 | 合规（Persistence） | **保留** |
| IMcpController | MCP 控制器 | 合规（Controller） | **保留**（进 Abstractions Mcp/） |
| IMcpManager | MCP 管理门面（组合 Status/Controller/Config） | 合规（Manager） | **保留**（进 Abstractions Mcp/） |
| IMcpOAuthProvider | MCP OAuth 提供方 | 合规（Provider） | **保留** |
| IMcpStatusProvider | MCP 状态提供方 | 合规（Provider） | **保留**（进 Abstractions Mcp/） |
| IMcpToolRegistry | MCP 工具注册表 | 合规（Registry） | **保留** |
| IMessageEvent | 消息事件 | 无后缀 | **保留**（进 Abstractions Events/） |
| IMetadataStore | 元数据 K-V 存取（消费：DefaultExecutionContext.Metadata + DI） | 合规（Store，纯存取无业务规则） | **保留** |
| IModelConfigManager | 模型配置读写（目录查询 + 持久化 + 变更事件） | 合规（Manager） | **保留**（Phase 0 结论：配置读写归此接口，进 Abstractions Llm/） |
| IModelManager | 模型解析门面（继承 IModelConfigManager；ResolveNativeModel/ResolveAcpModel/会话模型读写） | 合规（Manager） | **保留**（Phase 0 结论：模型解析归此接口） |
| IMultiHookHandler | 多 Hook 处理器（IHookHandler.cs:27 同文件） | 合规（Handler） | **保留** |
| IOnlineSkillParser | 在线技能解析器 | 合规（Parser） | **保留** |
| IPermissionAware | 权限感知标记 | 合规（Aware） | **保留** |
| IPermissionCache | 权限缓存 | 合规（Cache） | **保留** |
| IPermissionChannel | 权限通道 | 合规（Channel） | **保留**（进 Abstractions Permissions/） |
| IPermissionMemory | 权限记忆 | 合规（Memory） | **保留** |
| IPermissionService | 权限服务（6 个 Evaluate 显式方法） | 合规（Service） | **保留**（进 Abstractions Permissions/；方法合并列为后续可选优化） |
| IProviderEndpointInfo | Provider 端点信息（数据） | 合规（Info） | **保留** |
| IProviderEndpointLookup | Provider 端点查找 | 合规（Lookup） | **保留** |
| IProviderManager | Provider 配置编排（配置持久化/客户端获取/连接测试/ProviderInfo 视图） | 合规（Manager） | **保留**（Phase 0 结论：与 Registry 职责互补；GetDefaultProvider/SetDefaultProviderAsync 按 §6.7 删除） |
| IProviderRegistry | Provider 注册表（注册/注销/查询/owner 跟踪/变更事件） | 合规（Registry） | **保留**（Phase 0 结论：纯集合管理；进 Abstractions Llm/） |
| IRuleEvaluator | 规则评估器 | 合规（Evaluator） | **保留**（进 Abstractions Permissions/） |
| ISessionEventBus | Session 实时事件总线（消费：TaskTool 注入 + App 适配 ExecutionSessionEventBus） | 合规（Bus） | **保留** |
| ISessionTitleEnsuring | 会话标题保障（消费：ExecutionJobService 2 处 + DI） | Ensuring 非职责后缀（动词形式） | **改名**（建议 ISessionTitleService，随命名规范修订） |
| IShellEnvironmentService | Shell 环境服务 | 合规（Service） | **保留** |
| IShellService | Shell 服务 | 合规（Service） | **保留** |
| ITaskEventProjector | 事件投影器（Child Loop 事件降采样为 Task* 投影事件；消费：TaskTool + DI） | 合规（Projector） | **保留** |
| ITodoManager | Todo 管理器（**孤儿**：零消费方） | 命名合规但死代码 | **删除**（§5.2/§6.4，ITodoStore 替代） |
| ITool | 工具契约 | 无后缀 | **保留**（进 Abstractions Tools/） |
| IToolDecoratorRegistry | 工具装饰器注册表（超时→重试→缓存链） | 合规（Registry） | **保留** |
| IToolPermissionPolicy | 工具资源级权限策略（单方法 Evaluate；ToolManager 在 tool.execute.before 后评估） | 合规（Policy） | **保留**（进 Abstractions Permissions/ 或 Tools/，随实现迁移定） |
| IWorkspaceProvider | 工作区提供方 | 合规（Provider） | **保留** |
| IWorkspaceWhitelist | 工作区白名单 | 合规（Whitelist） | **保留** |
| ITextCompletion | 文本补全 | 合规（Completion） | **保留** |
| SkillInfo（注：**ISkill 接口不存在**，ISkill.cs 仅含 SkillInfo 数据类） | 技能数据模型（纯数据，含 DirectoryPath 计算属性） | 数据类无接口 | **保留**（迁 Abstractions Skills/；设计文档 §4.2 的「ISkill.cs」条目实为 SkillInfo，实施时以 SkillInfo.cs 命名） |

## 二、Seeing.Agent.App（2 个）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IChatOrchestrator | 聊天编排器 | 合规（Orchestrator） | **保留** |
| IExecutionEventPublisher | 执行事件发布器（ISessionEventBus 适配来源） | 合规（Publisher） | **保留** |

## 三、Seeing.Gateway / Seeing.Agent.Gateway（6 个）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IChannelBridge | 通道桥 | 合规（Bridge） | **保留** |
| IGatewayClient | Gateway 客户端 | 无后缀 | **保留** |
| IGatewayConnection | 连接（IAsyncDisposable） | 无后缀 | **保留** |
| IGatewayChannelPlugin | 通道插件 | 合规（Plugin） | **保留** |
| IGatewayEventSink | 事件接收器 | 合规（Sink，§5.1 单向出口） | **保留** |
| IGatewayServer | Gateway 服务器 | 合规（Server） | **保留** |

## 四、Seeing.Agent.Acp（6 个，扩展包内部契约，本次不动）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IAcpAssistantTextAccumulator | ACP 助手文本累积器 | 合规（Accumulator） | **保留** |
| IAcpBackendRegistry | ACP 后端注册表 | 合规（Registry） | **保留** |
| IAcpConfigurationReloader | ACP 配置重载器 | 合规（Reloader） | **保留** |
| IAcpSessionConfigClient | ACP 会话配置客户端 | 无后缀 | **保留** |
| IAcpSessionRunner | ACP 会话运行器 | 合规（Runner） | **保留** |
| IAcpUpdateSink | ACP 更新接收器 | 合规（Sink） | **保留** |

## 五、Seeing.Gateway.QQ / Seeing.Gateway.WeCom（2 个）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IQQCardKind | QQ 卡片类型标记 | 合规（Kind） | **保留** |
| IWeComActiveStreamHandle | 企微活跃流句柄 | 合规（Handle） | **保留** |

## 六、Seeing.Agent.Memory（30 个，扩展包内部契约，命名合规为主，本次不动）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IEmbeddingCache | 嵌入缓存 | 合规（Cache） | **保留** |
| IEmbeddingConnectionTester | 嵌入连接测试器 | 合规（Tester） | **保留** |
| IEmbeddingService | 嵌入服务 | 合规（Service） | **保留** |
| IEmbeddingStatus | 嵌入状态 | 合规（Status） | **保留** |
| IEvaluateMemoryAsync | 记忆评估（MemoryEvaluator.cs:7 内嵌） | Async 作后缀不符规范（异步方法后缀规则） | **审查中**（建议改名 IMemoryEvaluatorAsync 或并入 IMemoryEvaluator；扩展包内部，低优先） |
| IFileStore | 文件存储 | 合规（Store） | **保留** |
| IKeywordIndex | 关键词索引 | 合规（Index） | **保留** |
| IMigration | 模式迁移 | 合规（Migration） | **保留** |
| IMemoryBenchmark | 记忆基准测试 | 合规（Benchmark） | **保留** |
| IMemoryEvaluator | 记忆评估器 | 合规（Evaluator） | **保留** |
| IMemoryEvolutionService | 记忆进化服务 | 合规（Service） | **保留** |
| IMemoryExtractor | 记忆提取器 | 合规（Extractor） | **保留** |
| IMemoryFilter | 记忆过滤器 | 合规（Filter） | **保留** |
| IMemoryFlushService | 记忆冲刷服务 | 合规（Service） | **保留** |
| IMemoryGraph | 记忆图 | 合规（Graph） | **保留** |
| IMemoryHeuristicFilter | 记忆启发式过滤器 | 合规（Filter） | **保留** |
| IMemoryIndex | 记忆索引 | 合规（Index） | **保留** |
| IMemoryIndexer | 记忆索引器 | 合规（Indexer） | **保留** |
| IMemoryOptionsStore | 记忆选项存储 | 合规（Store） | **保留** |
| IMemoryPipeline | 记忆管道 | 合规（Pipeline） | **保留** |
| IMemoryRecallService | 记忆召回服务 | 合规（Service） | **保留** |
| IMemoryService | 记忆服务门面 | 合规（Service） | **保留** |
| IMemorySessionEvents | 记忆会话事件 | 合规（Events） | **保留** |
| IMemoryWorkQueue | 记忆工作队列 | 合规（Queue） | **保留** |
| IQuotaManager | 配额管理 | 合规（Manager） | **保留** |
| IRateLimiter | 限流器 | 合规（Limiter） | **保留** |
| ISessionActivityTracker | 会话活动跟踪器 | 合规（Tracker） | **保留** |
| ISessionMemoryBuffer | 会话记忆缓冲 | 合规（Buffer） | **保留** |
| ITokenTracker | Token 跟踪器 | 合规（Tracker） | **保留** |
| IVectorIndex | 向量索引 | 合规（Index） | **保留** |

## 七、Seeing.Agent.TokenBudget（7 个，扩展包内部契约，本次不动）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IBudgetStatusNotifier | 预算状态通知器 | 合规（Notifier） | **保留** |
| ICompressionService | 压缩服务 | 合规（Service） | **保留** |
| ICompressionStrategyFactory | 压缩策略工厂 | 合规（Factory） | **保留** |
| ICompressionTrigger | 压缩触发器 | 合规（Trigger） | **保留** |
| ITokenBudgetApi | Token 预算 API | 合规（Api） | **保留** |
| ITokenBudgetConfigResolver | Token 预算配置解析器 | 合规（Resolver） | **保留** |
| ITokenBudgetManager | Token 预算管理 | 合规（Manager） | **保留** |

## 八、Seeing.Agent.Scheduler（5 个，扩展包内部契约，本次不动）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| IJobExecutionListener | 作业执行监听器 | 合规（Listener） | **保留** |
| IScheduledJobDispatcher | 调度作业分发器 | 合规（Dispatcher） | **保留** |
| IScheduleManager | 调度管理 | 合规（Manager） | **保留** |
| IScheduleRepository | 调度仓储 | 合规（Repository） | **保留** |
| ISchedulerOptionsProvider | 调度选项提供方 | 合规（Provider） | **保留** |

## 九、Seeing.Session（7 个，原语层，本次不动）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| ICompressionStrategy | 压缩策略 | 合规（Strategy） | **保留** |
| IExecutionState | 执行状态 | 合规（State） | **保留** |
| ISessionEventPublisher | 会话事件发布器 | 合规（Publisher） | **保留** |
| ISessionHook | 会话钩子 | 合规（Hook） | **保留** |
| ISessionManager | 会话管理（生命周期） | 合规（Manager） | **保留** |
| ISessionStore | 会话存储 | 合规（Store） | **保留** |
| ISummarizer | 摘要器 | 合规（Summarizer） | **保留** |

## 十、Seeing.TokenEstimation（1 个，原语层，本次不动）

| 接口 | 现状职责 | 命名合规 | 处置 |
|------|---------|---------|------|
| ITokenCounter | Token 计数器 | 合规（Counter） | **保留** |

---

## 统计与待办汇总

| 处置 | 数量 | 说明 |
|------|------|------|
| 保留（含进 Abstractions） | 120 | 主库 61 + 其余包 59（扩展包/原语层本次不动） |
| 删除 | 5 | IAgent、IAgentConfigManager、IAgentExecutionRouter、IAgentManager、ITodoManager |
| 重新设计 | 1 | IExtension（§7.1 拆分） |
| 改名 | 1 | ISessionTitleEnsuring → ISessionTitleService（建议） |
| 审查中 | 7 | IAgentGenerator / IAgentLoopScheduler / ICommandDiscoveryInitializer / IExecutionPipeline（主库 4，待命名规范补充）+ IEvaluateMemoryAsync（Memory，低优先）；另 ISessionEventBus/ITaskEventProjector/IMetadataStore/IToolPermissionPolicy/ISkill(实为 SkillInfo) 经核对**有消费方或纯数据，直接保留** |
| 合计 | 135 | |

**Phase 2 实施时按此表处置；「审查中」项待命名规范修订（§5.1 补充 Generator/Scheduler/Initializer/Pipeline 后缀）后定案。**