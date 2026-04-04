# LLM 模块设计文档

**版本**: 1.0  
**生成时间**: 2026-04-04  
**目标框架**: .NET 10.0  

---

## 1. 概述

### 1.1 设计目标

将 LLM 模块重构为 **职责单一** 的架构：
- **Provider 只负责发送请求和接收响应**
- **模型定义由配置文件（seeing.json）管理**
- **参考 opencode 项目的设计模式**

### 1.2 核心原则

| 原则 | 说明 |
|------|------|
| 单一职责 | Provider 不定义模型，只处理 HTTP/API 调用 |
| 配置外置 | 模型能力、限制、定价由 JSON 配置定义 |
| 工厂模式 | ILlmClientFactory 创建客户端实例 |
| 服务层封装 | LlmService 统一管理配置 + 客户端调用 |

---

## 2. 代码结构图

```
Seeing.Agent/
│
├── Configuration/
│   └── SeeingAgentOptions.cs      # 应用配置入口
│       ├── DefaultModel           # 默认模型
│       ├── DefaultProvider        # 默认 Provider
│       ├── Providers: Dict<string, ProviderConfig>
│       ├── Agents: Dict<string, AgentConfig>
│       └── Models: Dict<string, ModelConfig> (可选)
│
├── Llm/                           # 🆕 LLM 模块（重构后）
│   │
│   ├── ILlmClient.cs              # 客户端接口（只负责发送请求）
│   │   ├── ProviderId             # Provider ID
│   │   ├── ProviderType           # Provider 类型
│   │   ├── CompleteAsync()        # 非流式请求
│   │   ├── CompleteStreamAsync()  # 流式请求
│   │   └── TestConnectionAsync()  # 连接测试
│   │   │
│   │   └── ILlmClientFactory      # 工厂接口
│   │       ├── Create(config)     # 创建客户端
│   │       ├── SupportedTypes     # 支持的类型
│   │       └── SupportsType()     # 类型检查
│   │
│   ├── LlmModels.cs               # 消息模型
│   │   ├── ChatRole               # System/User/Assistant/Tool
│   │   ├── ChatMessage            # 消息（角色、内容、工具调用）
│   │   ├── ChatRequest            # 请求（模型、消息、参数、工具）
│   │   ├── ChatResponse           # 响应（ID、消息、用量）
│   │   ├── StreamUpdate           # 流式更新
│   │   ├── TokenUsage             # Token 用量
│   │   ├── ToolDefinition         # 工具定义
│   │   ├── FunctionCall           # 函数调用
│   │   └── ToolCall               # 工具调用结果
│   │
│   ├── ModelConfig.cs             # 模型配置
│   │   ├── ModelConfig            # 模型定义
│   │   │   ├── Id                 # 模型 ID
│   │   │   ├── Name               # 显示名称
│   │   │   ├── Provider           # 所属 Provider
│   │   │   ├── Capabilities       # 能力
│   │   │   ├── Limits             # 限制
│   │   │   └ Pricing              # 定价
│   │   │
│   │   ├── ModelCapabilities      # 模型能力
│   │   │   ├── Temperature        # 支持温度
│   │   │   ├── Reasoning          # 支持推理
│   │   │   ├── ToolCall           # 支持工具调用
│   │   │   ├── Input/Output       # 输入输出模态
│   │   │
│   │   ├── ModelLimits            # 模型限制
│   │   │   ├── Context            # 上下文窗口
│   │   │   ├── Output             # 最大输出
│   │   │
│   │   ├── ModelPricing           # 定价信息
│   │   │
│   │   └── PredefinedModels       # 预定义模型（静态）
│   │       ├── OpenAI: gpt-4o, gpt-4o-mini, o1...
│   │       ├── Anthropic: claude-sonnet-4, claude-3-5...
│   │       └── GetAll()           # 获取所有
│   │
│   ├── ProviderConfig.cs          # Provider 配置
│   │   ├── ProviderConfig         # Provider 定义
│   │   │   ├── Id                 # Provider ID
│   │   │   ├── Type               # ProviderType 枚举
│   │   │   ├── BaseUrl            # API 地址
│   │   │   ├── ApiKey             # API 密钥
│   │   │   ├── Timeout            # 超时时间
│   │   │   ├── MaxRetries         # 最大重试
│   │   │   ├── DefaultModel       # 默认模型
│   │   │
│   │   ├── ProviderType           # 枚举
│   │   │   ├── OpenAI = 0
│   │   │   ├── Anthropic = 1
│   │   │
│   │   └── PredefinedProviders    # 预定义 Provider
│   │
│   ├── LlmService.cs              # 服务层（统一管理）
│   │   ├── ILlmService            # 服务接口
│   │   │   ├── GetAvailableModels()
│   │   │   ├── GetModelConfig()
│   │   │   ├── GetClientForModel()
│   │   │   ├── GetClient()
│   │   │   ├── CompleteAsync()
│   │   │   ├── CompleteStreamAsync()
│   │   │   └── TestConnectionAsync()
│   │   │
│   │   └── LlmService             # 实现
│   │       ├── _modelConfigs      # 模型配置缓存
│   │       ├── _clients           # 客户端缓存
│   │       ├── InitializePredefinedModels()
│   │       ├── InitializeClients()
│   │
│   └── Clients/                   # 客户端实现
│       ├── OpenAiClient.cs        # OpenAI SDK v2.2.0
│       │   ├── ChatClient         # SDK 客户端
│       │   ├── BuildMessages()    # 构建消息
│       │   ├── BuildOptions()     # 构建选项
│       │   ├── MapResponse()      # 映射响应
│       │
│       └── AnthropicClient.cs     # Anthropic HTTP API
│           ├── HttpClient         # HTTP 客户端
│           ├── BuildAnthropicRequest()
│           ├── BuildContent()
│           ├── MapResponse()
│           ├── AnthropicResponse  # API 响应模型
│           ├── AnthropicStreamEvent
│
├── Extensions/
│   └── ServiceCollectionExtensions.cs
│       ├── AddSeeingAgent()       # DI 注册入口
│       ├── RegisterLlmServices()  # LLM 服务注册
│       │   ├── ILlmClientFactory → DefaultLlmClientFactory
│       │   └── ILlmService → LlmService
│       │
│       └── DefaultLlmClientFactory  # 工厂实现
│           ├── Create(config)     # 根据类型创建客户端
│           ├── SupportedTypes     # [OpenAI, Anthropic]
│
└── samples/
    └── seeing.json                # 配置文件示例
        ├── Providers              # Provider 配置
        ├── Agents                 # Agent 配置
        ├── Models                 # 模型配置（可选）
```

---

## 3. 数据流转图

### 3.1 配置加载流程

```
┌─────────────────────────────────────────────────────────────────┐
│                    seeing.json 配置文件                          │
│                                                                 │
│  {                                                              │
│    "SeeingAgent": {                                             │
│      "Providers": {                                             │
│        "openai": { Id, Type, BaseUrl, ApiKey, Timeout, ... }   │
│        "anthropic": { ... }                                     │
│      },                                                         │
│      "Models": {                                                │
│        "gpt-4o": { Id, Provider, Capabilities, Limits, ... }   │
│      }                                                          │
│    }                                                            │
│  }                                                              │
└───────────────────────┬─────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────────┐
│              IConfiguration (Microsoft.Extensions)               │
│                                                                 │
│  configuration.GetSection("SeeingAgent")                        │
└───────────────────────┬─────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────────┐
│                  SeeingAgentOptions                              │
│                                                                 │
│  Providers: Dictionary<string, ProviderConfig>                  │
│  Models: Dictionary<string, ModelConfig> (可选)                 │
└───────────────────────┬─────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────────────┐
│                      LlmService                                  │
│                                                                 │
│  初始化阶段:                                                     │
│  1. InitializePredefinedModels() → 加载 PredefinedModels        │
│  2. InitializeClients() → 为每个 Provider 创建 ILlmClient       │
│                                                                 │
│  _modelConfigs: ConcurrentDictionary<string, ModelConfig>       │
│  _clients: ConcurrentDictionary<string, ILlmClient>             │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 请求处理流程

```
┌──────────────────┐
│   调用方代码      │
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
│     → 查找 _modelConfigs                                     │
│     → 返回 ModelConfig { Provider = "openai" }               │
│                                                              │
│  2. GetClient("openai")                                      │
│     → 查找 _clients                                          │
│     → 返回 OpenAiClient                                      │
│                                                              │
│  3. client.CompleteAsync(request)                            │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                      OpenAiClient                             │
│                                                              │
│  1. BuildMessages(request)                                   │
│     → ChatRequest.Messages → List<SdkChatMessage>            │
│     → 系统消息 + 用户消息 + 工具结果                           │
│                                                              │
│  2. BuildOptions(request)                                    │
│     → Temperature, MaxTokens, Tools                          │
│                                                              │
│  3. _client.CompleteChatAsync(messages, options)             │
│     → OpenAI SDK ChatClient                                  │
│                                                              │
│  4. MapResponse(completion)                                  │
│     → ChatCompletion → ChatResponse                          │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                    OpenAI API                                 │
│                                                              │
│  POST https://api.openai.com/v1/chat/completions            │
│                                                              │
│  {                                                           │
│    "model": "gpt-4o",                                        │
│    "messages": [...],                                        │
│    "temperature": 0.7,                                       │
│    "tools": [...]                                            │
│  }                                                           │
└────────┬─────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────┐
│                    ChatResponse                               │
│                                                              │
│  {                                                           │
│    Id: "chatcmpl-xxx",                                       │
│    Model: "gpt-4o",                                          │
│    Message: {                                                │
│      Role: Assistant,                                        │
│      Content: "...",                                         │
│      ToolCalls: [...]                                        │
│    },                                                        │
│    Usage: { InputTokens: 100, OutputTokens: 50 }            │
│  }                                                           │
└──────────────────────────────────────────────────────────────┘
```

### 3.3 流式请求流程

```
┌──────────────────┐     ┌─────────────────┐     ┌──────────────┐
│   调用方代码      │────▶│   LlmService    │────▶│  OpenAiClient│
│                  │     │                 │     │              │
│  await foreach   │     │ GetClientForModel│     │ SDK Stream   │
│  (update in      │◀────│ GetClient()     │◀────│              │
│   .CompleteStream│     │                 │     │ Yield returns│
│   Async(...))    │     │                 │     │              │
└──────────────────┘     └─────────────────┘     └──────────────┘
         │                       │                      │
         │                       │                      │
         ▼                       ▼                      ▼
┌──────────────────────────────────────────────────────────────┐
│                    StreamUpdate                               │
│                                                              │
│  流式迭代过程中:                                               │
│  {                                                           │
│    Id: "chatcmpl-xxx",                                       │
│    ContentDelta: "Hello",     // 每次增量                    │
│    IsComplete: false          // 未完成                      │
│  }                                                           │
│                                                              │
│  最终完成时:                                                   │
│  {                                                           │
│    Id: "chatcmpl-xxx",                                       │
│    ContentDelta: "Hello World", // 完整内容                  │
│    IsComplete: true,                                         │
│    Usage: { InputTokens: 10, OutputTokens: 20 }             │
│  }                                                           │
└──────────────────────────────────────────────────────────────┘
```

### 3.4 客户端创建流程

```
┌──────────────────────────────────────────────────────────────┐
│              DefaultLlmClientFactory                          │
│                                                              │
│  Create(ProviderConfig config)                               │
│  {                                                           │
│    return config.Type switch                                 │
│    {                                                         │
│      ProviderType.OpenAI                                     │
│        => new OpenAiClient(config, logger),                  │
│                                                              │
│      ProviderType.Anthropic                                  │
│        => new AnthropicClient(config, httpClient, logger),   │
│                                                              │
│      _ => throw NotSupportedException                        │
│    };                                                        │
│  }                                                           │
└────────┬─────────────────────────────────────────────────────┘
         │
         ├──────────────────────────┬───────────────────────────┐
         │                          │                           │
         ▼                          ▼                           ▼
┌─────────────────┐    ┌─────────────────────┐    ┌─────────────────┐
│   OpenAiClient  │    │   AnthropicClient   │    │  其他 Provider  │
│                 │    │                     │    │  (可扩展)       │
│  ChatClient     │    │  HttpClient         │    │                 │
│  (SDK 封装)     │    │  (HTTP API)         │    │                 │
└─────────────────┘    └─────────────────────┘    └─────────────────┘
```

---

## 4. 核心接口设计

### 4.1 ILlmClient 接口

```csharp
/// <summary>
/// LLM 客户端接口 - 只负责发送请求和接收响应
/// 不负责模型定义、配置管理、消息转换等工作
/// </summary>
public interface ILlmClient
{
    string ProviderId { get; }
    ProviderType ProviderType { get; }

    // 非流式请求
    Task<ChatResponse> CompleteAsync(
        ChatRequest request, 
        CancellationToken cancellationToken = default);

    // 流式请求
    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken = default);

    // 连接测试
    Task<bool> TestConnectionAsync(
        CancellationToken cancellationToken = default);
}
```

**设计要点**:
- 接口只定义发送请求的方法
- 不包含任何模型列表或能力定义
- `ProviderId` 和 `ProviderType` 用于标识客户端来源

### 4.2 ILlmService 接口

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

    // 请求发送（封装了模型→Provider查找）
    Task<ChatResponse> CompleteAsync(
        string modelId, 
        ChatRequest request, 
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        string modelId,
        ChatRequest request,
        CancellationToken cancellationToken = default);

    // 连接测试
    Task<bool> TestConnectionAsync(
        string providerId, 
        CancellationToken cancellationToken = default);
}
```

**设计要点**:
- `CompleteAsync(modelId, request)` 自动根据模型 ID 找到对应 Provider
- 模型配置和客户端缓存都在服务层管理
- 支持预定义模型 + 配置文件模型合并

---

## 5. 配置文件设计

### 5.1 seeing.json 结构

```json
{
  "SeeingAgent": {
    "DefaultModel": "gpt-4o",
    "DefaultProvider": "openai",
    
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
          "ToolCall": true,
          "Input": { "Text": true, "Image": true, "Audio": true },
          "Output": { "Text": true, "Audio": true }
        },
        "Limits": { "Context": 128000, "Output": 16384 },
        "Pricing": { "Input": 2.5, "Output": 10 }
      }
    },
    
    "Agents": {
      "primary": {
        "Provider": "openai",
        "Model": "gpt-4o",
        "SystemPrompt": "You are a helpful assistant.",
        "MaxSteps": 50,
        "Temperature": 0.7
      }
    }
  }
}
```

### 5.2 配置优先级

```
┌─────────────────────────────────────────────────────────────┐
│                    模型配置来源                              │
│                                                             │
│  优先级: seeing.json > PredefinedModels                     │
│                                                             │
│  1. seeing.json 中的 Models 配置                            │
│     → 用户自定义，可覆盖预定义                               │
│                                                             │
│  2. PredefinedModels 预定义模型                              │
│     → 框架内置，开箱即用                                     │
│                                                             │
│  合并策略:                                                   │
│  - 同 ID 时，seeing.json 覆盖预定义                          │
│  - 不同 ID 时，两者合并                                      │
└─────────────────────────────────────────────────────────────┘
```

---

## 6. 客户端实现对比

### 6.1 OpenAiClient vs AnthropicClient

| 特性 | OpenAiClient | AnthropicClient |
|------|--------------|-----------------|
| 实现方式 | OpenAI SDK v2.2.0 | HttpClient + JSON |
| SDK 依赖 | `OpenAI` 包 | 无（原生 HTTP） |
| 流式响应 | `CompleteChatStreamingAsync` | SSE 手动解析 |
| System 消息 | 在 messages 中 | 单独 `system` 字段 |
| 工具调用 | `ChatToolCall` | `tool_use` block |
| 代码量 | ~260 行 | ~360 行 |

### 6.2 消息格式对比

**OpenAI 格式**:
```json
{
  "model": "gpt-4o",
  "messages": [
    { "role": "system", "content": "..." },
    { "role": "user", "content": "..." },
    { "role": "assistant", "content": "...", "tool_calls": [...] },
    { "role": "tool", "tool_call_id": "xxx", "content": "..." }
  ],
  "tools": [{ "type": "function", "function": { ... } }]
}
```

**Anthropic 格式**:
```json
{
  "model": "claude-sonnet-4",
  "system": "...",
  "messages": [
    { "role": "user", "content": [{ "type": "text", "text": "..." }] },
    { "role": "assistant", "content": [
      { "type": "text", "text": "..." },
      { "type": "tool_use", "id": "xxx", "name": "...", "input": {} }
    ]}
  ],
  "tools": [{ "name": "...", "input_schema": {} }]
}
```

---

## 7. 扩展指南

### 7.1 添加新 Provider

1. 在 `ProviderType` 枚举添加新类型：
```csharp
public enum ProviderType
{
    OpenAI = 0,
    Anthropic = 1,
    Custom = 2  // 新增
}
```

2. 创建客户端实现：
```csharp
public class CustomClient : ILlmClient
{
    public string ProviderId => _config.Id;
    public ProviderType ProviderType => ProviderType.Custom;
    
    public async Task<ChatResponse> CompleteAsync(...)
    {
        // 实现 HTTP/API 调用逻辑
    }
    
    // 其他方法...
}
```

3. 在 `DefaultLlmClientFactory` 添加创建逻辑：
```csharp
public ILlmClient Create(ProviderConfig config)
{
    return config.Type switch
    {
        ProviderType.Custom => new CustomClient(config, ...),
        ...
    };
}
```

### 7.2 添加新模型

在 `PredefinedModels` 中添加：
```csharp
public static readonly Dictionary<string, ModelConfig> OpenAI = new()
{
    ["gpt-5"] = new()
    {
        Id = "gpt-5",
        Name = "GPT-5",
        Provider = "openai",
        Capabilities = new ModelCapabilities { ... },
        Limits = new ModelLimits { Context = 500000, Output = 100000 },
        Pricing = new ModelPricing { Input = 50, Output = 150 }
    }
};
```

或在 `seeing.json` 中配置：
```json
{
  "Models": {
    "gpt-5": {
      "Id": "gpt-5",
      "Provider": "openai",
      "Capabilities": { ... },
      "Limits": { ... }
    }
  }
}
```

---

## 8. 与旧架构对比

### 8.1 架构差异

| 维度 | 旧架构 | 新架构 |
|------|--------|--------|
| Provider 职责 | 定义模型 + 发送请求 | **只发送请求** |
| 模型定义位置 | 硬编码在 Provider | **配置文件** |
| 配置层级 | 3层割裂 | **统一 SeeingAgentOptions** |
| 单个 Provider 代码量 | 700+ 行 | **~200 行** |
| 扩展新 Provider | 重写整个 Provider | **只写 Client** |

### 8.2 旧代码已清理

```
Llm.old/  ← 已备份并删除
├── LlmClient.cs           (700+ 行，已删除)
├── LlmClientOptions.cs    (已删除)
├── OpenAiProvider.cs      (700+ 行，已删除)
├── AnthropicProvider.cs   (已删除)
├── OllamaProvider.cs      (已删除)
└── ...
```

---

## 9. 文件清单

### 9.1 新增文件

| 文件 | 行数 | 说明 |
|------|------|------|
| `Llm/ILlmClient.cs` | 62 | 客户端接口 + 工厂接口 |
| `Llm/LlmModels.cs` | ~120 | 消息模型定义 |
| `Llm/ModelConfig.cs` | 284 | 模型配置 + 预定义 |
| `Llm/ProviderConfig.cs` | ~80 | Provider 配置 |
| `Llm/LlmService.cs` | ~170 | 服务层实现 |
| `Llm/Clients/OpenAiClient.cs` | 262 | OpenAI SDK 封装 |
| `Llm/Clients/AnthropicClient.cs` | 356 | Anthropic HTTP 封装 |
| `samples/seeing.json` | ~70 | 配置示例 |

### 9.2 修改文件

| 文件 | 修改内容 |
|------|----------|
| `Extensions/ServiceCollectionExtensions.cs` | 注册 LlmService |
| `Configuration/SeeingAgentOptions.cs` | 使用 Llm.ProviderConfig |

---

## 10. 测试验证

- **构建**: ✅ 成功（无错误，仅警告）
- **单元测试**: ✅ 30 个全部通过
- **覆盖范围**: 消息模型、配置解析、客户端创建

---

## 11. 使用示例

```csharp
// 1. DI 注册
services.AddSeeingAgent(configuration);

// 2. 获取服务
var llmService = serviceProvider.GetRequiredService<ILlmService>();

// 3. 查询可用模型
var models = llmService.GetAvailableModels();
var gpt4Config = llmService.GetModelConfig("gpt-4o");

// 4. 发送请求
var request = new ChatRequest
{
    Model = "gpt-4o",
    Messages = new List<ChatMessage>
    {
        new() { Role = ChatRole.User, Content = "Hello!" }
    },
    Temperature = 0.7,
    MaxTokens = 100
};

var response = await llmService.CompleteAsync("gpt-4o", request);

// 5. 流式请求
await foreach (var update in llmService.CompleteStreamAsync("gpt-4o", request))
{
    Console.Write(update.ContentDelta);
}
```

---

## 附录：参考项目

- **opencode** (`E:\Projects\Ts\opencode`): LLM 架构设计参考
  - Provider 只负责创建 SDK 客户端
  - Config 模块定义 Provider 和模型配置
  - LLM 模块只负责调用 AI SDK 发送请求