# Gateway Client 连接状态检测 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gateway Client 卡片能区分"进程存活但未连接"（Disconnected）与"真正已连接"（Running），不再仅凭进程 PID 判断。

**Architecture:** 在 GatewayConnectionManager 新增查询方法 → AdminEndpoints 暴露 HTTP 端点 → Supervisor 刷新状态时调用端点 → UI 展示新状态。不修改 ChannelHost 子进程。

**Tech Stack:** C# ASP.NET Core Minimal API, AntDesign Blazor Tag 组件, HttpClient

---

### Task 1: GatewayConnectionManager 新增 GetRegisteredChannels 方法

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs`

- [ ] **Step 1: 添加查询方法**

在 `Unregister` 方法之后，`SubscribeSession` 之前，添加：

```csharp
/// <summary>获取当前已注册的所有 channel ID</summary>
public IReadOnlyCollection<string> GetRegisteredChannels()
    => _channelRegistrations.Keys.ToArray();
```

- [ ] **Step 2: 构建验证**

```bash
dotnet build src/Seeing.Agent.Gateway
```
Expected: 编译通过，无新增警告。

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs
git commit -m "feat: add GatewayConnectionManager.GetRegisteredChannels()"
```

---

### Task 2: AdminEndpoints 新增 channels/connected 端点

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Endpoints/AdminEndpoints.cs`

- [ ] **Step 1: 在 MapAdminEndpoints 中注册新端点**

找到 `MapAdminEndpoints` 方法（第 23–29 行），在 `app.MapGet("/api/admin/jobs", ...)` 之后添加：

```csharp
app.MapGet("/api/admin/channels/connected", GetConnectedChannelsAsync);
```

- [ ] **Step 2: 添加 handler 方法**

在 `GetJobsAsync` 方法之后，类结束 `}` 之前，添加：

```csharp
private static IResult GetConnectedChannelsAsync(GatewayConnectionManager connectionManager)
{
    var channels = connectionManager.GetRegisteredChannels();
    return Results.Ok(new { channels });
}
```

- [ ] **Step 3: 构建验证**

```bash
dotnet build src/Seeing.Agent.Gateway
```
Expected: 编译通过。

- [ ] **Step 4: 提交**

```bash
git add src/Seeing.Agent.Gateway/Endpoints/AdminEndpoints.cs
git commit -m "feat: add GET /api/admin/channels/connected endpoint"
```

---

### Task 3: 新增 Disconnected 状态常量

**Files:**
- Modify: `src/Seeing.Agent/Configuration/GatewayClientsOptions.cs`

- [ ] **Step 1: 添加状态常量**

在 `public const string Disabled = "Disabled";` 之后添加：

```csharp
/// <summary>进程存活但 WebSocket 未连接到 Gateway Server</summary>
public const string Disconnected = "Disconnected";
```

- [ ] **Step 2: 构建验证**

```bash
dotnet build src/Seeing.Agent
```
Expected: 编译通过。

- [ ] **Step 3: 提交**

```bash
git add src/Seeing.Agent/Configuration/GatewayClientsOptions.cs
git commit -m "feat: add Disconnected status for GatewayClientStatuses"
```

---

### Task 4: GatewayClientSupervisor 增加连通性检测

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Services/GatewayClientSupervisor.cs`

- [ ] **Step 1: 修改 RefreshStatusAsync 方法**

将第 53–58 行的 Running 判定逻辑替换为带连通性检测的版本。找到：

```csharp
        if (state.ProcessId is int pid && IsProcessAlive(pid))
        {
            client.Status = GatewayClientStatuses.Running;
            client.ProcessId = pid;
            client.LastError = null;
            return;
        }
```

替换为：

```csharp
        if (state.ProcessId is int pid && IsProcessAlive(pid))
        {
            client.ProcessId = pid;

            var connected = await IsChannelConnectedAsync(client.ChannelId, client.Gateway, ct);
            if (connected)
            {
                client.Status = GatewayClientStatuses.Running;
                client.LastError = null;
            }
            else
            {
                client.Status = GatewayClientStatuses.Disconnected;
                client.LastError = "进程已启动但未连接到 Gateway Server";
            }

            return;
        }
```

- [ ] **Step 2: 添加 IsChannelConnectedAsync 私有方法**

在 `GetSearchRoots` 方法之前，`ChannelHostLocation` record 之前，添加：

```csharp
    private static async Task<bool> IsChannelConnectedAsync(
        string channelId,
        GatewayClientConnectionOptions connection,
        CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"{connection.BaseUrl.TrimEnd('/')}/api/admin/channels/connected";
            var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("channels", out var channels))
                return false;

            foreach (var element in channels.EnumerateArray())
            {
                if (element.GetString()?.Equals(channelId, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
```

- [ ] **Step 3: 添加需要的 using**

确认文件顶部已有 `using System.Text.Json;`，如果没有则添加。

- [ ] **Step 4: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 编译通过。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Services/GatewayClientSupervisor.cs
git commit -m "feat: GatewayClientSupervisor checks WebSocket connectivity via admin endpoint"
```

---

### Task 5: GatewayClientCard UI 支持 Disconnected 状态

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/GatewayClients/GatewayClientCard.razor`

- [ ] **Step 1: 更新 GetStateClass 方法**

找到 `GetStateClass()` 方法（第 66–72 行），在 `GatewayClientStatuses.Running` case 之后，添加：

```csharp
        GatewayClientStatuses.Disconnected => "state-disconnected",
```

完整方法变为：

```csharp
    private string GetStateClass() => Client.Status switch
    {
        GatewayClientStatuses.Running => "state-connected",
        GatewayClientStatuses.Disconnected => "state-disconnected",
        GatewayClientStatuses.Error => "state-error",
        GatewayClientStatuses.Disabled => "state-disabled",
        _ => "state-pending"
    };
```

- [ ] **Step 2: 更新 GetStatusColor 方法**

找到 `GetStatusColor()` 方法（第 74–81 行），在 `GatewayClientStatuses.Starting` case 之前，添加：

```csharp
        GatewayClientStatuses.Disconnected => "warning",
```

完整方法变为：

```csharp
    private string GetStatusColor() => Client.Status switch
    {
        GatewayClientStatuses.Running => "green",
        GatewayClientStatuses.Disconnected => "warning",
        GatewayClientStatuses.Starting => "processing",
        GatewayClientStatuses.Error => "red",
        GatewayClientStatuses.Disabled => "default",
        _ => "orange"
    };
```

- [ ] **Step 3: 更新 GetStatusText 方法**

找到 `GetStatusText()` 方法（第 83–90 行），在 `GatewayClientStatuses.Running` case 之后，添加：

```csharp
        GatewayClientStatuses.Disconnected => "未连接",
```

完整方法变为：

```csharp
    private string GetStatusText() => Client.Status switch
    {
        GatewayClientStatuses.Running => "运行中",
        GatewayClientStatuses.Disconnected => "未连接",
        GatewayClientStatuses.Starting => "启动中",
        GatewayClientStatuses.Error => "错误",
        GatewayClientStatuses.Disabled => "已禁用",
        _ => "已停止"
    };
```

- [ ] **Step 4: 更新操作按钮区域**

找到 `Client.Status == GatewayClientStatuses.Running` 的按钮逻辑（第 42–46 行），添加 Disconnected 的按钮条件：

```csharp
            else if (Client.Status is GatewayClientStatuses.Running or GatewayClientStatuses.Disconnected)
            {
                <Button Size="@ButtonSize.Small" OnClick="@(() => OnStop.InvokeAsync(Client.ChannelId))">停止</Button>
                <Button Size="@ButtonSize.Small" OnClick="@(() => OnRestart.InvokeAsync(Client.ChannelId))">重启</Button>
            }
```

原条件 `Client.Status == GatewayClientStatuses.Running` 改为 pattern matching `Client.Status is GatewayClientStatuses.Running or GatewayClientStatuses.Disconnected`。

- [ ] **Step 5: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 编译通过。

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Components/GatewayClients/GatewayClientCard.razor
git commit -m "feat: show Disconnected status in GatewayClientCard with warning color"
```

---

### Task 6: GatewayClientsPage 统计更新

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/GatewayClientsPage.razor`

- [ ] **Step 1: 新增"未连接"统计项**

在 Stats 区域（第 27–49 行），"已停止"统计项之后，添加：

```razor
        <div class="stat-item">
            <div class="stat-icon"><Icon Type="@IconType.Outline.Disconnect" /></div>
            <div class="stat-content">
                <span class="stat-value">@_clients.Count(c => c.Status == GatewayClientStatuses.Disconnected)</span>
                <span class="stat-label">未连接</span>
            </div>
        </div>
```

- [ ] **Step 2: 构建验证**

```bash
dotnet build samples/Seeing.Agent.WebUI
```
Expected: 编译通过。

- [ ] **Step 3: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/GatewayClientsPage.razor
git commit -m "feat: add disconnected count stat to GatewayClientsPage"
```

---

### Task 7: 端到端验证

- [ ] **Step 1: 完整构建**

```bash
dotnet build
```
Expected: 全部编译通过，零警告。

- [ ] **Step 2: 运行现有测试**

```bash
dotnet test
```
Expected: 所有现有测试通过。

- [ ] **Step 3: 启动 WebUI 手动验证**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

手动验证场景：
1. 打开 `/gateway-clients` 页面
2. 观察无 channel 连接时，状态是否显示 "未连接"
3. 启动一个 channel，观察是否从 "未连接" 变为 "运行中"
4. 停止 Gateway Server 后刷新页面，观察运行中的 channel 是否变为 "未连接"

- [ ] **Step 4: 提交（如有修正）**

```bash
git add -u
git commit -m "chore: end-to-end verification adjustments"
```
