# 会话组快照污染与子会话切换 Release Notes

**日期：** 2026-11-09
**影响面：** `Seeing.Session`（组管理器/接口）、`Seeing.Agent.WebUI`（会话页/大屏/会话窗口/加载状态机/侧栏与卡片 CSS）

## 1. 问题现象

用户在 WebUI 观察到三组互相牵连的现象：

1. **侧栏子会话卡片显示「来源已删除」**，但父会话完好无损、子会话文件也在磁盘上。
2. **点子会话侧栏卡片无法切换**，主区一直停在骨架屏（截图 2/3 的全屏骨架），页面再也刷不出来。
3. 日志反复出现同一异常四次（每个子会话一次）：

```
Session ses_20261109115216_1a5c1d4c is not a member of group grp_20261109112332_5e19fe2d
  at Seeing.Session.Management.SessionGroupManager.SetActiveAsync(...) line 257
```

同时 `SessionWindowRegistry` 反复丢弃组事件：

```
[SessionWindowRegistry] 丢弃乱序组事件: GroupId=grp_...5e19fe2d, Version=2 < 5
```

## 2. 根因

**组快照丢失了子会话成员**，一条数据缺陷沿三条链路放大成三个界面症状。

现场取证（`D:\Projects\rezen-project\.seeing\session-groups\`）：

| 事实 | 值 |
|------|-----|
| 组文件 `members` | **1**（只有锚点） |
| 组文件 `version` | 4 |
| 4 个子会话 `groupId` | 均指向该组（未被删） |
| UI 内存记忆的 version | 5（含 4 个子会话） |

即「磁盘/AI 侧 version 4 只有 1 人，UI 侧 version 5 有 5 人」。

### 2.1 数据层：旧快照覆盖活组（真正的根）

`SessionGroupManager.CacheGroup()` 无条件覆盖：

```csharp
_cache[group.Id] = group;   // 无版本比较
```

而两条读路径会把**磁盘快照**喂进缓存：

- `ListChildrenAsync` 冷兜底：内存找不到子会话 → `_store.FindBySessionAsync` → `CacheGroup(found)`
- `GetGroupForSessionLiveAsync` 的 `FindBySessionAsync` 分支

`WriteBehindSessionGroupStore` 读前虽有 `TryFlushAllForReadAsync`，但它**有界且允许超时**，超时后按设计「退化为读取旧值」。一旦读到尚未 flush 完的旧快照（members=1），`CacheGroup` 就把它写回内存，**顶替含 4 个子会话的正确活组**；随后任意写操作（`SetActiveAsync` → `Touch` → `SaveAsync`）把这份旧快照落盘，成员关系永久丢失。

对照：`SessionWindowRegistry.ApplyGroup` 有 `if (group.Version < _appliedVersion) return;` 的乱序保护，**管理器层缺了同等的保护**。

### 2.2 切换层：乐观更新 + 未捕获异常

`Session.razor` 的 `OnTileClick`：

```csharp
_activeSessionId = sessionId;                  // 乐观更新，先渲染
await InvokeAsync(StateHasChanged);
await GroupManager.SetActiveAsync(groupId, sessionId);   // 抛异常，无人处理
```

异常冒到 `ErrorBoundary`。结果是 **UI 已切过去、组的 active 没变** → registry 事件回推又把 `_activeSessionId` 拉回旧值 → 主区与侧栏状态分叉。

### 2.3 展示层：「来源已删除」的直接成因

`SessionWindow.razor` 的 Summary 路径：

```csharp
_groupMember = group?.Members.FirstOrDefault(...);   // 组里没这个成员 → null
_relation = _groupMember?.Relation ?? (Kind==SubAgent ? Child : None);  // 兜底成 Child
_parentSessionId = _groupMember?.ParentSessionId;    // → null
```

`SessionSummaryProjector.BuildSourceLine` 拿到 `parentTitle = null`，
`SessionSummaryView.SourceLine` 便返回 **"来源已删除"**。

所以「来源已删除」**不是父会话真被删了**，而是组关系丢了 → 拿不到 `ParentSessionId`；
徽标还显示「子代理」是靠 `SessionData.Kind == SubAgent` 兜底。

### 2.4 附带缺陷：骨架常驻

`SessionLoadState.Fail` 只在 `sessionId == _targetSessionId` 时清目标：

```csharp
public void Fail(string? sessionId)
{
    if (!string.Equals(sessionId, _targetSessionId, StringComparison.Ordinal))
        return;           // target 已被后来的 Begin 改掉时，残留不清
    _targetSessionId = null;
}
```

快速连点/组事件回推造成 `Begin` 交错时，`IsLoading` 永久为 true，而
`ShowSkeleton == IsLoading && LoadedSessionId 非空` → 骨架常驻。

## 3. 修复

### 3.1 `CacheGroup` 加版本保护（根治）

`src/primitives/Seeing.Session/Management/SessionGroupManager.cs`

- 新增 `_cacheGate` 串行门，保护「版本比较 + 成员索引回填」的复合迁移。
- 规则：`loaded.Version < cached.Version` 时**丢弃旧快照并记 Information 日志**；同版本允许覆盖（内容等价，且能补齐索引）。
- 对齐 `SessionWindowRegistry` 的乱序保护语义。

### 3.2 冷兜底只在「内存完全没见过该父会话」时启用

- 新增 `TrySelectChildIdsLockedAsync`：父会话不在该组时返回 `null`，用于区分「没见过」与「见过但没有子会话」。
- `ListChildrenAsync` 仅在前者为真时才走存储兜底，避免拿磁盘快照污染已知活组。

### 3.3 UI 侧捕获切换失败（不改接口）

- **不加** `TrySetActiveAsync`（初版曾加，已撤销）：`SetActiveAsync` 在「组不存在 / 会话不在组内 / 落盘失败」
  时抛异常是**正确的契约**——数据不一致属违背前提，不应被静默吞掉；为 UI 便利扩接口会把契约错误正常化。
- `Pages/Session.razor`、`Pages/Conference.razor` 的 `OnTileClick` 自行捕获
  `InvalidOperationException`（数据不一致）与 `IOException`（落盘失败），
  回滚 `_activeSessionId`、重建侧栏与关系模型，并提示
  「该会话已不属于当前会话组，无法切换。请刷新页面重试。」

### 3.4 「来源已删除」不再误报

- `SessionSummaryView.SourceLine` 增加 `linkKnown` 参数：
  - `linkKnown == false`（父/后继 ID 为空）→ 才是「来源已删除」；
  - 有链接但标题未解析 → 「来源：…」中性文案。
- `SessionSummaryProjector.Build` 透传 `sourceLinkKnown`。
- `SessionWindow.RebuildSummarySnapshot` 传 `sourceLinkKnown: !string.IsNullOrEmpty(parentId)`，
  并在内存未命中时用 `SessionDirectory.GetAsync` 兜底解析父标题（每个父会话只尝试一次，
  切换会话时清缓存，避免串台）。

### 3.5 `SessionLoadState.Fail` 无条件退出加载态

- 无论传入会话是否仍为当前目标都清 `_targetSessionId`，杜绝骨架永久常驻。

### 3.6 现有损坏数据修复

`grp_20261109112332_5e19fe2d.json`（备份为 `.bak`）：

- 4 个子会话按 `order=1..4` 重新挂回组，`relation=Child`、`parentSessionId=锚点`；
- `version` 提到 5（与 UI 内存期望一致，避免被当作乱序快照丢弃）。

## 4. 测试

新增回归测试（全部通过）：

| 项目 | 用例 | 锁定行为 |
|------|------|----------|
| `Seeing.Session.Tests` | `CacheGroup_StaleDiskSnapshot_ShouldNotOverwriteLiveGroup` | 旧快照不得覆盖活组；子会话仍可切 active |
| `Seeing.Session.Tests` | `CacheGroup_SameVersion_ShouldBeAccepted` | 同版本允许覆盖 |
| `Seeing.Session.Tests` | `ListChildrenAsync_KnownParent_ShouldNotScanAllGroups` | 已知父会话只查所属组，不遍历全部组 |
| `Seeing.Session.Tests` | `ListChildrenAsync_UnknownParent_ShouldFallBackToStore` | 内存没见过时才走存储兜底 |
| `Seeing.Session.Tests` | `SetActiveAsync_NonMember_ShouldThrowAndKeepState` | 会话不在组内时抛异常且状态不变 |
| `Seeing.Session.Tests` | `SetActiveAsync_MissingGroup_ShouldThrow` | 组不存在时抛异常 |
| `Seeing.Session.Tests` | `SetActiveAsync_Member_ShouldSucceedAndBeRepeatable` | 正常切换生效、可重复 |
| `Seeing.Agent.WebUI.Tests` | `Build_SourceLinkKnownButTitleMissing_ShouldNotClaimDeleted` | 标题未加载不得断言已删除 |
| `Seeing.Agent.WebUI.Tests` | `Build_SourceLinkMissing_ShouldReportDeleted` | 链接缺失才报已删除 |
| `Seeing.Agent.WebUI.Tests` | `Build_NonAnchorWithoutRelation_ShouldHaveEmptySourceLine` | 无来源关系不渲染来源行 |
| `Seeing.Agent.WebUI.Tests` | `FailStaleSession_ShouldStillExitLoading` | 迟到失败仍退出加载态 |
| `Seeing.Agent.WebUI.Tests` | `FailTwice_ShouldBeIdempotent` | 重复失败幂等 |

验证结果：

- `dotnet build Seeing.Agent.slnx` → 0 错误
- `Seeing.Session.Tests` → 318 通过 / 0 失败
- `Seeing.Agent.WebUI.Tests` → 380 通过 / 0 失败

## 5. 已知边界

- 版本保护依赖 `Version` 单调递增。若外部脚本直接改盘且不回退版本，仍可能被接受（同版本覆盖是有意保留的语义）。
- 写回装饰器的「读旧值」行为本身未改：本次只在消费端（缓存）加保护，未改变读路径的保活设计。
- 父标题兜底解析是**每父会话一次**的尽力而为：解析失败后不再重试，需切换会话或刷新页面。

## 6. 附带修复：侧栏徽标竖排与卡片溢出（CSS，与组数据无关）

排查「子会话显示来源已删除」时，用户同时反馈了两组**独立的**样式问题。经取证，二者与组数据**无因果关系**，
是 `session-page.css` 中长期存在的布局缺陷。此处一并记录，避免与上文的成员丢失混淆。

### 6.1 徽标文字竖排（「主线」/「执行中」/「子代理」）

**现象**：工具栏与侧栏卡片里的状态徽标文字被逐字排列成竖列。

**根因**：两层缺失叠加。

1. AntDesign 的 `.ant-badge` 只有 `display: inline-block`，**未声明 `flex-shrink`**；
   作为 flex 子项时默认参与收缩。
2. `.ant-badge-status-text` 只给了 `margin-left: 8px`，**未声明 `white-space`**；
   中文每个字都是合法断行点。

横向空间一紧 → 徽标被压到一两个字宽 → 文字逐字换行成竖排。

旁证：同容器内 `.session-toolbar-title`、`.conference-tile-relation` 都写了 `white-space: nowrap`，
唯独徽标这几处漏了。

**修复**：工具栏左右两侧、卡片头部（含紧凑卡）的 `.ant-badge` 与 `.ant-badge-status-text`
统一 `flex-shrink: 0` + `white-space: nowrap`；`.session-toolbar-left` 加 `flex-wrap: nowrap`
（空间不足时由已 ellipsis 的标题承担收缩）。

### 6.2 侧栏横向滚动条 / 卡片被撑宽

**现象**：侧栏出现**横向**滚动条，卡片宽度超出侧栏可视区（纵向滚动正常，未改动）。

**根因**（横向溢出链，从外到内三层，任一层缺失都会让内层 nowrap 文本顶宽）：

1. `.session-side-area` 只有 `width` + `min-width: 280px`，**缺 `min-width: 0`**。
   flex 子项默认 `min-width: auto`，`min-width: 280px` 反而设了下限、没有任何防护作用。
2. `.session-side-section`（分区）同样是侧栏的 flex 子项，**未声明 `min-width: 0`**，
   被卡片 min-content 顶宽。
3. `.conference-tile-title` 在 `justify-content: space-between` 的 header 里**没有 `flex: 1 1 auto`**，
   按 max-content 参与分配 —— 长标题直接撑宽整行。

**修复（内容自身收敛，不在祖先层裁剪）**：

- `.session-side-area`：尺寸只由 `flex-basis` 决定（`flex: 0 0 <宽度>`），补 `min-width: 0`；
  去掉重复的 `width`/`max-width` 声明。**右侧留 `padding-right: 12px`** 给待审批角标。
- `.session-side-section` 加 `min-width: 0` + `max-width: 100%`。
- `.conference-tile-title` 改为 `flex: 1 1 auto` + `min-width: 0`（唯一可收缩项，超出走 ellipsis）；
  `.conference-tile-header` / `.conference-tile-compact-header` 加 `max-width: 100%`。
- `.session-toolbar-left` 加 `flex: 1 1 auto` + `max-width: 100%` + `overflow: hidden`
  （否则左侧一堆 nowrap 子项按 max-content 排布会撑宽整条工具栏）。
- `.session-side-entry` 及内层 `.ant-badge` / `.session-window--summary` / `.conference-tile`
  统一 `width/max-width: 100%` + `min-width: 0`。
- 大屏页 `Conference.razor` 的 `.conference-side-area`（内联 style）同步同一套约束。

> **不要在 `.session-side-entry` 上加 `overflow: hidden`**（初版曾如此，已回退）：
> AntDesign 待审批角标是 `position: absolute; right: 0; transform: translate(50%, -50%)`，
> **故意**向右溢出半个宽度才能露出圆形。祖先层裁剪会把它切掉。
> 横向溢出的正解是让内容自身收敛（标题 ellipsis + 徽标 flex-shrink:0），而非裁掉超出的部分。

> 注：这三处修改**不依赖也不修复**组快照问题。成员丢失会让徽标文案变成「来源已删除」，
> 但竖排与溢出即使组数据完好也会出现。
