# Learnings from TUI Live Display Refactor

- Followed Spectre.Console Live Display pattern to implement a single-loop UI.
- Used Layout with Header (2 lines) / Messages / Streaming (dynamic) / Input (5 lines).
- Implemented a dirty-regions render strategy via RenderContext and RenderRegion to minimize Refresh calls.
- Hooked into a dynamic host state via a best-effort StateChanged event to mark dirty regions.
- Handled terminal resize gracefully by relying on Spectre.Console Live layout recomputation (no manual Console API usage).
- Initial build success achieved for the TUI sample project: samples/Seeing.Agent.Tui.

Next steps:
- Connect real content rendering for Header, Messages, Streaming, and Input from actual TUI state.
- Wire BuildStreaming() to reflect real streaming data when available.
- Enhance state change handling to reflect precise Regions without runtime casts when possible.

## Task: StatusBar 增量更新（2026-04-09）

- 实现要点：在 samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs 的 StatusBar 中加入缓存与增量更新逻辑。
- 变更内容：
  - 新增缓存字段用以保存上一次渲染的 Panel 和状态键
  - 引入状态键 currentKey，只有状态变化时才重建 Panel
  - 增加消息数量显示与滚动位置显示
- 验证：通过 dotnet build samples/Seeing.Agent.Tui 成功编译。

## Task 5: MainChatScreen 渲染循环重构 (2026-04-09)

### 完成的修改

1. **TuiApplication.cs 重构**:
   - 将 `object _state` 改为强类型 `TuiState`
   - 移除 `dynamic` 绑定，使用直接事件订阅
   - 添加渲染回调委托 `RenderCallback`
   - 添加输入回调委托 `InputCallback` 和消息处理回调 `MessageProcessCallback`
   - 实现输入循环在 Live Display 主循环外运行

2. **MainChatScreen.cs 重构**:
   - 移除 `AnsiConsole.Clear()` 调用
   - 移除传统的 while 循环渲染
   - 通过回调委托将渲染逻辑传递给 TuiApplication
   - `RunAsync()` 简化为调用 `_app.RunAsync()`
   - 保留 CommandHandler 和 ChatOrchestrator 集成
   - 保留流式渲染逻辑

3. **命名空间冲突修复**:
   - `RenderRegion` 在 `Seeing.Agent.Tui.Core` 和 `Seeing.Agent.Tui.UI.Core` 重复定义
   - `StateChangedEvent` 同样重复定义
   - 解决方案：移除 UI.Core 命名空间中的重复定义，使用 Core 命名空间的版本

### 关键设计决策

- **渲染回调模式**: TuiApplication 提供主循环框架，MainChatScreen 通过回调提供具体渲染内容
- **增量更新**: 只在脏区域变化时更新对应 Layout 区块，避免全量重建
- **输入循环分离**: 输入处理在后台线程运行，不阻塞渲染主循环
- **事件驱动**: TuiState.StateChanged 事件自动触发脏标记更新

### 构建验证

```bash
dotnet build samples/Seeing.Agent.Tui --no-restore
# 已成功生成。0 个错误
```

## Task 6: InputArea 输入系统重构 (2026-04-09)

### 完成的修改

1. **TuiApplication.cs 输入协调机制**:
   - 添加 `_consoleLock` 对象用于渲染和输入线程协调
   - 添加 `_inputActive` 标志控制渲染刷新暂停
   - 修改 `RunInputLoopAsync` 在输入前设置 `_inputActive = true`，完成后恢复
   - 渲染循环检查 `_inputActive`，暂停刷新避免与输入处理冲突

2. **InputArea.cs 完全重构**:
   - 保留 `Console.ReadKey()` 原生输入（Spectre.Console 无等效 API）
   - 通过 TuiApplication 的锁机制保证与 Live Display 兼容
   - 实现按键处理器模式：`HandleKeyPress()` 分发到各专用方法
   - 支持功能：
     - **Tab**: 切换多行/单行模式
     - **Up/Down**: 历史记录翻阅
     - **Ctrl+Enter**: 多行模式发送
     - **Backspace**: 正确处理中文删除（使用 `GetDisplayWidth()` 计算字符宽度）
     - **Left/Right**: 光标移动（保留位置）
     - **Escape**: 清空输入

### 关键发现

- **Spectre.Console LiveDisplayContext 没有 Stop/Start 方法**：
  计划文档假设的 `ctx.Stop()/Start()` API 不存在，需要改用锁机制协调

- **原生 Console API 是必要的**：
  Spectre.Console 的 AnsiConsole.Prompt() 是阻塞式的，无法监听特殊按键
  原生 `Console.ReadKey(true)` 是唯一能处理 Tab/Ctrl+Enter/箭头的方案

- **中文输入删除问题**：
  中文字符在控制台显示为 2 个字符宽度，删除时需要计算 `GetDisplayWidth()`
  当前方案：整行重绘避免光标位置计算错误

### 设计模式

- **协调器模式**: TuiApplication 作为协调器，控制渲染和输入的交替执行
- **按键处理器**: InputArea.HandleKeyPress() 分发到专用方法，便于维护
- **缓冲区管理**: StringBuilder 作为输入缓冲区，支持光标位置操作

### 构建验证

```bash
dotnet build samples/Seeing.Agent.Tui --no-incremental
# 已成功生成。0 个错误，215 个警告（均为 XML 注释缺失）
```

## Task 8: MessageHistoryRenderer 虚拟滚动重构 (2026-04-09)

### 完成的修改

1. **MessageHistoryRenderer 完全重构**:
   - 实现虚拟滚动：只渲染可见区域的消息
   - 使用 ScrollOffset 计算可见范围（从底部向上滚动）
   - 添加估算消息高度 EstimatedMessageHeight = 4
   - 添加滚动指示器：顶部显示隐藏的更早消息数，底部显示隐藏的更新消息数

2. **新增辅助方法**:
   - GetMaxScrollOffset(): 计算最大滚动偏移，防止过度滚动
   - HandleScroll(): 处理 Page Up/Down 滚动命令
   - ScrollToTop(): 滚动到顶部（显示最早消息）
   - ScrollToBottom(): 滚动到底部（显示最新消息）

### 关键设计

- **虚拟滚动逻辑**: ScrollOffset 从底部计算，值为 0 时显示最新消息，值越大显示更早消息
- **边界保护**: startIndex/endIndex 双重校验，确保不会超出消息列表范围
- **状态触发**: 滚动操作触发 StateChanged 事件和 NeedsRefresh 标记

### 构建验证

```bash
dotnet build samples/Seeing.Agent.Tui --no-restore
# 已成功生成。0 个错误，6 个警告（环境变量相关）
```

## Task 7: LiveStreamRenderer 流式渲染优化 (2026-04-09)

### 完成的修改

1. **批处理机制实现**:
   - 添加 `BatchIntervalMs = 100ms` 时间间隔阈值
   - 添加 `TokenThreshold = 20` token 数阈值
   - 双重触发条件：超过时间间隔或超过 token 阈值时刷新

2. **缓存字段添加**:
   - `_lastContentLength`: 记录上次刷新时的内容长度
   - `_lastReasoningLength`: 记录上次刷新时的思考内容长度
   - `_lastRefreshTime`: 记录上次刷新时间
   - `_accumulatedTokens`: 累积的 token 计数

3. **核心方法修改**:
   - `HandleDeltaAsync()`: 使用批处理逻辑，只在满足条件时触发刷新
   - `ShouldTriggerRefresh()`: 检查刷新条件（时间/token/首次内容）
   - `UpdateCacheState()`: 更新缓存状态
   - `EstimateTokenCount()`: 简化估算 token 数（每 4 字符约 1 token）
   - `ResetCacheState()`: 重置缓存状态（用于流式结束和错误处理）

4. **工具调用优化**:
   - 工具调用状态变化立即触发渲染更新（不使用批处理）
   - 确保工具调用状态实时显示

5. **事件触发一致性**:
   - `HandleCompleteAsync()`: 触发 `OnStateChanged(RenderRegion.Streaming)` 清空 Streaming 区域
   - `HandleErrorAsync()`: 触发 `OnStateChanged(RenderRegion.Streaming)` 并重置缓存
   - `HandleToolCallAsync()`: 立即触发 `OnStateChanged(RenderRegion.Streaming)`

### 关键设计

- **批处理策略**: 减少渲染刷新频率，提升流式输出性能
- **首次内容优先**: 首次接收内容立即显示，避免用户等待
- **工具调用实时性**: 工具调用状态变化不使用批处理，确保实时反馈
- **状态一致性**: 所有事件处理方法都正确触发状态变化事件

### 构建验证

```bash
dotnet build samples/Seeing.Agent.Tui --no-restore
# 已成功生成。0 个错误，3 个警告（环境变量相关）
```

---

## 2026-04-09 更新: MarkdownToSpectreConverter 增强

### 语法高亮实现

**关键字字典模式:**
- `Dictionary<string, HashSet<string>>` 存储语言关键字（C#/JS/Python）
- 语言别名映射：`cs → csharp`, `js → javascript`
- 关键字匹配使用 `StringComparer.OrdinalIgnoreCase`

**代码段分割策略:**
- 正则表达式识别：字符串、注释、数字、方法调用、运算符
- 普通文本中识别关键字

**Spectre.Console 颜色映射:**
- Keyword: `[blue bold]`
- String: `[green]`
- Comment: `[dim grey italic]`
- Number: `[yellow]`
- Method: `[cyan]`
- Operator: `[magenta]`

### 表格渲染

**Markdig 扩展启用:** `.UsePipeTables().UseGridTables()`

**渲染流程:**
1. 计算列宽（最大 40 字符）
2. 表头（黄色粗体）+ 分隔线（`┌─┬─┐` / `├─┼─┤` / `└─┴─┘`）
3. 数据行使用白色

### 嵌套列表渲染

**递归处理:** `RenderList(ListBlock, StringBuilder, int indent)`
- 每层嵌套增加 2 空格缩进
- 处理 `ListItemBlock` 子块中的嵌套 `ListBlock`

### 链接显示优化

**类型识别与图标:**
- GitHub → 📦 cyan
- 文档 → 📖 yellow
- API → 🔌 green
- 外部 → 🔗 cyan
- 本地文件 → 📄 green
- 邮箱 → 📧 yellow

---

## 2026-04-09 更新: 消息导航功能实现

### 完成的修改

1. **TuiState.cs 扩展**:
   - 添加导航状态属性：SearchKeyword, SearchMatchIndices, CurrentSearchMatchIndex, IsSearchMode, FoldedMessageIds, HighlightedMessageIndex
   - 添加搜索导航方法：ClearSearch(), SetSearchKeyword(), NavigateNextMatch(), NavigatePrevMatch(), ScrollToMatch()
   - 添加折叠方法：ToggleMessageFold(), GetMessageId()

2. **MessageNavigator.cs 新组件**:
   - 静态类提供消息导航功能封装
   - SearchMessages(), ClearSearch(), NavigateNextMatch(), NavigatePrevMatch()
   - ToggleMessageFold(), HandleScrollNavigation()
   - BuildSearchIndicator(), BuildNavigationHelp(), GetSearchStatusText()

3. **CommandHandler.cs 扩展**:
   - 添加 /search 命令处理：支持搜索关键词、清除搜索、导航匹配项
   - 添加 /fold 命令处理：折叠/展开指定消息
   - 更新帮助文本添加导航命令说明

4. **UIComponents.cs 修改**:
   - MessagePanel.Build() 支持折叠和高亮参数
   - 折叠时显示简化预览（📂 折叠 + 内容截断）
   - 高亮时显示 🔍 图标和黄色边框
   - 消息编号显示（#1, #2...）
   - MessageHistoryRenderer.Build() 使用 TuiState 状态渲染折叠/高亮

5. **StatusBar 和 InputPrompt 更新**:
   - StatusBar 显示搜索状态（关键词 + 匹配数 + 当前位置）
   - InputPrompt 显示搜索模式导航提示（n/N 导航匹配）

6. **InputArea.cs 导航键处理**:
   - Page Up/Down: 滚动消息（缓冲区为空时）
   - Home/End: 滚动到顶部/底部
   - n/N/p/P: 搜索模式下导航匹配项

### 关键设计决策

- **搜索不遍历所有消息**: 使用简单 Contains 匹配，避免复杂正则
- **消息标识使用时间戳**: GetMessageId() 返回格式化的时间戳字符串
- **状态驱动渲染**: TuiState 状态变化自动触发 UI 更新
- **键盘导航仅在空闲时**: 输入缓冲区为空时才处理导航键

### 新增命令

```
/search <keyword>  - 搜索消息
/search clear      - 清除搜索
/search next       - 下一个匹配项
/search prev       - 上一个匹配项
/fold <index>      - 折叠/展开指定消息
```

### 构建验证

```bash
dotnet build samples/Seeing.Agent.Tui --no-restore
# 已成功生成。0 个错误，3 个警告（环境变量相关）
```
