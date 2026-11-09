# 会话工作台插件平台设计（Workbench Plugin Platform）

**日期:** 2026-11-09
**状态:** 待评审
**范围:** `Seeing.Agent.Abstractions`（`Ui/Workbench` 契约）、新增能力包 `Seeing.Agent.Workbench`、新增 Razor 类库 `Seeing.Agent.WebUI.Workbench`、`Seeing.Agent.Files`（文件工作台描述符）、`Seeing.Agent.WebUI.Files`（files 工作台组件）、`Seeing.Agent.WebUI`（会话页布局 + 宿主桥接 + 宿主请求路由改造）、`docs/architecture`
**触发:** 会话页右侧只有一个「文件面板」插槽，形态被文件语义写死；视频播放、网页操作、Markdown 预览等新形态无处安放，也没有统一的工作台目录与 agent 驱动入口
**约束:** 本设计文档不自动提交 git；程序集名与磁盘分类沿用 `docs/architecture/02-modules.md`

---

## 1. 背景与问题

| 现状 | 事实 | 位置 |
|------|------|------|
| 单插槽写死 | 会话页内容区直接放 `<SlotHost SlotName="session_file_panel" />`，插槽贡献只有一个 | `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:229` |
| 只能渲染固定组件 | `SlotContribution.ComponentType` 是 Blazor `Type`，契约层因此**永久绑定编译期组件** | `src/abstractions/Seeing.Agent.Abstractions/Ui/UiContributionTypes.cs:56-62` |
| 单面板 | `FilePanelState` 用单个 `IsOpen` + `SessionId` + `Side` 表达「同一时刻只有一块面板」，且状态是 circuit 级共享 | `samples/Seeing.Agent.WebUI.Files/Services/FilePanelState.cs` |
| 入口写死 | 开关入口是会话工具栏的「工作目录徽标」，语义与文件绑定 | `SessionToolbar.razor:36-46,300-305` |
| RPC 语义写死 | `HostRequestActivePanelRegistry` 按 `(sessionId, circuitId)` 只登记**一个** handler；kind 只有 `editor.command` / `editor.query` / `editor.list_commands` | `samples/Seeing.Agent.WebUI/Services/HostRequestActivePanelRegistry.cs`、`src/capabilities/Seeing.Agent.Tools.Editor/EditorHostRequests.cs` |
| 无目录 | 没有任何「有哪些可打开的面板」的查询面，agent 无法先列再开 | — |
| 无插件加载端口 | `PluginSpec` 只有数据模型，`plugs/providers/` 是编译期项目 | `src/abstractions/Seeing.Agent.Abstractions/Configuration/PluginSpec.cs` |

**问题：** ① 面板形态不可扩展；② 不能同时并排两种视图；③ agent 只能驱动编辑器，无法驱动其他面板；④ 契约层的 `Type` 让运行时加载永远不可能；⑤ 上下文里看不到「用户现在在看什么」。

**目标：**

1. 会话页有**左右两个独立 dock**，每个 dock 可承载一个工作台实例。
2. 工作台由**插件**提供，插件描述符（纯数据）与实现（UI/工具/上下文）分离。
3. 工作台目录可被用户（统一下拉菜单）与 agent（`list_workbenches`）共同消费。
4. agent 能以通用工具打开工作台并下发载荷；工作台自己解释载荷。
5. 工作台状态可作为上下文源参与既有基线/增量管道。
6. 本期只实现编译期插件，但契约上预留运行时加载端口。

**非目标：**

- 不做运行时 `.dll` 装配与静态资源托管（只留端口与空实现）。
- 不做工作台向会话主动发消息（仍走 `ISessionRuntime.DeliverAsync` 的既有领域）。
- 不做工作台内部状态的服务端持久化（只做 `localStorage` 的 UI 形态恢复）。
- 不改造既有 7 个文件工具，不改 `editor.*` 工具的对外行为。
- 不做任意多 dock（第三栏）、不做拖拽换边、不做工作台拖拽重排。

---

## 2. 命名决策

`Workspace` 在仓库内已被「工作区 / 项目根」占用（`IWorkspaceProvider`、`WorkspacePathGate`、`WorkspaceLaunch`、`WorkspacesPage`、`ISessionWorkspaceResolver`、`files.workspace` 模块 id）。再引入同名的「工作台」会造成全局歧义。

**框架名一律用 `Workbench`**：

| 概念 | 标识 |
|------|------|
| 插件的完整描述 | `WorkbenchDescriptor` |
| 插件契约 | `IWorkbenchPlugin` |
| 目录 / 注册表 | `IWorkbenchRegistry` |
| 停靠位 | `WorkbenchDock`（`Left` / `Right`） |
| dock 内实例 | `WorkbenchInstanceKey` |
| UI 侧组件映射 | `ViewKey` → `Type`（`WorkbenchViewRegistry`） |

UI 文案仍可显示「工作台」。

---

## 3. 分层与包结构

```
src/abstractions/Seeing.Agent.Abstractions/
  Ui/Workbench/
    IWorkbenchPlugin.cs              ← 插件契约（仅描述符）
    IWorkbenchRegistry.cs            ← 注册表 + 查询
    IWorkbenchProviderLoader.cs      ← 运行时加载端口（本期仅内置实现返回空）
    IWorkbenchSessionStateStore.cs   ← UI 推送的工作台快照端口（服务端只读）
    IWorkbenchClient.cs              ← 插件调用自己 UI 实例的端口
    WorkbenchModels.cs               ← 描述符 / dock / 尺寸 / 实例键 / 报告 / 调用结果
    WorkbenchRequestKinds.cs         ← kind 命名单一来源

src/capabilities/Seeing.Agent.Workbench/     ← 新能力包（模块 id: workbench）
  WorkbenchModule.cs
  WorkbenchRegistry.cs
  BuiltinWorkbenchLoader.cs
  WorkbenchSessionStateStore.cs
  WorkbenchClient.cs               ← IWorkbenchClient 唯一实现
  Sources/WorkbenchContextSource.cs
  Tools/OpenWorkbenchTool.cs
  Tools/ListWorkbenchesTool.cs
  WorkbenchRequestPayloads.cs      ← registry.* 载荷的构造 helper

samples/Seeing.Agent.WebUI.Workbench/        ← 新 Razor 类库（通用，任何 Blazor 宿主可用）
  Components/
    WorkbenchDockHost.razor
    WorkbenchTabStrip.razor
    WorkbenchPicker.razor
    WorkbenchUnavailableView.razor
  Services/
    WorkbenchDockState.cs           ← Scoped：按 (session, dock) 实例集合/激活/宽度
    WorkbenchViewRegistry.cs        ← ViewKey → Type + 反射校验
    WorkbenchViewContext.cs         ← 传给工作台组件的唯一参数对象
    WorkbenchLayoutPersistence.cs   ← localStorage 读写（JS interop 隔离在此）
    WorkbenchHostBridge.cs          ← IWorkbenchSessionStateStore 的 UI 侧推送方（去抖）
  wwwroot/workbench.css
  wwwroot/workbench.js

samples/Seeing.Agent.WebUI.Files/
  Workbenches/FilesWorkbench.razor  ← 内嵌现有 FilePanelHost，实现工作台视图

samples/Seeing.Agent.WebUI/
  Components/WorkbenchDockSlot.razor   ← 会话页挂载点（左右两 dock + registry handler）
  Services/WorkbenchViews.cs           ← 内置 ViewKey 注册（编译期插件）
  Services/AgentWorkbenchBridge.cs     ← open_workbench 的 UI 侧接线（替代 open_file 直连）
```

**依赖方向：**

| 项目 | 依赖 |
|------|------|
| Abstractions（`Ui/Workbench`） | 原语层 + BCL（`System.Text.Json`）；**零 Web 依赖** |
| `Seeing.Agent.Workbench` | Abstractions |
| `Seeing.Agent.WebUI.Workbench` | Abstractions + AntDesign |
| `Seeing.Agent.WebUI.Files` | Abstractions + `WebUI.Workbench` + AntDesign + BlazorMonaco + Markdig |
| `Seeing.Agent.WebUI` | 以上全部 |

**关键决策：契约层不出现 `Type`。**

`SlotContribution.ComponentType` 是 `Type`，这让 `IUiContribution` 天然绑定 Blazor。工作台契约改放 **`ViewKey`（`string`）**：

- 契约层完全不认识 `Microsoft.AspNetCore.Components`，Tui / Headless 宿主加载本契约不产生任何渲染依赖。
- 编译期插件在 WebUI 侧用 `WorkbenchViewRegistry.Register(viewKey, typeof(FilesWorkbench))` 建立映射。
- 运行时加载（后续期）只需提供「ViewKey → 组件装配」的实现，不影响契约。

---

## 4. 契约层

### 4.1 模型

```csharp
namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>停靠位。左右各一个独立 dock，同一插件可在两侧各有一个实例。</summary>
public enum WorkbenchDock { Left, Right }

/// <summary>dock 内的实例键。本期 Ordinal 恒为 0（每 dock 每插件一个），保留字段以便后续多实例不改键类型。</summary>
public readonly record struct WorkbenchInstanceKey(WorkbenchDock Dock, string WorkbenchId, int Ordinal = 0)
{
    public override string ToString() => $"{Dock}:{WorkbenchId}#{Ordinal}";
}

/// <summary>尺寸偏好（百分比相对会话内容区；px 为硬下限）。</summary>
public sealed record WorkbenchSizing(
    double DefaultPercent = 50,
    double MinPercent = 20,
    double MaxPercent = 80,
    double MinPx = 280);

/// <summary>工作台描述符（服务端可见的纯数据；UI 渲染经 ViewKey 解析）。</summary>
public sealed record WorkbenchDescriptor(
    string Id,
    string Title,
    string Icon,
    string ViewKey,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string>? Scenarios = null,
    int Order = 0,
    WorkbenchDock DefaultDock = WorkbenchDock.Right,
    WorkbenchSizing? Sizing = null,
    JsonElement? OpenPayloadSchema = null,
    IReadOnlyList<string>? ProvidedTools = null,
    bool ContributesContext = false);

/// <summary>工作台向宿主的报告（可恢复小状态 + 上下文摘要）。</summary>
public sealed record WorkbenchViewReport
{
    /// <summary>可恢复小状态（≤2KB，序列化后）；null 表示不覆盖已存值。</summary>
    public IReadOnlyDictionary<string, string>? ViewState { get; init; }

    /// <summary>当前查看内容的单行摘要（仅 ContributesContext 的工作台需要，≤200 字）。</summary>
    public string? ContextSummary { get; init; }
}
```

### 4.2 插件契约

```csharp
/// <summary>
/// 工作台插件：只声明描述符。
/// UI 渲染经 ViewKey；agent 能力由插件所属模块自行注册工具；上下文能力由 ContributesContext 声明。
/// </summary>
public interface IWorkbenchPlugin
{
    WorkbenchDescriptor Descriptor { get; }
}
```

**为什么插件接口只有描述符：** 插件「做事」的三个方向已各有其道 —— UI 侧是 `ViewKey` 对应的组件，agent 侧是插件自己注册的工具，上下文侧是 `ContributesContext` + UI 上报的快照。把三者塞进插件接口会让契约层反向依赖 Web/工具/上下文管道。

### 4.3 注册表与加载端口

```csharp
public interface IWorkbenchRegistry
{
    /// <summary>登记插件。ownerModuleId 为归属模块 id（用于 Deactivate 对称卸载）。</summary>
    void Register(string ownerModuleId, IWorkbenchPlugin plugin);

    /// <summary>按工作台 id 卸载。</summary>
    void Unregister(string workbenchId);

    /// <summary>卸载某模块登记的全部工作台（模块 Deactivate 调用）。</summary>
    void UnregisterByOwner(string ownerModuleId);

    IReadOnlyList<WorkbenchDescriptor> All { get; }          // 按 (Order, Id) 稳定排序
    WorkbenchDescriptor? Find(string workbenchId);
    bool IsEnabled(string workbenchId);                     // Requires ⊆ IModuleCatalog.IsEnabled
    event EventHandler? Changed;
}

/// <summary>运行时加载端口。本期只有 BuiltinWorkbenchLoader（返回空），为后续 dll 装配预留。</summary>
public interface IWorkbenchProviderLoader
{
    string Key { get; }
    Task<IReadOnlyList<IWorkbenchPlugin>> LoadAsync(CancellationToken ct = default);
}

/// <summary>UI 推送的工作台快照（服务端只读）。快照必须是已确认的稳定值，不得实时读易变状态。</summary>
public interface IWorkbenchSessionStateStore
{
    void Update(string sessionId, IReadOnlyList<WorkbenchSessionSnapshot> snapshots);
    void Clear(string sessionId);
    IReadOnlyList<WorkbenchSessionSnapshot> GetSnapshot(string sessionId);
    event Action<string>? Changed;
}

public sealed record WorkbenchSessionSnapshot(
    WorkbenchInstanceKey Key,
    string Title,
    bool Active,
    string? ContextSummary);
```

### 4.4 宿主请求 kind 命名

```csharp
public static class WorkbenchRequestKinds
{
    public const string Root = "workbench";
    public const string RegistryScope = "workbench.registry";
    public const string RegistryOpen = "workbench.registry.open";
    public const string RegistryList = "workbench.registry.list";
    public const string RegistryContext = "workbench.registry.context";

    public static string For(string workbenchId, string action) => $"{Root}.{workbenchId}.{action}";
    public static bool TryParse(string kind, out string workbenchId, out string action);
}
```

- 插件自有 action 一律走 `workbench.<id>.<action>`（如 `workbench.files.command`）。
- `editor.command` / `editor.query` / `editor.list_commands` **保持不变**，由 files 工作台的兼容作用域 `editor` 承接，既有 `editor_*` 工具零改动。

### 4.5 插件调用宿主的客户端端口

插件（能力包）需要向自己的 UI 实例发请求。为避免能力包之间互相引用，**端口放契约层，实现放 `Seeing.Agent.Workbench`**：

```csharp
public interface IWorkbenchClient
{
    /// <summary>向指定工作台实例发 workbench.&lt;id&gt;.&lt;action&gt; 请求。</summary>
    Task<WorkbenchCallResult> SendAsync(
        string sessionId,
        string workbenchId,
        string action,
        JsonElement? payload = null,
        CancellationToken ct = default);
}

/// <summary>宿主调用结果（已从 HostResponse 归一化，工具无需再判断五种状态）。</summary>
public sealed record WorkbenchCallResult(
    WorkbenchCallStatus Status,
    string Message,
    JsonElement? Payload = null,
    string? RequestId = null);

public enum WorkbenchCallStatus { Completed, Unavailable, Timeout, Cancelled, Failed, Ambiguous }
```

`IWorkbenchClient` 的唯一实现在 `Seeing.Agent.Workbench`（`WorkbenchClient`）。插件包（如 `Seeing.Agent.Files`）只 `GetService<IWorkbenchClient>()`，不引用实现包。

---

## 5. 能力包 `Seeing.Agent.Workbench`

模块 id **`workbench`**，只依赖 Abstractions。

| 成员 | 生命周期 | 职责 |
|------|----------|------|
| `WorkbenchRegistry` | Singleton | `IWorkbenchRegistry` 唯一实现；`Requires` 用 `IModuleCatalog` 判定 |
| `WorkbenchModule` | `ISeeingModule` | `ConfigureServices` 注册 registry + store + 工具；`ActivateAsync` 跑所有 `IWorkbenchProviderLoader` 并注册；`DeactivateAsync` 卸载其插件并清空 |
| `BuiltinWorkbenchLoader` | Singleton | 返回空列表（内置插件由各自模块注册，如 `files.workspace` 注册 `files`） |
| `WorkbenchSessionStateStore` | Singleton | 快照存储（按会话），稳定快照语义 |
| `WorkbenchContextSource` | Scoped（`TryAddEnumerable`） | `IContextSource` |
| `OpenWorkbenchTool` | Singleton | 工具 id `open_workbench` |
| `ListWorkbenchesTool` | Singleton | 工具 id `list_workbenches` |
| `WorkbenchClient` | Singleton | `IWorkbenchClient` 唯一实现：发 `workbench.<id>.<action>` 请求并把 `HostResponse` 归一化为 `WorkbenchCallResult` |

**描述符由插件所属模块在 Activate 注册**，与「工具在 Activate 挂载」同一条纪律：

```csharp
// src/capabilities/Seeing.Agent.Files/FileWorkspaceModule.cs
public async Task ActivateAsync(IServiceProvider sp, CancellationToken ct)
{
    // …既有工具挂载…
    sp.GetService<IWorkbenchRegistry>()?.Register(Id, new FilesWorkbenchPlugin());
}

public async Task DeactivateAsync(IServiceProvider sp, CancellationToken ct)
{
    // …既有工具卸载…
    sp.GetService<IWorkbenchRegistry>()?.UnregisterByOwner(Id);
}

public sealed class FilesWorkbenchPlugin : IWorkbenchPlugin
{
    public WorkbenchDescriptor Descriptor { get; } = new(
        Id: "files",
        Title: "文件",
        Icon: "folder",
        ViewKey: "files",
        Requires: ["files.workspace"],
        Order: 10,
        DefaultDock: WorkbenchDock.Right,
        Sizing: new WorkbenchSizing(DefaultPercent: 50),
        OpenPayloadSchema: /* { path?: string, line?: integer, column?: integer } */,
        ProvidedTools: ["open_file"]);
}
```

`Requires` 里写 `files.workspace` 是关键：`files` 工作台的可用性完全继承该模块的启用状态，模块停用即工作台从目录消失。

---

## 6. 会话页布局

```
.session-content-wrapper  (display:flex, position:relative)
├─ [左 dock]  WorkbenchDockSlot → WorkbenchDockHost   (order:-2, width: var(--workbench-left-width, 50%))
├─ .session-main-column                                (flex:1，消息 + 输入)
└─ [右 dock]  WorkbenchDockSlot → WorkbenchDockHost   (order:1)
```

| 规则 | 说明 |
|------|------|
| 空 dock 不渲染 DOM | 无实例时零占位（`display: contents` 的外壳 + 无内容） |
| 宽度独立 | `--workbench-left-width` / `--workbench-right-width`，百分比 clamp `[Sizing.MinPercent, Sizing.MaxPercent]`，`min-width: Sizing.MinPx` |
| 分隔线 | 仅在有实例时渲染一个 `ResizeHandle`（左侧 dock 的分隔线置右缘，右侧置左缘） |
| 窄屏 | `max-width: 1200px` 时两 dock 改为绝对定位覆盖（沿用现有 `.session-file-panel-dock` 策略），各自贴边 |
| 隔离 | 每个实例外包 `<ErrorBoundary>`，失败显示 `WorkbenchUnavailableView`（重新加载 / 关闭） |
| 挂载点 | `SessionWindow.razor` 的 `<SlotHost SlotName="session_file_panel" />` 删除，改为 `<WorkbenchDockSlot />` |

`session_file_panel` / `session_toolbar_actions` 作为**通用插槽**保留，只是文件面板不再走它们。

---

## 7. 状态与持久化

```csharp
public sealed class WorkbenchDockState          // Scoped，circuit 维度，纯 C#
{
    public event Action? Changed;

    public WorkbenchSessionLayout GetLayout(string sessionId);
    public void Open(string sessionId, WorkbenchDock dock, string workbenchId, JsonElement? payload);
    public void Activate(string sessionId, WorkbenchDock dock, WorkbenchInstanceKey key);
    public void Close(string sessionId, WorkbenchDock dock, WorkbenchInstanceKey key);
    public void SetWidth(string sessionId, WorkbenchDock dock, double percent);
    public void ApplyViewReport(string sessionId, WorkbenchInstanceKey key, WorkbenchViewReport report);
    public void Clear(string sessionId);
}

public sealed record WorkbenchSessionLayout(
    IReadOnlyList<WorkbenchInstance> Left,
    IReadOnlyList<WorkbenchInstance> Right,
    string? ActiveLeft,
    string? ActiveRight,
    double LeftWidthPercent,
    double RightWidthPercent);

public sealed record WorkbenchInstance(
    WorkbenchInstanceKey Key,
    JsonElement? OpenPayload,
    IReadOnlyDictionary<string, string>? ViewState);
```

| 项 | 存放 | 理由 |
|----|------|------|
| 开了哪些工作台 / 激活哪个 / dock 宽度 | `localStorage["seeing.workbench.<sessionId>"]`（JSON） | 纯 UI 形态，不进 `SessionData`；刷新可恢复 |
| 工作台内部内容 | 各工作台经 `ViewState` 上报（≤2KB 字典） | 框架不猜内部结构 |
| `OpenPayload` | 仅内存 | 一次性打开指令 |

**恢复语义：** 刷新后恢复 dock 成员、激活项与宽度；每个实例用 `ViewState` 重建自己能重建的部分，重建失败就地降级为空态（不弹错、不阻塞渲染）。会话被删除时 `Clear(sessionId)` 同时清 localStorage。

**会话隔离：** 布局按 `sessionId` 分桶，切换 active 会话不串场（与现有 `FilePanelState.GetTabs` 同思路）。

### 7.1 视图上下文

```csharp
public sealed class WorkbenchViewContext
{
    public required string SessionId { get; init; }
    public required WorkbenchInstanceKey Key { get; init; }
    public required WorkbenchDescriptor Descriptor { get; init; }
    public JsonElement? OpenPayload { get; init; }
    public IReadOnlyDictionary<string, string>? ViewState { get; init; }

    /// <summary>上报可恢复状态与上下文摘要（由 dock 转发到 WorkbenchDockState + IWorkbenchSessionStateStore）。</summary>
    public required Action<WorkbenchViewReport> Report { get; init; }

    /// <summary>请求关闭自己。</summary>
    public required Action Close { get; init; }

    /// <summary>向宿主发送 workbench.&lt;id&gt;.&lt;action&gt; 请求（返回契约层 <see cref="HostResponse"/>，已归一化）。</summary>
    public required Func<string, JsonElement?, CancellationToken, Task<HostResponse>> CallHostAsync { get; init; }
}
```

工作台组件只需声明 `[Parameter] public WorkbenchViewContext Context { get; set; }`。`CallHostAsync` 内部走 `IHostRequestManager` 并自动带 `WorkbenchRequestKinds.For(Descriptor.Id, action)`，工作台不拼 kind。

---

## 8. 宿主请求路由改造

现状 `HostRequestActivePanelRegistry` 按 `(sessionId, circuitId)` **单个** handler，多 dock 下必然冲突。

### 8.1 新契约

```csharp
public sealed class HostRequestHandlerRegistry          // 由 HostRequestActivePanelRegistry 更名
{
    /// <summary>登记 handler。kindScope 为 kind 前缀；"" = 兜底（最低优先级）。</summary>
    void Register(string sessionId, string circuitId, string kindScope,
                  Func<HostRequest, CancellationToken, Task<HostResponse>> handler);
    void Unregister(string sessionId, string circuitId, string kindScope);

    /// <summary>
    /// 按 kind 最长前缀匹配。命中多个同 scope 登记时，按 <c>req.Payload.dock</c> 选；
    /// 无法消歧时返回第一个并为 handler 保留完整请求（由 handler 决定是否报 ambiguous）。
    /// </summary>
    bool TryResolve(string sessionId, string circuitId, HostRequest req,
                    out Func<HostRequest, CancellationToken, Task<HostResponse>> handler);

    bool HasAny(string sessionId);
}
```

- `kindScope` 为 kind 前缀；`""` 是兜底（最低优先级，兼容旧登记）。
- 最长前缀胜出；同前缀重复登记按后写覆盖（与既有 registry 行为一致）。

### 8.2 登记方与作用域

| 登记方 | kindScope | 处理 |
|--------|-----------|------|
| `WorkbenchDockSlot`（每会话每 circuit 一个） | `workbench.registry` | `open` / `list` / `context` |
| 每个工作台实例 | `workbench.<id>` | 该插件自己的 action |
| files 工作台（兼容层） | `editor` | `editor.command` / `editor.query` / `editor.list_commands` 转给内层 `FilePanelHost` |

**同 scope 多实例的消歧**：`workbench.<id>` 作用域在两个 dock 都开着同一插件时会被登记两次。registry 因此保留**每个 (session, circuit, scope) 的登记列表**，`TryResolve` 在命中多实例时按 `req.Payload?.dock` 选择；未指定 dock 时回调收到原始请求，由 handler 自己返回 `Failed("ambiguous: 请指定 dock")`（不在 registry 里做业务判定）。

### 8.3 派发器改动

`HostRequestDispatcher` 只改「找 handler」这一步：`TryGetLocal(sessionId, circuitId)` → `TryResolve(sessionId, circuitId, req)`。其余不变：

- 他 circuit 持有 → 跳过（不抢）。
- 无人持有 → `Unavailable` 兜底。
- `TryResolve` 幂等。

### 8.4 同插件左右各一份

kind 相同，靠 `payload.dock` 消歧：

| 情况 | 行为 |
|------|------|
| payload 有 `dock` | 路由到该 dock 的实例 |
| payload 无 `dock`，只有一个实例 | 给唯一实例 |
| payload 无 `dock`，多个实例 | `Failed`，`Reason = "ambiguous: 请指定 dock"` |

该规则写进 `WorkbenchRequestKinds` 注释与 `open_workbench` 工具描述。

---

## 9. Agent 侧

### 9.1 `list_workbenches()`

- **不依赖 UI**：直接读 `IWorkbenchRegistry.All` + `IsEnabled` 过滤。
- 返回：`id / title / 默认 dock / 是否提供上下文 / 相关工具 / 打开载荷提示`。
- 供 agent「先列再开」；无 UI 时也可用（用于解释能力）。

### 9.2 `open_workbench(workbenchId, payload?, dock?)`

| 步骤 | 行为 |
|------|------|
| 1 | `IWorkbenchRegistry.Find(workbenchId)`；不存在 → `Failed`，并把当前可用 id 列出（自解释） |
| 2 | `IsEnabled == false` → `Failed("工作台依赖的模块未启用")`，附 `Requires` |
| 3 | payload 按 `OpenPayloadSchema` 做 **浅校验**（required 键存在 + 基本类型）；不做完整 JSON Schema 引擎，深度校验归工作台 |
| 4 | `IHostSurfaceRegistry.CanSurface(sessionId) == false` → `Failed("无可用界面，请先打开会话页面")`，**不静默成功** |
| 5 | 发 `WorkbenchRequestKinds.RegistryOpen`，payload 含 `workbenchId` / `dock` / `openPayload` |
| 6 | 成功回 `Metadata`：`workbench_id` / `dock` / `instance_key` |

### 9.3 插件自有工具

插件需要 agent 能力时，在**自己的能力包里**注册工具，注入契约端口 `IWorkbenchClient`：

```csharp
// 示例（files 之外的插件，说明用法）
var result = await _client.SendAsync(
    context.SessionId, workbenchId: "video", action: "seek",
    payload: JsonSerializer.SerializeToElement(new { seconds = 30 }),
    ct: context.CancellationToken);

return result.Status switch
{
    WorkbenchCallStatus.Completed => Success(result.Message, result.Payload),
    WorkbenchCallStatus.Unavailable => Failure("界面未打开该工作台"),
    WorkbenchCallStatus.Ambiguous => Failure(result.Message),
    _ => Failure(result.Message)
};
```

`IWorkbenchClient` 负责：拼 kind、`IHostRequestManager.BeginAsync/WaitAsync`、把 `HostResponse` 五种状态 + 歧义判定归一化为 `WorkbenchCallResult`。与现有 `EditorCommandTool.FormatResult` 语义一致，但插件不必重复这段映射。

### 9.4 边界（明确不做）

- 工作台不直接读写磁盘：落盘一律走自己的工具 + `IWorkspacePathGate`。
- 不读浏览器 / DOM 状态，只读 UI 推上来的稳定快照。
- 工作台不得向会话主动注入消息（`ISessionRuntime.DeliverAsync` 仍是唯一入站入口，工作台不持有它）。

---

## 10. 上下文源

`WorkbenchContextSource : IContextSource`

| 属性 | 值 |
|------|-----|
| `Key` | `typeof(WorkbenchContextSource).FullName!` |
| `Scope` | `ContextSourceScope.Session` |
| `Target` | `ContextSourceTarget.System` |
| `ValueSchemaVersion` | `1` |
| `MaxBaselineChars` | `2000` |

**读值：**

- 从 `IWorkbenchSessionStateStore.GetSnapshot(sessionId)` 取快照（`sessionId` 来自 `IContextExecutionScope`）。
- 快照为空（未开工作台 / 无 UI / 已清空）→ `ContextValueState.Removed`（叙述「工作台已全部关闭」），**不是** `Unavailable`——没有 UI 时这是事实，不是暂时不可读。
- 有内容 → `ContextValueState.Value`。

**Canonical**（按 `dock`、`id` ordinal 排序）：

```json
{"workbenches":[{"dock":"Right","id":"files","title":"文件","active":true,"summary":"editing docs/a.md"}]}
```

`summary` 仅对 `ContributesContext = true` 的实例采集（`ContextSummary` 已由 UI 截到 200 字）。

**渲染：**

- `RenderInitial`：逐条一行 `- [右] 文件（当前查看：…）`，含不可信内容声明（摘要来自工作台，可能含用户文件内容）。
- `Render`：只叙述变化 —— 新增（「打开了 X」）、移除（「关闭了 X」）、激活变更（「切到 X」）、摘要变更（「X 的查看内容变为 …」）。无变化返回 `null`。

**不做：** 源内不做去抖或节流（UI 侧已去抖 250ms）；源不读磁盘、不触发任何请求。

---

## 11. 错误处理

| 场景 | 行为 |
|------|------|
| `ViewKey` 不可解析 | **注册时**反射校验失败 → 启动即抛（开发期快速失败）；运行期 registry 缺失 → picker 隐藏该项 + `Warning` 日志 |
| 工作台组件抛异常 | 实例级 `ErrorBoundary` 占位 + 「重新加载 / 关闭」 |
| 模块 Deactivate 后有实例开着 | `IWorkbenchRegistry.Changed` → dock 关闭对应实例 + 提示「该工作台已随模块停用」 |
| HostRequest 无 handler | 既有 `Unavailable` 兜底；工具侧文案「界面未打开该工作台」 |
| 同插件左右各有、未指定 dock | `Failed("ambiguous: 请指定 dock")` |
| `OpenPayloadSchema` 浅校验失败 | `Failed`，附缺失/类型错误的键名 |
| localStorage 数据损坏 | 解析失败 → 丢弃该会话布局，回到默认（记 `Warning`，不阻塞） |
| `ViewState` 超 2KB | 截断并记 `Warning`（工作台作者错误，不阻断） |

---

## 12. 测试策略

沿用仓库纪律：**不引入 bUnit / Playwright**，渲染层保持极薄，逻辑下沉为纯 C# 服务。

| 套件 | 覆盖 |
|------|------|
| `tests/capabilities/Seeing.Agent.Workbench.Tests`（新） | registry 注册/按 id 卸载/按 owner 卸载/排序/模块门控；`WorkbenchRequestKinds` 往返 + 非法域；`WorkbenchSessionStateStore` 快照替换与清空；`WorkbenchContextSource` 初始渲染字节稳定、无变化 `Render == null`、全关 → `Removed`、摘要变化叙述；`OpenWorkbenchTool` 六步门闸（未知 id / 模块未启用 / 载荷浅校验 / 无界面 / 成功 Metadata）；`WorkbenchClient` 的 `WorkbenchCallStatus` 六态归一化 |
| `tests/apps/Seeing.Agent.WebUI.Tests`（扩） | `WorkbenchDockState` 开/激活/关/会话隔离/`ViewState` 保留/宽度 clamp/localStorage 往返；`HostRequestHandlerRegistry` 最长前缀匹配、跨 circuit 隔离、`ambiguous` 判定；**守门**：`IWorkbenchRegistry.All` 的每个 `ViewKey` 都能解析到组件，且该组件声明了 `WorkbenchViewContext` 参数；`AgentWorkbenchBridge` 的 open 语义 |
| `tests/apps/Seeing.Agent.WebUI.Files.Tests`（改） | 既有 `FilePanelStateTests` 按职责拆分：开合/侧别用例迁到 `WorkbenchDockStateTests`；`TreeVisible` / `PendingOpen` / `GetTabs` 用例保留 |
| `tests/apps/Seeing.Agent.HostComposition.Tests`（改） | `workbench` 模块登记守门 |
| `tests/spine/Seeing.Agent.Tests`（改） | `BuiltInCapabilitySets.Full` 含 `workbench`；`.slnx` 项目存在性 |

**Invariants：** 未启用 `workbench` 模块时，schema 中无 `open_workbench` / `list_workbenches`，且 `IWorkbenchRegistry.All` 为空。

---

## 13. 分期

| 阶段 | 内容 | 交付判据 |
|------|------|----------|
| **P0** | 契约（`Ui/Workbench/*`）+ `Seeing.Agent.Workbench` 能力包（registry / module / loader / store）+ `BuiltInCapabilitySets.Full` + `FullCapabilityComposition` + `.slnx` + `02-modules.md` 加行 | 构建通过；registry 单测绿；Invariants 绿 |
| **P1** | `HostRequestActivePanelRegistry` → `HostRequestHandlerRegistry`（kindScope + 最长前缀）；`HostRequestDispatcher` 改找 handler；旧 3 参重载标 `[Obsolete]` | WebUI 既有行为不回归；新单测绿 |
| **P2** | `WebUI.Workbench` 类库（dock / 标签条 / picker / 状态 / localStorage / ErrorBoundary）+ `WorkbenchDockSlot` 接入会话页；`WorkbenchViews` 注册内置 ViewKey | 手工验证：左右各开一个工作台、刷新恢复、窄屏覆盖 |
| **P3** | files 工作台重构：`FilesWorkbench.razor` 内嵌 `FilePanelHost`；`FilesWorkbenchPlugin` 注册；工具栏徽标 → 「工作台」picker；删除 `session_file_panel` 槽与 `FilePanelState.IsOpen/Side` | 文件浏览/编辑/diff/agent 改动刷新全链路等价 |
| **P4** | `open_workbench` / `list_workbenches` + `WorkbenchContextSource` + `AgentWorkbenchBridge`（`open_file` 的打开语义改走工作台） | agent 能列目录、打开工作台、上下文源可见当前工作台 |
| **P5** | 文档：`docs/architecture/39-workbench-plugin-platform-release-notes.md`；`05-extension-guide.md` 加「新增工作台插件」章节；`04-development-standards.md` 加工作台相关约定 | 文档索引同步 |

---

## 14. 破坏性变更清单

| 变更 | 影响 | 迁移 |
|------|------|------|
| `SlotContribution("session_file_panel", typeof(SessionFilePanel))` 移除 | 文件面板不再经插槽渲染 | 由 `files` 工作台提供；入口从「工作目录徽标」改为「工作台」picker |
| `FilePanelState.IsOpen` / `SessionId` / `Side` / `Toggle` / `SetSide` / `ToggleSide` 移除 | 编译期破坏 | 职责移交 `WorkbenchDockState`；`TreeVisible` / `PendingOpen` / `GetTabs` 保留在原类型 |
| `SessionFilePanel.razor` 移除 | 编译期破坏 | 由 `WorkbenchDockSlot` + `FilesWorkbench` 替代 |
| `HostRequestActivePanelRegistry` → `HostRequestHandlerRegistry`；`Register/Unregister` 增 `kindScope` | 编译期破坏 | 旧 3 参重载保留并标 `[Obsolete]`（scope = `""`），下个大版本移除 |
| `Seeing.Agent.WebUI.Files` 新增对 `Seeing.Agent.WebUI.Workbench` 的 ProjectReference | 依赖图变化 | — |
| `agent_file` 打开语义：`open_file` 工具不再经 `AgentFileChangeBridge.OpenRequested` 直调面板，改发 `workbench.registry.open` | 行为等价，链路变化 | 保留 `AgentFileChangeBridge.OpenRequested` 作为兼容入口，内部转 `WorkbenchDockState.Open` |

---

## 15. 风险与开放问题

| 风险 | 说明 | 缓解 |
|------|------|------|
| `ViewKey` 双处声明 | 描述符写字符串，UI 侧注册 `Type`，两处可能漂移 | 守门测试：`All` 的每个 `ViewKey` 必须可解析；解析失败在启动即抛 |
| 描述符注册点分散 | 各能力包模块自注册，`IUiContribution` 集中在 WebUI 注册，两种风格并存 | 本期接受（架构优先）；工作台目录的可卸载性依赖前者，后续可统一 |
| 多 dock 布局复杂度 | 两 dock + 主列的三段 flex，窄屏覆盖式定位可能与消息区下拉菜单冲突 | 沿用现有覆盖策略与 `z-index` 分层；P2 手工验证窄屏 |
| `WorkbenchSessionStateStore` 是 Singleton 而 UI 是 circuit 维度 | 多 circuit 同会话时谁写快照 | 后写覆盖 + 快照带 circuitId 内部标记，`Changed` 只通知；同会话多标签属已知边界（与权限 `TryAutoApprove` 进程级语义同类） |
| 上下文源摘要含用户内容 | 可能把文件内容/网页文本送进提示词 | `summary` 由 UI 截断到 200 字；渲染时明确标注为不可信内容；`ContributesContext` 默认 `false`，需插件作者显式开启 |
| 运行时加载端口是空壳 | 读者可能误以为已支持 dll 插件 | `IWorkbenchProviderLoader` 文档注释写明「本期仅内置实现」；`39` 文档列入后续演进 |
| 左侧 dock 挤压消息区 | 会话页原本只有右侧面板 | 默认 dock 为 `Right`；左侧仅在用户/agent 显式指定时出现 |

---

## 16. 附：关键代码引用

| 关注点 | 位置 |
|--------|------|
| 现有插槽契约 | `src/abstractions/Seeing.Agent.Abstractions/Ui/UiContributionTypes.cs:56-62` |
| 插槽渲染与可见性 | `samples/Seeing.Agent.WebUI/Shared/SlotHost.razor`、`Services/SessionShellVisibility.cs` |
| 会话页挂载点 | `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor:59-60,66,229` |
| 文件面板状态 | `samples/Seeing.Agent.WebUI.Files/Services/FilePanelState.cs` |
| 文件面板宿主 | `samples/Seeing.Agent.WebUI.Files/Components/FilePanelHost.razor` |
| 宿主包装组件 | `samples/Seeing.Agent.WebUI/Components/SessionFilePanel.razor` |
| 宿主请求通道 | `samples/Seeing.Agent.WebUI/Services/{HostRequestInbox,HostRequestActivePanelRegistry,HostRequestDispatcher}.cs` |
| 编辑器 RPC 先例 | `src/capabilities/Seeing.Agent.Tools.Editor/{EditorCommandTool,EditorHostRequests}.cs` |
| agent 驱动打开先例 | `samples/Seeing.Agent.WebUI/Services/AgentFileChangeBridge.cs` |
| 上下文源范式 | `src/capabilities/Seeing.Agent.Skills/Sources/SkillsContextSource.cs` |
| 上下文源注册 | `src/spine/Seeing.Agent.Core/Extensions/ServiceCollectionExtensions.cs:757-775` |
| 模块注册与能力集 | `samples/Seeing.Agent.HostComposition/FullCapabilityComposition.cs`、`src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs` |
| 呈现面（无 UI 判定） | `src/abstractions/.../Interactions/{IHostSurfaceRegistry,ISurfaceProvider}.cs`、`samples/Seeing.Agent.WebUI/Services/WebUiSurfacePresenter.cs` |
| 面板样式 | `samples/Seeing.Agent.WebUI/wwwroot/css/session-page.css:996-1060` |
