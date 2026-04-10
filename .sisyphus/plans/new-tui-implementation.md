# Seeing.Agent.NewTui 实现计划

## TL;DR

> **Quick Summary**: 创建一个基于 Terminal.Gui v2 的 TUI CLI，直接复用 Seeing.Agent 现有的 AgentExecutor 事件流和 SessionManager 会话管理，实现权限确认弹窗和实时消息渲染。
> 
> **Deliverables**:
> - 可运行的 TUI CLI 项目 (`Seeing.Agent.NewTui`)
> - 用户消息发送和 Agent 流式响应显示
> - 工具调用状态可视化（Pending/Running/Success/Failed）
> - 权限确认弹窗（实现 IPermissionChannel）
> - 取消操作支持（Ctrl+C）

> **Estimated Effort**: Medium (2-3 days)
> **Parallel Execution**: YES - 3 waves
> **Critical Path**: Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5

---

## Context

### Original Request
用户要求创建一个完全可运行的 Agent 交互式 TUI CLI，参考 opencode 项目设计，基于现有 Seeing.Agent 框架实现。要求避免过度设计，直接复用现有抽象。

### Interview Summary
**Key Discussions**:
- **架构模式**: 单进程架构，不分离 Worker 进程（C# 不像 JS 需要 Worker 防阻塞）
- **复用策略**: 直接使用 AgentExecutor 事件流、SessionManager、AgentRegistry
- **状态管理**: 简单字段 + 事件通知，不使用 Signal/Reactive 模式
- **TUI 框架**: Terminal.Gui v2（成熟、跨平台）
- **线程安全**: SynchronizationContext 确保 UI 线程安全

**Research Findings**:
- **opencode 架构**: Worker 进程分离（JS 需要）+ SSE 事件流 + SolidJS Signal 系统
- **Seeing.Agent 抽象**: AgentExecutor 返回 `IAsyncEnumerable<IMessageEvent>`，IPermissionChannel 多入口抽象
- **事件类型**: StreamDeltaEvent/StreamCompleteEvent/ToolCallEvent/ErrorEvent

### Metis Review (Oracle Consultation)
**审查结果**: ✅ 设计合理性通过（附条件）

**需补充项**:
1. 事件处理逻辑完整实现（必须）
2. 线程安全通知机制（必须）
3. 工具调用显示模型（推荐）
4. 取消操作支持（推荐）

**Guardrails Applied**:
- AgentRunner 职责边界明确：只负责事件流编排，不管理持久化
- AppState 不引入 Signal 模式：直接字段 + Action 事件
- 所有 UI 更新通过 SynchronizationContext.Post

---

## Work Objectives

### Core Objective
创建一个可运行的 TUI CLI，用户能发送消息、看到流式响应、确认工具权限、取消操作。

### Concrete Deliverables
- `src/Seeing.Agent.NewTui/` 项目目录
- 10 个源文件（Program/App/Views/Components/Dialogs/State/Services）
- 可执行程序：`dotnet run --project src/Seeing.Agent.NewTui`

### Definition of Done
- [ ] `dotnet build src/Seeing.Agent.NewTui` → 成功
- [ ] `dotnet run --project src/Seeing.Agent.NewTui` → TUI 启动显示 Logo
- [ ] 输入消息 → Agent 流式响应显示
- [ ] 工具调用 → 权限弹窗 → Allow/Deny
- [ ] Ctrl+C → 取消当前请求

### Must Have
- 流式消息渲染（StreamDeltaEvent）
- 工具调用状态显示（ToolCallEvent）
- 权限确认弹窗（IPermissionChannel 实现）
- 取消操作（CancellationTokenSource）
- 线程安全 UI 更新

### Must NOT Have (Guardrails)
- Worker 进程分离（C# 单进程足够）
- Signal/Reactive 状态系统（过度设计）
- 新的 Agent 抽象（复用 IAgent/AgentBase）
- 新的会话管理（复用 SessionManager）
- SQLite 持久化（内存 SessionData 足够）

---

## Verification Strategy

> **ZERO HUMAN INTERVENTION** - Agent-Executed QA Scenarios

### Test Decision
- **Infrastructure exists**: YES (xUnit in Seeing.Agent.Tests)
- **Automated tests**: None (TUI 手动测试为主)
- **Framework**: N/A
- **Agent-Executed QA**: YES (手动运行验证)

### QA Policy
Every task MUST include agent-executed QA scenarios:
- **TUI 启动**: `dotnet run` → 检查 Logo 显示
- **消息发送**: 输入文本 → 检查流式响应
- **工具权限**: 触发工具 → 检查弹窗
- **取消操作**: Ctrl+C → 检查状态恢复

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Foundation - 可并行 4 个任务):
├── Task 1: 项目创建 + .csproj [quick]
├── Task 2: Program.cs 入口 + DI [quick]
├── Task 3: AppState 状态容器 [quick]
├── Task 4: TuiPermissionChannel [quick]
└── Task 5: PermissionDialog [quick]

Wave 2 (核心服务 - 依赖 Wave 1):
├── Task 6: AgentRunner 核心 [deep]
├── Task 7: MessageList 组件 [quick]
├── Task 8: SessionView 视图 [quick]
└── Task 9: HomeView 视图 [quick]

Wave 3 (整合 + 对话框):
├── Task 10: App 主类 [quick]
├── Task 11: SessionListDialog [quick]
├── Task 12: AgentSelectDialog [quick]
└── Task 13: 整合测试 [quick]

Wave FINAL (验证 - 4 个并行审查):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Build verification (quick)
├── Task F3: Manual QA (unspecified-high)
└── Task F4: Code quality check (quick)
→ Present results → Get explicit user okay
```

### Dependency Matrix

- **1-5**: - - 6-9
- **6**: 1, 3, 4 - 8, 9, 10
- **7**: 3 - 8
- **8**: 3, 6, 7 - 10
- **9**: 3 - 10
- **10**: 6, 8, 9 - F1-F4
- **F1-F4**: 10 - user okay

### Agent Dispatch Summary

- **Wave 1**: 5 × `quick`
- **Wave 2**: 4 × `quick` + `deep`
- **Wave 3**: 4 × `quick`
- **FINAL**: 4 × `oracle`/`quick`/`unspecified-high`

---

## TODOs

- [ ] 1. **项目创建 + .csproj**

  **What to do**:
  - 创建 `src/Seeing.Agent.NewTui/` 目录
  - 创建 `Seeing.Agent.NewTui.csproj` 项目文件
  - 配置 TargetFramework=net10.0, OutputType=Exe
  - 添加 Terminal.Gui v2.0.0 包引用
  - 添加 Seeing.Agent 项目引用

  **Must NOT do**:
  - 不添加其他 NuGet 包
  - 不创建 global.json（使用项目默认）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单文件创建，配置简单
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2-5)
  - **Blocks**: Task 6
  - **Blocked By**: None

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:122-132` - 项目文件模板
  - `src/Seeing.Agent/Seeing.Agent.csproj` - 现有项目结构参考

  **Acceptance Criteria**:
  - [ ] 目录创建成功
  - [ ] .csproj 文件存在
  - [ ] `dotnet build src/Seeing.Agent.NewTui` → 无错误

  **QA Scenarios**:

  ```
  Scenario: 项目可构建
    Tool: Bash
    Steps:
      1. dotnet build src/Seeing.Agent.NewTui --no-restore
    Expected Result: Build succeeded
    Evidence: .sisyphus/evidence/task-1-build.log
  ```

  **Evidence to Capture**:
  - [ ] 构建输出日志

  **Commit**: YES (单独)
  - Message: `feat(tui): create NewTui project structure`
  - Files: `src/Seeing.Agent.NewTui/Seeing.Agent.NewTui.csproj`

---

- [ ] 2. **Program.cs 入口 + DI 注册**

  **What to do**:
  - 创建 `Program.cs` 文件
  - 实现 DI 注册：AddSeeingAgent() + TUI 服务
  - 实现 Terminal.Gui 初始化：Application.Init()
  - 实现 TerminalGuiSynchronizationContext
  - 实现 Main() 异步入口

  **Must NOT do**:
  - 不添加额外的服务注册
  - 不使用复杂的配置加载

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 入口文件，逻辑简单
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 3-5)
  - **Blocks**: Task 10
  - **Blocked By**: None

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:98-121` - Program.cs 模板
  - `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs` - AddSeeingAgent 实现

  **Acceptance Criteria**:
  - [ ] Program.cs 文件创建
  - [ ] DI 注册包含 AddSeeingAgent()
  - [ ] SynchronizationContext 实现正确

  **QA Scenarios**:

  ```
  Scenario: 程序可启动（空运行）
    Tool: Bash
    Steps:
      1. dotnet run --project src/Seeing.Agent.NewTui --no-build
      2. 等待 2 秒后 Ctrl+C 退出
    Expected Result: 程序启动无崩溃
    Evidence: .sisyphus/evidence/task-2-run.log
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add Program.cs entry point`
  - Files: `src/Seeing.Agent.NewTui/Program.cs`

---

- [ ] 3. **AppState 状态容器**

  **What to do**:
  - 创建 `State/AppState.cs` 文件
  - 实现 CurrentSession/CurrentAgent/IsProcessing 字段
  - 实现 StreamingContent/StreamingReasoning StringBuilder
  - 实现 ActiveToolCalls 列表
  - 实现 StartProcessing/EndProcessing/CancelProcessing 方法
  - 实现 NotifyChanged() 线程安全通知
  - 实现 ToolCallDisplay 记录类型

  **Must NOT do**:
  - 不使用 Signal<T> 或 ReactiveUI
  - 不添加持久化逻辑
  - 不添加配置存储

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 纯数据容器，逻辑简单
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1-2, 4-5)
  - **Blocks**: Task 6, 7, 8, 9
  - **Blocked By**: None

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:140-180` - AppState 模板
  - `src/Seeing.Agent/Core/Events/MessageEventTypes.cs:90-108` - ToolCallStatus 定义

  **Acceptance Criteria**:
  - [ ] AppState.cs 文件创建
  - [ ] ToolCallDisplay 记录类型定义
  - [ ] SynchronizationContext.Post 使用正确

  **QA Scenarios**:

  ```
  Scenario: 状态变更通知
    Tool: Bash (dotnet REPL)
    Steps:
      1. 创建 AppState 实例
      2. 订阅 StateChanged 事件
      3. 调用 NotifyChanged()
      4. 检查事件是否触发
    Expected Result: 事件触发一次
    Evidence: .sisyphus/evidence/task-3-state.log
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add AppState state container`
  - Files: `src/Seeing.Agent.NewTui/State/AppState.cs`

---

- [ ] 4. **TuiPermissionChannel 实现**

  **What to do**:
  - 创建 `Services/TuiPermissionChannel.cs` 文件
  - 实现 IPermissionChannel 接口
  - 实现 RequestToolPermissionAsync 方法
  - 使用 TaskCompletionSource 异步等待
  - 使用 Application.MainLoop.Invoke 显示弹窗
  - 实现 60 秒超时保护

  **Must NOT do**:
  - 不阻塞主线程
  - 不使用同步弹窗

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 单文件服务实现
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1-3, 5)
  - **Blocks**: Task 6
  - **Blocked By**: None

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:190-210` - TuiPermissionChannel 模板
  - `src/Seeing.Agent/Core/Interfaces/IPermissionChannel.cs` - 接口定义
  - `src/Seeing.Agent/Core/Models/AgentModels.cs:98-141` - PermissionDecision 定义

  **Acceptance Criteria**:
  - [ ] TuiPermissionChannel.cs 文件创建
  - [ ] IPermissionChannel 接口完整实现
  - [ ] TaskCompletionSource 使用正确

  **QA Scenarios**:

  ```
  Scenario: 权限请求异步等待
    Tool: Bash (dotnet REPL)
    Steps:
      1. 创建 TuiPermissionChannel 实例
      2. 调用 RequestToolPermissionAsync
      3. 模拟弹窗响应
    Expected Result: 返回 PermissionDecision
    Evidence: .sisyphus/evidence/task-4-permission.log
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add TuiPermissionChannel`
  - Files: `src/Seeing.Agent.NewTui/Services/TuiPermissionChannel.cs`

---

- [ ] 5. **PermissionDialog 权限弹窗**

  **What to do**:
  - 创建 `Dialogs/PermissionDialog.cs` 文件
  - 继承 Terminal.Gui Dialog 类
  - 显示工具名称和参数（JSON 格式）
  - 实现 Allow/Deny/Always Allow 按钮
  - 使用 Action<PermissionDecision> 回调

  **Must NOT do**:
  - 不使用复杂布局
  - 不添加额外的确认步骤

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: UI 组件，逻辑简单
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1-4)
  - **Blocks**: Task 4（依赖此弹窗）
  - **Blocked By**: None

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:215-250` - PermissionDialog 模板
  - Terminal.Gui Dialog 文档

  **Acceptance Criteria**:
  - [ ] PermissionDialog.cs 文件创建
  - [ ] 三个按钮正确实现
  - [ ] 回调机制正确

  **QA Scenarios**:

  ```
  Scenario: 弹窗按钮响应
    Tool: Playwright (模拟 TUI 交互，或手动验证)
    Steps:
      1. 显示 PermissionDialog
      2. 点击 Allow 按钮
      3. 检查回调结果
    Expected Result: PermissionDecision.Allow
    Evidence: .sisyphus/evidence/task-5-dialog.png
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add PermissionDialog`
  - Files: `src/Seeing.Agent.NewTui/Dialogs/PermissionDialog.cs`

---

- [ ] 6. **AgentRunner 核心服务**

  **What to do**:
  - 创建 `Services/AgentRunner.cs` 文件
  - 实现 SendMessageAsync 方法
  - 获取 AgentDefinition 并构建 AgentContext
  - 订阅 AgentExecutor.ExecuteAsync 事件流
  - 实现 HandleEvent 处理所有事件类型
  - 实现工具调用状态更新逻辑

  **Must NOT do**:
  - 不重新实现 AgentExecutor 逻辑
  - 不添加新的事件类型
  - 不修改 SessionManager 行为

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 核心编排逻辑，需深入理解事件流
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 2 (开始)
  - **Blocks**: Task 8, 10
  - **Blocked By**: Task 1, 3, 4

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:260-310` - AgentRunner 模板
  - `src/Seeing.Agent/Core/AgentExecutor.cs:62-248` - ExecuteAsync 实现
  - `src/Seeing.Agent/Core/Events/MessageEventTypes.cs` - 事件类型定义
  - `src/Seeing.Agent/Core/Models/AgentDefinition.cs` - AgentDefinition 结构
  - `src/Seeing.Agent/Core/Models/AgentModels.cs:10-75` - AgentContext 结构

  **Acceptance Criteria**:
  - [ ] AgentRunner.cs 文件创建
  - [ ] 事件流订阅正确
  - [ ] HandleEvent 处理所有事件类型

  **QA Scenarios**:

  ```
  Scenario: 发送消息触发事件流
    Tool: Bash (模拟测试)
    Steps:
      1. 创建 AgentRunner 实例
      2. 调用 SendMessageAsync("test")
      3. 检查 AppState.StreamingContent 变化
    Expected Result: StreamingContent 非空
    Evidence: .sisyphus/evidence/task-6-runner.log
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add AgentRunner core service`
  - Files: `src/Seeing.Agent.NewTui/Services/AgentRunner.cs`

---

- [ ] 7. **MessageList 消息列表组件**

  **What to do**:
  - 创建 `Components/MessageList.cs` 文件
  - 继承 Terminal.Gui View 类
  - 实现 Refresh() 刷新方法
  - 实现消息渲染：用户/助手/工具/错误
  - 实现流式内容显示
  - 实现工具调用状态图标渲染

  **Must NOT do**:
  - 不使用虚拟列表（Terminal.Gui ListView 足够）
  - 不添加复杂的 Markdown 渲染

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: UI 组件，逻辑简单
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 6, 8-9)
  - **Blocks**: Task 8
  - **Blocked By**: Task 3

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:320-380` - MessageList 模板
  - `src/Seeing.Agent/Llm/LlmModels.cs` - ChatMessage 定义
  - Terminal.Gui ListView 文档

  **Acceptance Criteria**:
  - [ ] MessageList.cs 文件创建
  - [ ] 消息渲染正确
  - [ ] 工具状态图标显示

  **QA Scenarios**:

  ```
  Scenario: 消息列表刷新
    Tool: Bash (手动验证)
    Steps:
      1. 创建 MessageList 实例
      2. 添加多条消息到 AppState
      3. 触发 StateChanged
      4. 检查 ListView 内容
    Expected Result: 消息显示正确
    Evidence: .sisyphus/evidence/task-7-messages.png
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add MessageList component`
  - Files: `src/Seeing.Agent.NewTui/Components/MessageList.cs`

---

- [ ] 8. **SessionView 会话视图**

  **What to do**:
  - 创建 `Views/SessionView.cs` 文件
  - 继承 Terminal.Gui Window 类
  - 添加 MessageList + TextView 输入框 + 状态栏
  - 实现 OnKeyPress 处理 Enter/Esc/Ctrl+C
  - 实现取消按钮显示/隐藏逻辑

  **Must NOT do**:
  - 不添加侧边栏（简化设计）
  - 不实现多窗口布局

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: UI 视图组装
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 6-7, 9)
  - **Blocks**: Task 10
  - **Blocked By**: Task 3, 6, 7

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:385-430` - SessionView 模板
  - Terminal.Gui Window 文档

  **Acceptance Criteria**:
  - [ ] SessionView.cs 文件创建
  - [ ] 快捷键处理正确
  - [ ] 取消按钮逻辑正确

  **QA Scenarios**:

  ```
  Scenario: Enter 发送消息
    Tool: interactive_bash (tmux)
    Steps:
      1. 运行 TUI
      2. 输入文本
      3. 按 Enter
      4. 检查消息发送
    Expected Result: 消息显示在列表中
    Evidence: .sisyphus/evidence/task-8-enter.png
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add SessionView`
  - Files: `src/Seeing.Agent.NewTui/Views/SessionView.cs`

---

- [ ] 9. **HomeView 首页视图**

  **What to do**:
  - 创建 `Views/HomeView.cs` 文件
  - 继承 Terminal.Gui Window 类
  - 显示 ASCII Logo
  - 添加大输入框
  - 实现 Enter 开始会话逻辑
  - 实现 Ctrl+H 历史会话快捷键

  **Must NOT do**:
  - 不添加复杂的动画效果
  - 不实现 Agent 选择逻辑（在 Dialog 中实现）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: UI 视图，逻辑简单
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 6-8)
  - **Blocks**: Task 10
  - **Blocked By**: Task 3

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:435-480` - HomeView 模板
  - Terminal.Gui Window 文档

  **Acceptance Criteria**:
  - [ ] HomeView.cs 文件创建
  - [ ] Logo 显示正确
  - [ ] Enter 逻辑正确

  **QA Scenarios**:

  ```
  Scenario: Logo 显示
    Tool: Bash
    Steps:
      1. 运行 TUI
      2. 检查首页显示
    Expected Result: Logo 和输入框可见
    Evidence: .sisyphus/evidence/task-9-home.png
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add HomeView`
  - Files: `src/Seeing.Agent.NewTui/Views/HomeView.cs`

---

- [ ] 10. **App 主应用类**

  **What to do**:
  - 创建 `App.cs` 文件
  - 实现 RunAsync() 方法
  - 显示 HomeView
  - 调用 Application.Run()

  **Must NOT do**:
  - 不添加复杂的路由逻辑

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 主应用入口
  - **Skills**: []
  - **Skills Evaluated but Omitted**: 无

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Wave 3 (开始)
  - **Blocks**: F1-F4
  - **Blocked By**: Task 6, 8, 9

  **References**:
  - `docs/IMPLEMENTATION_PLAN.md:485-495` - App.cs 模板

  **Acceptance Criteria**:
  - [ ] App.cs 文件创建
  - [ ] RunAsync 实现正确

  **QA Scenarios**:

  ```
  Scenario: TUI 完整启动
    Tool: Bash
    Steps:
      1. dotnet run --project src/Seeing.Agent.NewTui
      2. 检查 TUI 显示
    Expected Result: Logo + 输入框可见
    Evidence: .sisyphus/evidence/task-10-app.png
  ```

  **Commit**: YES (单独)
  - Message: `feat(tui): add App main class`
  - Files: `src/Seeing.Agent.NewTui/App.cs`

---

- [ ] 11. **SessionListDialog 会话列表**

  **What to do**:
  - 创建 `Dialogs/SessionListDialog.cs` 文件
  - 显示历史会话列表
  - 实现会话选择逻辑

  **Must NOT do**:
  - 不添加会话持久化

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 10, 12, 13)
  - **Blocks**: None
  - **Blocked By**: Task 3

  **References**:
  - Terminal.Gui Dialog 文档

  **Acceptance Criteria**:
  - [ ] SessionListDialog.cs 文件创建

  **Commit**: YES

---

- [ ] 12. **AgentSelectDialog Agent 选择**

  **What to do**:
  - 创建 `Dialogs/AgentSelectDialog.cs` 文件
  - 显示可用 Agent 列表
  - 实现选择逻辑

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 10-11, 13)
  - **Blocked By**: None

  **References**:
  - `src/Seeing.Agent/Core/AgentRegistry.cs` - AgentRegistry 参考

  **Acceptance Criteria**:
  - [ ] AgentSelectDialog.cs 文件创建

  **Commit**: YES

---

- [ ] 13. **整合测试验证**

  **What to do**:
  - 运行完整 TUI
  - 测试消息发送流程
  - 测试工具权限弹窗
  - 测试取消操作

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 10-12)
  - **Blocked By**: Task 10

  **QA Scenarios**:

  ```
  Scenario: 完整对话流程
    Tool: interactive_bash (tmux)
    Steps:
      1. 启动 TUI
      2. 输入 "hello"
      3. 按 Enter
      4. 等待响应
      5. 检查消息列表
    Expected Result: 用户消息 + Agent 响应显示
    Evidence: .sisyphus/evidence/task-13-full.png
  ```

  **Commit**: NO

---

## Final Verification Wave

> 4 review agents run in PARALLEL. ALL must APPROVE.

- [ ] F1. **Plan Compliance Audit** — `oracle`
  验证所有 Must Have 实现，所有 Must NOT Have 未实现。
  Output: `Must Have [N/N] | Must NOT Have [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Build Verification** — `quick`
  运行 `dotnet build` + `dotnet run`。
  Output: `Build [PASS/FAIL] | Run [PASS/FAIL] | VERDICT`

- [ ] F3. **Manual QA** — `unspecified-high`
  执行完整对话流程测试。
  Output: `Flow [N/N pass] | VERDICT`

- [ ] F4. **Code Quality Check** — `quick`
  检查线程安全、事件处理完整性。
  Output: `ThreadSafe [PASS/FAIL] | Events [N/N] | VERDICT`

---

## Commit Strategy

按任务单独提交，共 13 个 commits：

1. `feat(tui): create NewTui project structure`
2. `feat(tui): add Program.cs entry point`
3. `feat(tui): add AppState state container`
4. `feat(tui): add TuiPermissionChannel`
5. `feat(tui): add PermissionDialog`
6. `feat(tui): add AgentRunner core service`
7. `feat(tui): add MessageList component`
8. `feat(tui): add SessionView`
9. `feat(tui): add HomeView`
10. `feat(tui): add App main class`
11. `feat(tui): add SessionListDialog`
12. `feat(tui): add AgentSelectDialog`
13. 整合测试（不提交）

---

## Success Criteria

### Verification Commands
```bash
# 构建验证
dotnet build src/Seeing.Agent.NewTui

# 运行验证
dotnet run --project src/Seeing.Agent.NewTui

# 功能验证
# 1. 输入消息 → 流式响应显示
# 2. 工具调用 → 权限弹窗 → Allow/Deny
# 3. Ctrl+C → 取消 → 状态恢复
```

### Final Checklist
- [ ] 所有 Must Have 实现
- [ ] 所有 Must NOT Have 未实现
- [ ] 流式渲染正常
- [ ] 工具状态显示正确
- [ ] 权限弹窗工作
- [ ] 取消操作正常
- [ ] 线程安全验证通过