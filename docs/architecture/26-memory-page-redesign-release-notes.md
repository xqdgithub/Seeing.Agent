# 记忆页面重设计 Release Notes

**日期：** 2026-11-02
**设计规格：** `docs/superpowers/specs/2026-11-02-memory-page-redesign-design.md`
**实施计划：** `docs/superpowers/plans/2026-11-02-memory-page-redesign.md`

---

## 1. 变更摘要

`/memory` 从「空白搜索页」重设计为**单页总览**：概览卡 + 14 天时间分布 + 完整记忆列表。

| 变更 | 说明 |
|---|---|
| `/memory` 重写 | 4 张概览卡（全部/会话/每日/整合，可点击筛选）+ 14 天柱状图 + 分页列表 |
| 删除 `/memory/browse` | 列表并入 `/memory` |
| 删除 `/memory/stats` | 统计并入 `/memory` |
| 删除 `/memory/settings` | 改为设置页「记忆」页签 |
| 新增 `GetOverviewAsync` | 一次读盘出类型分布 + 时间分布 |
| 修复 `created_at` 解析 | 时间分布不再全部坍缩到「今天」 |

导航最终形态：`/memory`（工作区）、`/memory/graph`（工作区）、`/settings` → 记忆页签。

---

## 2. 修复的缺陷

### 2.1 `created_at` 键名错配（时间分布失真的根因）

**症状**：所有记忆的创建时间读出来都是「当前时刻」。时间分布图会坍缩成一根柱子。

**根因**：三个写入点写的都是 `created_at`（snake_case）：

| 写入点 | 写入键 |
|---|---|
| `Core/Pipeline/MemoryPipeline.cs` | `created_at` |
| `Integration/Tools/MemoryWriteTool.cs` | `created_at` |
| `Core/Evolution/LlmMemoryEvolution.cs` | `created_at` |

而 `LocalFileStore.ParseMetadata` 读的是 `createdAt`（camelCase）。实测确认
`YamlParser.ParseYamlFrontMatter` 反序列化到 `Dictionary<string, object?>` 时**保持原始键名**，
因此 `createdAt` 这个键永远不存在，每次都走 `?? DateTimeOffset.UtcNow`。

**修法**：两种写法都认，且缺失时回落**文件 mtime** 而非 `UtcNow`：

```csharp
var createdAt = GetDateTime(frontMatter, "created_at")
             ?? GetDateTime(frontMatter, "createdAt")
             ?? modifiedTime;
```

回落 mtime 而非 `UtcNow` 是关键：`session/{id}/index.md` 由 `AppendSessionIndexAsync` 手写
frontmatter（只有 `type` + `title`），没有 `created_at`。回落 `UtcNow` 会让这类文件永久堆在
「今天」，正是要消除的症状。

**存量数据无需迁移**：写入点一直是对的，修好解析器即自动恢复。

### 2.2 `GetDateTime` 判定顺序

原实现 `long.TryParse` 在 `DateTimeOffset.TryParse` 之前，8 位纯数字文本（如 `20260101`）
会被当成 unix 毫秒（= 1970-08-23）。改为先按日期文本解析，失败再按 unix 毫秒。

### 2.3 `ExpiresAt` 同类问题（未修，见 §5）

### 2.4 配置实质不可达

`UiContributionVisibility.FilterSidebarNav` 有一条前缀规则：

```csharp
// 有更短前缀路由时隐藏子路径（如 /memory 存在则不显示 /memory/settings）
return candidates.Where(n => !HasShorterPrefix(n.Route, candidates)).ToList();
```

`/memory` 存在时，其下所有子路由**从未在侧栏显示**。所以「记忆设置」实际只能从
`/memory` 页面的按钮点进去——这是「配置藏起来了」的字面机制。
改 `NavContribution.Group` 无效，因为前缀规则与 Group 无关。

修法：改用项目既有的 `SettingsCardContribution` 通道（`BootCapabilitySettingsCard` /
`SchedulerSettingsCard` / `ModelCapabilitiesSettingsCard` 都是这个模式），
设置页会把卡片渲染成「记忆」页签。

---

## 3. 新增契约

```csharp
// Abstractions/MemoryOverview.cs
public record MemoryDailyCount(DateOnly Date, int Count);

public record MemoryOverview(
    int TotalCount,
    IReadOnlyDictionary<MemoryType, int> CountByType,
    IReadOnlyList<MemoryDailyCount> DailyCounts,
    IndexStats Index,
    GraphStats Graph);

// Abstractions/IMemoryService.cs
Task<MemoryOverview> GetOverviewAsync(int days = 14, CancellationToken ct = default);

// Core/Models/FileMetadata.cs —— 新增字段
string? SourceSession = null
```

设计要点：

- **`DailyCounts` 长度恒为 `days`**：逐日补零，前端不必补零，柱子数量不随数据抖动。
- **三个类型键恒在**（值为 0 也保留）：前端 4 张卡片稳定渲染。
- **窗口内外语义分离**：窗口外的记忆不计入 `DailyCounts`，但仍计入 `TotalCount` 与 `CountByType`。
- **按本地日历归日**：`CreatedAt.LocalDateTime`，UTC 跨日会把「今晚写的」算到昨天。
- **`SourceSession`**：`daily` 文件来自 frontmatter 的 `source_session`；
  `session/{id}/index.md` 从路径推断（路径约定属存储层职责）。

---

## 4. 行为变更

| 路由 | 变更前 | 变更后 |
|---|---|---|
| `/memory/browse` | 目录浏览页 | **404** |
| `/memory/stats` | 统计页 | **404** |
| `/memory/settings` | 设置页 | **404**（改用 `/settings` 的「记忆」页签） |
| 侧栏 | 只有「记忆」可见（其它被子路由规则隐藏） | 不变，但设置已可从设置页直达 |

**未做重定向页**。理由：删除后全仓无内部引用（已 grep 确认），外部书签属可接受损耗。

---

## 5. 已知边界

1. **`ExpiresAt` 仍坏且无消费者**：解析读 `expiresAt`，全仓无任何写入点写该字段。
   当前没有任何代码消费 `ExpiresAt`，故不修。若将来做「过期记忆清理」，必须一并修。
2. **`/memory/settings` 不能作为侧栏导航项**：这是 `FilterSidebarNav` 前缀规则的必然结果，
   不是本次引入的限制。设置类内容走 `SettingsCardContribution` 是项目既有范式。
3. **时间分布用创建时间，列表排序用修改时间**：这是刻意的语义区分——
   「创建于何时」与「最近动过」是两个问题。编辑过的老记忆在分布图里停在原日期。
4. **柱状图不可点击**：避免与类型筛选形成二维组合态（哪个条件生效？如何表达？）。
5. **`Core/MemoryType.cs` 是死枚举**：`Semantic`/`Episodic`/`Procedural`/`Archive` 全仓无消费者，
   且与 `Core.Models.MemoryType`（`Session`/`Daily`/`Digest`）同名。本次未动，但它是真实的命名债：
   `MemoryService`、`LocalFileStore` 等位于 `Core` 命名空间内的文件必须显式加别名才能用对枚举。
6. **`GetStatsAsync()` 保留**：与新方法存在聚合逻辑重叠。接受这层冗余——它已有调用方，
   且新方法有明确的「单次读盘」职责。
7. **前端无自动化测试**：Blazor 页面靠人工验收，回归风险由后端测试（11 条新用例）覆盖。

### 5.1 现场发现的数据兼容问题（未处理）

本机 `~/.seeing/memory` 下有 1477 个记忆文件，分布在：

| 目录 | 文件数 | 当前代码是否扫描 |
|---|---|---|
| `daily/` | 5 | ✅ |
| `session/` | 0 | ✅ |
| `digest/` | 0 | ✅ |
| `episodic/` | 960 | ❌ |
| `procedural/` | 512 | ❌ |
| `semantic/`、`archive/` | 0 | ❌ |

`episodic/`、`procedural/` 是**旧版记忆格式**（对应已废弃的 `Core/MemoryType.cs` 那套枚举）。
`LocalFileStore.ListAsync` 只遍历 `session`/`daily`/`digest` 三个前缀，因此这 1472 个文件
在页面上完全不可见。

这不是本次改动引入的问题——它一直如此。但**用户视角的「看不出有哪些记忆」有一部分来自这里**。
是否需要迁移/兼容旧格式是独立议题，不在本次范围。

---

## 6. 验证记录

`dotnet build Seeing.Agent.slnx --no-incremental`：**0 error**

| 套件 | 用例数 | 结果 |
|---|---|---|
| spine | 1297 | 0 failed |
| memory | **232**（原 221，+11） | 0 failed |
| hosting | 75 | 0 failed |
| hostcomp | 10 | 0 failed |
| reflection | 108 | 0 failed |
| metadata | 269 | 0 failed |
| session | 285 | 0 failed |
| tui | 617 | 0 failed |
| cli | 84 | 0 failed |
| **合计** | **2977** | **0 failed** |

### 6.1 变异验证

| 变异 | 预期红 | 实际 |
|---|---|---|
| `created_at` 改回只读 `createdAt` | `ReadAsync_With_SnakeCase_CreatedAt_Should_Use_Written_Value` | ✅ 红 |
| `?? modifiedTime` 改成 `?? DateTimeOffset.UtcNow` | `ReadAsync_Without_CreatedAt_Should_FallBack_To_FileMtime_Not_UtcNow` | ✅ 红 |
| 去掉 `DailyCounts` 补零 | `GetOverview_DailyCounts_Should_Have_Exactly_N_Items` | ✅ 红（2 条） |
| 去掉窗口下界判断 | 窗口外两条用例 | ✅ 红（2 条） |
| `LocalDateTime` 改成 `UtcDateTime` | `GetOverview_DailyCounts_Should_Group_By_Local_Date` | ✅ 红 |

所有变异验证均加 `--no-incremental` 重建。中途遇到过一次「还原后复用旧 DLL」导致的假失败，
加 `--no-incremental` 后确认源码正确。

### 6.2 人工验收（真实服务 + 真实数据）

用本机 `~/.seeing/memory/daily` 的 5 条真实记忆（`created_at` 分布在 2026-07 ~ 2026-10）验证：

| 验收项 | 结果 |
|---|---|
| `/memory` 首屏同时出现概览卡、14 根柱、列表 | ✅ 4 张卡 / 14 根柱 / 5 行 |
| 类型分布正确 | ✅ 全部 5 / 会话 0 / 每日 5 / 整合 0 |
| 时间分布正确（都在 14 天窗口外） | ✅ 14 根柱全为 `is-zero`、高度 2px |
| x 轴标签 | ✅ `10-20` / `10-27` / `11-02` |
| 列表字段 | ✅ 标题、类型标签、来源会话截断（`ses_20261018…`）、修改时间 |
| 空状态 | ✅ 移走 daily 后出现「暂无记忆」+ 说明 + 「打开设置」按钮，概览卡不渲染 |
| `/settings` 出现「记忆」页签 | ✅ 位于「调度器」之后、「模型能力」之前 |
| 记忆页签内容 | ✅ 5 张子卡片、24 个开关、100 个数字输入框 |

### 6.3 未能验证的项

- **卡片点击筛选**、**语义检索切换**、**分页**：需要 JS 交互，`agent-browser` 在本环境不可用
  （未安装为全局命令，`npx` 启动超时）。静态 HTML 只能验证预渲染结果。
- **保存配置写回 `seeing.json`**：同上，需要点击。

这三项需要用户在真实浏览器里点一遍确认。

---

## 7. 文件清单

**新增**
- `src/capabilities/Seeing.Agent.Memory/Abstractions/MemoryOverview.cs`
- `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor.css`
- `samples/Seeing.Agent.WebUI/Components/Settings/MemorySettingsCard.razor`
- `tests/capabilities/Seeing.Agent.Memory.Tests/Storage/LocalFileStoreCreatedAtTests.cs`（5 条）
- `tests/capabilities/Seeing.Agent.Memory.Tests/Core/MemoryOverviewTests.cs`（6 条）

**修改**
- `src/capabilities/Seeing.Agent.Memory/Core/Storage/LocalFileStore.cs`（`ParseMetadata` 签名 + 键名 + `GetDateTime` 顺序）
- `src/capabilities/Seeing.Agent.Memory/Core/Models/FileMetadata.cs`（`SourceSession`）
- `src/capabilities/Seeing.Agent.Memory/Core/MemoryService.cs`（`GetOverviewAsync`）
- `src/capabilities/Seeing.Agent.Memory/Abstractions/IMemoryService.cs`
- `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs`（删除 `/memory/settings` Nav）
- `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor`（重写）
- `samples/Seeing.Agent.WebUI/Pages/MemoryDetailPage.razor`（导航）
- `samples/Seeing.Agent.WebUI/Pages/MemoryGraphPage.razor`（导航）
- `samples/Seeing.Agent.WebUI/Services/ModulePageRouteBinder.cs`（删除 3 条路由）
- `samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs`（新增 SettingsCard）

**删除**
- `samples/Seeing.Agent.WebUI/Pages/MemoryBrowsePage.razor`
- `samples/Seeing.Agent.WebUI/Pages/MemoryStatsPage.razor`
- `samples/Seeing.Agent.WebUI/Pages/MemorySettingsPage.razor`
