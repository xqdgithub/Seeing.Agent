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

| 转换 | 位置 | 输入 | 现状 |
|---|---|---|---|
| 历史重建 | `SessionHistoryBuilder.BuildHistory`（`SessionContentPart → ChatContentPart` 映射在 `:62-70`） | `SessionContentPart` | 补 `LocalPath` 物化 + 顺手补掉既有漏映射的 `FileId`/`ImageDetail` |
| 本轮活事件 | `AgentExecutor.ForwardToolEventsAsync`（`:796` 造 tool 消息） | `ToolCallEvent.Parts` | 补物化 |

**注意 `BuildHistory` 的签名改动面**：生产调用只有 `ExecutionRunner:996` 一处，但**测试经门面 `BuildHistoryFromSession` 直接调用有 12 处**（5 个测试文件）。因此 resolver 必须是**可选参数**（默认 `null` = 不物化），否则 12 处测试全部编译失败。

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
    /// <param name="sessionId">用于校验路径必须落在该会话的 ref 目录内。</param>
    ChatContentPart Resolve(string sessionId, SessionContentPart part);

    /// <param name="sessionId">用于校验路径必须落在该会话的 ref 目录内。</param>
    ChatContentPart Resolve(string sessionId, ToolOutputPart part);
}
```

`sessionId` 是必需的：路径边界校验必须以该会话自己的 ref 目录（`IToolOutputStore.GetRefDirectory(sessionId)`）为根，否则无法判断「这是不是本会话的图」。

实现 `ImagePartResolver`：

| 项 | 决定 |
|---|---|
| 生命周期 | Singleton |
| 依赖 | **`IServiceProvider` 取可空 `IFileSystem`**（`IFileSystem` 只在 `Seeing.IO.Local` 模块注册；构造器硬依赖会让无该模块的宿主与 `ValidateOnBuild` 测试解析失败）。取不到时退化为「全部返回文本占位」 |
| 缓存 | 进程内 LRU，键 = `(规范化路径, LastWriteTimeUtc, Size)`，容量可配（默认 32），配置节 `SeeingAgentOptions.ImageParts.CacheCapacity`，热重载有效 |
| 路径校验 | 物化前必须确认路径落在**本会话 ref 目录**内（`PathBoundary`）；越界返回文本占位并记 WARN。否则会话文件里被写入任意路径＝任意本地文件被 base64 送给模型 |
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

## 6. 三个 provider 客户端的出站形状

框架只保证 `ChatContentPart` 携带可用载荷，出站由 client 决定。

**存在三个 chat 客户端**（原设计只算了两个）：

| client | 协议 | 图片出站方式 | 依据 |
|---|---|---|---|
| `AnthropicClient` | Messages API | ✅ **原生**：`tool_result.content` 为 block 数组，直接内嵌 `{type:"image",source:{...}}` | 官方支持 |
| `OpenAiResponsesClient` | Responses API | ✅ **原生**：`function_call_output.output` 为数组，放 `{type:"input_image", image_url:"data:..."}` | 官方文档：「For functions that return images or files, you can pass an array of image or file objects instead of a string」 |
| `OpenAiChatClient` | Chat Completions | ⚠️ **必须绕路**：`role:"tool"` 的 `content` **只允许 text** | 官方 API reference：「For tool messages, only type `text` is supported」；实测报错 `image URLs are only allowed for role: "user", not role: "tool"` |

### 6.1 Chat Completions 的绕路规则（唯一特殊路径）

官方要求 `assistant(tool_calls)` 之后的**所有 tool 消息必须连续**，任何其它 role 都不能插在中间。实测确认（同一会话同一模型）：

| 形状 | 结果 |
|---|---|
| `tool, tool, user, user` | ✅ 200 |
| `tool, user, tool, user` | ❌ 400 `invalid_request_error`，报 `messages.[N].role` |

所以**唯一合法形态**：

```
assistant(tool_calls=[t1, t2, t3])
tool(t1)                       ← 全部连续
tool(t2)
tool(t3)
user([image_from_t1, image_from_t2])   ← 聚合一条，在最后
```

实现要点（`BuildMessages`）：
1. 先按原顺序输出全部消息，tool 消息只带文本。
2. tool 消息期间把图片**缓冲**到 `pendingImages`（不立即输出）。
3. 遇到第一条非 tool 消息（或列表结束）时，把 `pendingImages` 作为**单条** user 消息 flush 出去。
4. 一轮有 3 个带图工具 → 只产出 **1 条** user 消息，内含 3 个 image part。

这条规则只存在于 `OpenAiChatClient` 内部，不影响框架契约、不影响 `SessionHistoryBuilder`、不影响缓存（图片顺序稳定）。

### 6.2 为什么不在框架层统一成「tool + user」

若框架层就给 Chat Completions 造 user 消息，Anthropic 与 Responses 会收到**多余的消息**，破坏它们原生的工具结果形状，且 OpenAI 系历史重建会与原生路径不一致。框架只负责提供载荷，形状归 client。

---

## 7. WebUI 渲染（图片必须看得见）

改引用式后 base64 不再随消息下发，**UI 必须改从文件读**，否则用户会看到「消息里图片消失了」。这条不是增强，是改动的必要部分。

### 7.1 两个渲染缺口

| # | 缺口 | 现状 |
|---|---|---|
| 1 | **用户消息图片** | `PromptAttachmentRouter.Route:31` 遇空 `Base64Content` 直接 `continue` → 引用式附件被**静默丢弃**；`AttachmentPreview.razor` 与 `ContentPartViewModel.GetDisplayUrl()` 只认 base64 |
| 2 | **工具消息图片** | `ToolCallViewModel.FromSessionToolCall`（`:140-181`）**完全不读 `tc.Parts`**，`ToolCallMessageComponent` 只渲染 `Result` 文本 → 工具截图无渲染入口 |

### 7.2 取图端点

```csharp
// samples/Seeing.Agent.WebUI/Endpoints/SessionAssetEndpoints.cs
public const string Route = "/api/sessions/{sessionId}/assets/{scope}/{**relativePath}";
// scope: "ref"（工具产出） | "uploads"（用户附件）
```

实现与 `UploadEndpoints` / `TranscriptionEndpoints` 同构：

| 校验 | 做法 |
|---|---|
| 会话存在 | `ISessionManager.Get(sessionId) is not null`，否则 404 |
| 路径边界 | `scope=ref` → `PathBoundary.IsWithin(full, IToolOutputStore.GetRefDirectory(sessionId))`；`scope=uploads` → `PastedFileLayout.BuildSessionDirectory(root, sessionId)`。越界 404（**不** 403，不暴露路径存在性） |
| MIME 白名单 | 仅 `.png/.jpg/.jpeg/.webp/.gif`，其它 404 |
| 缓存头 | `Cache-Control: private, max-age=300` + `ETag = LastWriteTimeUtc ticks`，304 走浏览器缓存 |

不做 HMAC 签名：与 `api/uploads` 保持同一鉴权基线（会话存在 + 路径边界），本机/局域网部署足够。

### 7.3 URL 唯一生成点

```csharp
// samples/Seeing.Agent.WebUI/Services/SessionAssetUrlBuilder.cs
public sealed class SessionAssetUrlBuilder
{
    /// <summary>把会话内的 LocalPath 转成可被浏览器加载的 URL；不在本会话目录内返回 null。</summary>
    public string? ForSessionPath(string sessionId, string localPath);
}
```

`LocalPath` 在 ref 目录 → `.../assets/ref/{name}`；在 uploads 目录 → `.../assets/uploads/{relPath}`；都不在（跨机/历史遗留）→ `null`。
**禁止**在组件里各自拼 URL 字符串。

### 7.4 渲染改动

| # | 位置 | 改动 |
|---|---|---|
| 1 | `ContentPartViewModel.FromSessionPart` | 接受 `SessionAssetUrlBuilder` + `sessionId`，有 `LocalPath` 时设 `ThumbnailUrl`；`GetDisplayUrl()` 现有优先级（`ThumbnailUrl → Url → DataBase64`）无需改 |
| 2 | `ToolCallViewModel` | 新增 `List<ContentPartViewModel> Parts`；`FromSessionToolCall` 投影 `tc.Parts` |
| 3 | `ToolCallMessageComponent.razor` | 结果区下方渲染缩略图行（`<img loading="lazy">` + 固定高度 120px + `object-fit: cover`），点击在新标签打开原图 |
| 4 | `PromptAttachmentRouter.Route` | 空 `Base64Content` 但有 `LocalPath` → 设 `ChatAttachment.LocalPath`，**不 continue** |
| 5 | `AttachmentPreview.razor` | 走 `GetDisplayUrl()`（已优先 `ThumbnailUrl`） |

### 7.5 为什么缩略图走 URL 而不是 base64

| 原因 | 说明 |
|---|---|
| Blazor 消息大小 | `MaximumReceiveMessageSize` 默认 32KB；base64 进 render batch 会直接爆（`2026-10-23-paste-files-and-screenshots-design.md` 已有同类事故） |
| 渲染性能 | 一轮 10 张 2MB 图 ≈ 27MB base64 字符串反复序列化，长会话会卡死 |
| 浏览器能力 | `<img>` 原生并行加载 + HTTP 缓存 + `loading="lazy"`，Blazor 只传 URL 字符串 |

**直接返回原图，前端 CSS 缩放**：不引入图像编码依赖（与「不压缩」决策一致）。大图当缩略图的带宽代价接受，列入已知边界。

### 7.6 历史会话兼容

存量消息 `Parts` 里的图片是 `DataBase64`（用户粘贴走老路），`GetDisplayUrl()` 已能处理 → **刷新后旧会话图片照常显示**。新数据才有 `LocalPath`。

---

## 8. 交付清单

| # | 内容 | 位置 |
|---|---|---|
| 1 | `SessionContentPart.LocalPath` + `CreateImageFromPath` + **`SessionMessage.Clone()` 补字段** | `src/primitives/Seeing.Session/Core/SessionMessage.cs` |
| 2 | `ToolOutputPart` + `ToolResult.Parts` + `ToolCallEvent.Parts` + `Attachments` 标 Obsolete | `src/abstractions/Seeing.Agent.Abstractions/Tools/`、`Events/` |
| 3 | `ToolCallInvoker` 透传——**全部 5 处**：`BuildTerminalEvent` 调用点 ×2（`:96` Rejected、`:117` Cancelled）+ Success/Failed/Cancelled 构造 ×3（`:155/179/198`） | `src/spine/Seeing.Agent.Core/Core/ToolCallInvoker.cs` |
| 4 | `AgentExecutor.BuildTerminalEvent`（`:916`）同样需要透传（异常兜底路径） | `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs` |
| 5 | `SessionToolCall.Parts` + **`SessionToolCall.Clone()` 补字段** + `ChatEventTracker` 映射 | `src/primitives/.../SessionMessage.cs`、`src/hosting/.../Execution/ChatEventTracker.cs` |
| 6 | `IImagePartResolver` + `ImagePartResolver`（LRU）+ DI | `src/spine/Seeing.Agent.Core/Llm/` |
| 7 | **`SessionHistoryBuilder` 现有映射补 `FileId`/`ImageDetail`**（既有 bug）+ 接 resolver，可选参数（默认 null 不物化） | `src/hosting/.../Execution/SessionHistoryBuilder.cs` |
| 8 | `AgentExecutor.ForwardToolEventsAsync` 接物化器（加参数，保持 static） | `src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs` |
| 9 | `ReadTool.ReadBinaryFile` 改返回 `Parts` | `src/capabilities/Seeing.Agent.Tools.FileSystem/ReadTool.cs` |
| 10 | 用户附件改引用式 + **`PromptAttachmentRouter` 空 base64 分支**（否则粘贴图片静默消失） + `AttachmentPreview` 预览 | `samples/.../UploadedFileService.cs`、`PromptAttachmentRouter.cs`、`AttachmentPreview.razor`、`src/abstractions/.../Models/ChatInput.cs`、`src/hosting/.../SessionRuntime.cs` |
| 11 | **`SessionToolOutputStore` 增加二进制写入重载**（现有 `SaveAsync` 只写文本、固定 `.txt`） | `src/spine/Seeing.Agent.Core/Output/` |
| 12 | **取图端点** `SessionAssetEndpoints`（会话校验 + `PathBoundary` + MIME 白名单 + ETag） | `samples/.../Endpoints/SessionAssetEndpoints.cs` |
| 13 | **`SessionAssetUrlBuilder`**（LocalPath → URL 唯一生成点） | `samples/.../Services/SessionAssetUrlBuilder.cs` |
| 14 | `ContentPartViewModel.FromSessionPart` 接 URL builder → `ThumbnailUrl` | `samples/.../Models/ContentPartViewModel.cs` |
| 15 | **`ToolCallViewModel.Parts` + `FromSessionToolCall` 投影 `tc.Parts`**（当前完全没读） | `samples/.../Models/ToolCallViewModel.cs` |
| 16 | `ToolCallMessageComponent` 渲染缩略图行 | `samples/.../Components/Messaging/ToolCallMessageComponent.razor` |
| 17 | 三个 client 出站：`AnthropicClient`（原生）、`OpenAiResponsesClient`（原生）、`OpenAiChatClient`（聚合 user） | 各自 `Clients/` 目录 |
| 18 | 配置节 `SeeingAgentOptions.ImageParts.CacheCapacity` + 注册 | `src/spine/Seeing.Agent.Core/Configuration/`、`ConfigSectionRegistry` |
| 19 | 测试 | 见 §9 |

---

## 8. 测试

| 套件 | 钉住什么 |
|---|---|
| `ToolOutputPartTests` | 契约往返；`Kind` 常量唯一 |
| `ImagePartResolverTests` | LRU 命中（同一路径第二次不读盘）；文件修改后失效（改内容 + 改时间戳）；文件缺失降级为文本段；非图片 MIME 走 file 段；**越界路径（不在本会话 ref 目录）拒绝并降级** |
| `SessionHistoryBuilderTests`（改） | `SessionContentPart{LocalPath}` → `ChatContentPart{DataBase64}`；缺失图降级不抛；`FileId`/`ImageDetail` 不再丢 |
| `AgentExecutor` 相关（改） | 本轮工具图片直接进 `history`，不经过会话重建 |
| `ToolCallPersistenceTests`（新） | `ToolCallEvent.Parts` → `SessionToolCall.Parts` 落盘往返；`SessionToolCall.Clone()` 与 `SessionMessage.Clone()` 都不丢 `Parts`/`LocalPath` |
| `OpenAiChatClientTests`（新） | **聚合形状**：一轮 2 个带图工具 → `tool, tool, user([img, img])`（绝不出现 `tool, user, tool`）；无图时输出与旧版逐字节相同 |
| `OpenAiResponsesClientTests`（新） | `function_call_output.output` 为数组，含 `input_image` |
| `AnthropicClientTests`（改） | `tool_result.content` 为 block 数组，含 image block |
| `PromptAttachmentRouterTests`（新） | 引用式附件（`Base64Content` 为空、有 `LocalPath`）不再被 `continue` 跳过 |
| `SessionToolOutputStoreTests`（改） | 二进制写入重载；`.png` 文件名与内容正确 |
| `ReadToolFileSystemTests`（改） | 读 PNG 返回 `Parts`，`Attachments` 不再使用 |
| `SessionAssetEndpointsTests`（新） | 会话不存在 404；路径越界 404（不含 403）；非白名单扩展名 404；正常取图 200 + ETag；`If-None-Match` 命中返回 304 |
| `SessionAssetUrlBuilderTests`（新） | ref 目录路径 → `assets/ref/...`；uploads 路径 → `assets/uploads/...`；两者之外返回 null（不抛） |
| `ToolCallViewModelTests`（新） | `FromSessionToolCall` 投影 `tc.Parts`；无 Parts 时为空列表且不抛 |
| `ContentPartViewModelTests`（新） | `LocalPath` 有值 → `ThumbnailUrl` 非空；`GetDisplayUrl()` 优先级 `ThumbnailUrl → Url → DataBase64` |

守门断言：**任何 client 收到的 `ChatContentPart` 都不含 `LocalPath`**（该字段不存在于 `ChatContentPart`，编译期即保证）。

---

## 9. 已知边界

| 项 | 说明 |
|---|---|
| 无图片大小限制 | 模型不支持时由 `LlmRouter` 的 `Modalities` 层拒绝；不额外拦截 |
| 物化为同步读盘 | LRU 命中即常数；miss 时几 MB 同步阻塞。会话图片极多时可能可见 |
| 缓存容量默认 32 | 超过即按 LRU 淘汰；每轮历史含 30+ 图的长会话会持续 miss。可通过配置调大 |
| `ArchiveAsync` 会删 ref 目录 | `FileSessionStore.ArchiveAsync`（`:738`）归档时删除 ref，归档→恢复后图片全部丢失。本期接受此降级（恢复后图片变文本占位），下期改为随归档一起搬迁 |
| 同步读盘 + 序列化开销 | 20 张 2MB 图 → 每请求约 54MB base64 字符串，LRU 只省读盘、不省序列化与传输。超长图片会话的请求体仍会很大（按「不设限」决策接受） |
| 不设图片限制的代价 | 超模型能力时只会得到厂商不透明 400。物化器记 WARN（含路径/大小/模型 id）便于定位，但不拦截、不改写 |
| 会话 ref 目录随会话删除 | `SessionToolOutputStore.DeleteSessionRefDirectory` 已实现；删除会话即清图 |
| 缩略图直接用原图 | 无服务端缩放（不引图像编码依赖）。2MB 图当缩略图会浪费带宽，靠 `<img loading="lazy">` + HTTP 缓存缓解 |
| 取图端点无 HMAC 签名 | 与 `api/uploads` 同一鉴权基线（会话存在 + 路径边界）。能猜到会话 id 与文件名即可读图，本机/局域网部署可接受；公网暴露需另行加固 |
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
