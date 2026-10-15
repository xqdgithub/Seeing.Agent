# WebUI 工具调用展示组件重设计

**日期:** 2026-07-18  
**范围:** `samples/Seeing.Agent.WebUI` 消息流中的工具调用展开详情  
**状态:** 设计已确认，待实现

---

## 问题

当前 `ToolCallMessageComponent` 展开后信息不友好：

- **参数**仅显示 JSON 的键名（如 `filePath, content`），看不到实际值  
- **结果**截断（约 500 字符）且可视高度过矮，难以查看真实返回内容  

根因在 `FormatParametersBrief()` 只枚举属性名，以及结果区硬编码截断，而非数据缺失。会话层已有完整 `Arguments` / `Result`。

---

## 目标

1. 展开后能看到**具体参数值**与**真实完整结果**  
2. 参数默认键值表，可切换原始 JSON  
3. 结果默认可读视图（JSON 自动格式化 / 纯文本原样），可切换原始，并支持「展开全部」  
4. 收起行保持现状（工具名 + 状态 + 短预览）  
5. `todowrite` 继续走 `TodoCard`，不进入通用详情区  

非目标：语法高亮库、Diff 视图、按工具类型定制布局、改 Session/Event 数据管道。

---

## 方案

**选定：增强现有组件（方案 1）**  

在 `ToolCallMessageComponent` 内重做展开区，不新建侧边抽屉，暂不拆独立共享组件（后续若多处复用再抽离）。

### 结构

```
[收起行] 图标 | 工具名 | 状态Tag | 预览 | ▸     ← 不变
[展开区]
  ├─ 参数
  │    [键值表 | JSON]  [复制]
  │    · 键值表：name → value
  │    · JSON：格式化 Parameters
  └─ 结果 / 错误
       [可读 | 原始]  [展开全部]  [复制]
       · 可读：JSON 格式化或纯文本
       · 原始：完整 Result / Error 字符串
```

### 参数区规则

| 项 | 行为 |
|----|------|
| 默认视图 | 键值表 |
| 解析成功（JSON object） | 每行 `name` + 实际 `value`；嵌套 object/array 用紧凑 JSON |
| 解析失败 / 非 object | 显示原始字符串 |
| JSON 视图 | 缩进格式化；失败则原样 |
| 长值 | 约超过 8 行或 400 字符时单元格内滚动，不截断丢内容 |
| 复制 | 复制当前视图全文 |

### 结果区规则

| 项 | 行为 |
|----|------|
| 默认视图 | 可读（可 parse 为 JSON 则格式化，否则 `pre-wrap` 文本） |
| 原始视图 | 完整 `Result` / `Error`，不做语义截断 |
| 默认高度 | 约 240px，超出滚动 |
| 展开全部 | 取消高度限制 |
| 错误 | 同上交互，错误色样式 |
| 复制 | 复制当前视图内容 |

说明：若工具本身只返回短句（如「新文件已创建。」），UI 如实显示；但参数值必须完整可见。

---

## 数据流

不变：

- `SessionToolCall.Arguments` → `ToolCallViewModel.Parameters`  
- `SessionToolCall.Result` / `Error` → ViewModel 同名字段  
- `EventStreamHandler` / `FromSessionToolCall` 无需修改  

仅改 UI 渲染与本地交互状态（视图模式、是否展开全部）。

---

## 改动文件

| 文件 | 改动 |
|------|------|
| `Components/Messaging/ToolCallMessageComponent.razor` | 重做展开区；解析、切换、展开全部、复制；删除「只显示键名」逻辑 |
| `wwwroot/css/tool-components.css` | 展开详情：分区、切换控件、键值表、代码块、滚动区；亮/暗色适配 |

复制沿用现有 `IJSRuntime` → `navigator.clipboard.writeText`（与 `AgentMdPreview` 一致）。不新增 NuGet。

---

## 错误处理

- JSON 解析失败：降级为原始字符串展示，不抛到 UI  
- 剪贴板失败：静默或短暂提示（与现有复制模式一致即可）  
- 空参数（null / `{}`）或不展示结果时：对应分区不渲染

---

## 验收

展开写文件类工具调用时，应能看到类似：

```
参数 [键值表|JSON] [复制]
filePath  →  <真实路径>
content   →  <真实文件内容，可滚动>

结果 [可读|原始] [展开全部] [复制]
<工具返回的完整文本>
```

不再出现「参数: filePath, content」这种仅键名列表。

---

## 测试建议

- 手动：触发 `write` / `read` / `bash` 等工具，核对键值与完整结果  
- 边界：非法 JSON 参数、超长 content、失败错误信息、`todowrite` 仍显示 TodoCard  
- 主题：亮色 / 暗色下展开区可读性
