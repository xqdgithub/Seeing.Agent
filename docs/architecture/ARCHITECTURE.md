# Seeing.Agent 框架设计文档

**版本:** 2.0.0  
**目标框架:** .NET 10.0  
**生成日期:** 2026-04-04

---

## 一、架构总览

### 1.1 设计目标

Seeing.Agent 是一个完整的 AI Agent 框架，提供：
- 🤖 **Agent 编排** - 主 Agent / 子 Agent 模式，支持模型配置和系统提示词
- 🛠️ **Tool 工具系统** - 注解发现 + MCP 集成 + 装饰器链
- 🎯 **Skill 技能系统** - 文件发现 + 参数传递
- 🔗 **Hook 生命周期** - 20+ 扩展点
- 🛡️ **Rules 权限引擎** - Allow/Deny/Ask 三种动作
- 📦 **Extension 插件** - 简洁的 DI 注册入口
- 🧠 **LLM 模块** - OpenAI/Anthropic 客户端，配置外置

### 1.2 分层架构

```
┌─────────────────────────────────────────────────────────────────┐
│                        用户实现层                                │
│          User Agents / Skills / Tools / HookHandlers            │
├─────────────────────────────────────────────────────────────────┤
│                        核心抽象层                                │
│   IAgent, ISkill, ITool, IHookHandler, IRuleEngine, IExtension  │
│   AgentBase, SkillBase, ToolBase                                │
├─────────────────────────────────────────────────────────────────┤
│                        服务管理层                                │
│   HookManager, ToolInvoker, SkillManager, SessionManager,       │
│   RuleEngine, McpClientManager, LlmService                      │
├─────────────────────────────────────────────────────────────────┤
│                        基础设施层                                │
│   DI 注册, 日志, 配置, 并发集合, HTTP Client                     │
└─────────────────────────────────────────────────────────────────┘
```

### 1.3 模块依赖关系

```
                              ┌──────────────┐
                              │    IAgent    │
                              └──────┬───────┘
                                     │
               ┌─────────────────────┼─────────────────────┐
               │                     │                     │
               ▼                     ▼                     ▼
      ┌─────────────┐        ┌─────────────┐      ┌─────────────┐
      │    ITool    │        │   ISkill    │      │IHookHandler │
      └──────┬──────┘        └──────┬──────┘      └──────┬──────┘
             │                      │                    │
             ▼                      ▼                    ▼
      ┌─────────────┐        ┌─────────────┐      ┌─────────────┐
      │ ToolInvoker │        │SkillManager │      │ HookManager │
      │  (统一管理)  │        └─────────────┘      └──────┬──────┘
      └──────┬──────┘                                    │
             │                                           │
             │              ┌─────────────┐              │
             └─────────────►│ RuleEngine  │◄─────────────┘
                            └──────┬──────┘
                                   │
                                   ▼
                            ┌─────────────┐
                            │SessionManager│
                            └─────────────┘
                                   │
                                   ▼
                            ┌─────────────┐
                            │  LlmService │
                            └──────┬──────┘
                                   │
                    ┌──────────────┼──────────────┐
                    ▼              ▼              ▼
             ┌───────────┐  ┌───────────┐  ┌───────────┐
             │OpenAiClient│ │AnthropicClient│ │  Future   │
             └───────────┘  └───────────┘  └───────────┘
```

---

## 二、完整代码结构图

```
Seeing.Agent/
│
├── Configuration/
│   └── SeeingAgentOptions.cs           # 应用配置入口
│       ├── DefaultModel                # 默认模型
│       ├── DefaultProvider             # 默认 Provider
│       ├── DefaultAgent                # 默认 Agent
│       ├── Providers: Dict<string, ProviderConfig>
│       ├── Agents: Dict<string, AgentConfig>
│       ├── SkillPaths: List<string>
│       └── Plugins: List<string>
│
├── Core/
│   │
│   ├── Interfaces/                     # 核心接口定义
│   │   ├── IAgent.cs                   # Agent 核心接口
│   │   │   ├── Name, Mode, Description
│   │   │   ├── Permissions, SystemPrompt, Model
│   │   │   └── ExecuteAsync() → IAsyncEnumerable<ChatMessage>
│   │   │
│   │   ├── ITool.cs                    # 工具接口
│   │   │   ├── Id, Description, Tags, Category
│   │   │   ├── ParametersSchema: JsonElement
│   │   │   └── ExecuteAsync() → Task<ToolResult>
│   │   │
│   │   ├── ISkill.cs                   # 技能接口
│   │   │   ├── Name, Description, Location
│   │   │   └── ExecuteAsync() → Task<SkillResult>
│   │   │
│   │   ├── IHook.cs                    # Hook 接口
│   │   │   ├── HookPoints (20+ 常量)
│   │   │   └── IHookHandler
│   │   │
│   │   ├── IRuleEngine.cs              # 规则引擎接口
│   │   │   ├── AddRule(), LoadFromConfig()
│   │   │   ├── Evaluate(), IsToolDisabled()
│   │   │   └── PermissionRule, PermissionAction
│   │   │
│   │   ├── IRuleEvaluator.cs           # 规则评估器
│   │   ├── ISessionManager.cs          # 会话管理器
│   │   ├── IExtension.cs               # 扩展接口
│   │   ├── IExecutionPipeline.cs       # 执行管道
│   │   ├── IExecutionContext.cs        # 执行上下文
│   │   ├── IMetadataStore.cs           # 元数据存储
│   │   └── IPermissionChannel.cs       # 权限通道
│   │
│   ├── Abstractions/                   # 抽象基类
│   │   ├── AgentBase.cs                # Agent 基类
│   │   │   └── LogStart(), LogComplete(), LogError()
│   │   │
│   │   ├── SkillBase.cs                # Skill 基类
│   │   │   └── Success(), Failure(), GetParameter()
│   │   │
│   │   └── ToolBase.cs                 # Tool 基类
│   │       └── Success(), Failure(), ParseArgument()
│   │
│   └── Models/                         # 数据模型
│       ├── ChatMessage.cs              # 聊天消息
│       │   ├── ChatRole (System/User/Assistant/Tool)
│       │   ├── ChatMessage (Role, Content, ToolCalls)
│       │   ├── ToolCall (Name, Id, Arguments)
│       │   └── ToolCallResult
│       │
│       ├── ConfigurationModels.cs      # 配置模型
│       │   ├── AgentConfig, ModelType
│       │   ├── SessionInfo, ToolDefinition
│       │   └── FunctionDefinition, FunctionSchema
│       │
│       ├── ToolArguments.cs            # 工具参数解析
│       ├── ExecutionPipeline.cs        # 执行管道模型
│       ├── DefaultExecutionContext.cs  # 默认执行上下文
│       └── ConcurrentMetadataStore.cs  # 并发元数据存储
│
├── Llm/                                # 🆕 LLM 模块
│   │
│   ├── ILlmClient.cs                   # 客户端接口
│   │   ├── ILlmClient
│   │   │   ├── ProviderId, ProviderType
│   │   │   ├── CompleteAsync()
│   │   │   ├── CompleteStreamAsync()
│   │   │   └── TestConnectionAsync()
│   │   │
│   │   └── ILlmClientFactory
│   │       ├── Create(config) → ILlmClient
│   │       ├── SupportedTypes
│   │       └── SupportsType()
│   │
│   ├── LlmModels.cs                    # 消息模型
│   │   ├── ChatRole (System/User/Assistant/Tool)
│   │   ├── ChatMessage
│   │   │   ├── Role, Content, ReasoningContent
│   │   │   ├── ToolCalls, ToolCallId
│   │   │   └── Attachments
│   │   │
│   │   ├── ChatRequest
│   │   │   ├── Model, Messages, SystemPrompt
│   │   │   ├── Temperature, TopP, MaxTokens
│   │   │   └── Tools, ToolChoice
│   │   │
│   │   ├── ChatResponse
│   │   │   ├── Id, Model, Message
│   │   │   ├── FinishReason, Usage
│   │   │   └── ToolCalls
│   │   │
│   │   ├── StreamUpdate               # 流式更新
│   │   ├── TokenUsage                 # Token 用量
│   │   ├── ToolDefinition             # 工具定义
│   │   ├── FunctionCall               # 函数调用
│   │   └── ToolCall                   # 工具调用结果
│   │
│   ├── ModelConfig.cs                  # 模型配置
│   │   ├── ModelConfig
│   │   │   ├── Id, Name, Provider
│   │   │   ├── Capabilities (Temperature, Reasoning, ToolCall...)
│   │   │   ├── Limits (Context, Output)
│   │   │   └── Pricing (Input, Output, CacheRead/Write)
│   │   │
│   │   ├── ModelCapabilities          # 模型能力
│   │   ├── ModelLimits                # 模型限制
│   │   ├── ModelPricing               # 定价信息
│   │   ├── ModalityCapabilities       # 模态能力
│   │   │
│   │   └── PredefinedModels           # 预定义模型
│   │       ├── OpenAI: gpt-4o, gpt-4o-mini, o1...
│   │       ├── Anthropic: claude-sonnet-4, claude-3-5...
│   │       └── GetAll()
│   │
│   ├── ProviderConfig.cs               # Provider 配置
│   │   ├── ProviderConfig
│   │   │   ├── Id, Type
│   │   │   ├── BaseUrl, ApiKey
│   │   │   ├── Timeout, MaxRetries
│   │   │   └── DefaultModel
│   │   │
│   │   ├── ProviderType (OpenAI, Anthropic)
│   │   │
│   │   └── PredefinedProviders        # 预定义 Provider
│   │
│   ├── LlmService.cs                   # 服务层（统一管理）
│   │   ├── ILlmService
│   │   │   ├── GetAvailableModels()
│   │   │   ├── GetModelConfig()
│   │   │   ├── GetClientForModel()
│   │   │   ├── GetClient()
│   │   │   ├── CompleteAsync()
│   │   │   ├── CompleteStreamAsync()
│   │   │   └── TestConnectionAsync()
│   │   │
│   │   └── LlmService
│   │       ├── _modelConfigs: ConcurrentDict
│   │       ├── _clients: ConcurrentDict
│   │       ├── InitializePredefinedModels()
│   │       └── InitializeClients()
│   │
│   └── Clients/                        # 客户端实现
│       ├── OpenAiClient.cs             # OpenAI SDK v2.2.0
│       │   ├── ChatClient (SDK)
│       │   ├── BuildMessages()
│       │   ├── BuildOptions()
│       │   ├── MapResponse()
│       │   └── CompleteChatStreamingAsync()
│       │
│       └── AnthropicClient.cs          # Anthropic HTTP API
│           ├── HttpClient
│           ├── BuildAnthropicRequest()
│           ├── BuildContent()
│           ├── MapResponse()
│           ├── AnthropicResponse (内部模型)
│           └── AnthropicStreamEvent
│
├── Tools/                              # 工具系统
│   │
│   ├── ToolInvoker.cs                  # 🔧 统一工具管理器
│   │   ├── 注册: RegisterTool, RegisterToolsFromType<T>
│   │   ├── 注销: UnregisterTool
│   │   ├── 查询: GetTool, GetTools, GetToolsByTag, GetToolsByCategory
│   │   ├── 执行: ExecuteAsync (Hook 集成)
│   │   ├── Schema: GetToolSchemas (LLM Function Calling)
│   │   └── 装饰器: 自动应用 IToolDecoratorRegistry
│   │
│   ├── Attributes/                     # 注解属性
│   │   ├── ToolAttribute               # [Tool("描述")]
│   │   ├── ToolParamAttribute          # [ToolParam("描述")]
│   │   ├── RequiredAttribute           # [Required]
│   │   └── ToolParamTypeAttribute      # [ToolParamType("描述")]
│   │
│   └── Discovery/                      # 工具发现
│       ├── ToolDiscovery.cs            # 注解扫描 + Schema 构建
│       │   ├── DiscoverTools()
│       │   ├── BuildParametersSchema()
│       │   └── DiscoverFromAssembly()
│       │
│       └── ReflectedTool.cs            # 方法包装器
│           ├── ITool 实现
│           ├── ExecuteAsync() (反射调用)
│           └── ConvertParameter()
│
├── Decorators/                         # 工具装饰器
│   ├── ToolDecorator.cs                # 装饰器基类
│   ├── ToolDecoratorRegistry.cs        # 装饰器注册表
│   ├── RetryToolDecorator.cs           # 重试装饰器
│   ├── TimeoutToolDecorator.cs         # 超时装饰器
│   ├── CachedToolDecorator.cs          # 缓存装饰器
│   └── AgentDecorator.cs               # Agent 装饰器
│
├── MCP/                                # MCP 集成
│   ├── McpClientManager.cs             # MCP Server 管理
│   │   ├── ConnectAsync()
│   │   ├── GetTools() → List<ITool>
│   │   └── DisconnectAllAsync()
│   │
│   └── McpTool.cs                      # MCP 工具包装器
│       └── ITool 代理实现
│
├── Hooks/                              # Hook 系统
│   ├── IHookManager.cs                 # Hook 管理器接口
│   └── HookManager.cs                  # Hook 管理器实现
│       ├── RegisterHandler()
│       ├── TriggerAsync()
│       └── _handlers: ConcurrentDict<string, List<IHookHandler>>
│
├── Rules/                              # 规则引擎
│   └── RuleEngine.cs                   # 权限规则引擎
│       ├── AddRule()
│       ├── Evaluate()
│       ├── IsToolDisabled()
│       ├── LoadFromConfig()
│       └── _rules: ConcurrentBag<PermissionRule>
│
├── Skills/                             # 技能系统
│   └── SkillManager.cs                 # 技能管理器
│       ├── RegisterSkill()
│       ├── DiscoverSkillsAsync()
│       ├── ExecuteSkillAsync()
│       └── _skills: ConcurrentDict<string, ISkill>
│
├── Sessions/                           # 会话管理
│   └── SessionManager.cs               # 会话管理器 (Scoped)
│       ├── CreateSession()
│       ├── GetSession()
│       ├── AddMessage()
│       └── _sessions: ConcurrentDict<string, SessionData>
│
├── Middlewares/                        # 执行中间件
│   ├── LoggingMiddleware.cs            # 日志中间件
│   ├── PermissionMiddleware.cs         # 权限中间件
│   └── RetryMiddleware.cs              # 重试中间件
│
├── Helpers/                            # 辅助工具
│   └── JsonExtensions.cs               # JSON 扩展方法
│
└── Extensions/                         # DI 扩展
    └── ServiceCollectionExtensions.cs  # DI 注册入口
        ├── AddSeeingAgent()
        ├── AddExecutionPipeline()
        ├── AddToolsFromType<T>()
        ├── AddLlmProviders()
        ├── RegisterCoreServices()
        ├── RegisterLlmServices()
        └── DefaultLlmClientFactory (内部)
```

---

## 三、核心数据流转图

### 3.1 应用启动流程

```
┌─────────────────────────────────────────────────────────────────┐
│                      Program.cs / Startup                        │
│                                                                 │
│  services.AddSeeingAgent(configuration)                         │
└───────────────────────────────┬─────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│              ServiceCollectionExtensions.cs                      │
│                                                                 │
│  1. 配置绑定                                                     │
│     services.Configure<SeeingAgentOptions>(                     │
│         configuration.GetSection("SeeingAgent"))                │
│                                                                 │
│  2. 核心服务注册                                                 │
│     RegisterCoreServices(services)                              │
│     ├── RuleEngine (Singleton)                                  │
│     ├── HookManager (Singleton)                                 │
│     ├── SessionManager (Scoped)                                 │
│     ├── SkillManager (Singleton)                                │
│     ├── ToolInvoker (Singleton)                                 │
│     └── McpClientManager (Singleton)                            │
│                                                                 │
│  3. LLM 服务注册                                                 │
│     RegisterLlmServices(services)                               │
│     ├── ILlmClientFactory → DefaultLlmClientFactory             │
│     └── ILlmService → LlmService                                │
│                                                                 │
│  4. HttpClient 工厂                                              │
│     services.AddHttpClient()                                    │
└───────────────────────────────┬─────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                       LlmService 初始化                          │
│                                                                 │
│  1. InitializePredefinedModels()                                │
│     → 加载 PredefinedModels.OpenAI                              │
│     → 加载 PredefinedModels.Anthropic                           │
│     → 存入 _modelConfigs                                        │
│                                                                 │
│  2. InitializeClients()                                         │
│     → 遍历 options.Providers                                    │
│     → _clientFactory.Create(config)                             │
│     → 存入 _clients                                             │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 LLM 请求流程

```
┌──────────────────┐
│   Agent/用户代码  │
│                  │
│  llmService      │
│    .CompleteAsync│
│    ("gpt-4o",    │
│     request)     │
└────────┬─────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                       LlmService                              │
│                                                              │
│  1. GetModelConfig("gpt-4o")                                 │
│     ┌────────────────────────────────────────┐               │
│     │ _modelConfigs 查找                     │               │
│     │ "gpt-4o" → ModelConfig                 │               │
│     │   { Provider: "openai", ... }          │               │
│     └────────────────────────────────────────┘               │
│                                                              │
│  2. GetClient("openai")                                      │
│     ┌────────────────────────────────────────┐               │
│     │ _clients 查找                          │               │
│     │ "openai" → OpenAiClient                │               │
│     └────────────────────────────────────────┘               │
│                                                              │
│  3. client.CompleteAsync(request)                            │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                      OpenAiClient                             │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ BuildMessages(request)                                  │ │
│  │                                                         │ │
│  │ ChatRequest.Messages → List<SdkChatMessage>             │ │
│  │                                                         │ │
│  │ ┌─────────────┐  ┌─────────────┐  ┌─────────────┐       │ │
│  │ │SystemPrompt │  │UserMessage  │  │AssistantMsg │       │ │
│  │ │   → System  │  │   → User    │  │   → Assistant│       │ │
│  │ │ChatMessage  │  │ChatMessage  │  │ChatMessage   │       │ │
│  │ └─────────────┘  └─────────────┘  └─────────────┘       │ │
│  │                                                         │ │
│  │ ToolResults → ToolChatMessage                          │ │
│  │ ToolCalls → AssistantChatMessage with ChatToolCall     │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ BuildOptions(request)                                   │ │
│  │                                                         │ │
│  │ Temperature → options.Temperature                       │ │
│  │ MaxTokens → options.MaxOutputTokenCount                 │ │
│  │ Tools → options.Tools.Add(ChatTool.CreateFunctionTool)  │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ _client.CompleteChatAsync(messages, options)            │ │
│  │                                                         │ │
│  │ OpenAI SDK → HTTP Request                               │ │
│  │                                                         │ │
│  │ POST https://api.openai.com/v1/chat/completions        │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                              │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ MapResponse(completion)                                 │ │
│  │                                                         │ │
│  │ ChatCompletion → ChatResponse                           │ │
│  │ completion.Content → message.Content                    │ │
│  │ completion.ToolCalls → message.ToolCalls                │ │
│  │ completion.Usage → response.Usage                       │ │
│  └─────────────────────────────────────────────────────────┘ │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                     ChatResponse                              │
│                                                              │
│  {                                                           │
│    Id: "chatcmpl-xxx",                                       │
│    Model: "gpt-4o",                                          │
│    Message: {                                                │
│      Role: Assistant,                                        │
│      Content: "Hello! How can I help...",                    │
│      ToolCalls: []                                           │
│    },                                                        │
│    FinishReason: "stop",                                     │
│    Usage: { InputTokens: 15, OutputTokens: 20 }             │
│  }                                                           │
└──────────────────────────────────────────────────────────────┘
```

### 3.3 流式响应流程

```
┌──────────────────┐     ┌─────────────────┐     ┌──────────────┐
│   Agent/用户代码  │────▶│   LlmService    │────▶│  OpenAiClient│
│                  │     │                 │     │              │
│  await foreach   │     │ GetClientForModel│     │ SDK Stream   │
│  (update in      │◀────│ GetClient()     │◀────│              │
│   .CompleteStream│     │                 │     │ Yield returns│
│   Async(...))    │     │                 │     │              │
└──────────────────┘     └─────────────────┘     └──────────────┘
         │                                                │
         │                                                │
         ▼                                                ▼
┌──────────────────────────────────────────────────────────────────┐
│                      StreamUpdate (流式更新)                      │
│                                                                  │
│  迭代过程中的增量更新:                                             │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ { Id: "chatcmpl-xxx", ContentDelta: "Hel", IsComplete: false }│  │
│  │ { Id: "chatcmpl-xxx", ContentDelta: "lo",  IsComplete: false }│  │
│  │ { Id: "chatcmpl-xxx", ContentDelta: "!",   IsComplete: false }│  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  最终完成时:                                                      │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ {                                                          │  │
│  │   Id: "chatcmpl-xxx",                                      │  │
│  │   ContentDelta: "Hello!",                                  │  │
│  │   IsComplete: true,                                        │  │
│  │   Usage: { InputTokens: 10, OutputTokens: 5 }             │  │
│  │ }                                                          │  │
│  └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

### 3.4 工具调用流程

```
┌──────────────────┐
│   LLM Response   │
│   (Tool Call)    │
│                  │
│  ToolCalls: [    │
│    { Name:       │
│      "GetWeather"│
│      Arguments:  │
│      {...} }     │
│  ]               │
└────────┬─────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                      ToolInvoker                              │
│                                                              │
│  ExecuteAsync(toolCall, sessionId)                           │
│                                                              │
│  1. 查找工具                                                   │
│     _tools.TryGetValue("GetWeather") → ITool                 │
│                                                              │
│  2. 触发前置 Hook                                              │
│     ┌─────────────────────────────────────────────────────┐  │
│     │ HookPoints.ToolBeforeExecute                        │  │
│     │                                                     │  │
│     │ HookContext {                                       │  │
│     │   HookPoint: "tool.before_execute",                 │  │
│     │   Data: { toolId, arguments }                       │  │
│     │ }                                                   │  │
│     │                                                     │  │
│     │ 返回: HookResult { Continue: true/false }            │  │
│     └─────────────────────────────────────────────────────┘  │
│                                                              │
│  3. 执行工具                                                   │
│     ┌─────────────────────────────────────────────────────┐  │
│     │ tool.ExecuteAsync(arguments, context)               │  │
│     │                                                     │  │
│     │ context = ToolContext {                             │  │
│     │   SessionId, CallId, CancellationToken             │  │
│     │ }                                                   │  │
│     │                                                     │  │
│     │ 返回: ToolResult {                                  │  │
│     │   Success: true,                                    │  │
│     │   Title: "天气查询结果",                             │  │
│     │   Output: "北京今天晴，温度 25°C",                   │  │
│     │   Metadata: {...}                                   │  │
│     │ }                                                   │  │
│     └─────────────────────────────────────────────────────┘  │
│                                                              │
│  4. 触发后置 Hook                                              │
│     HookPoints.ToolAfterExecute                              │
│                                                              │
│  5. 返回结果                                                   │
│     ToolCallResult { Success, CallResult, Message }          │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                    Tool Call Result                           │
│                                                              │
│  {                                                           │
│    Success: true,                                            │
│    ToolCall: { Name: "GetWeather", Id: "call-001" },         │
│    CallResult: "北京今天晴，温度 25°C",                        │
│    Message: "天气查询结果"                                     │
│  }                                                           │
└──────────────────────────────────────────────────────────────┘
```

### 3.5 Hook 触发流程

```
┌──────────────────────────────────────────────────────────────┐
│                      HookManager                              │
│                                                              │
│  _handlers: ConcurrentDictionary<string, List<IHookHandler>> │
│                                                              │
│  {                                                           │
│    "tool.before_execute": [                                  │
│      LogHook (Priority: 10),                                 │
│      PermissionHook (Priority: 5),                           │
│      ...                                                     │
│    ],                                                        │
│    "tool.after_execute": [...],                              │
│    "tool.on_error": [...]                                    │
│  }                                                           │
└────────────────────────┬─────────────────────────────────────┘
                         │
                         │ TriggerAsync("tool.before_execute", data)
                         │
                         ▼
┌──────────────────────────────────────────────────────────────┐
│                    Hook 执行链                                │
│                                                              │
│  1. 按 Priority 排序所有 Handler                              │
│                                                              │
│  2. 顺序执行:                                                  │
│     ┌─────────────┐   ┌─────────────┐   ┌─────────────┐      │
│     │PermissionHook│──▶│  LogHook    │──▶│  NextHook   │      │
│     │ (Priority:5) │   │(Priority:10)│   │             │      │
│     └──────┬──────┘   └──────┬──────┘   └──────┬──────┘      │
│            │                 │                 │              │
│            ▼                 ▼                 ▼              │
│     HookResult {       HookResult {       HookResult {        │
│       Continue: true     Continue: true     Continue: true    │
│     }                 }                 }                     │
│                                                              │
│  3. 任一 Handler 返回 Continue=false 时中断                   │
│                                                              │
│  4. 返回最终的 HookResult                                     │
└──────────────────────────────────────────────────────────────┘
```

---

## 四、配置文件设计

### 4.1 seeing.json 完整结构

```json
{
  "$schema": "https://seeing-agent.dev/schema/seeing.json",
  "SeeingAgent": {
    "DefaultModel": "gpt-4o",
    "DefaultProvider": "openai",
    "DefaultAgent": "primary",
    
    "SkillPaths": [
      "./skills",
      "./.agents/skills"
    ],
    
    "Plugins": [],
    
    "Providers": {
      "openai": {
        "Id": "openai",
        "Type": "OpenAI",
        "BaseUrl": "https://api.openai.com/v1",
        "ApiKey": "${OPENAI_API_KEY}",
        "Timeout": 60000,
        "MaxRetries": 3,
        "DefaultModel": "gpt-4o"
      },
      "anthropic": {
        "Id": "anthropic",
        "Type": "Anthropic",
        "BaseUrl": "https://api.anthropic.com/v1",
        "ApiKey": "${ANTHROPIC_API_KEY}",
        "Timeout": 300000,
        "MaxRetries": 2,
        "DefaultModel": "claude-sonnet-4-20250514"
      }
    },
    
    "Models": {
      "gpt-4o": {
        "Id": "gpt-4o",
        "Name": "GPT-4o",
        "Provider": "openai",
        "Capabilities": {
          "Temperature": true,
          "Reasoning": false,
          "Attachment": true,
          "ToolCall": true,
          "Input": { "Text": true, "Image": true, "Audio": true },
          "Output": { "Text": true, "Audio": true }
        },
        "Limits": { "Context": 128000, "Output": 16384 },
        "Pricing": { "Input": 2.5, "Output": 10 }
      },
      "claude-sonnet-4-20250514": {
        "Id": "claude-sonnet-4-20250514",
        "Name": "Claude Sonnet 4",
        "Provider": "anthropic",
        "Capabilities": {
          "Temperature": true,
          "Reasoning": true,
          "Attachment": true,
          "ToolCall": true,
          "Input": { "Text": true, "Image": true },
          "Output": { "Text": true }
        },
        "Limits": { "Context": 200000, "Output": 16000 },
        "Pricing": { "Input": 3, "Output": 15 }
      }
    },
    
    "Agents": {
      "primary": {
        "Provider": "openai",
        "Model": "gpt-4o",
        "SystemPrompt": "You are a helpful AI assistant.",
        "MaxSteps": 50,
        "Temperature": 0.7,
        "MaxTokens": 4096
      },
      "coder": {
        "Provider": "anthropic",
        "Model": "claude-sonnet-4-20250514",
        "SystemPrompt": "You are an expert software developer.",
        "MaxSteps": 100,
        "Temperature": 0.3,
        "MaxTokens": 16000
      }
    }
  }
}
```

### 4.2 配置优先级

```
┌─────────────────────────────────────────────────────────────┐
│                    配置加载优先级                            │
│                                                             │
│  1. seeing.json 中的配置 (最高优先级)                        │
│     → 可覆盖预定义值                                         │
│                                                             │
│  2. PredefinedModels / PredefinedProviders (预定义)         │
│     → 框架内置，开箱即用                                     │
│                                                             │
│  3. 代码中手动配置 (最低优先级)                              │
│     → services.Configure<SeeingAgentOptions>(options => {})│
└─────────────────────────────────────────────────────────────┘
```

---

## 五、核心接口设计

### 5.1 ILlmClient - LLM 客户端接口

```csharp
/// <summary>
/// LLM 客户端接口 - 只负责发送请求和接收响应
/// </summary>
public interface ILlmClient
{
    string ProviderId { get; }
    ProviderType ProviderType { get; }

    Task<ChatResponse> CompleteAsync(
        ChatRequest request, 
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> TestConnectionAsync(
        CancellationToken cancellationToken = default);
}
```

### 5.2 ILlmService - LLM 服务接口

```csharp
/// <summary>
/// LLM 服务接口 - 统一管理模型配置和客户端调用
/// </summary>
public interface ILlmService
{
    // 模型配置查询
    IReadOnlyDictionary<string, ModelConfig> GetAvailableModels();
    ModelConfig? GetModelConfig(string modelId);

    // 客户端获取
    ILlmClient? GetClientForModel(string modelId);
    ILlmClient? GetClient(string providerId);

    // 请求发送
    Task<ChatResponse> CompleteAsync(
        string modelId, ChatRequest request, 
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId, ChatRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> TestConnectionAsync(
        string providerId, 
        CancellationToken cancellationToken = default);
}
```

### 5.3 ITool - 工具接口

```csharp
/// <summary>
/// LLM 可调用的工具接口
/// </summary>
public interface ITool
{
    string Id { get; }
    string Description { get; }
    IEnumerable<string> Tags { get; }
    ToolCategory Category { get; }
    JsonElement ParametersSchema { get; }
    
    Task<ToolResult> ExecuteAsync(
        JsonElement arguments, 
        ToolContext context);
}
```

### 5.4 IHookHandler - Hook 处理器接口

```csharp
/// <summary>
/// 生命周期钩子处理器
/// </summary>
public interface IHookHandler
{
    string HookPoint { get; }
    int Priority { get; }
    
    Task<HookResult> ExecuteAsync(HookContext context);
}
```

---

## 六、Hook 点列表

| Hook 点 | 触发时机 | 用途 |
|---------|----------|------|
| `chat.before_start` | 对话开始前 | 日志、权限检查 |
| `chat.after_complete` | 对话完成后 | 结果处理、统计 |
| `chat.before_retry` | 重试前 | 重试策略 |
| `chat.on_error` | 对话出错 | 错误处理、告警 |
| `tool.before_execute` | 工具执行前 | 权限、日志、缓存 |
| `tool.after_execute` | 工具执行后 | 结果处理、缓存更新 |
| `tool.on_error` | 工具出错 | 错误处理 |
| `session.created` | Session 创建 | 初始化 |
| `session.updated` | Session 更新 | 状态同步 |
| `session.deleted` | Session 删除 | 清理资源 |
| `agent.before_invoke` | Agent 调用前 | 准备工作 |
| `agent.after_invoke` | Agent 调用后 | 结果处理 |
| `skill.before_execute` | Skill 执行前 | 日志、权限 |
| `skill.after_execute` | Skill 执行后 | 结果处理 |
| `permission.ask` | 权限询问 | 用户交互 |
| `llm.params` | LLM 参数设置 | 动态调整参数 |
| `llm.headers` | LLM Headers | 添加自定义头 |
| `llm.system_prompt` | 系统提示词 | 动态修改提示词 |

---

## 七、使用示例

### 7.1 注册服务

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// 方式 1: 使用配置文件
builder.Services.AddSeeingAgent(builder.Configuration);

// 方式 2: 使用代码配置
builder.Services.AddSeeingAgent(options => 
{
    options.DefaultModel = "gpt-4o";
    options.DefaultProvider = "openai";
    
    options.Providers["openai"] = new ProviderConfig
    {
        Id = "openai",
        Type = ProviderType.OpenAI,
        ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        DefaultModel = "gpt-4o"
    };
});
```

### 7.2 使用 LLM 服务

```csharp
public class MyService
{
    private readonly ILlmService _llmService;
    
    public MyService(ILlmService llmService)
    {
        _llmService = llmService;
    }
    
    public async Task<string> AskAsync(string question)
    {
        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>
            {
                new() { Role = ChatRole.User, Content = question }
            },
            Temperature = 0.7
        };
        
        var response = await _llmService.CompleteAsync("gpt-4o", request);
        return response.Message.Content;
    }
    
    public async IAsyncEnumerable<string> AskStreamAsync(string question)
    {
        var request = new ChatRequest
        {
            Model = "gpt-4o",
            Messages = new List<ChatMessage>
            {
                new() { Role = ChatRole.User, Content = question }
            }
        };
        
        await foreach (var update in _llmService.CompleteStreamAsync("gpt-4o", request))
        {
            if (!string.IsNullOrEmpty(update.ContentDelta))
                yield return update.ContentDelta;
        }
    }
}
```

### 7.3 定义和注册工具

```csharp
// 1. 使用注解定义工具
public class WeatherTools
{
    [Tool("获取天气信息")]
    public static async Task<string> GetWeather(
        [ToolParam("城市名")] string city,
        [Required] DateTime date)
    {
        return $"城市: {city}, 日期: {date:yyyy-MM-dd}, 天气: 晴";
    }
}

// 2. 注册工具
var toolInvoker = serviceProvider.GetRequiredService<ToolInvoker>();
toolInvoker.RegisterToolsFromType<WeatherTools>();

// 3. 执行工具
var result = await toolInvoker.ExecuteAsync("GetWeather", new Dictionary<string, object?>
{
    ["city"] = "北京",
    ["date"] = DateTime.Now
});

Console.WriteLine(result.CallResult); // "城市: 北京, 日期: 2026-04-04, 天气: 晴"
```

### 7.4 使用 Hook

```csharp
// 定义 Hook 处理器
public class LogHook : IHookHandler
{
    public string HookPoint => HookPoints.ToolBeforeExecute;
    public int Priority => 10;
    
    public async Task<HookResult> ExecuteAsync(HookContext context)
    {
        var toolId = context.Data["toolId"]?.ToString();
        Console.WriteLine($"[Hook] 工具即将执行: {toolId}");
        return new HookResult { Continue = true };
    }
}

// 注册 Hook
services.AddSingleton<IHookHandler, LogHook>();
```

### 7.5 配置权限规则

```csharp
var ruleEngine = serviceProvider.GetRequiredService<RuleEngine>();

ruleEngine.AddRule(new PermissionRule
{
    Permission = "file_write",
    Pattern = "/safe/path/*",
    Action = PermissionAction.Allow
});

ruleEngine.AddRule(new PermissionRule
{
    Permission = "file_write",
    Pattern = "/system/*",
    Action = PermissionAction.Deny
});

ruleEngine.AddRule(new PermissionRule
{
    Permission = "tool",
    Pattern = "dangerous_*",
    Action = PermissionAction.Ask  // 需要用户确认
});
```

---

## 八、文件清单

| 目录 | 文件 | 说明 |
|------|------|------|
| Configuration/ | SeeingAgentOptions.cs | 应用配置 |
| Core/Interfaces/ | 9 个接口 | 核心契约 |
| Core/Abstractions/ | 3 个基类 | 抽象实现 |
| Core/Models/ | 5 个模型 | 数据模型 |
| Llm/ | 6 个文件 | LLM 模块 |
| Llm/Clients/ | 2 个文件 | 客户端实现 |
| Tools/ | 1 个文件 | 工具调用器 |
| Tools/Attributes/ | 1 个文件 | 注解属性 |
| Tools/Discovery/ | 2 个文件 | 工具发现 |
| Decorators/ | 6 个文件 | 装饰器 |
| MCP/ | 2 个文件 | MCP 集成 |
| Hooks/ | 2 个文件 | Hook 系统 |
| Rules/ | 1 个文件 | 规则引擎 |
| Skills/ | 1 个文件 | 技能管理 |
| Sessions/ | 1 个文件 | 会话管理 |
| Middlewares/ | 3 个文件 | 执行中间件 |
| Helpers/ | 1 个文件 | 辅助工具 |
| Extensions/ | 1 个文件 | DI 扩展 |
| **总计** | **47 个源文件** | |

---

## 九、测试覆盖

| 模块 | 测试文件 | 测试数量 |
|------|---------|---------|
| HookManager | HookManagerTests.cs | 6 |
| SkillManager | SkillManagerTests.cs | 9 |
| RuleEngine | RuleEngineTests.cs | 9 |
| ToolInvoker | ToolInvokerTests.cs | 6 |
| **总计** | | **30** |

**测试结果：** ✅ 全部通过

---

**文档版本:** 2.0.0  
**最后更新:** 2026-04-04