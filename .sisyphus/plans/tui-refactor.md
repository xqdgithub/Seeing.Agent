# TUI 架构重构工作计划

## TL;DR

> **Quick Summary**: 重构 TUI 渲染架构，采用 Live Display 单循环 + 增量更新 + 事件驱动，解决渲染与输入冲突、全量重建闪烁、流式输出卡顿问题。
> 
> **Deliverables**:
> - 新的渲染核心 `TuiApplication.cs`
> - 脏标记管理 `RenderContext.cs`
> - 重构的输入系统
> - 优化的流式渲染
> - 增强的消息导航
> 
> **Estimated Effort**: Medium (1-2天)
> **Parallel Execution**: YES - 多个组件可并行开发
> **Critical Path**: RenderContext → TuiApplication → InputHandler → StreamingRenderer

---

## Context

### Original Request
用户报告TUI无法正常渲染，需要整体看下整个TUI的渲染，可以重构以满足更加合理的展示和交互逻辑。用户期望：流畅流式输出、更好的输入体验、富文本展示、消息导航、Agent输出的流式渲染正确。

### Interview Summary
**Key Discussions**:
- 重构范围: 架构重构（修复核心渲染循环，保持现有组件结构）
- 交互特性: 流畅流式输出、更好的输入体验、富文本展示、消息导航
- 技术选择: Live Display单循环 + 增量更新 + 事件驱动

**Research Findings**:
- Spectre.Console 官方推荐 Live Display 模式
- `ctx.Stop()/Start()` 可用于包装输入处理
- 批处理更新可减少刷新频率
- `Table.Rows.Update()` 支持原地更新

### Metis Review
**Identified Gaps** (addressed):
- Gap 1: 输入系统与渲染系统兼容性 → 使用 ctx.Stop()/Start() 或自定义输入组件
- Gap 2: 流式渲染性能 → 批处理更新 + 内容缓冲
- Gap 3: 状态变化通知 → 添加 StateChanged 事件机制

---

## Work Objectives

### Core Objective
重构 TUI 渲染架构，实现流畅的流式输出、稳定的输入处理、增量更新的渲染机制。

### Concrete Deliverables
- `.sisyphus/plans/tui-refactor.md` 本计划文档
- 新文件: `RenderContext.cs` - 脏标记管理
- 新文件: `TuiApplication.cs` - 主应用入口
- 重构: `MainChatScreen.cs` - 核心渲染循环
- 重构: `InputArea.cs` - 输入系统
- 优化: `LiveStreamRenderer.cs` - 流式渲染
- 增强: `MessageHistoryRenderer` - 虚拟滚动

### Definition of Done
- [ ] 流式输出无闪烁，平滑显示
- [ ] 中文输入删除正常，无残留字符
- [ ] 多行输入模式正常工作
- [ ] 历史记录上下翻阅正常
- [ ] 终端 resize 正常处理
- [ ] `dotnet build samples/Seeing.Agent.Tui` 成功

### Must Have
- Live Display 单循环架构
- 增量更新机制
- Spectre.Console 兼容的输入系统
- 事件驱动的状态管理

### Must NOT Have (Guardrails)
- 不使用原生 Console API 直接操作终端
- 不在 Live 循环内阻塞刷新
- 不每帧重建整个 Layout 树
- 不忽略 Spectre.Console 的渲染模型

---

## Verification Strategy

### Test Decision
- **Infrastructure exists**: NO (当前项目无自动化测试)
- **Automated tests**: NO
- **Agent-Executed QA**: YES - 每个任务包含手动验证步骤

### QA Policy
每个任务包含 Agent 执行的 QA 场景：
- 启动 TUI 应用
- 验证特定功能
- 捕获终端输出作为证据

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately - 核心基础设施):
├── Task 1: 创建 RenderContext 脏标记管理 [quick]
├── Task 2: 增强 TuiState 事件机制 [quick]
├── Task 3: 创建 TuiApplication 主入口 [quick]
└── Task 4: 定义关键接口 IIncrementalRenderer [quick]

Wave 2 (After Wave 1 - 核心渲染重构):
├── Task 5: 重构 MainChatScreen 渲染循环 [deep]
├── Task 6: 重构 InputArea 输入系统 [unspecified-high]
├── Task 7: 优化 LiveStreamRenderer 流式渲染 [unspecified-high]
└── Task 8: 重构 MessageHistoryRenderer 添加虚拟滚动 [unspecified-high]

Wave 3 (After Wave 2 - 功能增强):
├── Task 9: 增强 MarkdownToSpectreConverter 代码高亮 [visual-engineering]
├── Task 10: 添加消息导航功能 [unspecified-high]
└── Task 11: 优化 StatusBar 状态更新 [quick]

Wave FINAL (After ALL tasks — 验证):
├── Task F1: 功能完整性验证 [unspecified-high]
├── Task F2: 性能测试 [unspecified-high]
└── Task F3: 用户体验验证 [unspecified-high]
```

### Dependency Matrix
- **1-4**: - - 5-11
- **5**: 1, 2, 3, 4 - F1-F3
- **6**: 1, 3 - F1, F3
- **7**: 1, 2 - F1, F2
- **8**: 1, 2 - F1, F2
- **9**: - F1, F3
- **10**: 1, 8 - F1, F3
- **11**: 1, 2 - F1

---

## TODOs

- [x] 1. 创建 RenderContext 脏标记管理

  **What to do**:
  - 创建 `UI/Core/RenderContext.cs`
  - 实现 `IRenderContext` 接口
  - 定义 `RenderRegion` 枚举: Header, Messages, Streaming, Input
  - 实现脏标记管理: IsDirty(), MarkDirty(), ClearDirty()
  - 添加区域版本号支持增量更新判断

  **Must NOT do**:
  - 不引入复杂的状态机
  - 不使用锁（单线程访问）

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2, 3, 4)
  - **Blocks**: Tasks 5, 6, 7, 8, 10, 11
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.Tui/Core/TuiState.cs:104-106` - 现有 NeedsRefresh 属性
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs` - 组件结构

  **Acceptance Criteria**:
  - [ ] RenderContext.cs 文件创建成功
  - [ ] 编译无错误
  - [ ] 接口定义清晰，易于实现

  **QA Scenarios**:
  ```
  Scenario: 验证脏标记功能
    Tool: Bash
    Steps:
      1. dotnet build samples/Seeing.Agent.Tui
      2. 检查编译输出无错误
    Expected Result: Build succeeded
    Evidence: .sisyphus/evidence/task-01-build.txt
  ```

  **Commit**: YES
  - Message: `feat(tui): add RenderContext for dirty flag management`
  - Files: `samples/Seeing.Agent.Tui/UI/Core/RenderContext.cs`

---

- [x] 2. 增强 TuiState 事件机制

  **What to do**:
  - 修改 `TuiState.cs`
  - 添加 `StateChanged` 事件
  - 添加 `OnStateChanged(RenderRegion region)` 方法
  - 在关键状态变化点触发事件:
    - AddMessage 时触发 Messages 区域脏标记
    - CurrentStreamingMessage 变化时触发 Streaming 区域脏标记
    - IsProcessing 变化时触发 Input 区域脏标记

  **Must NOT do**:
  - 不破坏现有的 AddMessage 等方法签名
  - 不引入异步事件处理

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 3, 4)
  - **Blocks**: Tasks 5, 7, 8, 11
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.Tui/Core/TuiState.cs:119-127` - AddMessage 方法
  - `samples/Seeing.Agent.Tui/Core/TuiState.cs:145-151` - CancelCurrentTask 方法

  **Acceptance Criteria**:
  - [ ] StateChanged 事件定义正确
  - [ ] 各状态变化点正确触发事件
  - [ ] 编译无错误

  **QA Scenarios**:
  ```
  Scenario: 验证事件触发
    Tool: Bash
    Steps:
      1. dotnet build samples/Seeing.Agent.Tui
      2. 检查编译输出无错误
    Expected Result: Build succeeded
    Evidence: .sisyphus/evidence/task-02-build.txt
  ```

  **Commit**: YES
  - Message: `feat(tui): add StateChanged event to TuiState`
  - Files: `samples/Seeing.Agent.Tui/Core/TuiState.cs`

---

- [x] 3. 创建 TuiApplication 主入口

  **What to do**:
  - 创建 `UI/Core/TuiApplication.cs`
  - 实现 `RunAsync(CancellationToken)` 主循环
  - 使用 `AnsiConsole.Live()` 包装整个应用生命周期
  - 集成 RenderContext 和 Layout 管理
  - 定义布局结构: Header (2行) / Messages / Streaming (动态) / Input (5行)

  **Must NOT do**:
  - 不在 Live 循环内使用原生 Console API
  - 不频繁调用 ctx.Refresh()

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2, 4)
  - **Blocks**: Tasks 5, 6
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Screens/MainChatScreen.cs:81-108` - 现有布局结构
  - Spectre.Console 文档: https://spectreconsole.net/console/live/live-display

  **Acceptance Criteria**:
  - [ ] TuiApplication.cs 文件创建成功
  - [ ] Live 循环正确初始化
  - [ ] 布局结构定义清晰

  **QA Scenarios**:
  ```
  Scenario: 验证主循环框架
    Tool: Bash
    Steps:
      1. dotnet build samples/Seeing.Agent.Tui
      2. 检查编译输出无错误
    Expected Result: Build succeeded
    Evidence: .sisyphus/evidence/task-03-build.txt
  ```

  **Commit**: YES
  - Message: `feat(tui): add TuiApplication main entry point`
  - Files: `samples/Seeing.Agent.Tui/UI/Core/TuiApplication.cs`

---

- [x] 4. 定义关键接口 IIncrementalRenderer

  **What to do**:
  - 创建 `UI/Core/Interfaces/IIncrementalRenderer.cs`
  - 定义接口方法:
    - `IRenderable BuildInitial()` - 初始构建
    - `IRenderable BuildUpdate(StateChangedEvent evt)` - 增量更新
    - `bool NeedsRebuild { get; }` - 是否需要重建
  - 定义 `StateChangedEvent` record

  **Must NOT do**:
  - 不过度设计接口
  - 保持接口简单易实现

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 1, 2, 3)
  - **Blocks**: Tasks 5, 7, 8
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs` - 现有组件结构

  **Acceptance Criteria**:
  - [ ] 接口定义清晰
  - [ ] 编译无错误
  - [ ] 易于现有组件实现

  **QA Scenarios**:
  ```
  Scenario: 验证接口定义
    Tool: Bash
    Steps:
      1. dotnet build samples/Seeing.Agent.Tui
      2. 检查编译输出无错误
    Expected Result: Build succeeded
    Evidence: .sisyphus/evidence/task-04-build.txt
  ```

  **Commit**: YES
  - Message: `feat(tui): add IIncrementalRenderer interface`
  - Files: `samples/Seeing.Agent.Tui/UI/Core/Interfaces/IIncrementalRenderer.cs`

---

- [x] 5. 重构 MainChatScreen 渲染循环

  **What to do**:
  - 重构 `MainChatScreen.cs` 的 `RunAsync()` 方法
  - 集成 TuiApplication 和 RenderContext
  - 实现增量更新逻辑:
    - 只在脏区域变化时更新对应 Layout 区块
    - 使用缓存避免重复构建
  - 移除 `AnsiConsole.Clear()` 调用
  - 处理终端 resize 事件

  **Must NOT do**:
  - 不在每次循环重建整个 Layout 树
  - 不使用原生 Console.Clear()

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO
  - **Parallel Group**: Sequential (depends on 1, 2, 3, 4)
  - **Blocks**: F1, F2, F3
  - **Blocked By**: Tasks 1, 2, 3, 4

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Screens/MainChatScreen.cs:38-108` - 现有渲染逻辑
  - Spectre.Console Live Display: https://spectreconsole.net/console/live/live-display

  **Acceptance Criteria**:
  - [ ] 渲染循环使用 Live Display
  - [ ] 增量更新正确实现
  - [ ] 无 AnsiConsole.Clear() 调用
  - [ ] 编译无错误

  **QA Scenarios**:
  ```
  Scenario: 验证渲染无闪烁
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 输入 "你好"
      3. 观察 AI 响应是否平滑显示
    Expected Result: 流式输出平滑，无闪烁
    Evidence: .sisyphus/evidence/task-05-streaming.txt

  Scenario: 验证终端resize
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 调整终端窗口大小
      3. 观察布局是否正确适应
    Expected Result: 布局正确适应新尺寸
    Evidence: .sisyphus/evidence/task-05-resize.txt
  ```

  **Commit**: YES
  - Message: `refactor(tui): implement incremental rendering in MainChatScreen`
  - Files: `samples/Seeing.Agent.Tui/UI/Screens/MainChatScreen.cs`

---

- [x] 6. 重构 InputArea 输入系统

  **What to do**:
  - 重构 `InputArea.cs`
  - 实现与 Spectre.Console 兼容的输入处理:
    - 方案选择: ctx.Stop()/Start() 包装 TextPrompt 或自定义组件
  - 支持功能:
    - 单行/多行模式切换 (Tab)
    - 历史记录翻阅 (上/下箭头)
    - 中文输入正确删除
    - Ctrl+Enter 发送多行内容

  **Must NOT do**:
  - 不使用原生 Console.ReadKey() 直接处理输入
  - 不破坏 Spectre.Console 的内部状态

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 7, 8)
  - **Blocks**: F1, F3
  - **Blocked By**: Tasks 1, 3

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/InputArea.cs:22-173` - 现有输入逻辑
  - Spectre.Console TextPrompt: https://spectreconsole.net/prompts/text

  **Acceptance Criteria**:
  - [ ] 输入系统与 Spectre.Console 兼容
  - [ ] 中文输入删除正常
  - [ ] 多行模式正常工作
  - [ ] 历史记录翻阅正常

  **QA Scenarios**:
  ```
  Scenario: 验证中文输入
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 输入 "你好世界"
      3. 按退格键删除 "世界"
      4. 观察是否正常删除，无残留字符
    Expected Result: 中文删除正常
    Evidence: .sisyphus/evidence/task-06-chinese.txt

  Scenario: 验证多行模式
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 按 Tab 切换到多行模式
      3. 输入多行内容
      4. 按 Ctrl+Enter 发送
    Expected Result: 多行内容正确发送
    Evidence: .sisyphus/evidence/task-06-multiline.txt

  Scenario: 验证历史记录
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 输入并发送几条消息
      3. 按上箭头翻阅历史
    Expected Result: 历史记录正确显示
    Evidence: .sisyphus/evidence/task-06-history.txt
  ```

  **Commit**: YES
  - Message: `refactor(tui): implement Spectre-compatible input handling`
  - Files: `samples/Seeing.Agent.Tui/UI/Components/InputArea.cs`

---

- [x] 7. 优化 LiveStreamRenderer 流式渲染

  **What to do**:
  - 优化 `LiveStreamRenderer.cs`
  - 实现增量内容追加:
    - 缓存上次渲染的内容
    - 只在内容变化时更新
  - 批处理更新:
    - 每 N 个 token 或每 100ms 刷新一次
  - 优化工具调用状态更新

  **Must NOT do**:
  - 不每个 token 都刷新
  - 不重建整个面板

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 6, 8)
  - **Blocks**: F1, F2
  - **Blocked By**: Tasks 1, 2

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Renderers/LiveStreamRenderer.cs` - 现有流式渲染
  - `samples/Seeing.Agent.Tui/Core/Models/StreamingMessage.cs` - 流式消息模型

  **Acceptance Criteria**:
  - [ ] 流式输出平滑无闪烁
  - [ ] 批处理更新实现
  - [ ] 工具调用状态正确显示

  **QA Scenarios**:
  ```
  Scenario: 验证流式输出平滑性
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 输入 "请写一首诗"
      3. 观察 AI 响应是否平滑显示
    Expected Result: 文字平滑出现，无闪烁
    Evidence: .sisyphus/evidence/task-07-smooth.txt

  Scenario: 验证工具调用显示
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 输入需要工具调用的请求
      3. 观察工具调用状态是否正确显示
    Expected Result: 工具调用状态正确显示
    Evidence: .sisyphus/evidence/task-07-tools.txt
  ```

  **Commit**: YES
  - Message: `perf(tui): optimize streaming rendering with batching`
  - Files: `samples/Seeing.Agent.Tui/UI/Renderers/LiveStreamRenderer.cs`

---

- [x] 8. 重构 MessageHistoryRenderer 添加虚拟滚动

  **What to do**:
  - 重构 `MessageHistoryRenderer` (在 UIComponents.cs 中)
  - 实现虚拟滚动:
    - 只渲染可见区域的消息
    - 支持 Page Up/Down 快速滚动
  - 优化大量消息时的性能
  - 添加滚动指示器

  **Must NOT do**:
  - 不每次渲染所有消息
  - 不在滚动时重建整个列表

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 6, 7)
  - **Blocks**: F1, F2
  - **Blocked By**: Tasks 1, 2

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs:263-287` - 现有消息历史渲染
  - `samples/Seeing.Agent.Tui/Core/TuiState.cs:95-96` - ScrollOffset 属性

  **Acceptance Criteria**:
  - [ ] 虚拟滚动正确实现
  - [ ] 大量消息时性能良好
  - [ ] 滚动指示器显示

  **QA Scenarios**:
  ```
  Scenario: 验证虚拟滚动
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 发送多条消息（超过终端高度）
      3. 使用 Page Up/Down 滚动
    Expected Result: 滚动流畅，内容正确显示
    Evidence: .sisyphus/evidence/task-08-scroll.txt
  ```

  **Commit**: YES
  - Message: `perf(tui): add virtual scrolling to message history`
  - Files: `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs`

---

- [x] 9. 增强 MarkdownToSpectreConverter 代码高亮

  **What to do**:
  - 增强 `MarkdownToSpectreConverter.cs`
  - 添加代码块语法高亮:
    - 支持 C#, JavaScript, Python 等语言
    - 使用 Spectre.Console 的语法高亮功能
  - 改进表格渲染
  - 优化链接显示

  **Must NOT do**:
  - 不引入外部语法高亮库（使用 Spectre 内置）
  - 不过度复杂化实现

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 10, 11)
  - **Blocks**: F1, F3
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Renderers/MarkdownToSpectreConverter.cs` - 现有转换器
  - Spectre.Console Syntax: https://spectreconsole.net/console/markup

  **Acceptance Criteria**:
  - [ ] 代码块语法高亮正确
  - [ ] 表格渲染美观
  - [ ] 链接显示友好

  **QA Scenarios**:
  ```
  Scenario: 验证代码高亮
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 请求 AI 输出代码块
      3. 观察代码是否正确高亮
    Expected Result: 代码语法正确高亮
    Evidence: .sisyphus/evidence/task-09-highlight.txt
  ```

  **Commit**: YES
  - Message: `feat(tui): add syntax highlighting to markdown converter`
  - Files: `samples/Seeing.Agent.Tui/UI/Renderers/MarkdownToSpectreConverter.cs`

---

- [x] 10. 添加消息导航功能

  **What to do**:
  - 创建 `UI/Components/MessageNavigator.cs`
  - 实现功能:
    - 滚动: Page Up/Down, Home/End
    - 搜索: /search 关键词搜索
    - 折叠: 折叠/展开长消息和工具调用详情
  - 添加导航状态到 TuiState

  **Must NOT do**:
  - 不破坏现有的消息渲染逻辑
  - 不在搜索时遍历所有消息（使用索引）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 9, 11)
  - **Blocks**: F1, F3
  - **Blocked By**: Tasks 1, 8

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs` - 消息组件
  - `samples/Seeing.Agent.Tui/Services/CommandHandler.cs` - 命令处理

  **Acceptance Criteria**:
  - [ ] 滚动功能正常
  - [ ] 搜索功能正常
  - [ ] 折叠功能正常

  **QA Scenarios**:
  ```
  Scenario: 验证消息搜索
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 发送多条消息
      3. 使用 /search 搜索关键词
    Expected Result: 搜索结果正确高亮显示
    Evidence: .sisyphus/evidence/task-10-search.txt
  ```

  **Commit**: YES
  - Message: `feat(tui): add message navigation with search and folding`
  - Files: `samples/Seeing.Agent.Tui/UI/Components/MessageNavigator.cs`

---

- [x] 11. 优化 StatusBar 状态更新

  **What to do**:
  - 优化 `StatusBar` (在 UIComponents.cs 中)
  - 实现增量更新:
    - 只在状态变化时更新
    - 使用缓存避免重复构建
  - 添加更多状态信息:
    - 消息数量
    - 当前滚动位置

  **Must NOT do**:
  - 不每次都重建整个状态栏
  - 不在快速变化的状态上过度刷新

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with Tasks 9, 10)
  - **Blocks**: F1
  - **Blocked By**: Tasks 1, 2

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs:12-62` - 现有状态栏

  **Acceptance Criteria**:
  - [ ] 状态更新高效
  - [ ] 状态信息完整

  **QA Scenarios**:
  ```
  Scenario: 验证状态栏更新
    Tool: Bash (interactive)
    Steps:
      1. dotnet run --project samples/Seeing.Agent.Tui
      2. 观察状态栏信息
      3. 切换 Agent
      4. 观察状态栏是否正确更新
    Expected Result: 状态栏信息正确更新
    Evidence: .sisyphus/evidence/task-11-statusbar.txt
  ```

  **Commit**: YES
  - Message: `perf(tui): optimize StatusBar incremental updates`
  - Files: `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs`

---

## Final Verification Wave

- [x] F1. 功能完整性验证
  - 启动 TUI 应用
  - 验证所有核心功能正常
  - 验证所有命令正常
  - 输出: 功能清单检查结果

- [x] F2. 性能测试
  - 测试大量消息时的性能
  - 测试长时间运行的稳定性
  - 测试内存使用
  - 输出: 性能报告

- [x] F3. 用户体验验证
  - 验证流式输出平滑性
  - 验证输入体验
  - 验证中文支持
  - 输出: 用户体验报告

---

## Commit Strategy

- **1**: `feat(tui): add RenderContext for dirty flag management` - RenderContext.cs
- **2**: `feat(tui): add StateChanged event to TuiState` - TuiState.cs
- **3**: `feat(tui): add TuiApplication main entry point` - TuiApplication.cs
- **4**: `feat(tui): add IIncrementalRenderer interface` - IIncrementalRenderer.cs
- **5**: `refactor(tui): implement incremental rendering in MainChatScreen` - MainChatScreen.cs
- **6**: `refactor(tui): implement Spectre-compatible input handling` - InputArea.cs
- **7**: `perf(tui): optimize streaming rendering with batching` - LiveStreamRenderer.cs
- **8**: `perf(tui): add virtual scrolling to message history` - UIComponents.cs
- **9**: `feat(tui): add syntax highlighting to markdown converter` - MarkdownToSpectreConverter.cs
- **10**: `feat(tui): add message navigation with search and folding` - MessageNavigator.cs
- **11**: `perf(tui): optimize StatusBar incremental updates` - UIComponents.cs

---

## Success Criteria

### Verification Commands
```bash
dotnet build samples/Seeing.Agent.Tui  # Expected: Build succeeded
dotnet run --project samples/Seeing.Agent.Tui  # Expected: TUI starts correctly
```

### Final Checklist
- [ ] 所有 "Must Have" 功能实现
- [ ] 所有 "Must NOT Have" 约束遵守
- [ ] 所有测试场景通过
- [ ] 编译无错误无警告
- [ ] 用户体验流畅