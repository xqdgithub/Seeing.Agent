# 工具产出图片与内容段（Tool Image Parts）设计

**日期：** 2026-11-11
**状态：** 设计已确认，待实施
**触发问题：** 「agent 能不能内嵌浏览器工作台并操作它？」→ 拆出前置问题「工具怎么把图片交给模型」

---

## 1. 问题

### 1.1 今天任何工具都回不了图片给模型

`ToolResult` 上有 `Attachments`（`FileAttachment`），但整条链路上**无人消费**：

| 环节 | 是否读取 `Attachments` |
|---|---|
| `ToolCallInvoker` → `ToolCallEvent` | ❌ |
| `ChatEventTracker` → `SessionToolCall` | ❌ |
| `SessionHistoryBuilder` → `ChatMessage` | ❌ |
| `AgentExecutor.ForwardToolEventsAsync` → `history` | ❌ |

唯一写入点是 `ReadTool.ReadBinaryFile`，唯一断言在 `ReadToolFileSystemTests`。**死字段**。

后果：`read` 一张 PNG，模型只收到 `"图片 读取成功"` + metadata，**看不到图**。

### 1.2 浏览器工作台依赖这个能力

要让 agent「打开网页、看懂页面、点击操作」，至少需要：

- 截图进模型（视觉判断布局/样式/图表）
- 无障碍树进模型（元素定位）

无障碍树是文本，走 `Output` 即可。截图是二进制，**必须**先解决 1.1。

---

## 2. 目标与非目标

### 目标

1. 工具能把图片/文件交给模型，两个 provider 都能正确出站。
2. 会话文件不因图片而膨胀（工具图片只存路径）。
3. `ChatContentPart`（LLM 契约）不出现本地路径概念。
4. 跨轮历史重建与本轮活事件两条路径行为一致。
5. 顺带修好 `read` 读图与死字段 `Attachments`。

### 非目标

| 项 | 原因 |
|---|---|
| 图片大小/数量/像素限制 | 多模态模型能力差异大，硬声明会绑死能力；仓库当前也无任何模型侧图片限制（用户粘贴上限 10MB 是**上传**约束，不是模型约束） |
| 事后图像压缩/降采样 | 会引入图像编码依赖（仓库当前零依赖），且模型无从知道「图被降过质」，会把伪影当页面缺陷 |
| 浏览器工作台 | 独立后续工作，本期只做前置能力 |
| 音频/文档段的对称支持 | `ChatContentPart` 已支持，本期不测不改 |

---

## 3. 核心设计

### 3.1 三层字段分布：路径只在下两层

| 层 | 类型 | 图片来源字段 |
|---|---|---|
| 工具产出 | `ToolOutputPart`（新） | `LocalPath` |
| 会话存储 | `SessionContentPart` | `LocalPath`（新增，与 `Url` / `DataBase64` 并列） |
| LLM 契约 | `ChatContentPart` | **不变**：`Url` / `DataBase64` / `FileId` |

`ChatContentPart` 一个字段都不加。`LocalPath` 是会话存储的物理概念，出现在 LLM 契约里会让每个 client 各自实现「读磁盘 → base64」，N 个 provider 就是 N 份重复。

### 3.2 物化点：Session/ToolEvent → Chat 的转换处

物化（读盘 → base64）发生在**两处转换**，共用同一个物化器：

| 转换 | 位置 | 输入 |
|---|---|---|
| 历史重建 | `SessionHistoryBuilder.BuildHistory`（`SessionContentPart → ChatContentPart` 已在做） | `SessionContentPart` |
| 本轮活事件 | `AgentExecutor.ForwardToolEventsAsync`（构造 tool 消息） | `ToolCallEvent.Parts` |

**为什么两处都要**：本轮工具刚产出的图片还没有经过会话重建，直接从事件进 `history`。只在历史处物化会漏掉本轮，只在事件处物化会漏掉历史。

**为什么不放 `LlmService.PrepareRequest`**：那里虽然在两条路径下游，但它是「出站归一化」，让 `ChatContentPart` 短暂持有未物化的路径会让契约含义变得依赖执行时机。转换点物化后契约恒为「可直接发送」，语义更窄。

### 3.3 物化器

```csharp
// src/spine/Seeing.Agent.Core/Llm/IImagePartResolver.cs
// public 而非 internal：Hosting 的 SessionHistoryBuilder 也要用，
// 而 Core 的 InternalsVisibleTo 只对测试程序集开放（Core → Hosting 是反向依赖，不可能开放）。
/// <summary>把会话/工具侧的本地产出物化为可直接出站的载荷。唯一实现：<see cref="ImagePartResolver"/>。</summary>
public interface IImagePartResolver
{
    ChatContentPart Resolve(SessionContentPart part);
    ChatContentPart Resolve(ToolOutputPart part);
}
```

实现 `ImagePartResolver`：

| 项 | 决定 |
|---|---|
| 生命周期 | Singleton |
| 依赖 | `IFileSystem`（不直接 `File.*`） |
| 缓存 | 进程内 LRU，键 = `(规范化路径, LastWriteTimeUtc)`，容量可配（默认 32），配置节 `SeeingAgentOptions.ImageParts.CacheCapacity`，热重载有效 |
| 注册 | `AddSeeingCore` 内 `services.AddSingleton<IImagePartResolver, ImagePartResolver>()` |
| 同步性 | **同步**（LRU 命中为常数开销；miss 时几 MB 一次性读） |
| 文件缺失/不可读 | 返回 `ChatContentPart.CreateText("[图片不可用：{path}]")`，**不抛** |
| 非图片 MIME | 走 file 段（`CreateFileFromBase64`） |

**为什么同步**：`SessionHistoryBuilder.BuildHistory` 是 `static` 同步纯函数，被 6 处测试直接调用；改异步签名波及 `ExecutionRunner` 与全部相关测试，收益不足以抵消。LRU 使稳态开销可忽略。

**为什么不抛缺失**：一张历史图片被用户删掉，不该让整轮对话失败。降级为文本占位让模型知道「这里原本有图」，并能自己决定下一步。

### 3.4 会话只存路径（与「无限额」正交）

会话文件是**整文件重写**，且每次工具状态迁移都触发：

```csharp
// ChatEventTracker.ApplyEvent — ToolCallEvent 分支
var statusChanged = !string.Equals(previousStatus, existing.Status, StringComparison.OrdinalIgnoreCase);
return isNewToolCall || statusChanged || taskIdFilled;
```

若 base64 进 `SessionToolCall`，则「10 张 2MB 截图 = 27MB 会话文件，每次状态变化重写一遍」。这不是「图太大」的问题，是**写放大**问题——无论上限设多少都成立。

落盘位置：会话 ref 目录（`{sessionId}.ref/`），复用 `SessionToolOutputStore` 的实现（已有原子写、超时、随会话删除）。

### 3.5 用户附件改引用式

当前链路会**删掉文件**再传 base64：

```csharp
// UploadedFileService.CreateImageAttachmentAsync
var bytes = await workspace.ReadBytesAsync(absolute, ct).ConfigureAwait(false);
try { await workspace.DeleteAsync(absolute, ct).ConfigureAwait(false); }
catch { /* .seeing 已忽略 */ }
...
Base64Content = $"data:{item.MimeType};base64,{Convert.ToBase64String(bytes)}",
```

改为：**保留文件**，返回 `LocalPath`。`ChatAttachment` 加同名字段，`SessionRuntime.BuildContentParts` 优先用它。

存量行为保留：`ChatAttachment.Base64Data` 不为空时仍走原路（Gateway 远端、TUI 剪贴板等）。

---

## 4. 契约变更

### 4.1 新增 `ToolOutputPart`

```csharp
// src/abstractions/Seeing.Agent.Abstractions/Tools/ToolOutputPart.cs
public static class ToolOutputPartKind
{
    public const string Text = "text";
    public const string Image = "image";
    public const string File = "file";
}

/// <summary>工具产出的内容块。框架负责在 Session→Chat 转换时物化，如何出站由各 client 决定。</summary>
public sealed record ToolOutputPart
{
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public string? LocalPath { get; init; }
    public string? DataBase64 { get; init; }
    public string? Url { get; init; }
    public string? MimeType { get; init; }
    public string? Name { get; init; }
}
```

### 4.2 四处新增字段

| 类型 | 新增 |
|---|---|
| `ToolResult` | `IReadOnlyList<ToolOutputPart>? Parts` |
| `ToolCallEvent` | `IReadOnlyList<ToolOutputPart>? Parts` |
| `SessionToolCall` | `ImmutableList<SessionContentPart>? Parts` |
| `SessionContentPart` | `string? LocalPath` + `CreateImageFromPath(path, mime, name)` |

`SessionToolCall.Parts` 用 `SessionContentPart`：它在原语层，不能引用 Abstractions；且 `SessionHistoryBuilder` 已有 `SessionContentPart → ChatContentPart` 的映射，跨轮重建只补一个字段赋值。

### 4.3 死字段处置

`ToolResult.Attachments`（`FileAttachment`）标 `[Obsolete("改用 Parts")]`，保留一版。Abstractions 是 NuGet 包，直接删属破坏性变更；它当前无人消费，标注即可。

`ReadTool.ReadBinaryFile` 改为填 `Parts`。

---

## 5. 数据流

```
工具
  └─ ToolResult { Output: "截图成功", Parts: [{ Kind=image, LocalPath=<ref dir>/shot.png }] }
       ↓ ToolCallInvoker（透传，不读盘、不限制）
     ToolCallEvent { Output, Parts }
       ├─→ ChatEventTracker → SessionToolCall.Parts = [SessionContentPart{ LocalPath }]
       │     （会话文件只有几十字节增量的路径）
       └─→ AgentExecutor.ForwardToolEventsAsync
             └─ IImagePartResolver.Resolve(part) → ChatContentPart{ DataBase64 }
                  ↓ history
                ChatRequest.Messages
                  ↓
             LlmService.PrepareRequest（仅模型归一化，不碰图片）
                  ↓
        OpenAiChatClient / AnthropicClient（只看到 DataBase64，直接出站）
```

跨轮：`SessionHistoryBuilder.BuildHistory(session, resolver)` 把 `SessionToolCall.Parts` 与消息 `Parts` 一并物化——前者物化后作为 **tool 消息的 `Parts`**，后者物化后作为消息自身的 `Parts`。

### 5.1 依赖注入

`ImagePartResolver` 在 `AddSeeingCore` 注册为 Singleton。两个使用点：

| 使用点 | 注入方式 |
|---|---|
| `SessionHistoryBuilder.BuildHistory` | 由 `ExecutionRunner` 从 `IServiceProvider` 取，作为参数传入（保持 method 的纯函数性，测试可传 null 表示不物化） |
| `AgentExecutor.ForwardToolEventsAsync` | `AgentExecutor` 构造器新增可选参数 `IImagePartResolver? imageParts`（null = 不物化，仅旧测试路径） |

`ForwardToolEventsAsync` 现在是 `static`，需要改为实例方法或把 resolver 作为参数传入。选后者：保持 static，签名加一个参数。

---

## 6. 两个 provider 的出站形状

框架只保证 `ChatContentPart` 携带可用载荷，出站由 client 决定：

| client | 做法 | 位置 |
|---|---|---|
| `OpenAiChatClient` | `role:"tool"` 只允许文本 → 拆成 tool 消息（`ModelContent`）+ 紧随的 user 消息（`image_url`） | `BuildMessages` 的 `ChatRole.Tool` 分支 |
| `AnthropicClient` | `tool_result.content` 原生接受 block 数组 → `[text, image]` 内嵌，不额外造消息 | `BuildContent` 的 `ToolCallId` 分支 |

同语义、不同形状，各自最优。

---

## 7. 交付清单

| # | 内容 | 位置 |
|---|---|---|
| 1 | `SessionContentPart.LocalPath` + `CreateImageFromPath` + `Clone()` 补字段 | `src/primitives/Seeing.Session/Core/SessionMessage.cs` |
| 2 | `ToolOutputPart` + `ToolResult.Parts` + `ToolCallEvent.Parts` + `Attachments` 标 Obsolete | `src/abstractions/Seeing.Agent.Abstractions/Tools/`、`Events/` |
| 3 | `ToolCallInvoker` 透传 | `src/spine/Seeing.Agent.Core/Core/ToolCallInvoker.cs` |
| 4 | `SessionToolCall.Parts` + `ChatEventTracker` 映射 + `Clone()` | `src/primitives/.../SessionMessage.cs`、`src/hosting/.../Execution/ChatEventTracker.cs` |
| 5 | `IImagePartResolver` + `ImagePartResolver`（LRU）+ DI 注册 | `src/spine/Seeing.Agent.Core/Llm/` |
| 6 | `SessionHistoryBuilder.BuildHistory(session, resolver)` | `src/hosting/.../Execution/SessionHistoryBuilder.cs` |
| 7 | `AgentExecutor.ForwardToolEventsAsync` 接物化器（签名加参数，保持 static） | `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs` |
| 8 | `ReadTool.ReadBinaryFile` 改返回 `Parts` | `src/capabilities/Seeing.Agent.Tools.FileSystem/ReadTool.cs` |
| 9 | 用户附件改引用式 | `samples/Seeing.Agent.WebUI/Services/UploadedFileService.cs`、`src/abstractions/.../Models/ChatInput.cs`、`src/hosting/.../SessionRuntime.cs` |
| 10 | WebUI 工具卡渲染缩略图 | `samples/Seeing.Agent.WebUI/Components/Messaging/ToolCallMessageComponent.razor` |
| 11 | 测试 | 见 §8 |

---

## 8. 测试

| 套件 | 钉住什么 |
|---|---|
| `ToolOutputPartTests` | 契约往返；`Kind` 常量唯一 |
| `ImagePartResolverTests` | LRU 命中（同一路径第二次不读盘）；文件修改后失效；文件缺失降级为文本段；非图片 MIME 走 file 段 |
| `SessionHistoryBuilderTests`（改） | `SessionContentPart{LocalPath}` → `ChatContentPart{DataBase64}`；缺失图降级不抛 |
| `AgentExecutor` 相关（改） | 本轮工具图片直接进 `history`，不经过会话重建 |
| `ToolCallPersistenceTests`（新） | `ToolCallEvent.Parts` → `SessionToolCall.Parts` 落盘往返；`Clone()` 不丢字段 |
| `OpenAiChatClient` / `AnthropicClient`（改） | 各自出站形状：OpenAI 拆 tool+user；Anthropic 内嵌 `tool_result` |
| `ReadToolFileSystemTests`（改） | 读 PNG 返回 `Parts`，`Attachments` 不再使用 |

守门断言：**任何 client 收到的 `ChatContentPart` 都不含 `LocalPath`**（该字段不存在于 `ChatContentPart`，编译期即保证）。

---

## 9. 已知边界

| 项 | 说明 |
|---|---|
| 无图片大小限制 | 模型不支持时由 `LlmRouter` 的 `Modalities` 层拒绝；不额外拦截 |
| 物化为同步读盘 | LRU 命中即常数；miss 时几 MB 同步阻塞。会话图片极多时可能可见 |
| 缓存容量默认 32 | 超过即按 LRU 淘汰；每轮历史含 30+ 图的长会话会持续 miss。可通过配置调大 |
| 会话 ref 目录随会话删除 | `SessionToolOutputStore.DeleteSessionRefDirectory` 已实现；删除会话即清图 |
| 存量会话 | 无 `Parts` 的旧消息行为完全不变 |
| Gateway 远端图片 | 走 `ChatAttachment.Base64Data` 原路，不经 `LocalPath` |
| `ToolResult.Attachments` | 标 Obsolete 保留一版，下期删除 |
| `ReadTool` 单文件只回 1 张图 | 一期不扩为多图（目录递归读图不是 `read` 的语义） |
| 会话 ref 目录不随会话压缩清理 | 压缩后图片消息仍在，只是不再进历史；文件保留至会话删除 |

---

## 10. 与浏览器工作台的关系

本期交付后，浏览器工作台的截图能力即可建立：

```
browser_screenshot 工具
  → 落盘到会话 ref 目录
  → ToolResult.Parts = [{ Kind=image, LocalPath }]
  → 模型看到截图
```

无障碍树走 `Output`（文本）。两件事都不需要额外机制。
