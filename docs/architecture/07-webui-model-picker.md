# WebUI 统一模型选择

> **更新日期：** 2026-09-12  
> **实现位置：** `samples/Seeing.Agent.WebUI/Components/Models/`

## 原则

- 目录真相源：`IModelConfigManager`（内存 `_modelCache` + `ModelConfigChanged`）。  
- **禁止** UI 再加静态 TTL 缓存（旧 `SessionToolbar.s_optionCache` 已移除）。  
- **禁止** 再引入 `IModelPickerSource` 之类第二套目录服务；组件直接注入 Manager。  
- 绑定值为 catalog key：`provider/model`。  
- ACP 自由文本模型 / Mode 输入不走本组件。

## 组件

| 组件 | 形态 | 场景 |
|------|------|------|
| `ModelSelectBadge` | 工具栏按钮：模型名 + 灰度 `/ provider`；点击打开 Modal | Session Native |
| `ModelSelectModal` | 搜索、按 Provider 分组、元数据更全；单击即选 | Badge 打开 |
| `ModelSelectDropdown` | AntDesign Select，分组 + Provider Tag | 配置页 |
| `ModelPickerItem` | 目录 → UI 行映射与 `FilterType` / `ProviderId` 过滤 | 共用 |

### 共用参数

- `Value` / `ValueChanged`  
- `FilterType`（`ModelType?`，默认 `Text`；`null` = 不过滤类型）  
- `ProviderId`（可选）  
- `AllowClear` / `Disabled` / `Placeholder` / `Style` / `Class`  
- 孤儿值：仍显示并标「不可用」  
- 订阅 `ModelConfigChanged`；打开下拉 / Modal 时再读快照  

### Badge UI

- 高度对齐工具栏 Agent Select（约 24px），宽度约 160px，超长省略。  
- 文案：`模型名` 优先完整显示；`/ provider` 小字灰度，空间不足时先省略 provider。  
- `IsInvalid` / 孤儿：危险样式。  
- 悬停用 `Tooltip` 显示完整 catalog key（AntDesign `Button` 无 `Title` 参数）。

## 调用约定

| 入口 | 组件 | 过滤 |
|------|------|------|
| Session 工具栏 | `ModelSelectBadge` | `Text` |
| Settings 默认模型 | `ModelSelectDropdown` | `Text`，可清空 |
| Agent 配置默认模型 | `ModelSelectDropdown` | `Text`，可清空 |
| Cron 任务模型 | `ModelSelectDropdown` | `Text` |
| Memory Embedding | `ModelSelectDropdown` | `Embedding`（UI 用 catalog key；存盘仍拆 Provider+Model） |
| Memory 抽取模型 | `ModelSelectDropdown` | `Text`（存盘仍为裸 `cfg.Id`） |
| Models 页测试连接 | `ModelSelectDropdown` | `FilterType=null` + `ProviderId` |

发送前 Native 校验：`SessionWindow` 使用 `ModelManager.GetModelsByType(Text)`，勿依赖可能陈旧的 `AppState.AvailableModels`。

## 测试

- `tests/apps/Seeing.Agent.WebUI.Tests/ModelPickerItemTests.cs`  
- 手测：模型页刷新目录 → Session 打开 Badge Modal 应立刻出现新 Text 模型  

## 样式

- `samples/Seeing.Agent.WebUI/wwwroot/css/model-picker.css`（`_Host.cshtml` 已引用）
