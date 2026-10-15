# Session 页 Native 模型选择器分组设计

**日期:** 2026-08-20
**状态:** 已确认

## 背景

session 页 native 模式下的模型选择器（`samples/Seeing.Agent.WebUI/Pages/Session.razor`）使用 Ant Design `Select` 将全部 provider 的所有模型平铺在下拉列表中，仅在选项右侧以小字附注 `(@provider)`。当模型数量较多时，视觉上无法直观区分模型归属的 provider。

## 目标

- 按 provider 分组展示模型选项，分组标题清晰可辨。
- 仅影响 session 页 native 模式的选择器（桌面端 header + 移动端溢出面板）；ACP 模式输入框与其他页面的模型选择器保持不变。
- 保留现有搜索能力与选中/同步逻辑，不改变数据流与事件。

## 方案

选用 Ant Design `Select` 原生 `GroupName` 分组能力，最小化改动与风险。

| 方案 | 说明 | 取舍 |
|------|------|------|
| A：Select 原生分组（采用） | 加 `GroupName` + 排序属性 | 改动最小，保留搜索 |
| B：Modal 弹窗选择器 | 点击弹出 Modal 按 provider 分区 | 展示更丰富，改动大 |
| C：两级联动 | 先选 provider 再选模型 | 交互成本高 |

## 设计

全部改动集中在 `samples/Seeing.Agent.WebUI/Pages/Session.razor`。

### ① 桌面端 Select（header，约 182-200 行）

- 追加属性：

  ```razor
  GroupName="@nameof(ModelViewModel.ProviderName)"
  SortByGroup="SortDirection.Ascending"
  SortByLabel="SortDirection.Ascending"
  ```

- 移除 `ItemTemplate` 中冗余的 `(@model.ProviderName)` 小字（分组后每项不再重复标注 provider）。
- 保留 `EnableSearch="true"`（搜索跨组过滤，Ant Design 原生支持）。

### ② 移动端 Select（溢出面板，约 261-275 行）

- 追加与桌面端相同的 `GroupName` 与排序属性；`ItemTemplate` 仅显示 `ModelName`，无需改动。

### ③ ProviderName 兜底（`ToModelViewModel`）

- 当 `ProviderName` 为空（目录键无 `/` 且 `ModelConfig.Provider` 为空）时，回退为固定分组名 `"其他"`，避免空分组标题显示空白。

## 不改动

- ACP 模式的 `Input` 框（约 165-177 行）。
- 其他页面的模型选择器（Settings / AgentConfigForm / JobEditorModal 等）。
- 侧边栏 `ModelSelector.razor` 组件。
- 孤儿模型保留逻辑（`_selectedModel` 不在目录中时保持原样，`SyncSelectorValues` 不涉及分组）。
- 选中事件（`OnModelChanged` / `ValueChanged`）与 `SessionManager.SetModelAsync` 数据流不变。

## 验证

- `dotnet build samples/Seeing.Agent.WebUI` 通过。
- 运行 `dotnet run --project samples/Seeing.Agent.WebUI`，切换 native Agent，确认：
  - 下拉选项按 provider 分组、组内按模型名排序；
  - 搜索仍可跨组过滤；
  - 选中模型后显示与持久化行为与改动前一致。

## 风险

- 低。仅新增 Select 展示属性与一处兜底逻辑，不涉及后端与状态管理。
