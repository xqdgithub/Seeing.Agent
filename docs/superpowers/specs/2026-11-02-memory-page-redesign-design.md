# 记忆页面重设计

**日期：** 2026-11-02
**状态：** 待批准
**影响范围：** `Seeing.Agent.Memory`（后端聚合 + 元数据解析）、`Seeing.Agent.WebUI`（页面与路由）

---

## 1. 问题

用户原话：「进去啥也没有，里面还藏着配置，有哪些记忆也看不出来，最好有总览」。

对照代码，三条抱怨各自对应一个真实缺陷：

### 1.1 「进去啥也没有」

`MemoryPage.razor` 的 `ChildContent` 只渲染 `_hits`（语义搜索命中）。`_hits` 初始为空，`_query` 为空时走 else 分支，显示一句静态提示文字。**首页没有任何「我有哪些记忆」的入口**，真正的列表在 `/memory/browse` 这个二级页面里。

### 1.2 「配置藏在里面」

`ModulePageRouteBinder.KnownModuleRoutes` 里 5 个记忆路由**全部**挂在 `NavGroups.Workspace`，Order 50–54 连续：

| 路由 | 标题 | 分组 | Order |
|---|---|---|---|
| `/memory` | 记忆 | 工作区 | 50 |
| `/memory/settings` | 记忆设置 | 工作区 | 51 |
| `/memory/browse` | 记忆浏览 | 工作区 | 52 |
| `/memory/graph` | 知识图谱 | 工作区 | 53 |
| `/memory/stats` | 记忆统计 | 工作区 | 54 |

但**真正的问题比「平级」更糟**：`UiContributionVisibility.FilterSidebarNav` 有一条规则——

```csharp
// 有更短前缀路由时隐藏子路径（如 /memory 存在则不显示 /memory/settings）
return candidates.Where(n => !HasShorterPrefix(n.Route, candidates)).ToList();
```

`/memory` 存在时，`/memory/settings`、`/memory/browse`、`/memory/graph`、`/memory/stats` **全部不出现在侧栏**。
所以「记忆设置」实际上只能从 `/memory` 首页的按钮点进去 —— 这才是「配置藏在里面」的字面机制。

由此得到一个硬约束：**单纯把 NavContribution 的 Group 改成 `NavGroups.Settings` 没有任何效果**，前缀规则照样把它藏掉。必须换一种贡献类型。

### 1.3 「看不出有哪些记忆」+「要总览」

- `/memory/stats` 给出类型分布，但只有数字，点不动，是死胡同。
- 类型分布与文件列表分居两页，用户的认知路径被迫是「统计页看数字 → 记住 → 去浏览页对照」。
- `GetStatsAsync()` 内部调 `ListAsync()`，而 `MemoryStatsPage` 又独立调一次 `ListAsync()` —— 同一份数据读两遍盘。

### 1.4 顺带发现的阻断性缺陷：`created_at` 读不出来

这是本次设计必须一并修掉的问题，**否则「按时间分布」无法实现**。

三个写入点写的都是 `created_at`：

| 写入点 | 行 |
|---|---|
| `Core/Pipeline/MemoryPipeline.cs` | `created_at: {DateTimeOffset.Now:O}` |
| `Integration/Tools/MemoryWriteTool.cs` | `sb.AppendLine($"created_at: ...")` |
| `Core/Evolution/LlmMemoryEvolution.cs` | `created_at: {DateTimeOffset.UtcNow:O}` |

而解析器读的是 `createdAt`：

```csharp
// Core/Storage/LocalFileStore.cs:450
var createdAt = GetDateTime(frontMatter, "createdAt") ?? DateTimeOffset.UtcNow;
```

已实测确认：`YamlParser.ParseYamlFrontMatter` 反序列化到 `Dictionary<string, object?>` 时**保持原始键名**
（探针输出 `KEYS=[id,type,created_at,expiresAt]`），所以 `createdAt` 这个键永远不存在。

后果：**每个记忆文件的 `Metadata.CreatedAt` 读出来都是「当前时刻」**。不论内容是三月还是昨天的，时间分布都会坍缩成一根柱子。

同类问题：`ExpiresAt` 读 `expiresAt`，而全仓没有任何写入点写这个字段，当前无消费者，本次不动（见 §8）。

---

## 2. 目标

1. `/memory` 首屏同时回答三个问题：**有多少 / 什么类型和时间的分布 / 具体是哪些**。
2. 配置从「藏在记忆首页按钮后面」变成**可直达**：进入「设置 → 系统设置 → 记忆」页签，
   或从 `/memory` 概览页的「设置」按钮进入。不再是一个只能靠记忆才知道存在的按钮。

### 1.5 配置归位为什么不用「改 Nav 分组」

先排除三种看起来可行、实测不行的做法：

| 做法 | 为什么不行 |
|---|---|
| 把 `NavContribution.Group` 改成 `NavGroups.Settings` | `FilterSidebarNav` 的前缀规则无条件隐藏 `/memory/settings`，与 Group 无关 |
| 把路由改成 `/settings/memory`，脱离 `/memory` 前缀 | 会与 `/settings` 构成前缀关系，同样被隐藏；且要新增页面与路由 |
| 改 `FilterSidebarNav` 支持「排除同模块前缀」 | 改动全局侧栏过滤器，影响所有模块的导航行为，风险远大于收益 |

采用**已有的 `SettingsCardContribution` 机制**：设置页 `Settings.razor` 把每张卡片渲染成一个
`TabPane`（`Key=card.Route`，`Tab=card.Title`，内容是 `<DynamicComponent Type=card.ComponentType />`）。
这是为「模块配置归入设置」专门设计的既有通道，`BootCapabilitySettingsCard` / `ScenariosSettingsCard` /
`SchedulerSettingsCard` / `ModelCapabilitiesSettingsCard` 都在用它。

`MemorySettingsPage.razor` 的内容（5 张 Card + 保存按钮）本身就是自包含的，可直接作为卡片渲染。
3. 消除重复读盘：一页一次数据加载。
4. 时间维度基于**真实创建时间**，而非解析失败后回落的时间戳。

非目标：知识图谱页的交互范式（与列表完全不同，本次不动）。

---

## 3. 信息架构（选定方案 A：单页收敛）

### 3.1 路由变更

| 路由 | 变更 | 说明 |
|---|---|---|
| `/memory` | **重写** | 唯一总览页：概览卡 + 时间分布 + 全部记忆列表 |
| `/memory/detail/{*FilePath}` | 保留，改面包屑 | 详情 |
| `/memory/graph` | 保留，改导航按钮 | 图谱 |
| `/memory/settings` | **删除页面**，内容改为 `SettingsCardContribution` | 进入「设置 → 系统设置 → 记忆」页签 |
| `/memory/browse` | **删除** | 列表并入 `/memory` |
| `/memory/stats` | **删除** | 统计并入 `/memory` |

`MemoryModule.Contribute()` 调整为：

```csharp
public IReadOnlyList<object> Contribute() =>
[
    new NavContribution("/memory", "记忆", "database", ["memory"],
        Group: NavGroups.Workspace, GroupIcon: NavGroups.WorkspaceIcon, Order: 50),
    // 不能再用 NavContribution 挂 /memory/settings：FilterSidebarNav 的前缀规则
    // 会因 /memory 存在而隐藏它。设置类配置统一走 SettingsCard。
    new SettingsCardContribution("/settings/memory", "记忆", typeof(MemorySettingsCard), ["memory"]),
];
```

能力包不能引用 WebUI 类型，因此 `typeof(...)` 不能写在 `MemoryModule` 里。**已确认既有模式**：
设置类卡片全部由 WebUI 侧的 `CoreShellUiContribution` 统一登记
（`BootCapabilitySettingsCard` / `ScenariosSettingsCard` / `SchedulerSettingsCard` 均如此），
能力包只贡献内容页 Nav。

所以记忆设置的接入点是 `samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs`，
在既有 `SettingsCardContribution` 列表中追加一条：

```csharp
new SettingsCardContribution(
    "/settings/memory",
    "记忆",
    typeof(MemorySettingsCard),
    ["memory"]),
```

`MemoryModule.Contribute()` 中**删除** `/memory/settings` 那条 `NavContribution`（它本来也显示不出来）。

**删除页面的链接必须全改干净**，否则点击 404。已知引用点：

- `MemoryDetailPage.razor`：面包屑 `<BreadcrumbItem Href="/memory/browse">目录浏览</BreadcrumbItem>`、`返回列表` 按钮
- `MemoryGraphPage.razor`：`记忆搜索` 按钮
- 各页 `Extra` 区的互跳按钮
- `ModulePageRouteBinder.KnownModuleRoutes` 的两条记录

### 3.2 页面结构

```
┌ 工作区 / 记忆 ─────────────────────────────── 刷新 | 设置 ┐
│                                                            │
│  总览                                    ← 可点击筛选      │
│  ┌────────┬────────┬────────┬────────┐                    │
│  │ 全部   │ 会话   │ 每日   │ 整合   │                    │
│  │  497   │  128   │  342   │   27   │                    │
│  └────────┴────────┴────────┴────────┘                    │
│                                                            │
│  最近 14 天                                                │
│   ▁ ▃ ▅ ▂ ▁ ▁ ▇ ▄ ▂ ▁ ▁ ▅ ▃ █                            │
│  10-20            10-27            11-02                   │
│                                                            │
├────────────────────────────────────────────────────────────┤
│  [类型 ▾] [搜索记忆……            ]  共 497 条              │
│                                                            │
│  标题            类型   来源会话      修改时间      操作    │
│  ─────────────────────────────────────────────────────     │
│  用户偏好        每日   sess-ab12…   11-02 14:03  查看 删除 │
│  ...                                                       │
└────────────────────────────────────────────────────────────┘
```

### 3.3 交互定义

**概览卡点击 = 筛选。** 点「会话」→ 列表只显示 `session` 类型，卡片变为选中态；再点一次取消。这解决了「统计页死胡同」问题：数字本身就是入口。

**时间分布不可点击。** 柱状图只做趋势感知，加点击会与类型筛选产生二维组合态，需要额外的选中态表达（哪个组合生效？）—— 收益不抵复杂度。

**空状态必须可行动。** 现状 `IsEmpty="@false"` 是硬编码，没数据时显示空白。改为：

```
暂无记忆
记忆在每次对话后自动抽取（默认开启，每 10 轮一次）。
如果一直为空，请检查设置中的「自动捕获」与「抽取」是否关闭。
[ 打开设置 ]
```

**搜索语义**：输入即时本地过滤（标题 / 路径 / 正文子串），回车触发语义检索（`SearchAsync`）。两种结果在标题上明确区分（`语义检索结果 (N)` vs `全部记忆 (N)`），并提供「清除搜索」回到列表。

保留语义检索的理由：删掉是能力倒退，且 `MemoryPage` 原本就是这个职责。保留本地过滤的理由：向量索引可能未建（`IsEmbeddingConfigured` 为假），此时语义检索会全部落空，本地过滤仍可用。

---

## 4. 后端设计

### 4.1 修复 `created_at` 解析

`LocalFileStore.ParseMetadata`：

```csharp
// 兼容两种写法：写入点统一用 snake_case，历史/手工文件可能用 camelCase。
// 错配的后果是每个文件都回落 UtcNow，时间分布完全失真。
var createdAt = GetDateTime(frontMatter, "created_at")
             ?? GetDateTime(frontMatter, "createdAt")
             ?? modifiedTime;   // 无创建时间（如 session/*/index.md 手写头）→ 回落文件 mtime
```

`ParseMetadata` 当前签名是 `(Dictionary<string, object?> frontMatter, string path)`，需要把 `modifiedTime` 传进来。
`CreateFileNode` 已持有 `fileInfo.LastWriteTimeUtc`，直接透传即可。

**为什么回落 mtime 而不是 `UtcNow`**：`session/{id}/index.md` 由 `AppendSessionIndexAsync` 手写 frontmatter，只有 `type` 与 `title`，没有 `created_at`。回落 `UtcNow` 会让这类文件永远堆在「今天」，正是我们要消除的症状。

**顺带修 `GetDateTime` 的判定顺序**：当前 `long.TryParse` 在 `DateTimeOffset.TryParse` 之前。纯数字文本（如 `20260101`）会被误判成 unix 毫秒。调整为「先尝试日期解析，失败再按 unix 毫秒」。

### 4.2 `FileMetadata` 增加来源会话

列表要回答「这条记忆哪来的」。`daily` 文件 frontmatter 里有 `source_session`，但 `FileMetadata` 没解析它。

```csharp
public record FileMetadata(
    string Id,
    MemoryType Type,
    string? Title,
    IReadOnlyList<string> Tags,
    double Importance,
    double Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? SourceSession = null);   // 新增，默认值放末尾避免破坏既有构造调用
```

`ParseMetadata` 补一行 `GetString(frontMatter, "source_session")`。
`session/{id}/index.md` 没有该字段，回落到「从路径推断」——路径形如 `session/{sessionId}/index.md`，可取出 sessionId。这条推断放在 `GetOverviewAsync` 的投影里做还是 `ParseMetadata` 里做？

**在 `ParseMetadata` 里做**：`session/{id}/index.md` 的 `SourceSession` 就是 `id`。这样调用方不必再重复理解路径约定，符合「路径约定属于存储层」的职责划分。

### 4.3 新增总览聚合接口

现状痛点：页面要拿「类型分布 + 时间分布 + 索引/图谱统计」，需要调 `GetStatsAsync()`（内部 `ListAsync` 一次）+ `ListAsync()`（再一次），读两遍盘，且分布计算散在 UI 里无法单测。

新增：

```csharp
// Abstractions/IMemoryService.cs
/// <summary>获取总览：类型/时间分布 + 索引与图谱统计。一次加载，供总览页单点消费。</summary>
Task<MemoryOverview> GetOverviewAsync(int days = 14, CancellationToken ct = default);
```

```csharp
// Abstractions/MemoryOverview.cs
public record MemoryDailyCount(DateOnly Date, int Count);

public record MemoryOverview(
    int TotalCount,
    IReadOnlyDictionary<MemoryType, int> CountByType,
    IReadOnlyList<MemoryDailyCount> DailyCounts,   // 恰好 days 项，含 0 值日期
    IndexStats Index,
    GraphStats Graph);
```

实现要点：

- **只读一次盘**：`var files = await _fileStore.ListAsync(ct)`，后续全部在内存聚合。
- **`DailyCounts` 长度恒为 `days`**：从 `today - (days-1)` 到今天逐日生成，没有记忆的日期填 `0`。调用方（前端）不需要补零，避免「柱子数量随数据变化」的渲染抖动。
- **时间归属用本地日期**：`CreatedAt.ToLocalTime()` 取 `DateOnly`。用户看的是自己的日历，UTC 跨日会造成「明明今天晚上写的算到昨天」。
- **窗口外的记忆不计入 `DailyCounts`，但计入 `TotalCount` 与 `CountByType`**。两者语义不同，不能混。
- **`CountByType` 包含三个键，值为 0 的也要在**（`Session`/`Daily`/`Digest`），前端无需判空。

`GetStatsAsync()` 保留不动（其他地方可能用到），但其内部实现可以复用同一段聚合逻辑。

### 4.4 为什么不是「前端自己算」

分布计算放后端有三个理由：

1. 补零、本地时区、窗口边界这三件事都有明确的正确性要求，需要单测锁定。
2. 前端算需要额外一次 `ListAsync`，与「消除重复读盘」目标冲突。
3. `MemoryOverview` 是一个稳定的产品契约（「记忆长什么样」），不该让每个宿主各自实现一遍。

---

## 5. 前端设计

### 5.1 `MemoryPage.razor` 重写

数据加载：

```csharp
private MemoryOverview? _overview;
private IReadOnlyList<FileNode> _files = [];
private string _typeFilter = "";      // "" = 全部
private string _search = "";
private IReadOnlyList<SearchHit>? _semanticHits;   // null = 非语义模式
```

一次 `OnInitializedAsync` 内并发加载：

```csharp
var overviewTask = MemoryService.GetOverviewAsync(14);
var filesTask = MemoryService.ListAsync();
await Task.WhenAll(overviewTask, filesTask);
_overview = await overviewTask;
_files = await filesTask;
```

`ListAsync` 仍是必要的：列表要显示标题、路径、来源——`MemoryOverview` 只给聚合数字。

渲染要点（沿用 AGENTS.md 已记录的项目陷阱）：

- 列表用原生 `<table class="items-table">`，**不用** `<Table>/<PropertyColumn>`。
- 按钮 `Size` 写 `@ButtonSize.Small`；`Tag` 的 `Color` 写 `@("blue")`。
- 分页用 AntDesign `<Pagination>`（`ModelsPage` 已有可用先例），每页 20 条。

概览卡与柱状图用纯 CSS，不引入图表库。

### 5.2 柱状图实现

用纯 CSS flex 柱状图，不引入图表库。标记与样式：

```html
<div class="mem-bars">
  @foreach (var d in Overview.DailyCounts)
  {
    <div class="mem-bar-slot" title="@($"{d.Date:MM-dd} · {d.Count} 条")">
      <div class="mem-bar @(d.Count == 0 ? "is-zero" : "")"
           style="height:@BarHeight(d.Count)px"></div>
    </div>
  }
</div>
```

```css
/* Pages/MemoryPage.razor.css */
.mem-bars { display:flex; align-items:flex-end; gap:4px; height:72px; margin-top:8px; }
.mem-bar-slot { flex:1; display:flex; align-items:flex-end; }
.mem-bar { width:100%; background:var(--color-primary); border-radius:2px 2px 0 0; }
.mem-bar.is-zero { background:var(--color-bg-tertiary); height:2px !important; }
```

- `BarHeight(count)`：`count == 0 ? 2 : Math.Max(4, (int)Math.Round(count / (double)max * 72))`，
  `max` 为窗口内最大值；`max == 0` 时全部返回 2。
- 零值柱用 `is-zero` 换色但仍占位 —— 空档必须能看出来，否则「14 天」会塌缩成「有数据的那几天」。
- x 轴标签只画首/中/末三处（`10-20` … `10-27` … `11-02`），避免 14 个标签挤压。
- 原生 `title` 属性做 tooltip，不依赖组件库。

### 5.3 删除的页面

- `Pages/MemoryBrowsePage.razor`：**删除**
- `Pages/MemoryStatsPage.razor`：**删除**
- `Pages/MemorySettingsPage.razor`：**删除**，内容搬进 `Components/Settings/MemorySettingsCard.razor`
- `ModulePageRouteBinder` 删除 `/memory/settings`、`/memory/browse`、`/memory/stats` 三条 `ModuleRouteBinding`
- `CoreShellUiContribution` 新增一条 `SettingsCardContribution("/settings/memory", "记忆", typeof(MemorySettingsCard), ["memory"])`
- `MemoryModule.Contribute()` 删除 `/memory/settings` 那条 `NavContribution`
- `MemoryDetailPage` 面包屑由「记忆 / 目录浏览 / 详情」改为「记忆 / 详情」，返回按钮指向 `/memory`
- `MemoryGraphPage` 的「记忆搜索」按钮改文案「记忆总览」，指向 `/memory`

### 5.3.1 设置卡片的改造要点

`MemorySettingsPage` → `MemorySettingsCard` 不是简单改文件名，有三处必须处理：

1. **去掉 `<WorkspacePageShell>` 外壳**（面包屑、页面标题、"返回"按钮）。设置页已经在外壳内，
   再嵌一层会出现双标题。
2. **保存按钮移进卡片内部**。原来「保存」在 `Extra` 区（页面头部右侧），卡片渲染时没有 Extra 区。
   改为卡片底部一个右对齐的 `<Button Type="@ButtonType.Primary" OnClick="SaveAsync">保存</Button>`。
   这与 `ModelCapabilitiesSettingsCard` 的做法一致（它在卡片内部自带保存按钮）。
3. **`@page` 指令删除**，组件改为纯 `<Component>`。

其余 5 张配置卡片与 `@code` 块**原样搬迁**，不改任何字段绑定逻辑。

### 5.4 保留

- `MemoryDetailPage.razor` 只改导航，编辑/删除逻辑不动。
- `MemoryGraphPage.razor` 只改导航。

---

## 6. 测试计划

### 6.1 必须新增（后端）

| 用例 | 目的 |
|---|---|
| `ParseMetadata_With_SnakeCase_CreatedAt_Should_Preserve_Real_Time` | 写 `created_at` 的临时文件，读出的 `CreatedAt` 必须等于写入值（当前会失败——这是回归锚点） |
| `ParseMetadata_With_CamelCase_CreatedAt_Should_Still_Work` | 兼容两种写法 |
| `ParseMetadata_Without_CreatedAt_Should_FallBack_To_FileMtime` | 无字段 → mtime，不是 `UtcNow` |
| `GetDateTime_Should_Prefer_DateString_Over_UnixMilliseconds` | `20260101` 不被当成 unix ms（8 位纯数字既像日期也像毫秒） |
| `GetOverview_CountByType_Should_Include_Zero_Value_Types` | 三键恒在 |
| `GetOverview_DailyCounts_Should_Have_Exactly_N_Items_With_Zero_Fill` | 补零、长度稳定 |
| `GetOverview_DailyCounts_Should_Exclude_Entries_Outside_Window` | 窗口外不进分布 |
| `GetOverview_TotalCount_Should_Include_Entries_Outside_Window` | 总数与分布语义分离 |
| `GetOverview_Should_Group_By_Local_Date` | 时区归属 |
| `ParseMetadata_Should_Read_SourceSession_For_Daily` | `source_session` 字段 |
| `ParseMetadata_Should_Infer_SourceSession_From_SessionIndexPath` | `session/{id}/index.md` → `id` |

### 6.2 变异验证（必须做）

对每条关键用例，故意改坏实现，确认用例变红：

- 把 `created_at` 改回只读 `createdAt` → 第一条必须红
- 去掉补零循环 → `DailyCounts` 长度用例必须红
- 把窗口过滤从 `DailyCounts` 挪到 `TotalCount` → 两条语义用例同时红

执行时**必须加 `--no-incremental`**（`Copy-Item` 还原会保留旧时间戳，MSBuild 复用旧产物会得出假结论）。

### 6.3 前端

项目无 Blazor 组件测试基建，前端靠人工验收：

1. `/memory` 首屏：有数据时能同时看到概览卡、柱状图、列表。
2. 点「会话」卡 → 列表只剩 session 类型；再点 → 恢复全部。
3. 输入关键词 → 本地过滤；回车 → 语义检索，标题变为「语义检索结果」。
4. 无数据时 → 空状态出现「打开设置」按钮，点击到达 `/memory/settings`。
5. 侧栏：`记忆` 在「工作区」组；`记忆设置` **不出现在侧栏**（前缀规则决定，属预期），
   改为从 `/memory` 概览页的「设置」按钮，或「设置 → 系统设置 → 记忆」页签进入，两条路径都要能到。
6. `/memory/browse`、`/memory/stats`、`/memory/settings` 直链 → 不再有页面（已知行为变更，见 §8）。
7. 详情页「返回列表」→ `/memory`。
8. 全仓搜索 `memory/browse`、`memory/stats`、`memory/settings`，确认无残留导航引用
   （`/settings/memory` 作为 SettingsCard 的 Route 保留，它不是页面路由，是页签 Key）。

---

## 7. 实施顺序

1. **后端修复**：`ParseMetadata`（`created_at` + `source_session` + mtime 回落）、`GetDateTime` 顺序 → 补测试 → 变异验证。
2. **后端聚合**：`MemoryOverview` / `MemoryDailyCount` / `GetOverviewAsync` → 补测试 → 变异验证。
3. **前端重写**：`MemoryPage.razor` + CSS。
4. **删除与重定向**：删两页、改路由表、改三处导航、改 `MemoryModule.Contribute()`。
5. **验证**：全量构建 + 全量测试 + 人工验收 8 条。

第 1 步必须先于第 3 步：柱状图的数据源依赖它。

---

## 8. 已知边界与取舍

1. **`/memory/browse`、`/memory/stats`、`/memory/settings` 直链会失效**。没有做重定向页。理由是删除后不存在任何内部引用，外部书签属于可接受损耗。若要保留，需额外增加三个只做 `NavigateTo` 的桩页——当前判断不值得。
2. **`/memory/settings` 不能作为侧栏导航项**。这是 `FilterSidebarNav` 前缀规则的必然结果，不是本次引入的限制。设置类内容走 `SettingsCardContribution` 是项目既有范式；本次只是把记忆设置纳入其中。
2. **时间分布用创建时间，不用修改时间**。编辑过的老记忆停在原日期。这是「创建于何时」的语义选择，不是缺陷。
3. **`ExpiresAt` 仍是坏的同类问题**（读 `expiresAt`，无人写）。当前无任何消费者，不修。若将来做「过期记忆清理」，必须一并修。
4. **存量数据不需要迁移**。写入点一直写的是 `created_at`，只是没被读到；修好解析器即自动恢复。唯一例外是 `session/*/index.md`，它本来就没有该字段，走 mtime 回落。
5. **柱状图不可点击**。避免与类型筛选形成二维组合态。
6. **`Core/MemoryType.cs`（`Semantic`/`Episodic`/`Procedural`/`Archive`）全仓无消费者**，与 `Core.Models.MemoryType`（`Session`/`Daily`/`Digest`）同名易混。本次不动，但它是真实的命名债。
7. **`GetStatsAsync()` 保留**，与新方法存在聚合逻辑重叠。接受这层冗余，因为它已有调用方，且新方法有明确的「单次读盘」职责。
8. **前端无自动化测试**。验收依赖人工清单，回归风险由后端测试覆盖。
