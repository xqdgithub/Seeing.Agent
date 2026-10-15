# LLM Provider 抽象层重设计

**日期:** 2026-09-09
**状态:** 已批准（用户授权快速实现，关键点审查）
**目标框架:** .NET 10.0

## 1. 背景与驱动

OpenCode Zen 免费层 `MissingSessionID` 暴露了 LLM Provider 抽象层的架构缺口：`ILlmClient` 只收 `ChatRequest`、看不到调用期身份（SessionId）；`chat.headers` Hook 在非流式触发后未应用、流式根本未触发（死债）；无 provider 作用域的出站塑形扩展点；重试硬编码在 `LlmService`。

驱动：**前瞻**——为更多品牌网关/协议（含完全自定义 `ILlmClient`）做可扩展底座。

## 2. 三层职责

| 层 | 载体 | 职责 | 不做什么 |
|----|------|------|----------|
| 协议层 | `ILlmClient`（OpenAI/Anthropic/自定义） | 构建模型请求体 + 经 HttpClient 发送；纯协议 | 不感知横切编排 |
| 出站塑形层 | `LlmCallContext` + `ILlmCallInterceptor` + `OutboundPipeline` | 按调用注入头/选项；provider 作用域 | 不碰重试/日志 |
| 横切层 | `ILlmClient` 装饰器（镜像 `IToolDecoratorRegistry`） | 重试、上下文传播 | 不构建请求体 |

## 3. 契约（Abstractions，协议无关）

```csharp
public sealed class LlmCallContext {
    public string? SessionId { get; init; }
    public string? CorrelationId { get; init; }
    public string? ParentSessionId { get; init; }
    public Dictionary<string,string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string,object?> Items { get; } = new(StringComparer.Ordinal);
}

public sealed class LlmOutboundRequest {
    public required string ProviderId { get; init; }
    public required string ProviderType { get; init; }
    public LlmCallContext? Call { get; init; }
    public IDictionary<string,string> Headers { get; }
}

public interface ILlmCallInterceptor {
    int Order { get; }
    bool AppliesTo(string providerId, string providerType);
    void OnSending(LlmOutboundRequest request);
}

public interface ILlmCallInterceptorRegistry {
    IReadOnlyList<ILlmCallInterceptor> Resolve(string providerId, string providerType);
    void Register(ILlmCallInterceptor interceptor);
}

// 协议无关 helper：HTTP 客户端构造 HttpRequestMessage 后、发送前调用
public static class OutboundPipeline {
    public static void Apply(
        HttpRequestMessage msg, LlmCallContext? call,
        IReadOnlyList<ILlmCallInterceptor> interceptors,
        string providerId, string providerType);
}
```

`ILlmClient` 签名：`CompleteAsync(ChatRequest, LlmCallContext? call = null, CancellationToken)`；流式同理。`TestConnectionAsync` 同样加可选 `call`。**不修改** `ChatRequest` 形状。

## 4. 横切装饰器

```csharp
public interface ILlmClientDecorator {
    ILlmClient Wrap(ILlmClient inner);
}
public interface ILlmClientDecoratorRegistry {
    ILlmClient Apply(ILlmClient client, ProviderConfig config);
    void Register(ILlmClientDecorator decorator);
}
```

镜像 [`IToolDecoratorRegistry`](src/spine/Seeing.Agent.Core/Decorators/ToolDecoratorRegistry.cs)。工厂创建 client 后由 registry 包装。

**重试迁移**：`RetryLlmClientDecorator` 承接 `LlmService` 现有 attempt 循环 + `IsRetryableException` + 退避。
- 流式：`CompleteStreamAsync` 内 `await foreach` 内层流；异常时**未 yield 任何 chunk**则重试从头，**已 yield**则直接抛（避免重复吐 chunk，与现状语义一致）。
- `MaxRetries` 来自 `ProviderConfig.MaxRetries`。
- `IsRetryableException` 迁到 Abstractions 共享工具（`LlmRetryPolicy` 静态类）。

## 5. hook 解耦

`chat.on_error` 现带 `attempt/willRetry`。迁移后：
- 装饰器写 `Call.Items["retry.attempt"]`、`["retry.willRetry"]`
- `LlmService` 捕获最终异常时读 `Call.Items` 触发 `chat.on_error`，payload 形状不变
- hook 留在编排层，重试在横切层，经 `Call.Items` 解耦

`chat.headers` 死债修复：`LlmService` 流式/非流式**两端统一触发** + 产出合并进 `call.ExtraHeaders`；协议客户端经 `OutboundPipeline` 应用。hook 复活为「通用、多订阅、全 provider」头贡献点。

## 6. 端到端数据流

```
AgentExecutor(context.SessionId)
  → LlmService.CompleteStreamAsync(modelId, request, sessionId)
     1. 解析 modelConfig + client（装饰后）
     2. 构造 LlmCallContext{SessionId, ParentSessionId, CorrelationId}
     3. chat.before_start / chat.params / chat.headers（阻塞）→ 合并 ExtraHeaders
     4. llm.system_prompt
     5. client.CompleteStreamAsync(request, call, ct)
        → RetryDecorator：attempt 循环，写 Call.Items[retry.*]
        → OpenAiChatClient：构造 HttpRequestMessage
           → OutboundPipeline.Apply(msg, call, interceptors, providerId, type)
              静态 Headers → call.ExtraHeaders → 拦截器 OnSending → 写本次 msg.Headers
           → Send
     6. chat.message / chat.after_complete；异常 → chat.on_error（读 Call.Items[retry.*]）
```

出站顺序：静态 `ProviderConfig.Headers` → `call.ExtraHeaders`（通用 hook）→ 拦截器（provider 专属），最具体者胜。

## 7. OpenCodeZen（首个消费者）

- `OpenCodeZenCallInterceptor`：`AppliesTo` 仅 `opencode-zen`；`OnSending` 设 `x-opencode-session = call.SessionId?.Trim() ?? fallback`
- `CreateClient` 静态头仅留 `User-Agent`、`x-opencode-client`；**移除**静态 `x-opencode-session` 与进程 GUID 常量
- Module 注册拦截器到 `ILlmCallInterceptorRegistry`

## 8. 错误处理

- 拦截器 `OnSending` 抛异常 → 装饰器按 `IsRetryableException` 决定重试；不可重试则上抛
- `OutboundPipeline.Apply` 内拦截器异常不吞，向上冒泡
- 无 `call` / 无拦截器：行为与现状一致（向后兼容）
- 自定义客户端不调 `OutboundPipeline`：不塑形，仍可工作（opt-in）

## 9. 测试

- 无 call / 无拦截器：行为一致
- 拦截器读 `call.SessionId`；`AppliesTo` 假不被调用
- OpenCodeZen：有/无 SessionId 头值；静态头不含 session；无 SessionId 回退非空
- `chat.headers`：流式与非流式均送到出站
- 注册表：按 `Order` 排序、`AppliesTo` 过滤
- 重试装饰器：可重试异常重试 N 次；流式已 yield 不重试；`Call.Items[retry.*]` 写入
- `chat.on_error`：读 `Call.Items[retry.*]` 还原 payload
- 编译期：所有 `ILlmClient` 实现已更新（签名变更兜底）

## 10. 预留缝（文档化、不实现）

- `ILlmResponseInterceptor`（入站响应/流）
- `ILlmRetryPolicy`（按 provider 重试策略，超出 `MaxRetries`）
- `ChatRequest.ProviderOptions`（body 选项袋，避免字段 sprawl）

## 11. 非目标

- 不把编排字段塞进 `ChatRequest`
- 不动 `ChatRequest.SystemPrompt` 的 `[JsonIgnore]` 历史污染（独立任务）
- 不做按 Session 拆 HttpClient
- 不动 agent 执行层 `IExecutionMiddleware`（logging/permission/retry）

## 12. 文件

| 路径 | 职责 |
|------|------|
| `src/abstractions/.../Llm/LlmCallContext.cs` | 调用上下文 |
| `src/abstractions/.../Llm/ILlmCallInterceptor.cs` + `LlmOutboundRequest` | 出站拦截器 |
| `src/abstractions/.../Llm/ILlmCallInterceptorRegistry.cs` | 拦截器注册表 |
| `src/abstractions/.../Llm/OutboundPipeline.cs` | 协议无关 helper |
| `src/abstractions/.../Llm/ILlmClient.cs` | 签名加 `LlmCallContext?` |
| `src/abstractions/.../Llm/LlmRetryPolicy.cs` | `IsRetryableException` 共享 |
| `src/abstractions/.../Llm/ILlmClientDecorator*.cs` | 装饰器契约 |
| `src/spine/.../Llm/LlmCallInterceptorRegistry.cs` | 注册表实现 |
| `src/spine/.../Llm/LlmClientDecoratorRegistry.cs` | 装饰器注册表实现 |
| `src/spine/.../Llm/RetryLlmClientDecorator.cs` | 重试装饰器 |
| `src/spine/.../Llm/LlmService.cs` | 构造 Call、修 chat.headers、移除内嵌重试 |
| `src/capabilities/.../Llm.OpenAI/Clients/*` | 签名 + OutboundPipeline |
| `src/capabilities/.../Llm.Anthropic/Clients/AnthropicClient.cs` | 签名 + OutboundPipeline |
| `plugs/.../OpenCodeZenCallInterceptor.cs` | 首个消费者 |
| `plugs/.../OpenCodeZenProvider.cs` | 移除静态 session 头 |
| `tests/**` | 镜像改动 |
