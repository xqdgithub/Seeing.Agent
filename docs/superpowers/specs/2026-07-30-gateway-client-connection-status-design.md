# Gateway Client 连接状态检测

## 问题

Gateway Client（ChannelHost 子进程）启动后，`GatewayClientSupervisor` 仅通过进程 PID 存活性判断状态。进程可能正常运行但 WebSocket 从未连上 Gateway Server，"运行中"状态有误导性。

## 目标

- Gateway Client 卡片能区分"进程存活但未连接"与"真正已连接"
- 不修改 ChannelHost 子进程代码
- 利用 Server 端已有的 `GatewayConnectionManager` 数据

## 改动

### 1. GatewayConnectionManager 新增查询方法

**文件：** `src/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs`

```csharp
public IReadOnlyCollection<string> GetRegisteredChannels()
    => _channelRegistrations.Keys.ToArray();
```

### 2. AdminEndpoints 新增端点

**文件：** `src/Seeing.Agent.Gateway/Endpoints/AdminEndpoints.cs`

```csharp
app.MapGet("/api/admin/channels/connected", GetConnectedChannelsAsync);
```

返回已注册 channel 列表：

```json
{ "channels": ["wecom"] }
```

直接从 `GatewayConnectionManager.GetRegisteredChannels()` 获取，零依赖。

### 3. 新增 Disconnected 状态

**文件：** `src/Seeing.Agent/Configuration/GatewayClientsOptions.cs`

```csharp
public const string Disconnected = "Disconnected"; // 进程存活但 WebSocket 未连接
```

### 4. GatewayClientSupervisor 增加连通性检测

**文件：** `samples/Seeing.Agent.WebUI/Services/GatewayClientSupervisor.cs`

**`RefreshStatusAsync` 逻辑变更：**

```
进程存活?
  ├─ 否 → 原有逻辑（Stopped/Error）
  └─ 是 → 请求 GET /api/admin/channels/connected
            ├─ channelId 在列表中 → Running（真正连接）
            └─ channelId 不在列表中 → Disconnected
```

**新增方法：**

```csharp
// 检查 channel 是否已连接到 Gateway Server
private async Task<bool> IsChannelConnectedAsync(string channelId, GatewayClientConnectionOptions connection, CancellationToken ct)
{
    try
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var url = $"{connection.BaseUrl.TrimEnd('/')}/api/admin/channels/connected";
        var response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return false;
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("channels", out var channels)) return false;
        return channels.EnumerateArray().Any(e =>
            e.GetString()?.Equals(channelId, StringComparison.OrdinalIgnoreCase) == true);
    }
    catch
    {
        return false; // 连不上 Server 也返回 false
    }
}
```

### 5. UI 更新

**文件：** `samples/Seeing.Agent.WebUI/Components/GatewayClients/GatewayClientCard.razor`

| 维度 | Disconnected 状态表现 |
|------|----------------------|
| `GetStateClass()` | `"state-disconnected"` |
| `GetStatusColor()` | `"orange"` |
| `GetStatusText()` | `"未连接"` |
| 操作按钮 | 显示"停止""重启"（和 Running 一样） |

`LastError` 显示 "进程已启动但未连接到 Gateway Server" 提示。

### 6. GatewayClientsPage 统计更新

**文件：** `samples/Seeing.Agent.WebUI/Pages/GatewayClientsPage.razor`

- 新增统计项："未连接"数量（`Disconnected` 状态计数）
- 已停止统计改为排除 Disconnected（或保持原有逻辑不变，看实际体验）

## 边界情况

| 场景 | 预期行为 |
|------|---------|
| Gateway Server 未启动 | Disconnected（HTTP 请求失败） |
| Client 刚启动，WebSocket 还未注册 channel | Disconnected（暂未连上） |
| WebSocket 断线重连中 | Disconnected（重连期间） |
| Server 正常运行，Client WebSocket 已注册 | Running |
| 进程退出 | 走原有 Stopped/Error 逻辑 |

## 不涉及

- 不修改 ChannelHost 子进程
- 不修改 GatewayClientRuntimeState 结构（Disconnected 是动态判定，不持久化到 state.json）
- 不添加轮询或后台定时检查（仅在页面刷新时检测）
