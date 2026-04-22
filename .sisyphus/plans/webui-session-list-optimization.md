# WebUI 会话列表布局优化计划

## TL;DR

> **Quick Summary**: 优化 WebUI 会话列表布局，采用紧凑单行显示 + 时间分组 + 可折叠设计，减少视觉占用，提升用户体验。
> 
> **Deliverables**:
> - 紧凑的会话列表样式（单行约 36px 高度）
> - 按时间分组（今天/昨天/本周/更早）
> - 可折叠的分组标题
> - 更小的侧边栏宽度（200px）
> 
> **Estimated Effort**: Medium
> **Parallel Execution**: NO - 顺序执行，CSS/组件相互依赖
> **Critical Path**: CSS 变量 → 样式修改 → 组件逻辑 → 验证

---

## Context

### Original Request
用户反馈当前会话列表存在问题：
1. 展示面积过大 - 每个会话项占用太多空间
2. 过多会话导致滚动条出现，影响右侧聊天界面展示
3. 希望布局更优化

### Interview Summary
**Key Discussions**:
- 分组功能: 用户选择启用分组 + 可折叠
- 搜索方案: 用户选择保留搜索框，缩小高度
- 布局优化: 减小侧边栏宽度，紧凑会话项样式

### Research Findings
- 当前会话项高度约 50-60px（含标题+meta两行）
- 侧边栏宽度 260px 占用过多空间
- ChatGPT/Claude 等产品的紧凑设计是业界最佳实践

---

## Work Objectives

### Core Objective
实现紧凑、可分组、可折叠的会话列表，减少视觉占用，提升聊天界面展示效果。

### Concrete Deliverables
- `design-system.css` - 更小的布局变量
- `session-sidebar.css` - 紧凑的会话项样式 + 分组样式
- `main-layout.css` - 优化的侧边栏布局
- `SessionSidebar.razor` - 分组逻辑 + 折叠状态管理

### Definition of Done
- [ ] 会话项高度减小到约 36px
- [ ] 分组标题显示并可折叠
- [ ] 侧边栏宽度减小到 200px
- [ ] 搜索框高度缩小
- [ ] 暗色主题样式同步更新
- [ ] 页面构建成功无错误

### Must Have
- 紧凑的单行会话项显示
- 时间分组（今天/昨天/本周/更早）
- 可折叠的分组标题
- 更小的侧边栏默认宽度

### Must NOT Have (Guardrails)
- 不要破坏现有功能（搜索、右键菜单等）
- 不要改变会话数据结构
- 不要影响暗色主题兼容性

---

## Verification Strategy

### Test Decision
- **Infrastructure exists**: NO (Blazor UI 测试通常需要手动验证)
- **Automated tests**: None - UI 修改通过手动 QA 验证
- **Agent-Executed QA**: Playwright 浏览器自动化验证

### QA Policy
每个任务包含 Agent-Executed QA Scenarios - 使用 Playwright 打开浏览器，验证 UI 效果。

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (基础 CSS 变量):
└── Task 1: 更新设计系统变量 [quick]

Wave 2 (样式修改):
├── Task 2: 更新 session-sidebar.css 紧凑样式 [visual-engineering]
├── Task 3: 更新 main-layout.css 侧边栏布局 [visual-engineering]
└── Task 4: 添加分组折叠样式 [visual-engineering]

Wave 3 (组件逻辑):
├── Task 5: 实现分组逻辑 GetSessionGroup [quick]
├── Task 6: 实现分组折叠状态管理 [quick]
└── Task 7: 重构 SessionSidebar.razor 渲染逻辑 [unspecified-high]

Wave FINAL (验证):
├── Task F1: Playwright UI 验证 - 紧凑效果 [unspecified-high]
├── Task F2: Playwright UI 验证 - 分组折叠 [unspecified-high]
├── Task F3: 构建验证 [quick]
└── Task F4: 用户确认 [quick]

Critical Path: T1 → T2-T4 → T5-T7 → F1-F4
```

---

## TODOs

- [x] 1. 更新设计系统变量

  **What to do**:
  - 修改 `wwwroot/css/design-system.css`
  - 更小侧边栏宽度、更小 header/footer 高度

  **Must NOT do**:
  - 不要改变颜色变量
  - 不要影响其他组件的布局

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: CSS 变量修改是简单任务
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO - 基础变量，其他任务依赖
  - **Parallel Group**: Wave 1
  - **Blocks**: T2, T3, T4
  - **Blocked By**: None

  **References**:
  - `wwwroot/css/design-system.css:117-121` - 当前布局变量定义

  **Acceptance Criteria**:
  - [ ] --sidebar-width: 200px
  - [ ] --sidebar-collapsed-width: 64px
  - [ ] --header-height: 56px
  - [ ] --footer-height: 40px

  **QA Scenarios**:
  ```
  Scenario: CSS 变量已更新
    Tool: Bash
    Steps:
      1. grep "sidebar-width" design-system.css
      2. 验证值为 200px
    Expected Result: 输出显示 --sidebar-width: 200px
    Evidence: .sisyphus/evidence/task-1-css-vars.txt
  ```

  **Commit**: YES
  - Message: `style(webui): reduce sidebar and header dimensions`
  - Files: `wwwroot/css/design-system.css`

- [x] 2. 更新 session-sidebar.css 紧凑样式

  **What to do**:
  - 减小会话项 padding (6px 8px)
  - 移除边框，改为 hover 背景
  - 单行显示标题 + 时间
  - 减小选中指示器宽度
  - 缩小搜索框区域

  **Must NOT do**:
  - 不要移除右键菜单样式
  - 不要破坏响应式布局

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: UI 样式优化需要视觉设计考量
  - **Skills**: []
  - **Skills Evaluated but Omitted**:
    - `frontend-ui-ux`: CSS 样式优化，不需要完整 UI/UX 设计

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with T3, T4)
  - **Blocks**: T5
  - **Blocked By**: T1

  **References**:
  - `wwwroot/css/session-sidebar.css:45-53` - 当前会话项样式
  - `wwwroot/css/session-sidebar.css:28-32` - session-list-container

  **Acceptance Criteria**:
  - [ ] session-item padding: 6px 8px (或更小)
  - [ ] session-item 无边框
  - [ ] session-item 高度约 36px
  - [ ] session-title 单行显示
  - [ ] session-time 同行显示，靠右

  **QA Scenarios**:
  ```
  Scenario: 紧凑样式生效
    Tool: Playwright
    Steps:
      1. 启动 WebUI 应用
      2. 导航到主页面
      3. 检查会话列表区域高度
      4. 截图对比
    Expected Result: 会话项明显变小，单行显示
    Evidence: .sisyphus/evidence/task-2-compact.png
  ```

  **Commit**: YES
  - Message: `style(webui): compact session sidebar styles`
  - Files: `wwwroot/css/session-sidebar.css`

- [x] 3. 更新 main-layout.css 侧边栏布局

  **What to do**:
  - 优化 sider-header/sider-footer 高度
  - 减小 sider-selectors padding
  - 确保侧边栏整体紧凑

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: 布局样式优化
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with T2, T4)
  - **Blocks**: None
  - **Blocked By**: T1

  **References**:
  - `wwwroot/css/main-layout.css:17-46` - 侧边栏样式

  **Commit**: YES
  - Message: `style(webui): optimize sidebar layout dimensions`
  - Files: `wwwroot/css/main-layout.css`

- [x] 4. 添加分组折叠样式

  **What to do**:
  - 新增 session-group 样式
  - 分组标题样式（点击折叠/展开）
  - 折叠图标样式
  - 折叠状态下的隐藏效果

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: 新增 UI 样式
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with T2, T3)
  - **Blocks**: T7
  - **Blocked By**: T1

  **Acceptance Criteria**:
  - [ ] .session-group-title 样式定义
  - [ ] .session-group-collapsed 折叠状态样式
  - [ ] .session-group-icon 展开/折叠图标样式

  **Commit**: YES
  - Message: `style(webui): add session group styles`
  - Files: `wwwroot/css/session-sidebar.css`

- [x] 5. 实现分组逻辑 GetSessionGroup

  **What to do**:
  - 在 SessionSidebar.razor 添加 GetSessionGroup 方法
  - 分组逻辑: 今天(24h) / 昨天(48h) / 本周(7d) / 更早
  - 添加 GetGroupedSessions 方法返回分组后的会话

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 简单的日期计算逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with T6)
  - **Blocks**: T7
  - **Blocked By**: T2, T4

  **References**:
  - `Components/SessionSidebar.razor:210-224` - 现有的 GetSessionTime 方法可参考

  **Code Template**:
  ```csharp
  private string GetSessionGroup(SessionViewModel session)
  {
      var diff = DateTime.Now - session.UpdatedAt;
      if (diff.TotalHours < 24) return "今天";
      if (diff.TotalHours < 48) return "昨天";
      if (diff.TotalDays < 7) return "本周";
      return "更早";
  }

  private IEnumerable<IGrouping<string, SessionViewModel>> GetGroupedSessions()
  {
      return FilteredSessions
          .OrderByDescending(s => s.UpdatedAt)
          .GroupBy(s => GetSessionGroup(s));
  }
  ```

  **Commit**: YES
  - Message: `feat(webui): add session grouping logic`
  - Files: `Components/SessionSidebar.razor`

- [x] 6. 实现分组折叠状态管理

  **What to do**:
  - 添加 _groupCollapsed Dictionary 状态
  - 实现 ToggleGroupCollapse 方法
  - 默认: 今天/昨天展开，本周/更早折叠

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 简单的状态管理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 3 (with T5)
  - **Blocks**: T7
  - **Blocked By**: T4

  **Code Template**:
  ```csharp
  private Dictionary<string, bool> _groupCollapsed = new()
  {
      ["今天"] = false,
      ["昨天"] = false,
      ["本周"] = true,
      ["更早"] = true
  };

  private void ToggleGroupCollapse(string group)
  {
      _groupCollapsed[group] = !_groupCollapsed[group];
  }
  ```

  **Commit**: YES
  - Message: `feat(webui): add group collapse state management`
  - Files: `Components/SessionSidebar.razor`

- [x] 7. 重构 SessionSidebar.razor 渲染逻辑

  **What to do**:
  - 移除现有 foreach 循环
  - 添加分组遍历逻辑
  - 添加分组标题渲染（含折叠图标）
  - 添加折叠状态判断
  - 紧凑的单行会话项渲染

  **Must NOT do**:
  - 不要移除右键菜单功能
  - 不要移除选中状态处理
  - 不要移除搜索功能

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 组件重构需要仔细处理现有功能
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: NO - 综合之前的修改
  - **Parallel Group**: Wave 3 (after T5, T6)
  - **Blocks**: F1, F2
  - **Blocked By**: T5, T6

  **References**:
  - `Components/SessionSidebar.razor:56-92` - 现有会话列表渲染逻辑

  **Render Template**:
  ```razor
  <!-- 会话列表 -->
  <div Class="session-list-container">
      @foreach (var group in GetGroupedSessions())
      {
          var groupName = group.Key;
          var isCollapsed = _groupCollapsed.GetValueOrDefault(groupName, false);
          
          <!-- 分组标题 -->
          <div Class="session-group-title" @onclick="@(() => ToggleGroupCollapse(groupName))">
              <Icon Type="@IconType.Outline.@(isCollapsed ? "Plus" : "Minus")" />
              <span>@groupName</span>
              <Badge Count="@group.Count()" Style="margin-left: auto;" />
          </div>
          
          <!-- 分组内容 -->
          @if (!isCollapsed)
          {
              <div Class="session-group-content">
                  @foreach (var session in group)
                  {
                      <!-- 紧凑的会话项 -->
                  }
              </div>
          }
      }
  </div>
  ```

  **Commit**: YES
  - Message: `feat(webui): implement grouped session list with collapse`
  - Files: `Components/SessionSidebar.razor`

---

## Final Verification Wave

- [x] F1. **Playwright UI 验证 - 紧凑效果**
  启动应用，验证会话列表紧凑效果，截图对比。

- [x] F2. **Playwright UI 验证 - 分组折叠**
  点击分组标题，验证折叠/展开效果。

- [x] F3. **构建验证**
  运行 `dotnet build samples/Seeing.Agent.WebUI`，确保无错误。

- [x] F4. **用户确认**
  展示修改效果，等待用户确认。

---

## Commit Strategy

- **T1-T4**: 单次提交 CSS 变量和样式修改
- **T5-T6**: 单次提交分组逻辑
- **T7**: 单次提交组件重构

---

## Success Criteria

### Verification Commands
```bash
dotnet build samples/Seeing.Agent.WebUI  # Expected: Build succeeded
```

### Final Checklist
- [ ] 会话项高度减小到约 36px
- [ ] 分组标题显示并可折叠
- [ ] 侧边栏宽度减小到 200px
- [ ] 搜索框保留且高度缩小
- [ ] 暗色主题样式同步
- [ ] 构建成功
- [ ] 用户确认效果满意