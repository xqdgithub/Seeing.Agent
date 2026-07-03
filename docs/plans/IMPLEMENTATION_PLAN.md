# Seeing.Agent.NewTui 实施计划

**生成时间**: 2026-04-10  
**状态**: Oracle 审查通过  
**预计工期**: 2-3 天

---

## 一、审查结论

### Oracle 审查结果

**✅ 设计合理性通过（附条件）**

核心架构正确复用 Seeing.Agent 抽象，需补充：
- 事件处理逻辑（必须）
- 线程安全机制（必须）
- 工具调用显示（推荐）
- 取消操作支持（推荐）

### 审查意见摘要

1. **AgentRunner 职责合理** - 封装事件流处理，避免 View 层复杂逻辑
2. **AppState 足够简单** - 字段+事件模式，符合 TUI 场景
3. **需要补充**：线程安全通知、完整事件处理、取消操作

---

## 二、架构设计

### 2.1 架构图

```
┌────────────────────────────────────────────────────┐
│            Seeing.Agent.NewTui (单进程)             │
│                                                     │
│  TUI 层                          │
│  ├─ HomeView (Logo + 大输入框)                      │
│  ├─ SessionView (消息列表 + 输入框)                  │
│  └─ StatusBar (Agent/Model/状态)                    │
│                                                     │
│  状态层 (简单字段)                                   │
│  ├─ CurrentSession: SessionData                     │
│  ├─ StreamingContent: StringBuilder                 │
│  ├─ ActiveToolCalls: List<ToolCallDisplay>          │
│  └─ IsProcessing: bool                              │
│                                                     │
│  服务封装层                                          │
│  ├─ TuiPermissionChannel (实现 IPermissionChannel)  │
│  └─ AgentRunner (封装 AgentExecutor 调用)           │
│                                                     │
└────────────────────────────────────────────────────┘
                    ↓ 直接调用
┌────────────────────────────────────────────────────┐
│         Seeing.Agent 现有服务 (不修改)               │
│  AgentExecutor │ SessionManager │ AgentRegistry     │
│  ToolInvoker │ IHookManager │ IRuleEngine           │
└────────────────────────────────────────────────────┘
```

### 2.2 核心集成点

| 组件 | 职责 | TUI 如何使用 |
|------|------|-------------|
| `AgentExecutor` | 统一执行引擎 | 订阅 `IAsyncEnumerable<IMessageEvent>` 事件流 |
| `SessionManager` | 会话管理 | 直接调用管理会话 |
| `AgentRegistry` | Agent 注册 | 获取可用 Agent 列表 |
| `IPermissionChannel` | 权限请求 | **TUI 实现** - 弹窗确认权限 |

---

## 三、项目结构

```
src/Seeing.Agent.NewTui/
├── Program.cs                      # 入口：DI + Terminal.Gui 初始化
├── App.cs                          # 主应用类
│
├── Views/
│   ├── HomeView.cs                 # 首页：Logo + 输入框
│   └── SessionView.cs              # 会话页：消息列表 + 输入框
│
├── Components/
│   └── MessageList.cs              # 消息列表渲染（支持工具调用）
│
├── Dialogs/
│   ├── PermissionDialog.cs         # 权限确认弹窗
│   ├── SessionListDialog.cs        # 会话列表
│   └── AgentSelectDialog.cs        # Agent 选择
│
├── State/
│   └── AppState.cs                 # 全局状态容器（线程安全）
│
└── Services/
    ├── TuiPermissionChannel.cs     # 实现 IPermissionChannel
    └── AgentRunner.cs              # 封装 AgentExecutor
```

---

## 四、实施计划

### Phase 1: 基础框架（3 小时）

| 任务 | 文件 | 说明 |
|------|------|------|
| 创建项目 | `Seeing.Agent.NewTui.csproj` | Terminal.Gui v2.0 + Seeing.Agent 引用 |
| DI 注册 | `Program.cs` | AddSeeingAgent() + TUI 服务 |
| 状态容器 | `State/AppState.cs` | 线程安全通知 + CancellationToken |
| 同步上下文 | `Program.cs` | TerminalGuiSynchronizationContext |

### Phase 2: 权限通道（2 小时）

| 任务 | 文件 | 说明 |
|------|------|------|
| 权限通道 | `Services/TuiPermissionChannel.cs` | TaskCompletionSource 实现 |
| 权限弹窗 | `Dialogs/PermissionDialog.cs` | Allow/Deny/Always Allow 按钮 |

### Phase 3: 核心服务（3 小时）

| 任务 | 文件 | 说明 |
|------|------|------|
| Agent 封装 | `Services/AgentRunner.cs` | ExecuteAsync + 事件处理 |
| 事件处理 | `AgentRunner.HandleEvent()` | StreamDelta/ToolCall/Error |

### Phase 4: UI 组件（4 小时）

| 任务 | 文件 | 说明 |
|------|------|------|
| 消息列表 | `Components/MessageList.cs` | 工具调用状态显示 |
| 首页视图 | `Views/HomeView.cs` | Logo + 输入框 |
| 会话视图 | `Views/SessionView.cs` | 流式渲染 + 取消按钮 |

### Phase 5: 测试调试（2 小时）

| 测试项 | 验证点 |
|--------|--------|
| 基础对话 | 用户消息 → Agent 响应流式显示 |
| 工具调用 | 权限弹窗 → 工具执行 → 结果显示 |
| 取消操作 | Ctrl+C 取消 → 状态恢复 |
| 错误处理 | Agent 未找到 → 错误提示 |
| 多轮对话 | SessionData.Messages 累积 |

---

## 五、关键代码实现

### 5.1 项目文件

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Terminal.Gui" Version="2.0.0" />
    <ProjectReference Include="..\Seeing.Agent\Seeing.Agent.csproj" />
  </ItemGroup>
</Project>
```

### 5.2 AppState（线程安全）

```csharp
public class AppState
{
    private readonly SynchronizationContext? _syncContext;
    private CancellationTokenSource? _currentCts;
    
    public SessionData? CurrentSession { get; set; }
    public string CurrentAgent { get; set; } = "build";
    public bool IsProcessing { get; private set; }
    public StringBuilder StreamingContent { get; } = new();
    public List<ToolCallDisplay> ActiveToolCalls { get; } = new();
    public string? LastError { get; set; }
    
    public event Action? StateChanged;
    
    public AppState() => _syncContext = SynchronizationContext.Current;
    
    public CancellationToken StartProcessing()
    {
        _currentCts = new CancellationTokenSource();
        IsProcessing = true;
        StreamingContent.Clear();
        ActiveToolCalls.Clear();
        NotifyChanged();
        return _currentCts.Token;
    }
    
    public void EndProcessing()
    {
        IsProcessing = false;
        _currentCts?.Dispose();
        _currentCts = null;
        NotifyChanged();
    }
    
    public void CancelProcessing() => _currentCts?.Cancel();
    
    public void NotifyChanged()
    {
        if (_syncContext != null)
            _syncContext.Post(_ => StateChanged?.Invoke(), null);
        else
            StateChanged?.Invoke();
    }
}

public record ToolCallDisplay
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ToolCallStatus Status { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}
```

### 5.3 AgentRunner（核心事件处理）

```csharp
public class AgentRunner
{
    private readonly AgentExecutor _executor;
    private readonly AgentRegistry _registry;
    private readonly SessionManager _sessions;
    private readonly TuiPermissionChannel _permission;
    private readonly AppState _state;
    private readonly IServiceProvider _services;
    
    public async Task SendMessageAsync(string input)
    {
        var ct = _state.StartProcessing();
        
        try
        {
            _state.CurrentSession ??= await _sessions.CreateSessionAsync();
            _state.CurrentSession.AddMessage(new ChatMessage { Role = ChatRole.User, Content = input });
            
            var agent = _registry.GetOrCreateAgentInstance(_state.CurrentAgent);
            var definition = AgentDefinition.FromAgent(agent!);
            
            var context = new AgentContext
            {
                SessionId = _state.CurrentSession.SessionId,
                Services = _services,
                PermissionChannel = _permission,
                History = _state.CurrentSession.Messages.ToList(),
                WorkingDirectory = Directory.GetCurrentDirectory()
            };
            
            await foreach (var evt in _executor.ExecuteAsync(definition, context, ct))
            {
                HandleEvent(evt);
                if (ct.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) { _state.LastError = "Cancelled"; }
        catch (Exception ex) { _state.LastError = ex.Message; }
        finally { _state.EndProcessing(); }
    }
    
    private void HandleEvent(IMessageEvent evt)
    {
        switch (evt)
        {
            case StreamDeltaEvent delta:
                _state.StreamingContent.Append(delta.ContentDelta);
                break;
            case StreamCompleteEvent complete:
                _state.CurrentSession?.AddMessage(complete.Message);
                _state.StreamingContent.Clear();
                break;
            case ToolCallEvent tool:
                UpdateToolCall(tool);
                break;
            case ErrorEvent error:
                _state.LastError = error.Message;
                break;
        }
        _state.NotifyChanged();
    }
}
```

### 5.4 TuiPermissionChannel

```csharp
public class TuiPermissionChannel : IPermissionChannel
{
    public async Task<PermissionDecision> RequestToolPermissionAsync(
        string toolName, object? arguments, AgentContext context)
    {
        var tcs = new TaskCompletionSource<PermissionDecision>();
        
        Application.MainLoop.Invoke(() =>
        {
            var dialog = new PermissionDialog(toolName, arguments, tcs.SetResult);
            Application.Run(dialog);
        });
        
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await tcs.Task.WaitAsync(timeout.Token);
    }
}
```

---

## 六、依赖清单

### NuGet 包

```xml
<PackageReference Include="Terminal.Gui" Version="2.0.0" />
```

### 项目引用

```xml
<ProjectReference Include="..\Seeing.Agent\Seeing.Agent.csproj" />
```

### 复用的 Seeing.Agent 服务

- `AgentExecutor` - 执行引擎
- `SessionManager` - 会话管理
- `AgentRegistry` - Agent 注册表
- `ToolInvoker` - 工具调用器
- `IRuleEngine` - 权限规则引擎
- `IHookManager` - Hook 管理

---

## 七、风险和缓解

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| Terminal.Gui UI 更新限制 | 中 | 使用 SynchronizationContext 确保 UI 线程安全 |
| 流式渲染性能 | 低 | 16ms 防抖，批量更新 |
| 权限弹窗阻塞 | 低 | TaskCompletionSource 异步等待 |
| 取消操作状态不一致 | 中 | CancellationTokenSource 统一管理 |

---

## 八、后续扩展

### 低优先级功能

- [ ] 会话持久化（SQLite）
- [ ] 多会话切换
- [ ] Agent 选择 UI
- [ ] 主题切换
- [ ] Markdown 渲染
- [ ] Diff 显示

---

## 九、验收标准

- [ ] 用户可以发送消息并收到流式响应
- [ ] 工具调用显示状态（Pending/Running/Success/Failed）
- [ ] 权限请求弹窗正常工作
- [ ] Ctrl+C 可取消正在进行的请求
- [ ] 错误信息正确显示
- [ ] 多轮对话消息历史累积

---

**文档版本**: v1.0  
**最后更新**: 2026-04-10