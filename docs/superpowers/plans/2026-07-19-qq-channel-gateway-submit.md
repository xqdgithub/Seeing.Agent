# QQ Channel + Gateway Submit/Subscribe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 删除 App/Gateway 耦合式 `ExecuteAsync`/`ChatAsync`，改为 Submit/Subscribe/Cancel；WeCom 跟进；新增官方 QQ Channel 并接入 WebUI Gateway 客户端管理。

**Architecture:** Gateway Server 用 `IChatOrchestrator.SubmitAsync` + `SubscribeEvents`，按 `executionId` 推送并完成；Client 暴露 `SubmitAsync`/`CancelAsync`/`ReceiveAsync`；WeCom/QQ Bridge 持有 `executionId` 消费事件。QQ 镜像 WeCom 插件结构，WebUI 复用 Gateway 客户端页（Registry + Schema）。

**Tech Stack:** .NET 10、xUnit、现有 Seeing.Gateway / WeCom / WebUI GatewayClients、QQ 官方 Bot WS+HTTP（参考 QwenPaw `qq`）。

**Spec:** `docs/superpowers/specs/2026-07-19-qq-channel-gateway-submit-design.md`

## Global Constraints

- 旧 API **直接删除**，不保留 `[Obsolete]` 适配层：`IChatOrchestrator.ExecuteAsync`/`Stop`，`IGatewayClient.ChatAsync`/`StopChatAsync`，HTTP `/chat`/`/chat/stop`，WS `Chat`/`Stop` 帧。
- 企微 `aibot_*` 协议不改；不做 OneBot。
- WebUI 不为 QQ 单独做配置页；复用 Gateway 客户端。
- **禁止 git commit / push**，除非用户明确要求。
- 完成判定必须匹配本 job 的 `executionId`（`ExecutionCompleteEvent.ExecutionId`）。
- QQ sessionId：`qq_{openid}` / `qq_group_{group}` / `qq_group_{group}_{member}` / `qq_channel_{channel_id}`。

---

## File map（锁定分解）

| 区域 | 主要文件 |
|------|----------|
| App 清理 | `src/Seeing.Agent.App/IChatOrchestrator.cs`, `ChatOrchestrator.cs` |
| Gateway Server | `GatewayOrchestratorV2.cs`, `GatewayEndpoints.cs`, `GatewayWebSocketHandler.cs`, `GatewayConnectionManager.cs`, `GatewayRunTracker.cs` |
| Protocol | `GatewayWsFrameType.cs`, `GatewayWsFrame.cs`（新 payload 类型） |
| Client | `IGatewayClient.cs`, `IGatewayConnection.cs`, `WebSocketGatewayClient.cs`, `HttpGatewayClient.cs`, 删除 Adapter |
| WeCom | `WeComChannelBridge.cs`, README, 相关测试 |
| QQ 新包 | `src/Seeing.Gateway.QQ/**`, `tests/Seeing.Gateway.QQ.Tests/**` |
| 注册 | `GatewayChannelRegistry.cs`, `Seeing.Agent.Gateway.csproj`, `ChannelHost.csproj`, `Seeing.Agent.slnx` |

---

## Phase A — App 清理

### Task 1: 删除 ChatOrchestrator 旧接口并修编译

**Files:**
- Modify: `src/Seeing.Agent.App/IChatOrchestrator.cs` — 删除 `#region 旧接口（已弃用）` 整段（`ExecuteAsync`、`Stop`）
- Modify: `src/Seeing.Agent.App/ChatOrchestrator.cs` — 删除对应实现
- Modify: 全仓库对 `IChatOrchestrator.ExecuteAsync` / `.Stop(` 的引用（**不是** Tool/Agent/Hook 的 `ExecuteAsync`）

**已知必须改的调用点：**
- `src/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`（Task 2 会整体改写；本任务若先改 App，Gateway 会暂时编不过——**允许 Task 1+2 同一工作单元完成后再 `dotnet build`**）

**Interfaces:**
- Consumes: 现有 `SubmitAsync` / `SubscribeEvents` / `Cancel` / `GetOverview`
- Produces: `IChatOrchestrator` 仅含新接口

- [ ] **Step 1: 写失败测试（App 层行为契约）**

在 `tests/Seeing.Agent.Tests/` 新增或扩展（若已有 Orchestrator 测试则改之）`ChatOrchestratorSubmitSubscribeTests.cs`：

```csharp
[Fact]
public async Task SubscribeEvents_ShouldExit_WhenExecutionCompleteMatchesId()
{
    // Arrange: 使用现有测试 DI / mock ExecutionJobService 模式
    // Act: SubmitAsync → SubscribeEvents
    // Assert: 收到 ExecutionCompleteEvent 且 ExecutionId 匹配后结束；不把其它 execution 当结束
}
```

若项目暂无轻量可 mock 的 Orchestrator 测试基建，改为：

```csharp
[Fact]
public void IChatOrchestrator_ShouldNotDeclare_ExecuteAsync_Or_Stop()
{
    var methods = typeof(IChatOrchestrator).GetMethods();
    Assert.DoesNotContain(methods, m => m.Name == "ExecuteAsync");
    Assert.DoesNotContain(methods, m => m.Name == "Stop" && m.GetParameters().Length == 1);
}
```

- [ ] **Step 2: 运行测试确认失败或接口仍存在**

```bash
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~IChatOrchestrator_ShouldNotDeclare"
```

Expected: FAIL（方法仍存在）或编译失败前先删接口。

- [ ] **Step 3: 删除接口与实现**

从 `IChatOrchestrator` / `ChatOrchestrator` 删除 `ExecuteAsync`、`Stop` 及 Obsolete region。

- [ ] **Step 4: 与 Task 2 一并修 Gateway 后验证**

```bash
dotnet build Seeing.Agent.slnx
dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~IChatOrchestrator_ShouldNotDeclare"
```

Expected: PASS

- [ ] **Step 5: 跳过 commit**（用户要求）

---

## Phase B — Gateway Protocol + Client

### Task 2: Protocol 帧类型与 payload

**Files:**
- Modify: `src/Seeing.Gateway/Protocol/GatewayWsFrameType.cs`
- Modify: `src/Seeing.Gateway/Protocol/GatewayWsFrame.cs`（及同目录 payload）

**Interfaces:**
- Produces:

```csharp
public enum GatewayWsFrameType
{
    Connected,
    Submit,              // 原 Chat
    SubmitAck,           // 新增：返回 executionId
    ChatEvent,           // 保留名，payload 增加 executionId 关联
    ExecutionComplete,   // 原 ChatComplete
    ChatError,           // 可保留或改名 SubmitError；本计划保留 ChatError
    Cancel,              // 原 Stop
    CancelAck,           // 原 StopAck
    PermissionRespond,
    PermissionAck,
    Error,
    Ping,
    Pong
}

public record GatewaySubmitAckPayload
{
    public required string SessionId { get; init; }
    public required string ExecutionId { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int QueuePosition { get; init; }
}

public record GatewayCancelPayload
{
    public required string ExecutionId { get; init; }
}

public record GatewayCancelAckPayload
{
    public required string ExecutionId { get; init; }
    public bool Cancelled { get; init; }
}

public record GatewayExecutionCompletePayload
{
    public required string SessionId { get; init; }
    public required string ExecutionId { get; init; }
    public string? LoopId { get; init; }
}
```

删除：`GatewayStopPayload` / `GatewayStopAckPayload` / `GatewayChatCompletePayload`（全部引用改为上表）。

- [ ] **Step 1: 更新枚举与 payload，修编译错误至仅剩 handler/client**

- [ ] **Step 2: 单元测试序列化往返**

```csharp
[Fact]
public void SubmitAck_RoundTrips()
{
    var frame = GatewayWsFrameSerializer.Create(
        GatewayWsFrameType.SubmitAck,
        "req1",
        new GatewaySubmitAckPayload { SessionId = "s", ExecutionId = "e", Success = true });
    var json = GatewayWsFrameSerializer.Serialize(frame);
    var back = GatewayWsFrameSerializer.Deserialize(json);
    Assert.Equal(GatewayWsFrameType.SubmitAck, back!.Type);
}
```

- [ ] **Step 3: `dotnet test` 相关 Gateway 测试项目**

- [ ] **Step 4: 跳过 commit**

---

### Task 3: GatewayOrchestratorV2 改为 Submit + Subscribe

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Core/GatewayOrchestratorV2.cs`
- Modify: `src/Seeing.Agent.Gateway/Core/GatewayRunTracker.cs`（若当前按 sessionId 取消 → 改为按 executionId；保留 session 索引以便查询）
- Modify: `src/Seeing.Agent.Gateway/Endpoints/GatewayWebSocketHandler.cs`
- Modify: `src/Seeing.Agent.Gateway/Endpoints/GatewayEndpoints.cs`
- Modify: `src/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs`（`ChatComplete` → `ExecutionComplete`）
- Modify: `src/Seeing.Agent.Gateway/README.md`

**Interfaces:**
- Produces:

```csharp
// GatewayOrchestratorV2
Task<GatewaySubmitResult> SubmitAsync(GatewayRequest request, CancellationToken ct = default);
IAsyncEnumerable<GatewayEvent> SubscribeExecutionEventsAsync(
    string sessionId,
    string executionId,
    CancellationToken ct = default);
bool Cancel(string executionId);

public sealed class GatewaySubmitResult
{
    public bool Success { get; init; }
    public string? ExecutionId { get; init; }
    public string SessionId { get; init; } = "";
    public int QueuePosition { get; init; }
    public string? Error { get; init; }
}
```

**核心逻辑（替换 `ExecuteChatAsync` / `StopChat`）：**

```csharp
public async Task<GatewaySubmitResult> SubmitAsync(GatewayRequest request, CancellationToken ct = default)
{
    var sessionId = request.SessionId;
    // Build ChatInput / ChatOptions 同现有 BuildChatInput / options 逻辑
    using var scope = _services.CreateScope();
    var orch = scope.ServiceProvider.GetRequiredService<IChatOrchestrator>();
    var result = await orch.SubmitAsync(sessionId, input, options);
    if (!result.Success || string.IsNullOrEmpty(result.ExecutionId))
        return new GatewaySubmitResult { Success = false, SessionId = sessionId, Error = result.Error };
    _runTracker.TrackExecution(sessionId, result.ExecutionId); // 按设计实现
    return new GatewaySubmitResult
    {
        Success = true,
        SessionId = sessionId,
        ExecutionId = result.ExecutionId,
        QueuePosition = result.QueuePosition
    };
}

public async IAsyncEnumerable<GatewayEvent> SubscribeExecutionEventsAsync(
    string sessionId,
    string executionId,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var scope = _services.CreateScope();
    var orch = scope.ServiceProvider.GetRequiredService<IChatOrchestrator>();
    // 设置 PermissionRunContext 等同现有逻辑
    await foreach (var chatEvent in orch.SubscribeEvents(sessionId, ct))
    {
        if (chatEvent is ExecutionCompleteEvent complete &&
            !string.Equals(complete.ExecutionId, executionId, StringComparison.Ordinal))
            continue;

        if (ChatEventMapper.TryMap(chatEvent, out var gatewayEvent) && gatewayEvent != null)
            yield return gatewayEvent;

        if (chatEvent is ExecutionCompleteEvent done &&
            string.Equals(done.ExecutionId, executionId, StringComparison.Ordinal))
            yield break;
    }
}

public bool Cancel(string executionId)
{
    using var scope = _services.CreateScope();
    var orch = scope.ServiceProvider.GetRequiredService<IChatOrchestrator>();
    return orch.Cancel(executionId);
}
```

**HTTP：**

| 删除 | 新增 |
|------|------|
| `POST /api/gateway/chat` | `POST /api/gateway/submit` → JSON `GatewaySubmitResult` |
| `POST /api/gateway/chat/stop` | `POST /api/gateway/cancel` body `{ "executionId": "..." }` |
| | `GET /api/gateway/events?sessionId=&executionId=` SSE，过滤至 complete |

**WS handler：**

- `Submit`：调用 `SubmitAsync` → 回 `SubmitAck`；再 `SubscribeExecutionEventsAsync` 推 `ChatEvent`，结束发 `ExecutionComplete`
- `Cancel`：`Cancel(executionId)` → `CancelAck`
- 删除 `Chat` / `Stop` case

- [ ] **Step 1: 实现 Orchestrator + Endpoints + WS handler**

- [ ] **Step 2: 更新/新增测试**（`tests/Seeing.Gateway.Tests`）覆盖：submit 失败、executionId 过滤、cancel

- [ ] **Step 3:**

```bash
dotnet test tests/Seeing.Gateway.Tests
```

Expected: PASS

- [ ] **Step 4: 跳过 commit**

---

### Task 4: Gateway Client 契约切换

**Files:**
- Modify: `src/Seeing.Gateway/IGatewayClient.cs`
- Modify: `src/Seeing.Gateway/IGatewayConnection.cs`
- Modify: `src/Seeing.Gateway.Client/WebSocketGatewayClient.cs`
- Modify: `src/Seeing.Gateway.Client/HttpGatewayClient.cs`
- Delete: `WebSocketGatewayClientAdapter` 类（在 `WebSocketGatewayClient.cs` 底部）
- Modify: `src/Seeing.Gateway.Client/README.md`, `src/Seeing.Gateway/README.md`
- Modify: `GatewayInbound` 增加 `SubmitAck` / `CancelAck` / `ExecutionComplete` 字段，删除 Stop* / ChatComplete

**Interfaces:**

```csharp
public interface IGatewayConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<GatewaySubmitResult> SubmitAsync(GatewayRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<GatewayInbound> ReceiveAsync(CancellationToken cancellationToken = default);
    Task<GatewayCancelAckPayload> CancelAsync(string executionId, CancellationToken cancellationToken = default);
    Task<GatewayPermissionRespondResult> RespondPermissionAsync(...); // 保留
}

public interface IGatewayClient
{
    Task<GatewaySubmitResult> SubmitAsync(GatewayRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<GatewayEvent> SubscribeAsync(string sessionId, string executionId, CancellationToken cancellationToken = default);
    Task CancelAsync(string executionId, CancellationToken cancellationToken = default);
    Task<GatewayPermissionRespondResult> RespondPermissionAsync(...);
    Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(...);
    Task<GatewaySessionResetResult> ResetSessionAsync(...);
}
```

`HttpGatewayClient.SubscribeAsync`：打开 SSE `/api/gateway/events?sessionId=&executionId=`。

`WebSocketGatewayClient.SubmitAsync`：发 `Submit` 帧，等待匹配 `Id` 的 `SubmitAck`。

- [ ] **Step 1: 改接口与实现，修编译**

- [ ] **Step 2: Client 测试**（`tests/Seeing.Gateway.Client.Tests`）用假 WS/HTTP handler

- [ ] **Step 3:**

```bash
dotnet test tests/Seeing.Gateway.Client.Tests
dotnet test tests/Seeing.Gateway.Tests
```

- [ ] **Step 4: 跳过 commit**

---

## Phase C — WeCom

### Task 5: WeComChannelBridge 切到 Submit/Subscribe/Cancel

**Files:**
- Modify: `src/Seeing.Gateway.WeCom/WeComChannelBridge.cs`
- Modify: `src/Seeing.Gateway.WeCom/Extensions/WeComServiceCollectionExtensions.cs`（若仅注册 Connection，无需 Adapter）
- Modify: `src/Seeing.Gateway.WeCom/README.md`
- Modify: `tests/Seeing.Gateway.WeCom.Tests/**` 中所有 `ChatAsync` / Adapter mock

**Interfaces:**
- Consumes: `IGatewayConnection.SubmitAsync` + `ReceiveAsync` 或注入的 `IGatewayClient.SubscribeAsync`
- 删除字段：`WebSocketGatewayClientAdapter _gatewayAdapter`

**Message Job 核心替换：**

```csharp
var submit = await _gatewayClient.SubmitAsync(request, cancellationToken);
if (!submit.Success || string.IsNullOrEmpty(submit.ExecutionId))
{
    await streamState.FailAsync(submit.Error ?? "Submit failed", cancellationToken);
    return;
}

var executionId = submit.ExecutionId;
var reply = new GatewayAssistantReplyCollector();

await foreach (var gatewayEvent in _gatewayClient.SubscribeAsync(sessionId, executionId, cancellationToken))
{
    // 与现有 switch disposition 相同
}

// 超时分支：
await _gatewayClient.CancelAsync(executionId, CancellationToken.None);
```

注意：若 WeCom 直接持有 `WebSocketGatewayClient`（`IGatewayConnection`），则：

```csharp
var submit = await _gatewayClient.SubmitAsync(request, ct);
await foreach (var inbound in _gatewayClient.ReceiveAsync(ct))
{
    if (inbound.Type == GatewayWsFrameType.ChatEvent && inbound.Event != null
        && IsForExecution(inbound, executionId))
    {
        // Apply collector...
    }
    if (inbound.Type == GatewayWsFrameType.ExecutionComplete
        && inbound.ExecutionComplete?.ExecutionId == executionId)
        break;
}
```

在 `GatewayEvent` 或 inbound 上确保能关联 `executionId`（Submit 后该连接上的事件流按 request 绑定，或事件 payload 带 executionId）。

- [ ] **Step 1: 改 Bridge + 修测试编译**

- [ ] **Step 2:**

```bash
dotnet test tests/Seeing.Gateway.WeCom.Tests
```

Expected: PASS

- [ ] **Step 3: 跳过 commit**

---

## Phase D — QQ Channel

### Task 6: 创建 Seeing.Gateway.QQ 项目骨架 + Options + Plugin

**Files:**
- Create: `src/Seeing.Gateway.QQ/Seeing.Gateway.QQ.csproj`（镜像 WeCom csproj，PackageId=`Seeing.Gateway.QQ`）
- Create: `src/Seeing.Gateway.QQ/QQOptions.cs`
- Create: `src/Seeing.Gateway.QQ/QQChannelPlugin.cs`
- Create: `src/Seeing.Gateway.QQ/Extensions/QQServiceCollectionExtensions.cs`
- Create: `tests/Seeing.Gateway.QQ.Tests/Seeing.Gateway.QQ.Tests.csproj`
- Modify: `Seeing.Agent.slnx` 加入两项目
- Modify: `src/Seeing.Agent.Gateway/Seeing.Agent.Gateway.csproj` — ProjectReference QQ
- Modify: `samples/Seeing.Gateway.ChannelHost/Seeing.Gateway.ChannelHost.csproj` — ProjectReference QQ
- Modify: `src/Seeing.Agent.Gateway/Channels/GatewayChannelRegistry.cs` — 注册 builtin qq

**QQOptions 关键字段（均带 `[Display]`）：**  
`Enabled`, `AppId`, `ClientSecret`, `ApiBase`(=`https://api.sgroup.qq.com`), `BotPrefix`, `MarkdownEnabled`, `ShareSessionInGroup`, `SessionIdleTimeoutMinutes`, `MaxReconnectAttempts`, `AckMessage`, `MediaCacheDirectory`, `MaxMediaBytes`, 权限策略字段对齐 WeCom。

**Plugin:**

```csharp
public sealed class QQChannelPlugin : IGatewayChannelPlugin
{
    public string ChannelId => "qq";
    public string DisplayName => "QQ";
    public string Description => "QQ 官方机器人 WebSocket/HTTP 通道桥接";
    public bool IsBuiltin => true;
    public Type OptionsType => typeof(QQOptions);
    public string OptionsSectionName => QQOptions.SectionName; // "QQ"
    public IReadOnlyList<ConfigFieldSchema>? GetConfigSchema() =>
        OptionsSchemaBuilder.FromType(typeof(QQOptions));
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<QQOptions>(configuration.GetSection(QQOptions.SectionName));
        services.AddSeeingQQChannel();
        services.Configure<GatewayClientOptions>(configuration.GetSection(GatewayClientOptions.SectionName));
        services.ConfigureGatewayClientCommon(configuration);
    }
}
```

**Registry:**

```csharp
public const string BuiltinQQSpec = "builtin:qq";
// RegisterBuiltinPlugins:
RegisterPluginInstance(discovered, new QQChannelPlugin(), ResolveBuiltinAssemblyPath<QQChannelPlugin>(), BuiltinQQSpec);
```

- [ ] **Step 1: 脚手架 + 注册**

- [ ] **Step 2: 测试 Registry 含 qq**

```csharp
[Fact]
public void Registry_ShouldInclude_BuiltinQQ()
{
    // 按 WeCom 测试或 WebUI 测试模式加载 Registry
    Assert.Contains(registry.Types, t => t.ChannelId == "qq" && t.IsBuiltin);
}
```

- [ ] **Step 3:**

```bash
dotnet build src/Seeing.Gateway.QQ
dotnet build src/Seeing.Agent.Gateway
```

- [ ] **Step 4: 跳过 commit**

---

### Task 7: QQ Session / Message 解析（纯逻辑）

**Files:**
- Create: `src/Seeing.Gateway.QQ/QQSessionResolver.cs`
- Create: `src/Seeing.Gateway.QQ/QQMessageParser.cs`
- Create: `tests/Seeing.Gateway.QQ.Tests/QQSessionResolverTests.cs`
- Create: `tests/Seeing.Gateway.QQ.Tests/QQMessageParserTests.cs`

**SessionResolver（写死规则）：**

```csharp
// C2C → qq_{user_openid}
// group + ShareSessionInGroup → qq_group_{group_openid}
// group + !Share → qq_group_{group_openid}_{member_openid}
// guild/dm AT → qq_channel_{channel_id}
```

**Parser：** 将 QQ WS `d` 负载映射为内部 `ParsedQQMessage`（Text、Attachments 元数据、MsgId、OpenIds、MessageType、GatewayRequest 工厂）。

- [ ] **Step 1: 先写 Session/Parser 失败测试（固定 JSON fixture）**

- [ ] **Step 2: 实现至 PASS**

```bash
dotnet test tests/Seeing.Gateway.QQ.Tests --filter "FullyQualifiedName~QQSessionResolver|QQMessageParser"
```

- [ ] **Step 3: 跳过 commit**

---

### Task 8: QQ Token + HTTP API + WS Connection

**Files:**
- Create: `src/Seeing.Gateway.QQ/Connection/QQAccessTokenProvider.cs`
- Create: `src/Seeing.Gateway.QQ/Connection/QQHttpApiClient.cs`
- Create: `src/Seeing.Gateway.QQ/Connection/QQWebSocketClient.cs`（Hello/Identify/Resume/Heartbeat/Reconnect）
- Create: `src/Seeing.Gateway.QQ/Connection/QQConnectionManager.cs`（可选，对齐 WeCom 简化版）
- Test: token 缓存、HTTP 路径拼装、WS 帧 opcode 处理（假 handler）

参考常量（来自 QwenPaw）：

```csharp
public static class QQOpcodes
{
    public const int Dispatch = 0;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Hello = 10;
    public const int HeartbeatAck = 11;
}

public static class QQIntents
{
    public const int PublicGuildMessages = 1 << 30;
    public const int DirectMessage = 1 << 12;
    public const int GroupAndC2C = 1 << 25;
    public const int Interaction = 1 << 26;
}
```

Token URL: `https://bots.qq.com/app/getAppAccessToken`  
API Base: `https://api.sgroup.qq.com`

出站路径（与 QwenPaw 一致）：
- C2C: `/v2/users/{openid}/messages`
- Group: `/v2/groups/{group_openid}/messages`
- Guild: `/channels/{channel_id}/messages`
- DM: `/dms/{guild_id}/messages`
- Interaction ack: `PUT /interactions/{id}`

- [ ] **Step 1–3: TDD 实现 Token/HTTP/WS 可测部分**

- [ ] **Step 4: 跳过 commit**

---

### Task 9: QQChannelBridge + 命令 + 终态回复

**Files:**
- Create: `src/Seeing.Gateway.QQ/QQChannelBridge.cs`
- Create: `src/Seeing.Gateway.QQ/QQCommandInterceptor.cs`（`/new` `/clear`，对齐 WeCom）
- Create: `src/Seeing.Gateway.QQ/QQSessionTracker.cs`（可简化自 WeCom）
- Modify: `QQServiceCollectionExtensions` 注册 Bridge 为 `IChannelBridge`

**行为：**
1. 入站消息 → parse → session → 可选 AckMessage
2. `SubmitAsync` → 合并增量文本 → **complete 时一条 HTTP 回复**
3. 超时/`CancelAsync(executionId)`
4. 不实现企微式 stream 气泡

- [ ] **Step 1: Bridge 单测用 mock `IGatewayClient` + mock `QQHttpApiClient`**

- [ ] **Step 2: 实现至 PASS**

- [ ] **Step 3: 跳过 commit**

---

### Task 10: QQ 媒体 + 权限键盘

**Files:**
- Create: `src/Seeing.Gateway.QQ/QQMediaFetcher.cs`
- Create: `src/Seeing.Gateway.QQ/QQPermissionPolicy.cs`（可复用 WeCom 策略模式）
- Create: `src/Seeing.Gateway.QQ/QQPermissionCardBuilder.cs`（markdown + keyboard JSON）
- Create: `src/Seeing.Gateway.QQ/QQPermissionResponder.cs`
- Test: keyboard payload、interaction parse、去重 ack

**权限流：**
1. Gateway `Permission` 事件 → 发带 keyboard 的消息
2. `INTERACTION_CREATE` → `PUT /interactions/{id}` → `RespondPermissionAsync`

- [ ] **Step 1–3: TDD 实现**

- [ ] **Step 4: README** `src/Seeing.Gateway.QQ/README.md`（配置、session、与 WeCom 差异：整段回复）

- [ ] **Step 5: 跳过 commit**

---

## Phase E — 接线与验收

### Task 11: WebUI / Host / 文档验收清单

**Files:**
- 确认 ChannelHost 引用 QQ（Task 6）
- 确认 WebUI CopyChannelHost 后输出目录含 `Seeing.Gateway.QQ.dll`
- Modify: `src/Seeing.Gateway/README.md`、`docs` 若有 Gateway 索引则加 QQ 链接（仅必要）
- Modify: design/plan 状态无需改代码

**手工验收（不写自动化也可）：**
1. 启动 WebUI → Gateway 客户端 → 见「QQ」内置卡片
2. 填写 AppId/Secret → 保存 → `.seeing/gateway-clients/qq.json` 存在
3. Enabled + 启动 → 进程 Running

- [ ] **Step 1:**

```bash
dotnet build samples/Seeing.Agent.WebUI
dotnet test tests/Seeing.Gateway.WeCom.Tests
dotnet test tests/Seeing.Gateway.QQ.Tests
dotnet test tests/Seeing.Gateway.Tests
dotnet test tests/Seeing.Gateway.Client.Tests
```

Expected: 全部 PASS

- [ ] **Step 2: 仓库扫描确认旧 API 已删**

```bash
rg "IChatOrchestrator" -g "*.cs" -A2
rg "ChatAsync|StopChatAsync|ExecuteChatAsync|/api/gateway/chat" -g "*.cs"
```

Expected: 无旧耦合 API；无 `ExecuteAsync` on `IChatOrchestrator`

- [ ] **Step 3: 跳过 commit**；向用户汇报完成与残余风险

---

## Spec coverage checklist

| Spec 项 | Task |
|---------|------|
| 删除 App ExecuteAsync/Stop | 1 |
| Gateway Submit/Subscribe/Cancel | 2–3 |
| 删除旧 HTTP/WS chat/stop | 3 |
| Client 新契约、删 Adapter | 4 |
| WeCom 迁移 | 5 |
| QQ 包 + 官方 API + 能力 C | 6–10 |
| WebUI 复用 Gateway 客户端 | 6, 11 |
| executionId 过滤 | 3, 5, 9 |
| 不 commit | Global + 各 Task 末步 |

## Placeholder / 一致性自检

- 帧名统一：`Submit`/`SubmitAck`/`Cancel`/`CancelAck`/`ExecutionComplete`
- Client 方法统一：`SubmitAsync`/`CancelAsync`/`SubscribeAsync`
- QQ ChannelId 恒为 `"qq"`，Options 节 `"QQ"`
- 无 TBD；媒体/频道在 Task 8–10 有明确路径与事件名
