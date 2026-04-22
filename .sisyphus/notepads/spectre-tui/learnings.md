# Spectre TUI: StatusBar Implementation Learnings

- Built a lightweight StatusBar using Spectre.Console Markup to display key runtime state.
- Source state comes from AgentContext (Seeing.Agent.Tui.Core.State).
- Used static color configuration from ColorScheme to colorize segments and LayoutConfig as reference for future layout decisions.
- Implemented dynamic read of MessageCount via reflection (fallback to 0 if not present).
- Render() returns a Markup object so the consuming UI can update efficiently without full redraw.
- Ensured no hard-coded values; relies on runtime state for counts and model.

## 2026-04-11: ToolCallPanel 可折叠工具卡片

### 设计模式

1. **状态-图标-颜色三元组映射**
   - GetStatusIcon(), GetStatusColor(), GetSpectreColor() 三个方法统一管理
   - 状态: Pending ⏳ → Running 🔄 → Success ✓ → Failed ✗ → Rejected ⊘
   - 使用 ColorScheme.Icons 常量而非硬编码

2. **双模式渲染**
   - RenderCollapsed(): 紧凑显示，只显示图标+名称+耗时
   - RenderExpanded(): 完整显示，参数+结果+错误+耗时
   - IsExpanded 属性控制切换

3. **JSON 格式化显示**
   - 使用 JsonSerializer.WriteIndented 美化 JSON
   - 失败时降级为原始文本显示
   - 使用 JavaScriptEncoder.UnsafeRelaxedJsonEscaping 保留中文

4. **静态批量渲染方法**
   - RenderMultiple(IEnumerable<ToolCallDisplay>, HashSet<string> expandedIds)
   - 用于消息列表中的工具调用批量显示

5. **实时状态更新**
   - UpdateStatus(status, result, error) 方法支持状态流转
   - 完成状态自动设置 EndTime

### 文件位置
- UI/ToolCallPanel.cs: 可折叠工具卡片组件
## 2026-04-11: MessagePanel 消息历史渲染

### 核心设计

1. **Panel + Rows 组合模式**
   - Panel 包装整体，提供边框和头部
   - Rows 垂直排列单条消息
   - Expand = true 填满可用宽度

2. **虚拟滚动实现**
   ```csharp
   // 计算可见范围
   var availableHeight = TerminalHeight - PanelOverhead;
   var visibleCount = availableHeight / EstimatedMessageHeight;
   var startIndex = Math.Min(ScrollOffset, totalCount - visibleCount);
   ```
   - 只渲染 startIndex 到 endIndex 范围的消息
   - 避免一次性渲染大量消息导致性能问题

3. **搜索高亮**
   ```csharp
   // 使用 Replace 进行关键词高亮
   text.Replace(keyword, "[yellow on black]keyword[/]", StringComparison.OrdinalIgnoreCase);
   ```
   - 先转义内容（防止破坏 Markup）
   - 使用黄色背景高亮关键词
   - 忽略大小写匹配

4. **命名冲突解决方案**
   - Core.TuiState vs State.TuiState
   - 使用别名：`using TuiStateBase = Seeing.Agent.Tui.Core.State.TuiState;`
   - 避免歧义引用错误

### 文件位置
- `UI/MessagePanel.cs`：消息面板渲染组件

### 颜色方案使用
- ColorScheme.Icons.User/Assistant/Tool：角色图标
- ColorScheme.FoldHintColor：折叠提示（dim）
- ColorScheme.WarningColor：警告/匹配数（yellow）
- ColorScheme.SystemColor：系统消息（white）

## 2026-04-11: CommandPalette Spotlight 风格命令面板

### 核心设计

1. **SelectionPrompt + EnableSearch 实现搜索过滤**
   -  扩展方法启用搜索功能（不是  属性方法调用）
   -  设置搜索占位提示
   -  设置选中项样式

2. **CommandItem 结构定义命令属性**
   - Id: 命令唯一标识
   - Name: 显示名称
   - Description: 命令描述（显示在名称后）
   - Group: 分组名称（用于组织命令）
   - Execute: 执行动作委托
   - IsEnabled: 命令是否可用

3. **分组显示实现**
   -  作为分组分隔行
   - 用户选择时忽略分隔行（）
   - 使用 Dictionary 映射显示文本到 CommandItem

4. **异常处理**
   - 捕获 InvalidOperationException/TaskCanceledException 处理用户取消（Escape/Ctrl+C）
   - SelectionPrompt 是阻塞调用，需在 ShowAsync 中用 Task.Run 包装

### API 注意事项
-  是扩展方法，返回修改后的 prompt
-  是字符串常量，Style 需要 Spectre.Console.Color 类型
- SelectionPrompt<string> 返回 string 类型，可直接使用字符串方法

### 文件位置
- ：命令面板组件

## 2026-04-11: ReasoningPanel 可折叠思考过程

### 设计模式

1. **灰色斜体样式**
   - 使用 ColorScheme.ReasoningColor (grey)
   - italic 标记强调思考过程的特殊视觉层次
   - 折叠提示用 ColorScheme.FoldHintColor (dim)

2. **折叠/展开双模式**
   - RenderCollapsed(): 显示图标 + "思考过程" + 摘要预览
   - RenderExpanded(): 完整内容 + 灰色斜体格式
   - Toggle() 方法支持状态切换

3. **摘要预览**
   - 折叠状态截取前50字符作为预览
   - 移除换行，取第一行有效内容
   - 避免折叠状态过长影响界面

4. **静态批量渲染**
   - RenderMultiple(IEnumerable<string>, HashSet<string> expandedIds)
   - RenderMultiple(IEnumerable<ReasoningPanel>, HashSet<string> expandedIds)
   - 用于消息列表中的思考过程批量显示

5. **ID 标识**
   - 每个面板有唯一 Id（用于跟踪展开状态）
   - 自动生成 Guid 截取前8位

### 文件位置
- `UI/ReasoningPanel.cs`: 可折叠思考过程组件

### 颜色方案使用
- ColorScheme.ReasoningColor: 思考内容主色 (grey)
- ColorScheme.FoldHintColor: 折叠提示 (dim)
- ColorScheme.Icons.Reasoning: 💭 图标
- ColorScheme.Icons.Folded/Expanded: ▶/▼ 折叠图标
- Implemented a new ErrorPanel in samples/Seeing.Agent.Tui/UI/ErrorPanel.cs:
- Displays errors in a red bordered panel using Spectre.Console
- Shows error source (LLM/Tool/Agent) and the error message
- Supports optional expansion of error details (Exception.ToString())
- Reuses existing color schemes from ColorScheme for consistent UI

## 2026-04-11: PermissionDialog 模态权限对话框

### 核心设计

1. **阻塞式模态对话框**
   - 使用 Spectre.Console ConfirmationPrompt 实现阻塞式确认
   - 对象初始化语法设置属性：`{ DefaultValue = false, ShowChoices = true }`
   - 默认拒绝（安全优先原则）

2. **权限详情展示**
   - Panel + Rows 组合模式，显示请求类型、详情、风险提示
   - 支持四种请求类型：Tool、SubAgent、FileWrite、Confirmation
   - 每种类型有专属图标和风险提示模板

3. **超时机制**
   - CancellationTokenSource + CreateLinkedTokenSource
   - 超时后自动拒绝，返回 PermissionDecision.Deny
   - 异常处理捕获 OperationCanceledException

4. **API 设计**
   - `ShowAndWait()` 同步阻塞方法
   - `ShowAndWaitAsync()` 异步方法支持外部取消令牌
   - `ShowAndWaitMultiple()` 批量处理多个权限请求

### Spectre.Console API 注意事项
- ConfirmationPrompt 属性通过对象初始化语法设置
- `DefaultValue` 是属性，不是方法
- `ShowChoices`, `ShowDefaultValue` 也是属性

### 文件位置
- `UI/PermissionDialog.cs`: 模态权限对话框组件

### 颜色方案使用
- ColorScheme.Icons.Tool: 工具图标
- [yellow] 边框和标题颜色
- [red bold] 风险提示

## 2026-04-11: SubAgentPanel 子代理状态面板

### 设计模式

1. **简洁状态显示**
   - 子代理状态通常短暂，无需折叠功能
   - 一行显示： 或 
   - 时间戳 + 耗时显示

2. **状态-图标-颜色三元组映射**
   - Starting ⏳ → Running 🔄 → Completed ✓ → Failed ✗
   - 颜色：启动中黄色(yellow)，运行中蓝色(blue)，完成绿色(green)，失败红色(red)
   - 使用 ColorScheme.Icons 和 ColorScheme.PendingColor/RunningColor/SuccessColor/ErrorColor

3. **SubAgentStatus 枚举定义**
   - Starting: 启动中（Pending 状态）
   - Running: 运行中（已启动）
   - Completed: 完成
   - Failed: 失败

4. **从事件创建显示模型**
   - FromEvent(SubAgentEvent) 方法映射事件到显示模型
   - Status 字符串映射：started → Running, completed → Completed, failed → Failed

5. **可选跳转功能**
   - ShowJumpHint 属性控制是否显示跳转提示
   - SubSessionId 用于跳转标识（非阻塞式）

### 文件位置
- UI/SubAgentPanel.cs: 子代理状态面板组件
- Core/Models/MessageDisplay.cs: SubAgentDisplay 数据模型 + SubAgentStatus 枚举
2026-04-11: 计划新增一个简单的宿主引导示例，用 Generic Host 启动 Seeing.Agent，并通过反射调用 MainApp.Run() 作为入口点。
- 新增文件 samples/Seeing.Agent.Host/Program.cs，实现要点：
  1) 构建配置：加载 appsettings.json 和环境变量
  2) 使用 Host.CreateDefaultBuilder 注册 Seeing.Agent 服务（通过 AddSeeingAgent(configuration)）
  3) 通过反射查找类型 MainApp，并执行 Run() 或 RunAsync()，以实现对外部主应用的启动解耦
  4) 提供基本的错误处理和优雅退出
- 设计要点与收益：避免对 MainApp 的直接依赖，提升引导阶段的灵活性；适配后续存在的不同入口实现。

## 2026-04-11: MainApp（应用入口和 DI）

### 核心设计

1. **Live 主循环架构**
   - `AnsiConsole.Live(layout).StartAsync(ctx => MainLoopAsync(ctx))`
   - 主循环处理输入 + 定时刷新显示
   - 使用 Stopwatch 计算刷新间隔（默认 100ms）
   - _needsRefresh 标记控制刷新频率

2. **事件订阅模式**
   - InputService 提供 5 个事件：OnSendMessage, OnOpenCommandPalette, OnCancelTask, OnClosePanel, OnToggleMultiline
   - InputState.OnStateChanged 触发刷新标记
   - 命令处理（/ 开头）独立于普通消息

3. **异步模拟响应（临时）**
   - `Task.Run()` 后台处理响应
   - 流式输出使用 `UpdateStreamingContent(chunk)` + `CompleteStreaming()`
   - CancellationToken 支持任务取消

4. **DI 注册顺序**
   ```
   AgentContext → InputState → StatusBar → InputBox → CommandPalette → LayoutService → InputService → MainApp
   ```
   - 单例生命周期（所有服务）
   - `ConfigureDefaultCommands()` 后配置命令

5. **LayoutService 两栏布局**
   - Spectre.Console Layout 垂直分割
   - 四区域：Header(1行) → Messages(比例4) → Input(固定3行) → Footer(1行)
   - `Update()` 方法更新各区域内容

6. **Spectre.Console API 注意事项**
   - `LiveDisplay` 没有 `CancellationToken()` 方法
   - `_rootLayout ?? new Text()` 类型不匹配，需要 `(IRenderable)new Text()`
   - `Layout["RegionName"]` 访问子布局

### 文件位置
- `MainApp.cs`: 应用入口，Live 循环，事件协调
- `Services/LayoutService.cs`: 两栏布局服务
- `Infrastructure/ServiceCollectionExtensions.cs`: DI 注册
- `Program.cs`: 启动引导

### 预留接口
- `ChatOrchestrator` 集成点：`SimulateAiResponse()` 待替换
- `EventRouter` 集成点：事件流路由待实现
- `EventChannelService` 集成点：Channel 缓冲待实现

## 2026-04-11: EventChannelService + EventRouter 事件流路由

### 核心设计

1. **Channel 生产者-消费者模式**
   - `System.Threading.Channels.Channel<IMessageEvent>` 实现异步事件流
   - `BoundedChannelOptions` 配置：容量 1000，满时等待（背压）
   - `SingleReader = true`，`SingleWriter = false`（多 Agent 发布）
   
2. **批量刷新策略**
   - 刷新间隔：100ms（EventRouter.RefreshIntervalMs）
   - 批量阈值：10 事件（EventRouter.BatchRefreshThreshold）
   - 满足任一条件触发刷新：`elapsedMs >= RefreshIntervalMs || pendingCount >= BatchRefreshThreshold`

3. **事件路由分发**
   ```csharp
   switch (evt.Type)
   {
       case StreamDelta → HandleStreamDelta（追加到 StreamingMessage）
       case StreamComplete → HandleStreamComplete（保存到 MessageStore）
       case ToolCallEvent → HandleToolCall（更新 ToolCallDisplay）
       case SubAgentEvent → HandleSubAgent（更新 SubAgentDisplay）
       case ErrorEvent → HandleError（添加错误消息）
   }
   ```

4. **Live 上下文刷新**
   - `ctx.Refresh()` 在 Live.StartAsync 中调用
   - 单线程渲染：EventRouter 是唯一消费者
   - `ctx.UpdateTarget(newContent)` 更新渲染目标

5. **只读属性处理**
   - `ToolCallDisplay.Duration` 和 `SubAgentDisplay.Duration` 是只读计算属性
   - 只设置 `EndTime`，Duration 自动由 `EndTime - StartTime` 计算

### C# 关键字避坑

- `event` 是 C# 关键字，参数名必须用 `evt` 或其他名称替代

### 命名冲突解决

- Core.TuiState vs State.TuiState 使用别名：
  ```csharp
  using TuiState = Seeing.Agent.Tui.Core.State.TuiState;
  ```

### 文件位置
- `Services/EventChannelService.cs`: Channel 事件发布服务
- `Services/EventRouter.cs`: Live 上下文事件路由器
- `UI/Renderers/LiveRenderer.cs`: Spectre.Console Live 渲染封装

### DI 注册（Program.cs）
```csharp
services.AddSingleton<EventChannelService>();
services.AddSingleton<RenderService>();
services.AddSingleton<EventRouter>();
```

### Spectre.Console API 注意事项
- `AnsiConsole.Live(target).StartAsync(async ctx => {...})`
- `AutoRefresh()` 不是 Live 方法，是 Progress 扩展方法
- `Overflow.Vertical` 和 `VerticalCropping` 不存在，应移除
