# TUI 样式优化计划（方案C）

## TL;DR

> **Quick Summary**: 优化 Spectre.Console TUI 样式，包括状态栏布局、消息面板、主题系统、欢迎界面、Markdown渲染、流式消息等7个阶段。
>
> **Deliverables**:
> - 重构的状态栏（信息分组、视觉分隔）
> - 优化的消息面板（边框简化、折叠改进）
> - 完善的主题系统（统一颜色定义）
> - 简化的欢迎界面（卡片式布局）
> - 增强的 Markdown 渲染（VS Code 风格代码高亮）
> - 改进的流式消息（边框样式）
> - 新增原生组件（Table、Tree、BarChart）
>
> **Estimated Effort**: Medium (5-7天)
> **Parallel Execution**: YES - 多个阶段可并行
> **Critical Path**: Phase 3(主题) → Phase 1(状态栏) → Phase 2(消息面板) → Phase 4-7

---

## Context

### Original Request
用户要求优化 TUI 样式显示，选择方案C：在现有 Spectre.Console 框架上优化样式，不更换框架。

### Interview Summary
- **优化范围**: 状态栏、消息面板、欢迎界面、Markdown渲染、流式消息
- **优先级**: 状态栏 > 消息面板 > 主题系统 > 欢迎界面 > Markdown > 流式消息 > 原生组件
- **主题策略**: 完善现有主题（不支持用户自定义）
- **欢迎界面**: 简化布局（移除 FigletText）
- **新组件**: 添加 Spectre.Console 原生组件（Table、Tree、BarChart）

---

## Work Objectives

### Core Objective
优化 TUI 视觉呈现和用户体验，提升信息可读性和界面美观度。

### Definition of Done
- [ ] 状态栏信息清晰分组，可读性提升
- [ ] 消息面板边框紧凑，折叠显示改进
- [ ] 主题系统统一颜色定义
- [ ] 欢迎界面简洁美观
- [ ] Markdown 渲染使用 VS Code 风格
- [ ] 流式消息有明确边框
- [ ] 原生 Spectre 组件集成

---

## Execution Strategy

### Parallel Execution Waves

```
Phase 3 (Start Immediately - 主题基础):
├── Task 1: 完善主题系统 [quick]

Phase 1 (After Phase 3 - 状态栏):
├── Task 2: 状态栏布局重构 [quick]

Phase 2 (After Phase 3 - 消息面板):
├── Task 3: 消息面板边框优化 [quick]
├── Task 4: 折叠状态优化 [quick]
├── Task 5: 工具调用显示改进 [quick]

Phase 4 (Parallel with Phase 1-2 - 欢迎界面):
├── Task 6: 欢迎界面简化 [quick]

Phase 5 (Parallel - Markdown):
├── Task 7: 代码块样式改进 [visual-engineering]
├── Task 8: 颜色方案VS Code风格 [visual-engineering]

Phase 6 (Parallel - 流式消息):
├── Task 9: 流式消息边框 [quick]

Phase 7 (Parallel - 原生组件):
├── Task 10: 添加 Table 组件 [quick]
├── Task 11: 添加 Tree 组件 [quick]
├── Task 12: 添加 BarChart 组件 [quick]

Phase FINAL (验证):
├── Task F1: 视觉验收测试 [unspecified-high]
├── Task F2: 功能测试 [unspecified-high]
├── Task F3: 不同终端测试 [unspecified-high]
```

---

## TODOs

### Phase 3: 主题系统完善

- [x] 1. 完善主题系统

  **What to do**:
  - 扩展 `DefaultTheme.cs`，添加：
    - 状态栏专用颜色组
    - 消息面板专用样式组
    - Markdown 渲染专用颜色（VS Code 风格）
    - 流式消息颜色
  - 创建样式组类：`StatusBarStyles`、`MessageStyles`
  - 在 `UIComponents.cs` 中替换硬编码颜色

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO - 基础设施，其他阶段依赖
  - **Blocks**: Tasks 2-12
  - **Blocked By**: None

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Themes/DefaultTheme.cs`
  - VS Code 颜色方案: https://code.visualstudio.com/api/references/theme-color

  **Acceptance Criteria**:
  - [ ] 状态栏颜色定义存在
  - [ ] 消息面板样式定义存在
  - [ ] VS Code 风格颜色定义存在
  - [ ] 编译无错误

  **Commit**: YES
  - Message: `feat(tui): extend DefaultTheme with specialized color groups`
  - Files: `UI/Themes/DefaultTheme.cs`

---

### Phase 1: 状态栏布局重构

- [x] 2. 状态栏布局重构

  **What to do**:
  - 修改 `UIComponents.cs` 的 `StatusBar.Build()` 方法
  - 重新排列 columns 列表，使用分组布局：
    - Group 1: Logo (Seeing.Agent)
    - Group 2: Agent Info (Agent: primary, Model: gpt-4)
    - Group 3: Resources (🔧14 📚8 🔌1)
    - Group 4: Session (#2)
  - 使用分隔符：`║`（主分隔），`│`（组内分隔）
  - 优化处理状态图标显示
  - 优化搜索状态显示

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 3-6)
  - **Blocked By**: Task 1

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs:21-77`
  - `samples/Seeing.Agent.Tui/Core/TuiState.cs`

  **Acceptance Criteria**:
  - [ ] 状态栏信息清晰分组
  - [ ] 分隔符美观
  - [ ] 处理中状态正常显示
  - [ ] 搜索状态正常显示
  - [ ] 编译无错误

  **Commit**: YES
  - Message: `refactor(tui): reorganize StatusBar layout with grouped columns`
  - Files: `UI/Components/UIComponents.cs`

---

### Phase 2: 消息面板样式优化

- [x] 3. 消息面板边框优化
- [x] 4. 折叠状态优化
- [x] 5. 工具调用显示改进

  **What to do**:
  - 修改工具调用渲染逻辑
  - 添加状态图标：✓（成功）、✗（失败）、⏳（执行中）
  - 显示执行耗时
  - 错误信息使用红色背景显示

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2, 3, 4)
  - **Blocked By**: Task 1

  **Acceptance Criteria**:
  - [ ] 工具状态清晰显示
  - [ ] 编译无错误

  **Commit**: YES (groups with Tasks 3, 4)

---

### Phase 4: 欢迎界面简化

- [x] 6. 欢迎界面简化

  **What to do**:
  - 修改 `UIComponents.cs` 的 `BuildWelcomeScreen()` 方法
  - 移除 `FigletText` Logo
  - 使用简洁 Logo：`[cyan bold]◆[/] [white bold]Seeing.Agent[/]`
  - 使用 Panel 分组信息：状态卡片、快捷命令卡片
  - 简化工作区显示

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2-5, 7-12)
  - **Blocked By**: Task 1

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Components/UIComponents.cs:420-472`

  **Acceptance Criteria**:
  - [ ] FigletText 已移除
  - [ ] 使用卡片式布局
  - [ ] 编译无错误

  **Commit**: YES
  - Message: `refactor(tui): simplify welcome screen with card layout`
  - Files: `UI/Components/UIComponents.cs`

---

### Phase 5: Markdown 渲染增强

- [x] 7. 代码块样式改进

  **What to do**:
  - 修改 `MarkdownToSpectreConverter.cs` 的 `RenderFencedCodeBlock()` 方法
  - 使用 VS Code 风格代码块边框
  - 添加行号显示
  - 优化语言标签显示

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2-6, 8-12)
  - **Blocked By**: Task 1

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Renderers/MarkdownToSpectreConverter.cs:364-384`

  **Acceptance Criteria**:
  - [ ] 代码块边框美观
  - [ ] 行号显示正确
  - [ ] 编译无错误

  **Commit**: YES (groups with Task 8)

---

- [x] 8. 颜色方案 VS Code 风格

  **What to do**:
  - 修改 `MarkdownToSpectreConverter.cs` 的 `ApplySyntaxHighlighting()` 方法
  - 使用 VS Code 颜色：
    - 关键字：`#569CD6`（蓝色）
    - 字符串：`#CE9178`（橙色）
    - 注释：`#6A9955`（绿色）
    - 数字：`#B5CEA8`（浅绿）
  - 应用主题颜色定义

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2-7, 9-12)
  - **Blocked By**: Task 1

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Renderers/MarkdownToSpectreConverter.cs:413-427`

  **Acceptance Criteria**:
  - [ ] VS Code 风格颜色应用
  - [ ] 编译无错误

  **Commit**: YES (groups with Task 7)

---

### Phase 6: 流式消息优化

- [x] 9. 流式消息边框

  **What to do**:
  - 修改 `MainChatScreen.cs` 的 `BuildStreaming()` 方法
  - 添加轻量级边框
  - 区分思考过程、正文、工具调用
  - 优化等待提示

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2-8, 10-12)
  - **Blocked By**: Task 1

  **References**:
  - `samples/Seeing.Agent.Tui/UI/Screens/MainChatScreen.cs:104-156`

  **Acceptance Criteria**:
  - [ ] 流式消息有明确边框
  - [ ] 各部分视觉区分
  - [ ] 编译无错误

  **Commit**: YES
  - Message: `feat(tui): add visual borders to streaming messages`
  - Files: `UI/Screens/MainChatScreen.cs`

---

### Phase 7: 原生组件集成

- [x] 10. 添加 Table 组件
- [x] 11. 添加 Tree 组件
- [x] 12. 添加 BarChart 组件

  **What to do**:
  - 在 `SpectreComponents.cs` 添加 `BuildResourceChart()` 方法
  - 在欢迎界面添加资源柱状图

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2-11)
  - **Blocked By**: Task 1

  **Acceptance Criteria**:
  - [ ] BarChart 组件工作正常
  - [ ] 编译无错误

  **Commit**: YES (groups with Tasks 10, 11)

---

## Final Verification Wave

- [x] F1. 视觉验收测试
  - 运行 TUI 应用
  - 验证状态栏布局
  - 验证消息面板样式
  - 验证欢迎界面
  - 验证 Markdown 渲染
  - 验证流式消息

- [x] F2. 功能测试
  - 发送消息测试流式渲染
  - 测试工具调用显示
  - 测试折叠功能
  - 测试搜索状态显示

- [x] F3. 不同终端测试
  - Windows Terminal
  - CMD
  - VS Code Terminal
  - 验证颜色显示

---

## Commit Strategy

- **1**: `feat(tui): extend DefaultTheme with specialized color groups`
- **2**: `refactor(tui): reorganize StatusBar layout with grouped columns`
- **3-5**: `refactor(tui): simplify message panel border and improve tool display`
- **6**: `refactor(tui): simplify welcome screen with card layout`
- **7-8**: `feat(tui): add VS Code style code highlighting`
- **9**: `feat(tui): add visual borders to streaming messages`
- **10-12**: `feat(tui): add native Spectre components (Table, Tree, BarChart)`