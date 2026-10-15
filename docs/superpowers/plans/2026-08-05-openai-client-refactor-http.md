# OpenAiClient 重构为原生 HTTP 实现（支持三接口）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 `OpenAiClient` 从 OpenAI SDK 2.x 重构为原生 `HttpClient` 实现，拆分为 `OpenAiChatClient`（Chat Completions）+ `OpenAiResponsesClient`（Responses API）+ `OpenAiModelsClient`（List Models），共用 HTTP 和 SSE 解析辅助类。直接解析原始 JSON，彻底解决 SDK 丢弃 `reasoning_content` 的问题。

**Architecture:**
- `OpenAiHttpHelper` — 共用：构建 Base URL、Bearer Auth 头、发送 HttpRequestMessage、SSE 行解析、`data:` / `event:` 分发行
- `OpenAiChatClient : ILlmClient` — Chat Completions API（`POST /v1/chat/completions`），标准 SSE（纯 `data:` 行）
- `OpenAiResponsesClient : ILlmClient` — Responses API（`POST /v1/responses`），语义化 SSE（`event:` + `data:` 双行），映射到 `StreamUpdate`
- `OpenAiModelsClient` — List Models（`GET /v1/models`），非流式 REST

**Tech Stack:** C# 12, `System.Net.Http.HttpClient`, `System.Text.Json`, .NET 10.0

---

## 标准 OpenAI API SSE 流规范（核实后）

### Chat Completions API SSE 格式

**请求：** `POST {base}/chat/completions`
```json
{
  "model": "deepseek-chat",
  "messages": [
    {"role": "system", "content": "..."},
    {"role": "user", "content": "..."},
    {"role": "assistant", "content": "...", "tool_calls": [{"id":"x","type":"function","function":{"name":"f","arguments":"{}"}}]},
    {"role": "tool", "tool_call_id": "x", "content": "result"}
  ],
  "stream": true,
  "max_tokens": 4096,
  "temperature": 0.7,
  "top_p": 1.0,
  "tools": [{"type": "function", "function": {"name": "search", "description": "...", "parameters": {...}}}]
}
```

**SSE chunk（纯 `data:` 行，无 `event:` 行）：**
```
data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":123,"model":"deepseek-chat","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"reasoning_content":"让我思考..."},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"search","arguments":"{\"q\""}}]}},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"completion_tokens_details":{"reasoning_tokens":200}}}

data: [DONE]
```

**关键字段路径：**
| 数据 | 路径 |
|------|------|
| Response ID | `chunk.id` |
| Content delta | `chunk.choices[0].delta.content`（string） |
| Reasoning delta | `chunk.choices[0].delta.reasoning_content`（string，DeepSeek/OpenAI o1） |
| Tool call index | `chunk.choices[0].delta.tool_calls[0].index`（int） |
| Tool call id | `chunk.choices[0].delta.tool_calls[0].id`（首 chunk 带） |
| Tool call fn name | `chunk.choices[0].delta.tool_calls[0].function.name`（首 chunk 带） |
| Tool call fn args | `chunk.choices[0].delta.tool_calls[0].function.arguments`（增量 JSON 片段） |
| Finish reason | `chunk.choices[0].finish_reason`（"stop"\|"tool_calls"\|"length"\|"content_filter"） |
| Usage | 最后一个带 `finish_reason` 的 chunk 中 `chunk.usage` |
| Reasoning tokens | `chunk.usage.completion_tokens_details.reasoning_tokens`（int） |

**非流式 response：**
```json
{
  "id": "chatcmpl-xxx",
  "object": "chat.completion",
  "model": "deepseek-chat",
  "choices": [{
    "index": 0,
    "message": {
      "role": "assistant",
      "content": "最终回答",
      "reasoning_content": "思考过程...",
      "tool_calls": [{"id":"call_1","type":"function","function":{"name":"search","arguments":"{}"}}]
    },
    "finish_reason": "stop"
  }],
  "usage": {
    "prompt_tokens": 10,
    "completion_tokens": 5,
    "total_tokens": 15,
    "completion_tokens_details": {"reasoning_tokens": 200}
  }
}
```

### Responses API SSE 格式

**请求：** `POST {base}/responses`
```json
{
  "model": "gpt-4o",
  "input": [
    {"role": "system", "content": "..."},
    {"role": "user", "content": "..."}
  ],
  "instructions": "系统指令（可选，与 input[role=system] 二选一）",
  "stream": true,
  "max_output_tokens": 4096,
  "temperature": 0.7,
  "top_p": 1.0,
  "tools": [{"type": "function", "name": "search", "description": "...", "parameters": {...}}]
}
```

**SSE chunk（`event:` + `data:` 双行协议）：**
```
event: response.created
data: {"type":"response.created","response":{"id":"resp_xxx","model":"gpt-4o","status":"in_progress"}}

event: response.output_item.added
data: {"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","status":"in_progress"}}

event: response.content_part.added
data: {"type":"response.content_part.added","item_id":"rs_1","output_index":0,"content_index":0,"part":{"type":"reasoning_summary_text","text":""}}

event: response.reasoning_summary_text.delta
data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"content_index":0,"delta":"思考摘要..."}

event: response.content_part.done
data: {"type":"response.content_part.done","item_id":"rs_1","output_index":0,"content_index":0,"part":{"type":"reasoning_summary_text","text":"完整的思考摘要"}}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs_1","status":"completed"}}

event: response.output_item.added
data: {"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","status":"in_progress"}}

event: response.content_part.added
data: {"type":"response.content_part.added","item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":""}}

event: response.output_text.delta
data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":1,"content_index":0,"delta":"你好"}

event: response.function_call_arguments.delta
data: {"type":"response.function_call_arguments.delta","item_id":"msg_1","output_index":1,"call_id":"call_1","delta":"{\"q\""}

event: response.function_call_arguments.done
data: {"type":"response.function_call_arguments.done","item_id":"msg_1","output_index":1,"call_id":"call_1","name":"search","arguments":"{\"q\":\"hello\"}"}

event: response.output_text.done
data: {"type":"response.output_text.done","item_id":"msg_1","output_index":1,"content_index":0,"text":"你好"}

event: response.content_part.done
data: {"type":"response.content_part.done","item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":"你好"}}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"msg_1","status":"completed"}}

event: response.completed
data: {"type":"response.completed","response":{"id":"resp_xxx","model":"gpt-4o","status":"completed","usage":{"input_tokens":10,"output_tokens":5,"output_tokens_details":{"reasoning_tokens":200}}}}
```

**全部事件类型：**
| 事件 | 含义 |
|------|------|
| `response.created` | 响应已创建 |
| `response.in_progress` | 模型开始生成 |
| `response.output_item.added` | 新增输出项（message/reasoning） |
| `response.output_item.done` | 输出项完成 |
| `response.content_part.added` | 新增内容片段 |
| `response.content_part.done` | 内容片段完成 |
| `response.output_text.delta` | 文本增量 |
| `response.output_text.done` | 文本完成（含完整 text） |
| `response.reasoning_summary_part.added` | 推理摘要片段开始 |
| `response.reasoning_summary_text.delta` | 推理摘要增量 |
| `response.reasoning_summary_text.done` | 推理摘要完成 |
| `response.function_call_arguments.delta` | 函数调用参数增量 |
| `response.function_call_arguments.done` | 函数调用参数完成（含 name + arguments） |
| `response.completed` | 响应完成（含 usage） |
| `response.incomplete` | 响应未完成 |
| `response.failed` | 响应失败 |
| `error` | 错误 |

### List Models API

**请求：** `GET {base}/models`

**响应：**
```json
{
  "object": "list",
  "data": [
    {"id": "deepseek-chat", "object": "model", "created": 1686935002, "owned_by": "deepseek"}
  ]
}
```

---

## 变更文件清单

| 操作 | 文件 | 职责 |
|------|------|------|
| 新增 | `src/Seeing.Agent/Llm/Clients/OpenAiHttpHelper.cs` | 共用 HTTP + SSE 解析辅助 |
| 重写 | `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs` → `OpenAiChatClient.cs` | Chat Completions API（ILlmClient） |
| 新增 | `src/Seeing.Agent/Llm/Clients/OpenAiResponsesClient.cs` | Responses API（ILlmClient） |
| 新增 | `src/Seeing.Agent/Llm/Clients/OpenAiModelsClient.cs` | List Models API |
| 新增 | `src/Seeing.Agent/Llm/Clients/OpenAiJsonModels.cs` | 共用 JSON DTO |
| 修改 | `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` | DI 注册新客户端 |
| 修改 | `src/Seeing.Agent/Seeing.Agent.csproj` | 可选：移除 OpenAI NuGet |
| 修改 | `src/Seeing.Agent/Llm/LlmModels.cs` | 扩展 ChatRequest/ChatResponse 支持新字段 |

---

### Task 1: 创建共用 JSON DTO 模型

**Files:**
- Create: `src/Seeing.Agent/Llm/Clients/OpenAiJsonModels.cs`

包含三个 API 的所有请求/响应 DTO（均为 `internal`）。

- [ ] **Step 1: 创建文件**

TODO: 文件包含：

```csharp
using System.Text.Json.Serialization;

namespace Seeing.Agent.Llm.Clients;

#region Chat Completions 请求

internal class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatCompletionMessage> Messages { get; set; } = new();
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("max_tokens")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }
    [JsonPropertyName("temperature")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }
    [JsonPropertyName("top_p")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }
    [JsonPropertyName("tools")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatCompletionTool>? Tools { get; set; }
}

internal class ChatCompletionMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Content { get; set; } // string | null | List<ChatContentPart>
    [JsonPropertyName("tool_call_id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
    [JsonPropertyName("tool_calls")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ChatCompletionToolCall>? ToolCalls { get; set; }
}

internal class ChatContentPart
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }
    [JsonPropertyName("image_url")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatImageUrl? ImageUrl { get; set; }
}

internal class ChatImageUrl
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("detail")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

internal class ChatCompletionTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ChatCompletionFunction? Function { get; set; }
}

internal class ChatCompletionFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("parameters")] public object? Parameters { get; set; }
}

internal class ChatCompletionToolCall
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("function")] public ChatCompletionToolCallFunction? Function { get; set; }
}

internal class ChatCompletionToolCallFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

#endregion

#region Chat Completions 响应

internal class ChatCompletionResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<ChatCompletionChoice>? Choices { get; set; }
    [JsonPropertyName("usage")] public OpenAiUsage? Usage { get; set; }
}

internal class ChatCompletionChoice
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("delta")] public ChatCompletionDelta? Delta { get; set; }
    [JsonPropertyName("message")] public ChatCompletionResponseMessage? Message { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

internal class ChatCompletionDelta
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<ChatCompletionChunkToolCall>? ToolCalls { get; set; }
}

internal class ChatCompletionChunkToolCall
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("function")] public ChatCompletionToolCallFunction? Function { get; set; }
}

internal class ChatCompletionResponseMessage
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<ChatCompletionToolCall>? ToolCalls { get; set; }
}

internal class ChatCompletionChunk
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<ChatCompletionChoice>? Choices { get; set; }
    [JsonPropertyName("usage")] public OpenAiUsage? Usage { get; set; }
}

#endregion

#region Responses API 请求

internal class ResponsesRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public List<ResponsesInputItem> Input { get; set; } = new();
    [JsonPropertyName("instructions")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("max_output_tokens")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }
    [JsonPropertyName("temperature")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }
    [JsonPropertyName("top_p")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }
    [JsonPropertyName("tools")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponsesTool>? Tools { get; set; }
}

internal class ResponsesInputItem
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string? Content { get; set; }
}

internal class ResponsesTool
{
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
    [JsonPropertyName("parameters")] public object? Parameters { get; set; }
}

#endregion

#region Responses API 响应（SSE 事件）

internal class ResponsesStreamEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }

    // response.created / response.completed
    [JsonPropertyName("response")] public ResponsesResponseObj? Response { get; set; }

    // response.output_item.added / response.output_item.done
    [JsonPropertyName("output_index")] public int? OutputIndex { get; set; }
    [JsonPropertyName("item")] public ResponsesOutputItem? Item { get; set; }

    // response.content_part.added / response.content_part.done
    [JsonPropertyName("item_id")] public string? ItemId { get; set; }
    [JsonPropertyName("content_index")] public int? ContentIndex { get; set; }
    [JsonPropertyName("part")] public ResponsesContentPart? Part { get; set; }

    // response.output_text.delta / response.reasoning_summary_text.delta
    [JsonPropertyName("delta")] public string? Delta { get; set; }

    // response.output_text.done / response.reasoning_summary_text.done
    [JsonPropertyName("text")] public string? Text { get; set; }

    // response.function_call_arguments.delta / .done
    [JsonPropertyName("call_id")] public string? CallId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

internal class ResponsesResponseObj
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("usage")] public OpenAiResponsesUsage? Usage { get; set; }
}

internal class OpenAiResponsesUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("output_tokens_details")] public OpenAiOutputTokensDetails? OutputTokensDetails { get; set; }
}

internal class ResponsesOutputItem
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

internal class ResponsesContentPart
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
}

#endregion

#region 共用 Usage 模型

internal class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    [JsonPropertyName("completion_tokens_details")] public OpenAiCompletionTokensDetails? CompletionTokensDetails { get; set; }
}

internal class OpenAiCompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")] public int ReasoningTokens { get; set; }
}

internal class OpenAiOutputTokensDetails
{
    [JsonPropertyName("reasoning_tokens")] public int ReasoningTokens { get; set; }
}

#endregion

#region List Models

internal class ListModelsResponse
{
    [JsonPropertyName("object")] public string? Object { get; set; }
    [JsonPropertyName("data")] public List<ModelInfo>? Data { get; set; }
}

internal class ModelInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("object")] public string? Object { get; set; }
    [JsonPropertyName("created")] public long Created { get; set; }
    [JsonPropertyName("owned_by")] public string? OwnedBy { get; set; }
}

#endregion
```

包含 Chat Completions + Responses + List Models 三套 API 的所有请求/响应 JSON DTO（均为 `internal`）。

- [ ] **Step 2: 编译验证** → `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
- [ ] **Step 3: Commit**

---

### Task 2: 创建共用 HTTP/SSE 辅助类

**Files:**
- Create: `src/Seeing.Agent/Llm/Clients/OpenAiHttpHelper.cs`

**职责：**
- 构建 `HttpClient`（Bearer Auth、自定义 Headers、BaseUrl 处理）
- `ResolveEndpoint(path)` — 返回 `{BaseUrl}/chat/completions` 等
- `PostJsonAsync(url, body)` — POST JSON，返回 `HttpResponseMessage`
- `ReadSseLinesAsync(response)` — 读取 SSE 行流，处理 `data:` 行
- `ReadSseEventsAsync(response)` — 读取 Responses API 的 `event:` + `data:` 双行 SSE

- [ ] **Step 1: 创建文件**

```csharp
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI 兼容接口共用 HTTP/SSE 辅助
/// </summary>
internal static class OpenAiHttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 创建并配置 HttpClient
    /// </summary>
    public static HttpClient CreateHttpClient(ProviderConfig config, ILogger logger)
    {
        var client = new HttpClient();
        var baseUrl = (config.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/');

        client.BaseAddress = new Uri(baseUrl + "/"); // ← 结尾 / 确保相对路径正确
        client.Timeout = TimeSpan.FromMilliseconds(config.Timeout > 0 ? config.Timeout : 300000);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
        client.DefaultRequestHeaders.Add("Accept", "application/json");

        if (config.Headers != null)
        {
            foreach (var (key, value) in config.Headers)
                client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        logger.LogDebug("OpenAI HTTP: BaseAddress={BaseAddress}", client.BaseAddress);
        return client;
    }

    /// <summary>
    /// POST JSON body 并返回响应
    /// </summary>
    public static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient httpClient, string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// GET 请求并返回响应
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient httpClient, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// 读取 Chat Completions 风格的 SSE 流——纯 data: 行，每行一个 JSON chunk
    /// </summary>
    public static async IAsyncEnumerable<ChatCompletionChunk> ReadChatCompletionsSseAsync(
        Stream responseStream,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(responseStream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                yield break;

            ChatCompletionChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(data, JsonOpts);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "SSE chunk JSON 解析失败");
                continue;
            }

            if (chunk != null)
                yield return chunk;
        }
    }

    /// <summary>
    /// 读取 Responses API 风格的 SSE 流——event: + data: 双行
    /// </summary>
    public static async IAsyncEnumerable<ResponsesStreamEvent> ReadResponsesSseAsync(
        Stream responseStream,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(responseStream);
        var jsonOpts = JsonOpts;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            // 期望格式:
            // event: response.output_text.delta
            // data: {"type":"response.output_text.delta",...}
            if (!line.StartsWith("event:", StringComparison.Ordinal))
                continue;

            var eventType = line[6..].Trim();

            // 读 data: 行
            line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                // 空事件体或格式异常
                continue;
            }

            var data = line[5..].TrimStart();
            if (data == "[DONE]")
                continue;

            ResponsesStreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ResponsesStreamEvent>(data, jsonOpts);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Responses SSE 事件 JSON 解析失败: type={EventType}", eventType);
                continue;
            }

            if (evt != null)
                yield return evt;
        }
    }

    /// <summary>
    /// 解析响应 body JSON
    /// </summary>
    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOpts);
}
```

- [ ] **Step 2: 编译验证** → `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
- [ ] **Step 3: Commit**

---

### Task 3: 重写 OpenAiClient → OpenAiChatClient

**Files:**
- Create: `src/Seeing.Agent/Llm/Clients/OpenAiChatClient.cs`（新文件）
- Delete（稍后）: `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs`

- [ ] **Step 1: 创建 OpenAiChatClient**

```csharp
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.Clients;

/// <summary>
/// OpenAI Chat Completions API 客户端（原生 HTTP 实现）
/// 直接解析 SSE 流，正确提取 choices[0].delta.reasoning_content
/// </summary>
public class OpenAiChatClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ProviderConfig _config;

    public string ProviderId => _config.Id;
    public ProviderType ProviderType => ProviderType.OpenAI;

    public OpenAiChatClient(ProviderConfig config, HttpClient httpClient, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = OpenAiHttpHelper.CreateHttpClient(config, logger);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        _logger.LogDebug("ChatCompletions 非流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: false);
        var response = await OpenAiHttpHelper.PostJsonAsync(_httpClient, "chat/completions", body, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var completion = OpenAiHttpHelper.Deserialize<ChatCompletionResponse>(json);
        return MapResponse(completion!, request.Model);
    }

    public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogDebug("ChatCompletions 流式: Model={Model}", request.Model);

        var body = BuildRequest(request, stream: true);
        var response = await OpenAiHttpHelper.PostJsonAsync(_httpClient, "chat/completions", body, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);

        var responseId = string.Empty;
        var streamingTools = new StreamingToolCallAccumulator();
        var streamFinalizeSent = false;
        TokenUsage? lastUsage = null;

        await foreach (var chunk in OpenAiHttpHelper.ReadChatCompletionsSseAsync(stream, _logger, ct))
        {
            if (string.IsNullOrEmpty(responseId) && !string.IsNullOrEmpty(chunk.Id))
                responseId = chunk.Id;

            var delta = chunk.Choices?.FirstOrDefault()?.Delta;
            if (delta != null)
            {
                // reasoning_content delta
                if (!string.IsNullOrEmpty(delta.ReasoningContent))
                {
                    yield return new StreamUpdate { Id = responseId, ReasoningDelta = delta.ReasoningContent };
                }

                // content delta
                if (!string.IsNullOrEmpty(delta.Content))
                {
                    yield return new StreamUpdate { Id = responseId, ContentDelta = delta.Content };
                }

                // tool_calls delta
                if (delta.ToolCalls != null)
                {
                    foreach (var tc in delta.ToolCalls)
                        streamingTools.Append(tc);
                }
            }

            var finishReason = chunk.Choices?.FirstOrDefault()?.FinishReason;
            if (!string.IsNullOrEmpty(finishReason))
            {
                if (chunk.Usage != null)
                {
                    lastUsage = MapUsage(chunk.Usage);
                }

                yield return new StreamUpdate
                {
                    Id = responseId,
                    IsComplete = true,
                    ToolCallDeltas = streamingTools.Build(),
                    Usage = lastUsage
                };
                streamFinalizeSent = true;
            }
        }

        if (!streamFinalizeSent)
        {
            yield return new StreamUpdate
            {
                Id = responseId,
                IsComplete = true,
                ToolCallDeltas = streamingTools.Build(),
                Usage = lastUsage
            };
        }
    }

    public async Task<bool> TestConnectionAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            var req = new ChatRequest { Model = modelId, Messages = new() { new() { Role = ChatRole.User, Content = "Hi" } }, MaxTokens = 5 };
            await CompleteAsync(req, ct);
            return true;
        }
        catch (Exception ex) { _logger.LogError(ex, "Chat Completions 连接测试失败"); return false; }
    }

    #region 请求构建

    private ChatCompletionRequest BuildRequest(ChatRequest request, bool stream)
    {
        var body = new ChatCompletionRequest
        {
            Model = request.Model,
            Stream = stream,
            MaxTokens = request.MaxTokens ?? 4096,
            Temperature = request.Temperature,
            TopP = request.TopP,
            Messages = BuildMessages(request)
        };

        if (request.Tools?.Count > 0)
        {
            body.Tools = request.Tools.Select(t => new ChatCompletionTool
            {
                Type = "function",
                Function = new ChatCompletionFunction
                {
                    Name = t.Function?.Name ?? "",
                    Description = t.Function?.Description,
                    Parameters = t.Function?.Parameters
                }
            }).ToList();
        }

        return body;
    }

    private List<ChatCompletionMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<ChatCompletionMessage>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
            messages.Add(new ChatCompletionMessage { Role = "system", Content = request.SystemPrompt });

        foreach (var msg in request.Messages)
        {
            messages.Add(msg.Role switch
            {
                ChatRole.User => BuildUserMessage(msg),
                ChatRole.Assistant => BuildAssistantMessage(msg),
                ChatRole.Tool => new ChatCompletionMessage
                {
                    Role = "tool",
                    Content = msg.Content,
                    ToolCallId = msg.ToolCallId
                },
                _ => BuildUserMessage(msg)
            });
        }

        return messages;
    }

    private ChatCompletionMessage BuildUserMessage(ChatMessage msg)
    {
        var parts = msg.GetEffectiveParts();
        if (parts.Count == 0)
            return new ChatCompletionMessage { Role = "user", Content = "" };

        if (parts.Count == 1
            && string.Equals(parts[0].Type, ChatContentPart.KindText, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(parts[0].Text))
            return new ChatCompletionMessage { Role = "user", Content = parts[0].Text };

        var contentParts = new List<ChatContentPart>();
        foreach (var p in parts)
        {
            switch (p.Type?.ToLowerInvariant())
            {
                case "text":
                    if (!string.IsNullOrEmpty(p.Text))
                        contentParts.Add(new ChatContentPart { Type = "text", Text = p.Text });
                    break;
                case "image":
                    contentParts.Add(new ChatContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ChatImageUrl
                        {
                            Url = !string.IsNullOrEmpty(p.Url) ? p.Url
                                : !string.IsNullOrEmpty(p.DataBase64) && !string.IsNullOrEmpty(p.MimeType)
                                    ? $"data:{p.MimeType};base64,{p.DataBase64}" : "",
                            Detail = p.ImageDetail?.ToLowerInvariant() switch { "low" => "low", "high" => "high", _ => "auto" }
                        }
                    });
                    break;
                case "file":
                case "document":
                case "input_audio":
                    _logger.LogWarning("ChatCompletions HTTP 暂不支持 {Type} 内容块", p.Type);
                    break;
            }
        }

        return contentParts.Count == 0
            ? new ChatCompletionMessage { Role = "user", Content = msg.Content }
            : new ChatCompletionMessage { Role = "user", Content = contentParts };
    }

    private ChatCompletionMessage BuildAssistantMessage(ChatMessage msg)
    {
        if (msg.ToolCalls?.Count > 0)
        {
            return new ChatCompletionMessage
            {
                Role = "assistant",
                Content = msg.Content,
                ToolCalls = msg.ToolCalls.Select(tc => new ChatCompletionToolCall
                {
                    Id = tc.Id,
                    Type = "function",
                    Function = new ChatCompletionToolCallFunction
                    {
                        Name = tc.Function?.Name ?? "",
                        Arguments = tc.Function?.Arguments ?? "{}"
                    }
                }).ToList()
            };
        }
        return new ChatCompletionMessage { Role = "assistant", Content = msg.Content };
    }

    #endregion

    #region 响应映射

    private ChatResponse MapResponse(ChatCompletionResponse completion, string model)
    {
        var choice = completion.Choices?.FirstOrDefault();
        var msg = choice?.Message;

        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = msg?.Content ?? "",
            ReasoningContent = msg?.ReasoningContent
        };

        if (msg?.ToolCalls?.Count > 0)
        {
            message.ToolCalls = msg.ToolCalls
                .Where(tc => string.Equals(tc.Type, "function", StringComparison.OrdinalIgnoreCase))
                .Select(tc => new ToolCall
                {
                    Id = tc.Id ?? "",
                    Type = "function",
                    Function = new FunctionCall { Name = tc.Function?.Name ?? "", Arguments = tc.Function?.Arguments ?? "{}" }
                }).ToList();
        }

        return new ChatResponse
        {
            Id = completion.Id ?? "",
            Model = completion.Model ?? model,
            Message = message,
            FinishReason = choice?.FinishReason,
            Usage = MapUsage(completion.Usage)
        };
    }

    private static TokenUsage? MapUsage(OpenAiUsage? u)
    {
        if (u == null) return null;
        return new TokenUsage
        {
            InputTokens = u.PromptTokens,
            OutputTokens = u.CompletionTokens,
            ReasoningTokens = u.CompletionTokensDetails?.ReasoningTokens ?? 0
        };
    }

    #endregion

    #region 流式工具调用聚合

    private sealed class StreamingToolCallAccumulator
    {
        private readonly Dictionary<int, (string Id, string Name, StringBuilder Args)> _byIndex = new();

        public void Append(ChatCompletionChunkToolCall tc)
        {
            if (!_byIndex.TryGetValue(tc.Index, out var slot))
            {
                slot = ("", "", new StringBuilder());
                _byIndex[tc.Index] = slot;
            }
            if (!string.IsNullOrEmpty(tc.Id)) slot.Id = tc.Id;
            if (!string.IsNullOrEmpty(tc.Function?.Name)) slot.Name = tc.Function.Name;
            if (!string.IsNullOrEmpty(tc.Function?.Arguments)) slot.Args.Append(tc.Function.Arguments);
        }

        public List<ToolCall>? Build()
        {
            if (_byIndex.Count == 0) return null;
            var list = new List<ToolCall>();
            foreach (var kv in _byIndex.OrderBy(x => x.Key))
            {
                if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                list.Add(new ToolCall
                {
                    Id = kv.Value.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name = kv.Value.Name,
                        Arguments = kv.Value.Args.Length > 0 ? kv.Value.Args.ToString() : "{}"
                    }
                });
            }
            return list.Count > 0 ? list : null;
        }
    }

    #endregion
}
```

- [ ] **Step 2: 编译验证** → `dotnet build src/Seeing.Agent/Seeing.Agent.csproj`
- [ ] **Step 3: Commit**

---

### Task 4: 创建 OpenAiResponsesClient

**Files:**
- Create: `src/Seeing.Agent/Llm/Clients/OpenAiResponsesClient.cs`

Responses API 的输入/输出模型与 Chat Completions 不同，但都映射到统一的 `ChatRequest` → `ChatResponse`/`StreamUpdate`：

| Responses 概念 | 映射到 |
|---------------|--------|
| `input[]` | `ChatRequest.Messages` |
| `instructions` | `ChatRequest.SystemPrompt` |
| `response.output_text.delta` | `StreamUpdate.ContentDelta` |
| `response.reasoning_summary_text.delta` | `StreamUpdate.ReasoningDelta` |
| `response.function_call_arguments.delta/.done` | `StreamUpdate.ToolCallDeltas` |
| `response.completed.usage` | `StreamUpdate.Usage` |

- [ ] **Step 1: 创建文件**

实现与 `OpenAiChatClient` 相同的 `ILlmClient` 接口，但请求体用 Responses API 格式，流式解析用 `event:` + `data:` 双行协议。关键差异：
- `CompleteAsync` → 发 POST `/responses`，非流式
- `CompleteStreamAsync` → 发 POST `/responses`，`stream: true`，读取 `event:`/`data:` 双行 SSE
- 事件路由：`response.output_text.delta` → `ContentDelta`，`response.reasoning_summary_text.delta` → `ReasoningDelta`，`response.function_call_arguments.delta` → 聚合 tool_calls，`response.completed` → `IsComplete = true`

- [ ] **Step 2: 编译验证**
- [ ] **Step 3: Commit**

---

### Task 5: 创建 OpenAiModelsClient

**Files:**
- Create: `src/Seeing.Agent/Llm/Clients/OpenAiModelsClient.cs`

```csharp
/// <summary>
/// List Models API 客户端（GET /v1/models）
/// </summary>
public class OpenAiModelsClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public OpenAiModelsClient(ProviderConfig config, ILogger logger)
    {
        _httpClient = OpenAiHttpHelper.CreateHttpClient(config, logger);
        _logger = logger;
    }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var response = await OpenAiHttpHelper.GetAsync(_httpClient, "models", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var result = OpenAiHttpHelper.Deserialize<ListModelsResponse>(json);
        return result?.Data ?? new();
    }
}
```

- [ ] **Step 2: 编译验证**
- [ ] **Step 3: Commit**

---

### Task 6: 更新 DI 注册

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 修改工厂类**

`DefaultLlmClientFactory` 需要能创建两种 `ILlmClient`——`OpenAiChatClient` 和 `OpenAiResponsesClient`。需要一种方式区分。选择方案：在 `ProviderConfig` 中新增一个可选字段 `ApiStyle`（`"chat"` | `"responses"`），默认为 `"chat"`。

若不做这个区分，可以新增一个 `ProviderType` 枚举值如 `OpenAIResponses`。但更简单的做法是先只在 `ProviderType.OpenAI` 下创建 `OpenAiChatClient`，Responses API 单独注册。

```csharp
// 修改第 733-736 行
ProviderType.OpenAI => new OpenAiChatClient(
    config,
    _httpClientFactory.CreateClient("LlmClient"),
    _loggerFactory.CreateLogger<OpenAiChatClient>()),
```

- [ ] **Step 2: 编译验证**
- [ ] **Step 3: Commit**

---

### Task 7: 删除旧的 SDK 依赖和旧文件

**Files:**
- Delete: `src/Seeing.Agent/Llm/Clients/OpenAiClient.cs`
- Modify: `src/Seeing.Agent/Seeing.Agent.csproj`（移除 `OpenAI` PackageReference）

- [ ] **Step 1: 删除旧文件**
- [ ] **Step 2: 移除 NuGet 引用**
- [ ] **Step 3: 编译验证** → `dotnet build`
- [ ] **Step 4: Commit**

---

### Task 8: 回归验证

- [ ] **Step 1: 运行全部测试** → `dotnet test`
- [ ] **Step 2: 更新/删除不再相关的单元测试（如 OpenAiClientReasoningTests 中对 SDK Patch 的依赖）**

---

## Self-Review

**1. Spec coverage:**
- ✅ Chat Completions 流式 reasoning delta → Task 3（直接从 delta.reasoning_content 读）
- ✅ Chat Completions 非流式 reasoning → Task 3（直接从 message.reasoning_content 读）
- ✅ Chat Completions tool_calls（流式+非流式）→ Task 3
- ✅ Chat Completions 多模态 → Task 3
- ✅ Responses API 流式（语义化 SSE）→ Task 4
- ✅ Responses API 非流式 → Task 4
- ✅ List Models → Task 5
- ✅ reasoning_tokens（两种 API 各自路径）→ Tasks 3 & 4
- ✅ 共用辅助类 → Task 2
- ✅ JSON DTO 模型 → Task 1

**2. Placeholder scan:** Task 4（ResponsesClient）只有架构描述，具体代码需按描述完整写出。无 TBD。

**3. Type consistency:**
- `StreamUpdate.ReasoningDelta` ← ChatCompletions `delta.reasoning_content` / Responses `response.reasoning_summary_text.delta` ✅
- `ChatMessage.ReasoningContent` ← ChatCompletions `message.reasoning_content` / Responses `output_item[type=reasoning]` ✅
- `TokenUsage.ReasoningTokens` ← `completion_tokens_details.reasoning_tokens` / `output_tokens_details.reasoning_tokens` ✅
