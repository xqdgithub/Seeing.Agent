# 会话工作台插件平台 Release Notes

**日期:** 2026-11-10
**范围:** `src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/`、`src/spine/Seeing.Agent.Core/Ui/UiContributionQuery.cs`、新增能力包 `Seeing.Agent.Workbench`、新增 Razor 类库 `Seeing.Agent.WebUI.Workbench`、`Seeing.Agent.Files`、`samples/Seeing.Agent.WebUI.Files`、`samples/Seeing.Agent.WebUI`
**分支:** `refactor/ui-extension-envelope`
**前置:** [`40-ui-extension-unified-architecture-release-notes.md`](40-ui-extension-unified-architecture-release-notes.md)（UI 扩展信封）

## 一句话

会话页的「文件面板插槽」升级为**可停靠工作台平台**：在 UI 信封上只加一种新载荷（`WorkbenchContribution`）和一个扩展点（`workbench.dock`），就得到了左右双 dock、多实例并存、状态持久化、agent 可列举可驱动、查看内容回流为上下文源 —— 而**没有**引入任何新的注册表 / 插件接口 / 描述符类型。

---

## 一、为什么不需要第二套注册表

实现前先写了一份独立工作台平台规格（`docs/superpowers/specs/2026-11-09-workbench-plugin-platform-design.md`），里面设计了 `IWorkbenchPlugin` / `IWorkbenchRegistry` / `WorkbenchDescriptor` / `IWorkbenchProviderLoader` 四件套。信封落地后逐条核对，发现它们**与信封完全重叠**：

| 旧设计概念 | 信封已有的对应物 | 结论 |
|-----------|-----------------|------|
| `IWorkbenchPlugin.Descriptor` | `UiContribution` 本身（`Id` / `Order` / `Requires`） | 删除 |
| `IWorkbenchRegistry.Register/Unregister/UnregisterByOwner` | `IUiContributionRegistry.AddRange` / `RemoveByOwner`（且模块停用**不**撤销声明） | 删除 |
| `WorkbenchDescriptor.Requires` + `IsEnabled` | `Requires` + `UiVisibility`（唯一实现） | 删除 |
| `WorkbenchViewRegistry`（第二个 ViewKey 空间） | 宿主通用视图表 `WebUiViewResolver` | 复用，只额外做契约校验 |
| `IWorkbenchProviderLoader`（运行时加载端口） | 无对应物 | **删除**：信封的 `IUiViewKeyCatalog` 也没解决 ALC / 静态资源 / CSS 隔离，留一个空壳端口只会误导 |
| `ChangeEvent` 通知 | `IUiContributionRegistry.Changed` | 复用 |

最终新增的契约只有 5 个类型文件，其中 3 个是纯数据。

## 二、分层与新增包

| 层 | 位置 | 内容 |
|----|------|------|
| 契约 | `src/abstractions/Seeing.Agent.Abstractions/Ui/Workbench/` | `WorkbenchContribution`（载荷）、`WorkbenchModels`（dock / 实例键 / 尺寸 / 报告 / 快照）、`WorkbenchPoints`（扩展点 id 与 ViewKey 约定）、`WorkbenchRequestKinds`（kind 命名）、`IWorkbenchClient`（插件调用自己 UI 的端口）、`IWorkbenchSessionStateStore`（UI → 服务端快照） |
| 契约（通用） | `src/abstractions/Seeing.Agent.Abstractions/Ui/IUiContributionQuery.cs` | 给**能力层**的「可见贡献」只读视图端口 |
| 脊柱 | `src/spine/Seeing.Agent.Core/Ui/UiContributionQuery.cs` | 上者的唯一实现（委托 `UiVisibility`，不复制判定） |
| 能力包 | `src/capabilities/Seeing.Agent.Workbench/`（模块 id `workbench`） | `WorkbenchSessionStateStore` / `WorkbenchClient` / `OpenWorkbenchTool` / `ListWorkbenchesTool` / `WorkbenchContextSource` |
| UI 类库 | `samples/Seeing.Agent.WebUI.Workbench/` | `WorkbenchDockState` / `WorkbenchSurface` / `WorkbenchDockHost` / `WorkbenchTabStrip` / `WorkbenchResizeHandle` / `WorkbenchUnavailableView` / `WorkbenchRequestHandlerRegistry` / `WorkbenchLayoutPersistence` / `WorkbenchHostBridge` / `WorkbenchViewContext` |
| 插件 | `samples/Seeing.Agent.WebUI.Files/Workbenches/` | `FilesWorkbench`（内嵌既有 `FilePanelHost`）、`MarkdownWorkbench`、`MarkdownRenderer` |
| 宿主接线 | `samples/Seeing.Agent.WebUI/` | `Components/WorkbenchDockSlot.razor`、`Components/WorkbenchPicker.razor`、`Services/WorkbenchProjection.cs`、`Shell/WebUiWorkbenchViews.cs` |

**依赖方向**：契约零 Web 依赖（`WorkbenchViewContext` 在 UI 类库而非契约，因为它携带 `HostResponse`）；能力包只依赖 Abstractions；`Seeing.Agent.Files` 只**声明** `ViewKey` 字符串，组件类型登记在宿主。

## 三、契约要点

### 3.1 `WorkbenchContribution`

```csharp
public sealed record WorkbenchContribution : UiContribution
{
    public required string Title { get; init; }
    public required string ViewKey { get; init; }
    public string? Icon { get; init; }                         // 中性 token，宿主翻译
    public WorkbenchDock DefaultDock { get; init; } = WorkbenchDock.Right;
    public WorkbenchSizing? Sizing { get; init; }
    public JsonElement? OpenPayloadSchema { get; init; }       // 只做浅校验 + 给 agent 的提示
    public IReadOnlyList<string> ProvidedTools { get; init; } = [];
    public bool ContributesContext { get; init; }              // 默认 false
}
```

`UiPayloadKind.Workbench` 是封闭集新增的第 4 个值；`UiPayloadKindMap` 同步登记，漏登记会被判 `PayloadMismatch` 并在启动日志里报出来。

### 3.2 扩展点

WebUI 声明 `workbench.dock`（`PayloadKind = Workbench`、`Scope = Session`），与既有 3 个插槽点同语义：随会话工作模式收窄。

### 3.3 归属与门控完全继承信封

`FileWorkspaceModule` 同时实现 `IUiContributor`，一次声明两个工作台（`files` / `markdown`），`Requires = ["files.workspace"]`。因此：

- 模块停用 → 工作台从目录消失（`UiVisibility.ModuleDisabled`）；
- 会话工作模式收窄 → 隐藏（`UiVisibility.ScenarioMismatch`）；
- **不需要**任何 `Unregister` 对称代码（与 `docs/architecture/40` §4.5 同一条纪律）。

### 3.4 `IUiContributionQuery`：能力层的可见视图

新端口解决一个真实的分层矛盾：`list_workbenches` 必须只列**当前可用**的工作台，而可见性判定只有 Core 的 `UiVisibility` 一份实现，能力包又不能引用 Core。

```csharp
public interface IUiContributionQuery
{
    IReadOnlyList<T> GetVisible<T>(string pointId, string? sessionScenario = null) where T : UiContribution;
}
```

实现只做「取候选集 → 逐条问 `UiVisibility.IsVisible` → 过滤」，**不复制任何规则**。任何把它重写成 `Catalog.IsEnabled(...)` 的改动都是在造第二份实现。

### 3.5 宿主请求 kind

| kind | 处理方 | 说明 |
|------|--------|------|
| `workbench.registry.open` | 会话级 `WorkbenchSurface` | payload `{ workbenchId, dock?, openPayload? }` |
| `workbench.registry.close` | 同上 | payload `{ workbenchId }` |
| `workbench.registry.list` | 同上 | 返回已打开实例 |
| `workbench.<id>.<action>` | 该工作台实例 | 插件自定义动作 |

`registry` 是保留 id；`WorkbenchRequestKinds.TryParse` 对保留域与不含 action 的 kind 一律失败。

## 四、宿主请求路由：从「单面板」到「按 kind 前缀」

迁移前 `HostRequestActivePanelRegistry` 按 `(sessionId, circuitId)` 只存**一个** handler —— 两个工作台并存时必然互相顶掉。

新的 `WorkbenchRequestHandlerRegistry`：

| 规则 | 行为 |
|------|------|
| **最长 kind 前缀胜出** | `workbench.files` 胜过 `workbench`；空串是兜底（最低优先级） |
| **scope 边界** | 前缀后必须紧跟 `.` 或结束（`workbench.file` 不匹配 `workbench.files.x`） |
| **circuit 隔离** | 不同标签页互不干扰 |
| **多实例消歧** | 同 scope 登记多个时按 `payload.dock` 选；无法消歧时返回第一个并标 `ambiguous = true` |

`ambiguous` 只在 registry 里**标记**，不做业务判定 —— 由 `WorkbenchClient` 归一化成 `WorkbenchCallStatus.Ambiguous` 并提示「请指定 dock」。

> 已知边界：旧 `HostRequestActivePanelRegistry` 与 `HostRequestDispatcher` 仍在仓库中（编辑器的 `editor.*` kind 走它）。本期只新增路由表，未删旧通道 —— 编辑器工具的对外行为零改动。下期把 `editor.*` 迁到工作台作用域后即可删除。

## 五、agent 侧

### 5.1 `list_workbenches()`

不依赖 UI：读 `IUiContributionQuery`（注册表 + 可见性）。无界面宿主返回「可用工作台：0」并说明原因，**不是错误**。

返回每个工作台的 `id` / 标题 / 默认 dock / 是否提供上下文 / 相关工具 / 打开参数 Schema。

### 5.2 `open_workbench(workbenchId, payload?, dock?)`

五道门闸，每道都自解释：

| # | 门闸 | 失败文案 |
|---|------|---------|
| ① | 扩展点未声明 | 「当前宿主未声明工作台扩展点（无可用界面）」 |
| ② | id 未知 | 「未知工作台 'x'。当前可用：`files`, `markdown`」 |
| ③ | 声明在表内但不可见 | 区分「依赖的模块未启用：files.workspace」与「被当前会话工作模式隐藏」 |
| ④ | 载荷浅校验 | 「缺少 'path'」/「'path' 类型应为 string」 |
| ⑤ | 无可呈现界面 | 「没有可呈现该会话的界面，请先在浏览器/桌面端打开该会话页面」 |

**浅校验的边界**：只查 `required` 键存在 + 基本类型；不做 JSON Schema 引擎，深度校验（取值范围、业务约束）归工作台自己。

### 5.3 `IWorkbenchClient`：插件驱动自己的 UI

```csharp
var result = await client.SendAsync(sessionId, "video", "seek",
    JsonSerializer.SerializeToElement(new { seconds = 30 }), dock: WorkbenchDock.Right, ct);
```

实现（`WorkbenchClient`）负责拼 kind、走 `IHostRequestManager`、归一化五种状态 + 歧义判定。插件包不必重复这段映射，也不必知道 `dock` 消歧规则。

## 六、会话页布局

```
.session-content-wrapper (display:flex)
├─ [左 dock] WorkbenchDockHost   (order:-2, flex-basis: var(--workbench-width))
├─ .session-main-column          (flex:1)
└─ [右 dock] WorkbenchDockHost   (order:1)
```

| 规则 | 实现 |
|------|------|
| 空 dock 零占位 | 无实例时组件渲染**什么都没**（不产生 DOM） |
| 宽度独立 | `--workbench-width` 百分比，clamp `[20%, 75%]`，拖动经 `WorkbenchResizeHandle` → JS → `DotNetObjectReference` 回传稳定值 |
| 只渲染激活项 | 非激活标签保留状态但不付渲染成本 |
| 实例级隔离 | 每个实例包 `<ErrorBoundary>`，失败显示 `WorkbenchUnavailableView`（含原因 + 关闭） |
| 窄屏 | `max-width: 1200px` 时 dock 改绝对定位覆盖（沿用旧文件面板策略） |
| 挂载点 | `SessionWindow.razor` 的 `<SlotHost SlotName="session_file_panel" />` → `<WorkbenchDockSlot />` |

## 七、状态与持久化

| 项 | 存放 | 生命周期 |
|----|------|----------|
| 开了哪些工作台 / 激活哪个 / dock 宽度 | `localStorage["seeing.workbench.<sessionId>"]`（`WorkbenchLayoutPersistence`，schema 版本 1） | 跨刷新 |
| 工作台内部可恢复状态 | `ViewState` 字典（≤2KB，工作台自行上报） | 跨刷新（随布局快照） |
| `OpenPayload` | 仅内存 | 一次性 |
| 服务端可见快照 | `IWorkbenchSessionStateStore`（UI 写、服务端读，250ms 去抖） | 进程内 |

**恢复语义**：刷新后恢复成员、激活项与宽度；每个实例用 `ViewState` 重建自己能重建的部分。快照里的工作台 id 若已不可用（模块停用）→ **静默丢弃该项**（不弹错、不阻塞）。

**为什么服务端不自己算**：工作台是 UI 侧概念，只有浏览器知道开合与查看内容。服务端只需要一个稳定事实 ——「用户现在打开着哪些、在看什么」。

## 八、上下文源

`WorkbenchContextSource`：`Scope = Session`、`Target = System`、`MaxBaselineChars = 2000`。

- 快照为空 → `ContextValueState.Removed`（「工作台已全部关闭」是**事实**，不是 `Unavailable`）；
- `Canonical` 按 `dock → id → ordinal` 稳定排序，重复读取逐字节相等；
- `Render` 只叙述变化：打开 / 关闭 / 激活变更 / 查看内容变更；无变化返回 `null`；
- 渲染带「不可信的用户数据」声明；
- `summary` 仅对 `ContributesContext = true` 的实例采集（默认 false，需工作台作者显式开启——摘要可能含用户文件内容）。

## 九、破坏性变更

| 变更 | 影响 | 迁移 |
|------|------|------|
| `SlotContribution("slot.file.panel")` 与 `session.file.panel` 扩展点删除 | 编译期 | 由 `workbench.dock` + `files` 工作台取代 |
| `SessionFilePanel.razor` 删除 | 编译期 | `WorkbenchSurface` + `FilesWorkbench` |
| `FilePanelUiContribution` 删除 | 编译期 | `FileWorkspaceModule.DescribeUi()` 自己声明 |
| `FilePanelState.IsOpen` / `SessionId` / `Side` / `Open` / `Close` / `Toggle` / `ToggleSide` / `SetSide` / `FilePanelSide` 删除 | 编译期 | 开合 / 停靠侧 / 宽度移交 `WorkbenchDockState`；`TreeVisible` / `PendingOpen` / `GetTabs` / `FileOpenRequest` 保留 |
| `FilePanelState.RequestOpen` 增 `hideTree` 参数（默认 true） | 源码兼容 | agent 驱动时收起目录树；用户点击时传 `false` |
| `WebUiPoints.SessionFilePanel` 删除 | 编译期 | `WebUiPoints.WorkbenchDock` |
| `SlotHost` 的槽名映射从 4 条变 3 条 | 行为 | `session_file_panel` 槽名不再被识别（渲染空容器） |
| 工具栏「工作目录徽标」不再可点击 | 行为 | 改为只读徽标 + 独立的「工作台」下拉 |
| `UiPayloadKind` 新增 `Workbench` | 源码兼容（`switch` 无 `default` 时会警告） | `UiPayloadKindMap` 已登记 |

## 十、已知边界

| 项 | 说明 |
|----|------|
| 旧 `HostRequestActivePanelRegistry` 仍在 | 编辑器 `editor.*` 通道未迁移（本期保证零行为变化）。两套路由表并存，`HostRequestDispatcher` 仍走旧表 |
| 每 dock 每工作台一个实例 | `WorkbenchInstanceKey.Ordinal` 恒为 0，字段保留以便后续多实例不改键类型 |
| 不做拖拽换边 / 重排 | 只有左右两个固定 dock |
| 同会话多标签页 | `IWorkbenchSessionStateStore` 后写覆盖（与权限 `TryAutoApprove` 的进程级语义同类） |
| 无运行时插件加载 | 删除 `IWorkbenchProviderLoader` 空壳：信封的 `IUiViewKeyCatalog` 也没解决 ALC / 静态资源 / CSS 隔离 |
| 工作台不得直接读写磁盘 | 落盘走自己的工具 + `IWorkspacePathGate`；工作台不持有 `ISessionRuntime` |
| `WorkbenchPicker` 图标是字符 emoji | 与 `NavPlacement.Icon` 的中性 token + 宿主翻译机制尚未统一（工作台图标只在两处用到，暂用内联映射） |

### 10.1 两个会「静默失败」的 Razor 陷阱（已修复，已守门）

实现期间踩到两个**编译器不报错、运行时也不报错**的坑，症状统一表现为「状态显示已打开，但界面什么都不出现」：

| # | 陷阱 | 症状 | 守门 |
|---|------|------|------|
| 1 | `SessionId="SessionId"` —— `string` 参数写裸标识符会被当成**字面量** `"SessionId"`。`int` / 枚举 / 集合等类型走表达式，只有 `string` 中招。`WorkbenchSurface` / `WorkbenchDockHost` 拿到的会话 id 是字面量 → 查布局恒为空 → 永不渲染 | 点「文件」后选择器显示「已打开」（它用的是 `@(...)`），但 dock 不出现、localStorage 不写入 | `WorkbenchRazorWiringTests.StringParameters_ShouldNeverBeBareIdentifiers` |
| 2 | `<WorkbenchSurface>` 缺少 `@using Seeing.Agent.WebUI.Workbench.Components` —— Razor 找不到组件类型时**不报错**，而是降级成普通 HTML 元素：参数全部变成小写 attribute（`workbenches="_visible"`）、组件逻辑与子内容一律不执行 | 同上，且 DOM 里能看到 `<workbenchsurface sessionid=... workbenches="_visible"></workbenchsurface>` 这种空壳 | `WorkbenchRazorWiringTests.{ComponentFile_ShouldExistForEveryUsedTag, WorkbenchComponents_ShouldImportTheirNamespace}` |

排查方法（写下来因为这两个坑只能这样定位）：`document.querySelector('<tag>').outerHTML` 若出现**小写 attribute 名**，就说明该标签没被解析成组件。

## 十一、测试

| 套件 | 数量 | 钉住什么 |
|------|------|---------|
| `tests/capabilities/Seeing.Agent.Workbench.Tests` | **63** | kind 往返与保留域、实例键解析；快照存储的等值静默 / 清空 / 事件隔离；上下文源的空→Removed、canonical 稳定性排序、四种增量叙述、截断；`list_workbenches` 三项；`open_workbench` 五道门闸各一组 |
| `tests/apps/Seeing.Agent.WebUI.Workbench.Tests` | **47** | 停靠状态（会话分桶 / 不开重复 / 双 dock 并存 / 宽度夹取 / ViewState 保留 / Prune / 恢复 / 订阅者异常隔离）；路由表（最长前缀 / scope 边界 / circuit 隔离 / dock 消歧 / ambiguous）；上报桥（去抖用 `FakeTimeProvider`、`ContributesContext` 过滤、截断、释放后停写） |
| `tests/apps/Seeing.Agent.WebUI.Tests`（改） | 434 | 原 `SettingsAndSlotMigrationTests` 改为：3 个插槽点 + `workbench.dock` 声明、两个工作台的归属与 Schema、视图键可解析、模块门控与会门；**新增 `WorkbenchRazorWiringTests` 4 条**：组件标签必须有同名 `.razor`、消费方必须 using 组件命名空间、`SessionId`/`CircuitId`/`SessionScenario` 不得写裸标识符 |
| `tests/apps/Seeing.Agent.WebUI.Files.Tests`（改） | 42 | `FilePanelStateTests` 削去开合/侧别用例，只留目录树 / 待处理请求 / 会话隔离 |

**全量**：53 个测试程序集，**0 失败**。

## 十二、手工验收

- `dotnet run --project samples/Seeing.Agent.WebUI` 启动无 UI 贡献诊断，`Activating module workbench` 出现在日志；
- 首页 200；会话页工具栏出现「工作台」下拉，列出「文件」「Markdown 预览」；
- 左右 dock 可各开一个，宽度可拖，刷新后成员与宽度恢复；
- 停用 `files.workspace` 模块 → 两个工作台从选择器消失，已开的实例被 `Prune` 清除；
- agent 调 `list_workbenches` 能看到两个工作台与打开参数；`open_workbench` 传错误 payload 得到「缺少 'path'」。

## 十三、后续演进

| 项 | 说明 |
|----|------|
| 迁 `editor.*` 到工作台作用域 | 之后可删除 `HostRequestActivePanelRegistry`，宿主请求只剩一条路由表 |
| 多实例（`Ordinal > 0`） | 键类型已预留；UI 需要标签条去重策略 |
| 更多工作台 | 视频播放、网页操作、Terminal —— 只需实现一个带 `[Parameter] WorkbenchViewContext Context` 的组件 + 在模块 `DescribeUi()` 加一条声明 + 宿主 `WebUiWorkbenchViews` 加一行 |
| `WorkbenchContribution.Icon` 与 `NavPlacement.Icon` 统一 | 让工作台图标也走 `WebUiIconMap` 翻译 |
