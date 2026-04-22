# Work Plan: 集成 AgentExecutor 到 WebUI

## TL;DR

> **Quick Summary**: 创建 AgentExecutorAdapter 服务将 Core 事件转换为 WebUI 事件，替换 Session.razor 的模拟响应为真实 Agent 调用，并注册 BlazorPermissionChannel 实现权限请求 UI 交互。
> 
> **Deliverables**:
> - `Services/AgentExecutorAdapter.cs` - 事件转换适配层
> - `Services/BlazorPermissionChannel.cs` - 权限请求 UI 通道
> - 修改 `Pages/Session.razor` - 替换 SimulateAgentResponseAsync
> - 修改 `Program.cs` - 注册权限通道
> - 修改 `Services/EventStreamHandler.cs` - 添加推理过程处理
> 
> **Estimated Effort**: Medium (4-6 小时)
> **Parallel Execution**: YES - 2 waves
> **Critical Path**: Task 1 → Task 4 → Task 5 → Task 6

---

## Context

### Original Request
用户要求在 `samples\Seeing.Agent.WebUI\Pages\Session.razor` 中使用真实的 `AgentExecutor` 实现替换 `SimulateAgentResponseAsync` 模拟方法。

### Interview Summary
**Key Discussions**:
- **事件接口冲突**: WebUI 定义了本地 `IMessageEvent`，Core 库有独立的 `IMessageEvent`，需要适配层转换
- **DI 配置**: AgentExecutor 已通过 `AddSeeingAgent()` 注册，但权限通道未注册
- **取消处理**: 使用 `CreateLinkedTokenSource` 组合用户取消 + 会话取消
- **流式处理**: IAsyncEnumerable + StateHasChanged 节流（每 30-50 字符刷新）

**Research Findings**:
- AgentExecutor.ExecuteAsync 返回 `IAsyncEnumerable<IMessageEvent>`
- 事件类型映射需要处理: StreamDeltaEvent, StreamCompleteEvent, ToolCallEvent, SubAgentEvent, ErrorEvent
- BlazorPermissionChannel 需要实现 `IPermissionChannel.RequestToolPermissionAsync()` 方法

### Metis Review
**Identified Gaps** (addressed):
- **事件属性不匹配**: Core 使用 ContentDelta/ReasoningDelta，WebUI 使用 Delta → 适配器映射
- **ToolResultEvent 处理**: Core 的工具结果在 ToolCallEvent 中，需要转换逻辑
- **推理过程显示**: StreamDeltaEvent 有 ReasoningDelta，EventStreamHandler 未处理 → 需扩展
- **BlazorPermissionChannel 注册**: 未在 DI 中注册 → 需添加

---

## Work Objectives

### Core Objective
将 Session.razor 的模拟响应替换为真实的 AgentExecutor 调用，实现完整的事件转换和权限请求 UI 交互。

### Concrete Deliverables
- `Services/AgentExecutorAdapter.cs` - Core 事件 → WebUI 事件适配器
- `Services/BlazorPermissionChannel.cs` - IPermissionChannel 实现（权限 Modal）
- `Pages/Session.razor` - SimulateAgentResponseAsync 替换为真实调用
- `Program.cs` - BlazorPermissionChannel DI 注册

### Definition of Done
- [ ] 输入消息后，UI 显示真实 Agent 响应（非模拟文本）
- [ ] 流式内容逐字显示，推理过程正确渲染
- [ ] 工具调用显示卡片，状态正确更新
- [ ] 点击取消按钮，响应立即停止
- [ ] 权限请求触发 Modal（如实现）

### Must Have
- AgentExecutorAdapter 服务正确转换所有 Core 事件类型
- Session.razor 正确注入 AgentExecutorAdapter 并调用
- 取消令牌正确链接（SessionState.CancellationTokenSource + 用户取消）
- 流式 UI 更新正确节流

### Must NOT Have (Guardrails)
- **不修改 Core 库的事件类型** - 保持 Core.Events.IMessageEvent 接口不变
- **不修改 EventStreamHandler 的事件处理逻辑** - 适配器负责转换，EventStreamHandler 保持不变
- **不添加新的 MessageViewModel 字段** - 保持现有 UI 绑定兼容
- **不处理多轮对话历史累积** - 首次实现只处理单轮对话，History 初始为空
- **不实现 ToolResultEvent 独立处理** - 工具结果在 ToolCallEvent 中已包含

---

## Verification Strategy (MANDATORY)

> **ZERO HUMAN INTERVENTION** - ALL verification is agent-executed. No exceptions.

### Test Decision
- **Infrastructure exists**: YES (WebUI 项目有测试基础设施)
- **Automated tests**: Tests-after (先实现后验证)
- **Framework**: xUnit (项目已有配置)
- **Agent-Executed QA**: Playwright 浏览器自动化测试

### QA Policy
Every task includes agent-executed QA scenarios using Playwright:
- Navigate to WebUI page
- Fill input, submit message
- Assert streaming content appears
- Assert tool call cards display
- Test cancellation
- Capture screenshots as evidence

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Foundation - 3 tasks, can start immediately):
├── Task 1: 创建 AgentExecutorAdapter 服务 [quick]
├── Task 2: 创建 BlazorPermissionChannel 服务 [quick]
└── Task 3: 扩展 EventStreamHandler 处理推理过程 [quick]

Wave 2 (Integration - 3 tasks, after Wave 1):
├── Task 4: 注册 BlazorPermissionChannel 到 DI [quick]
├── Task 5: 替换 Session.razor SimulateAgentResponseAsync [deep]
└── Task 6: 集成测试验证 [unspecified-high]

Wave FINAL (After ALL tasks — 4 parallel reviews):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
├── Task F3: Real manual QA (unspecified-high + playwright)
└── Task F4: Scope fidelity check (deep)
-> Present results -> Get explicit user okay

Critical Path: Task 1 → Task 5 → Task 6 → F1-F4 → user okay
Parallel Speedup: ~50% faster than sequential
Max Concurrent: 3
```

### Dependency Matrix

- **1-3**: - (can start immediately)
- **4**: - (can start immediately, independent)
- **5**: 1, 2, 3 - (needs adapter and permission channel)
- **6**: 4, 5 - (needs DI registration and integration)
- **F1-F4**: 6 - (needs all implementation complete)

### Agent Dispatch Summary

- **Wave 1**: 3 tasks - T1-T3 → `quick`
- **Wave 2**: 3 tasks - T4 → `quick`, T5 → `deep`, T6 → `unspecified-high`
- **FINAL**: 4 tasks - F1 → `oracle`, F2 → `unspecified-high`, F3 → `unspecified-high`, F4 → `deep`

---

## TODOs

- [x] 1. 创建 AgentExecutorAdapter 服务

  **What to do**:
  - 创建 `Services/AgentExecutorAdapter.cs` 文件
  - 实现 Core.Events.IMessageEvent → WebUI.Services.IMessageEvent 的转换逻辑
  - 处理所有事件类型映射: StreamDeltaEvent, StreamCompleteEvent, ToolCallEvent, SubAgentEvent, ErrorEvent
  - 添加消息 ID 生成逻辑（Core 事件使用 SessionId，WebUI 使用 MessageId）

  **事件映射规则**:
  ```
  Core.StreamDeltaEvent → WebUI.StreamDeltaEvent
    - ContentDelta → Delta
    - SessionId → MessageId (使用 SessionId 或生成 GUID)
    - ReasoningDelta → 单独处理（Task 3）

  Core.StreamCompleteEvent → WebUI.StreamCompleteEvent
    - Message.Content → 传递给 EventStreamHandler
    - Message.ReasoningContent → Reasoning

  Core.ToolCallEvent → WebUI.ToolCallEvent
    - ToolName → ToolName
    - Arguments (object) → Payload (JSON string)
    - ToolCallId → ToolCallId
    - Status.Success → 发送 ToolResultEvent.Success=true
    - Status.Failed → 发送 ToolResultEvent.Success=false, Error

  Core.SubAgentEvent → WebUI.SubAgentEvent
    - AgentName → AgentName
    - Status → Status
    - Result → Output
    - Error → Output (包含错误信息)

  Core.ErrorEvent → WebUI.ErrorEvent
    - Message → Message
    - Source → Code
  ```

  **Must NOT do**:
  - 不要修改 Core 库的事件类型定义
  - 不要添加新的 WebUI IMessageEvent 接口成员
  - 不要在适配器中直接更新 SessionState

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单一服务创建，逻辑清晰，主要是类型转换
  - **Skills**: []
    - 无特殊技能需求

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2, 3)
  - **Blocks**: Task 5
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Agent/Core/Events/MessageEventTypes.cs:54-186` - Core 事件类型定义
  - `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:12-97` - WebUI 事件类型定义
  - `src/Seeing.Agent/Core/AgentExecutor.cs:62-248` - ExecuteAsync 返回的事件类型

  **Acceptance Criteria**:
  - [ ] 文件创建: `samples/Seeing.Agent.WebUI/Services/AgentExecutorAdapter.cs`
  - [ ] 类定义包含 `ExecuteAndAdaptAsync` 方法返回 `IAsyncEnumerable<IMessageEvent>`
  - [ ] 所有 5 种 Core 事件类型都有对应的转换逻辑
  - [ ] `dotnet build samples/Seeing.Agent.WebUI` → PASS

  **QA Scenarios**:
  ```
  Scenario: 事件转换正确性验证
    Tool: Bash (dotnet build)
    Preconditions: AgentExecutorAdapter.cs 已创建
    Steps:
      1. dotnet build samples/Seeing.Agent.WebUI
      2. 检查无编译错误
    Expected Result: Build succeeded with 0 errors
    Evidence: .sisyphus/evidence/task-1-build.txt
  ```

  **Commit**: YES
  - Message: `feat(webui): add AgentExecutorAdapter for Core event conversion`
  - Files: `samples/Seeing.Agent.WebUI/Services/AgentExecutorAdapter.cs`

- [x] 2. 创建 BlazorPermissionChannel 服务

  **What to do**:
  - 创建 `Services/BlazorPermissionChannel.cs` 文件
  - 实现 `IPermissionChannel` 接口
  - 实现 `RequestToolPermissionAsync` 方法，触发 UI Modal 等待用户响应
  - 使用事件机制与 UI 通信（EventStreamHandler 或直接事件）

  **实现要点**:
  ```csharp
  public class BlazorPermissionChannel : IPermissionChannel
  {
      private readonly EventStreamHandler _eventHandler;
      private readonly SessionState _sessionState;
      
      // 存储 pending 权限请求，等待用户响应
      private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionDecision>> _pendingRequests;
      
      public async Task<PermissionDecision> RequestToolPermissionAsync(
          string toolName, 
          object? arguments, 
          AgentContext context)
      {
          var requestId = Guid.NewGuid().ToString();
          var tcs = new TaskCompletionSource<PermissionDecision>();
          _pendingRequests[requestId] = tcs;
          
          // 发送权限请求事件到 UI
          await _eventHandler.ProcessEventAsync(new PermissionRequestEvent
          {
              PermissionId = requestId,
              Permission = toolName,
              Resource = arguments?.ToString(),
              Message = $"工具 {toolName} 需要权限确认",
              RiskLevel = "medium"
          });
          
          // 等待用户响应（通过 UI Modal）
          return await tcs.Task;
      }
      
      // UI 调用此方法响应权限请求
      public void RespondToPermission(string requestId, PermissionAction action)
      {
          if (_pendingRequests.TryGetValue(requestId, out var tcs))
          {
              var decision = action == PermissionAction.Allow 
                  ? PermissionDecision.Allow() 
                  : PermissionDecision.Deny("用户拒绝");
              tcs.SetResult(decision);
              _pendingRequests.Remove(requestId);
          }
      }
  }
  ```

  **Must NOT do**:
  - 不要在构造函数中阻塞
  - 不要自动批准所有权限（安全风险）
  - 不要静默拒绝（需要通知用户）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单一服务创建，接口实现清晰
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 3)
  - **Blocks**: Task 4, Task 5
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Agent/Core/Interfaces/IPermissionChannel.cs` - IPermissionChannel 接口定义
  - `src/Seeing.Agent/Core/Interfaces/DefaultPermissionChannel.cs` - 默认实现参考
  - `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:86-96` - PermissionRequestEvent 定义

  **Acceptance Criteria**:
  - [ ] 文件创建: `samples/Seeing.Agent.WebUI/Services/BlazorPermissionChannel.cs`
  - [ ] 实现 IPermissionChannel 接口
  - [ ] RequestToolPermissionAsync 方法正确触发事件
  - [ ] `dotnet build samples/Seeing.Agent.WebUI` → PASS

  **QA Scenarios**:
  ```
  Scenario: 权限通道编译验证
    Tool: Bash (dotnet build)
    Preconditions: BlazorPermissionChannel.cs 已创建
    Steps:
      1. dotnet build samples/Seeing.Agent.WebUI
      2. 检查无编译错误
    Expected Result: Build succeeded with 0 errors
    Evidence: .sisyphus/evidence/task-2-build.txt
  ```

  **Commit**: YES (group with Task 1)
  - Message: `feat(webui): add AgentExecutorAdapter and BlazorPermissionChannel`
  - Files: `samples/Seeing.Agent.WebUI/Services/BlazorPermissionChannel.cs`

- [x] 3. 扩展 EventStreamHandler 处理推理过程

  **What to do**:
  - 在 `EventStreamHandler.cs` 的 `HandleStreamDelta` 方法中添加 ReasoningDelta 处理
  - 查找关联的 MessageViewModel，更新其 Reasoning 字段
  - 确保 UI 能正确显示推理过程

  **修改位置**: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:168-179`

  **修改内容**:
  ```csharp
  private void HandleStreamDelta(StreamDeltaEvent delta)
  {
      _sessionState.Messages.Add(delta.Delta);

      var message = FindOrCreatePendingMessage(delta.MessageId);
      if (message != null)
      {
          // 内容增量
          message.Content += delta.Delta;
          
          // 推理过程增量（新增）
          if (!string.IsNullOrEmpty(delta.Reasoning))
          {
              message.Reasoning += delta.Reasoning;
          }
          
          message.IsComplete = false;
      }
  }
  
  // 同时修改 StreamDeltaEvent 类定义，添加 Reasoning 属性
  public class StreamDeltaEvent : IMessageEvent
  {
      public string EventType => "StreamDelta";
      public string Delta { get; set; } = "";
      public string? Reasoning { get; set; }  // 新增
      public string MessageId { get; set; } = "";
  }
  ```

  **Must NOT do**:
  - 不要修改其他事件处理方法
  - 不要添加新的 MessageViewModel 字段
  - 不要修改 IMessageEvent 接口

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单文件小范围修改
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2)
  - **Blocks**: Task 5
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:168-179` - HandleStreamDelta 方法
  - `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs:20-25` - StreamDeltaEvent 类定义
  - `samples/Seeing.Agent.WebUI/Models/MessageViewModel.cs` - MessageViewModel Reasoning 字段

  **Acceptance Criteria**:
  - [ ] StreamDeltaEvent 类添加 Reasoning 属性
  - [ ] HandleStreamDelta 方法处理 Reasoning 字段
  - [ ] `dotnet build samples/Seeing.Agent.WebUI` → PASS

  **QA Scenarios**:
  ```
  Scenario: 推理过程处理验证
    Tool: Bash (dotnet build)
    Preconditions: EventStreamHandler 已修改
    Steps:
      1. dotnet build samples/Seeing.Agent.WebUI
    Expected Result: Build succeeded with 0 errors
    Evidence: .sisyphus/evidence/task-3-build.txt
  ```

  **Commit**: YES (group with Tasks 1, 2)
  - Message: `feat(webui): add AgentExecutorAdapter, BlazorPermissionChannel, and reasoning support`
  - Files: `samples/Seeing.Agent.WebUI/Services/EventStreamHandler.cs`

- [x] 4. 注册 BlazorPermissionChannel 到 DI

  **What to do**:
  - 在 `Program.cs` 中注册 BlazorPermissionChannel 为 IPermissionChannel
  - 确保注册为 Scoped（与 SessionState 生命周期一致）
  - 替换默认的 DefaultPermissionChannel 注册

  **修改位置**: `samples/Seeing.Agent.WebUI/Program.cs`

  **修改内容**:
  ```csharp
  // 在 AddSeeingAgent 之后添加
  builder.Services.AddScoped<IPermissionChannel, BlazorPermissionChannel>();
  builder.Services.AddScoped<AgentExecutorAdapter>();
  ```

  **注意**: AddSeeingAgent 内部已注册 IPermissionChannel 为 Singleton（DefaultPermissionChannel），需要在此处覆盖注册。

  **Must NOT do**:
  - 不要删除 AddSeeingAgent 调用
  - 不要注册为 Singleton（会导致 SessionState 不一致）
  - 不要注册为 Transient

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单文件小范围修改（DI 注册）
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Task 5 start prep)
  - **Blocks**: Task 5
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.WebUI/Program.cs:15-20` - 当前 DI 注册位置
  - `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs:531-544` - 默认 IPermissionChannel 注册

  **Acceptance Criteria**:
  - [ ] Program.cs 包含 `AddScoped<IPermissionChannel, BlazorPermissionChannel>`
  - [ ] Program.cs 包含 `AddScoped<AgentExecutorAdapter>`
  - [ ] `dotnet build samples/Seeing.Agent.WebUI` → PASS

  **QA Scenarios**:
  ```
  Scenario: DI 注册验证
    Tool: Bash (dotnet build)
    Preconditions: Program.cs 已修改
    Steps:
      1. dotnet build samples/Seeing.Agent.WebUI
    Expected Result: Build succeeded with 0 errors
    Evidence: .sisyphus/evidence/task-4-build.txt
  ```

  **Commit**: NO (group with Task 5 in Wave 2 commit)

- [x] 5. 替换 Session.razor SimulateAgentResponseAsync

  **What to do**:
  - 在 Session.razor 中注入 AgentExecutorAdapter 和 IAgentRegistry
  - 重写 `SimulateAgentResponseAsync` 方法，调用真实 AgentExecutor
  - 实现 AgentContext 构建（包含 SessionId, CancellationToken, PermissionChannel）
  - 处理事件流并更新 UI

  **修改位置**: `samples/Seeing.Agent.WebUI/Pages/Session.razor`

  **修改内容**:

  1. 添加 DI 注入（第 8-14 行附近）:
  ```razor
  @inject AgentExecutorAdapter AgentExecutorAdapter
  @inject IAgentRegistry AgentRegistry
  @inject IPermissionChannel PermissionChannel
  ```

  2. 替换 SimulateAgentResponseAsync 方法（第 405-463 行）:
  ```csharp
  private async Task SimulateAgentResponseAsync(string userInput, List<AttachmentViewModel>? attachments = null)
  {
      // 创建助手消息
      var assistantMessageId = Guid.NewGuid().ToString();
      var assistantMessage = new MessageViewModel
      {
          Id = assistantMessageId,
          Role = "assistant",
          Content = "",
          Reasoning = "",
          IsComplete = false
      };
      SessionState.AddMessageViewModel(assistantMessage);

      try
      {
          // 1. 获取 Agent 定义
          var agentInstance = AgentRegistry.GetOrCreateAgentInstance(SessionState.SelectedAgent);
          if (agentInstance == null)
          {
              throw new InvalidOperationException($"Agent '{SessionState.SelectedAgent}' not found");
          }
          var agentDefinition = AgentDefinition.FromAgent(agentInstance);

          // 2. 构建执行上下文
          var context = new AgentContext
          {
              SessionId = SessionState.SessionId,
              CancellationToken = SessionState.CancellationToken,
              PermissionChannel = PermissionChannel,
              History = new List<ChatMessage>
              {
                  new ChatMessage { Role = ChatRole.User, Content = userInput }
              },
              WorkingDirectory = Directory.GetCurrentDirectory()
          };

          // 3. 调用 AgentExecutor 并处理事件流
          var charCount = 0;
          await foreach (var webuiEvent in AgentExecutorAdapter.ExecuteAndAdaptAsync(
              agentDefinition, context, SessionState.CancellationToken))
          {
              SessionState.CancellationToken.ThrowIfCancellationRequested();

              // 通过 EventStreamHandler 处理事件
              await EventStreamHandler.ProcessEventAsync(webuiEvent);

              // UI 更新节流（每 30 字符或收到换行）
              if (webuiEvent is StreamDeltaEvent delta)
              {
                  charCount += delta.Delta?.Length ?? 0;
                  if (charCount >= 30 || delta.Delta?.Contains("\n") == true)
                  {
                      await InvokeAsync(StateHasChanged);
                      charCount = 0;
                  }
              }
              else
              {
                  // 其他事件立即更新 UI
                  await InvokeAsync(StateHasChanged);
              }
          }

          assistantMessage.IsComplete = true;
      }
      catch (OperationCanceledException)
      {
          assistantMessage.Content += "\n\n⚠️ 执行已取消";
          assistantMessage.IsComplete = true;
      }
      catch (Exception ex)
      {
          assistantMessage.Content += $"\n\n❌ 执行出错: {ex.Message}";
          assistantMessage.IsComplete = true;
          ErrorMessage = $"执行失败: {ex.Message}";
      }
  }
  ```

  3. 删除 SimulateToolCallAsync 方法（第 468-495 行）- 真实工具调用由 AgentExecutor 处理

  **Must NOT do**:
  - 不要删除 OnSubmitMessage 的整体结构
  - 不要修改 EventStreamHandler 的其他用法
  - 不要修改其他 UI 组件逻辑
  - 不要处理多轮对话历史累积（首次实现只处理单轮）

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 核心集成任务，涉及多个服务协作，需要仔细处理事件流和异常
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO (核心任务)
  - **Parallel Group**: Sequential (Wave 2, after Tasks 1-4)
  - **Blocks**: Task 6, Final Wave
  - **Blocked By**: Tasks 1, 2, 3, 4

  **References**:
  - `samples/Seeing.Agent.WebUI/Pages/Session.razor:405-463` - SimulateAgentResponseAsync 方法
  - `samples/Seeing.Agent.WebUI/Pages/Session.razor:329-400` - OnSubmitMessage 方法
  - `src/Seeing.Agent/Core/AgentExecutor.cs:62-66` - ExecuteAsync API
  - `src/Seeing.Agent/Core/Models/AgentModels.cs:10-75` - AgentContext 构建
  - `src/Seeing.Agent/Core/Models/AgentDefinition.cs:65-80` - AgentDefinition.FromAgent

  **Acceptance Criteria**:
  - [ ] DI 注入已添加: AgentExecutorAdapter, IAgentRegistry, IPermissionChannel
  - [ ] SimulateAgentResponseAsync 使用真实 AgentExecutor 调用
  - [ ] 取消令牌正确链接
  - [ ] SimulateToolCallAsync 方法已删除
  - [ ] `dotnet build samples/Seeing.Agent.WebUI` → PASS

  **QA Scenarios**:
  ```
  Scenario: 流式响应显示验证
    Tool: Playwright
    Preconditions: WebUI 运行中，LLM Provider 已配置
    Steps:
      1. Navigate to http://localhost:5000/session
      2. Wait for page load (selector: .session-page-container)
      3. Fill textarea with "Hello, what can you help me with?"
      4. Click send button (selector: button[type="submit"])
      5. Wait for assistant message (selector: .message.assistant, timeout: 30s)
      6. Assert message content not empty
      7. Assert message not contains "模拟响应" (mock response text)
    Expected Result: Assistant message displays real AI response
    Failure Indicators: Message contains "这是一个模拟响应" or empty content
    Evidence: .sisyphus/evidence/task-5-streaming.png

  Scenario: 取消操作验证
    Tool: Playwright
    Preconditions: WebUI 运行中
    Steps:
      1. Navigate to http://localhost:5000/session
      2. Fill textarea with long question
      3. Click send button
      4. Click cancel button (selector: button containing "取消" text)
      5. Wait for system message (selector: .message.system)
      6. Assert message contains "取消" or "cancelled"
    Expected Result: UI shows cancellation message, execution stops
    Evidence: .sisyphus/evidence/task-5-cancel.png

  Scenario: 错误处理验证
    Tool: Playwright
    Preconditions: WebUI 运行中，LLM Provider 未配置或配置错误
    Steps:
      1. Navigate to http://localhost:5000/session
      2. Fill textarea with test message
      3. Click send button
      4. Wait for error alert or system message
      5. Assert error message displayed
    Expected Result: UI shows error message gracefully
    Evidence: .sisyphus/evidence/task-5-error.png
  ```

  **Commit**: YES
  - Message: `feat(webui): integrate real AgentExecutor in Session.razor`
  - Files: `samples/Seeing.Agent.WebUI/Pages/Session.razor`, `samples/Seeing.Agent.WebUI/Program.cs`

- [x] 6. 集成测试验证

  **What to do**:
  - 运行所有 QA 场景验证
  - 检查编译无错误
  - 确保流式响应正常工作
  - 确保取消功能正常
  - 确保错误处理正常

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: QA 验证需要仔细检查每个场景
  - **Skills**: [`playwright-cli`]
    - `playwright-cli`: 需要用于浏览器自动化测试

  **Parallelization**:
  - **Can Run In Parallel**: NO (需要所有实现完成)
  - **Parallel Group**: Sequential (after Task 5)
  - **Blocks**: Final Wave
  - **Blocked By**: Task 5

  **References**:
  - 所有前述任务的 QA Scenarios

  **Acceptance Criteria**:
  - [ ] `dotnet build samples/Seeing.Agent.WebUI` → PASS (0 errors, 0 warnings)
  - [ ] WebUI 可启动运行
  - [ ] 流式响应正常显示（真实内容，非模拟）
  - [ ] 取消按钮功能正常
  - [ ] 错误情况正确处理

  **QA Scenarios**:
  ```
  Scenario: 编译验证
    Tool: Bash
    Steps:
      1. dotnet build samples/Seeing.Agent.WebUI --no-incremental
    Expected Result: Build succeeded
    Evidence: .sisyphus/evidence/task-6-build.txt

  Scenario: 运行验证
    Tool: Bash
    Steps:
      1. dotnet run --project samples/Seeing.Agent.WebUI --urls=http://localhost:5000
      2. Wait for application start (timeout: 30s)
      3. curl http://localhost:5000/ - check HTTP 200
    Expected Result: Application starts successfully
    Evidence: .sisyphus/evidence/task-6-run.txt
  ```

  **Commit**: NO (验证任务，不产生代码变更)

---

## Final Verification Wave (MANDATORY)

- [x] F1. **Plan Compliance Audit** — `oracle`
  Verify all "Must Have" items implemented, all "Must NOT Have" absent. Check evidence files exist.

- [x] F2. **Code Quality Review** — `unspecified-high`
  Run `dotnet build`, check for compiler errors, review for AI slops.

- [x] F3. **Real Manual QA** — `unspecified-high` (+ playwright)
  Execute every QA scenario, capture evidence to `.sisyphus/evidence/`.

- [x] F4. **Scope Fidelity Check** — `deep`
  Verify 1:1 mapping between spec and implementation, no scope creep.

---

## Commit Strategy

- **Wave 1**: `feat(webui): add AgentExecutor adapter and permission channel`
- **Wave 2**: `feat(webui): integrate real AgentExecutor in Session.razor`

---

## Success Criteria

### Verification Commands
```bash
# Build
dotnet build samples/Seeing.Agent.WebUI

# Run
dotnet run --project samples/Seeing.Agent.WebUI

# Test (Playwright)
npx playwright test e2e/session-agent.spec.ts
```

### Final Checklist
- [ ] All "Must Have" present
- [ ] All "Must NOT Have" absent
- [ ] Streaming response displays correctly
- [ ] Tool call cards render properly
- [ ] Cancellation works
- [ ] Reasoning process shows (if model supports)