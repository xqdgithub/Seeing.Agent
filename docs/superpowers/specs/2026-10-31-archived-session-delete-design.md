# 已归档会话支持删除与批量删除

- 日期：2026-10-31
- 状态：待批准（批准后进入实现计划）
- 范围：`samples/Seeing.Agent.WebUI`（会话管理页）+ `src/primitives/Seeing.Session`（归档删除的存储语义修正）+ 对应测试

## 1. 意图与为什么

「会话管理」页有普通视图与「已归档」视图。普通视图已支持单项删除、勾选、全选、批量删除；
已归档视图只有「恢复」，想清理归档会话只能先恢复、再回到普通视图删除，或者手动删磁盘文件。

用户要求：已归档会话页面支持删除，并支持批量删除。批量恢复不在本次范围（用户已确认）。

## 2. 目标 / 非目标

目标：

- 每张归档会话卡片提供「删除」（带确认），删除后从归档列表消失；
- 归档视图提供勾选、全选、批量删除，交互与普通视图一致；
- 删除归档会话时，磁盘上的归档数据（`_archive/{id}.json.gz`）与内存归档表一并清理；
- 补充存储层测试，锁定「删除后不再出现在 `ListArchivedAsync`」。

非目标：

- 不做批量恢复；
- 不做已归档视图的搜索过滤（现有搜索只作用于普通会话列表，保持原样）；
- 不引入回收站/软删除，不做归档保留期策略。

## 3. 现状与关键发现

- 归档视图渲染在 `SessionsPage.razor` 的 `_showArchived` 分支，卡片内只有「恢复」按钮；
  勾选状态 `_selectedIds`、批量栏、`DeleteSessionsCoreAsync` 均已存在，但只作用于普通列表
  （`IsAllSelected()` / `OnToggleSelectAll()` 写死 `_filteredSessions`）。
- 存储层存在缺陷：`FileSessionStore.DeleteAsync` 只删除活跃目录的 `{id}.json`，
  会话不存在时直接 return，归档文件 `_archive/{id}.json.gz` 永远不会被删；
  `InMemorySessionStore.DeleteAsync` 同样只清 `_sessions`，不清 `_archived`。
  因此仅加 UI 会出现「提示删除成功、已归档列表仍有该会话」的假成功。
- 组关系：归档时组记录保留（供恢复）；`SessionDirectory.DeleteAsync` 走
  `SessionGroupManager.RemoveSessionAsync`，对归档会话可经 `FindBySessionAsync` 找到组，
  移除成员并级联删除 Child 子树；组清空时一并删除组记录，行为可复用。

## 4. 方案选择（Approaches）

| 方案 | 说明 | 取舍 |
|---|---|---|
| A · 新增 `DeleteArchivedAsync` 端口 | 在 `ISessionArchiveStore` 增加归档删除原语，`SessionDirectory.DeleteAsync` 按是否归档分流 | 语义最显式；但要扩接口 + 扩展方法 + SessionManager + WriteBehind + InMemory + 所有测试假实现，改动面大 |
| **B · 让 `DeleteAsync` 幂等清理归档数据（已选）** | 存储层 `DeleteAsync` 同时删除活跃数据与归档数据；上层与 UI 无需区分会话是否归档 | 语义自然（删除=彻底删除，含归档副本）；改动集中、无接口变更；对现有调用方是纯增强 |

选择 B：删除本来就应当彻底清理，归档只是「移出活跃集但保留可恢复数据」，
一旦用户显式删除，保留归档副本没有任何意义。方案 A 的显式端口适合未来做「仅删归档副本」，
当前没有这个需求。

## 5. 实现要点

### 5.1 存储层

- `FileSessionStore.DeleteAsync`：
  - 保持 `DeleteRefDirectory` 与活跃文件删除；
  - 无条件尝试删除 `_archive/{sessionId}.json.gz`（不存在则跳过，不抛）；
  - 归档文件删除失败按现有策略记 Warning，不阻塞活跃文件删除；
  - 活跃文件不存在时不再提前 return（仍需清理归档副本）。
- `InMemorySessionStore.DeleteAsync`：同时 `_sessions.TryRemove` 与 `_archived.TryRemove`。
- `WriteBehindSessionStore.DeleteAsync` 已先 `DiscardAsync` 再委托内层，归档删除随内层生效，无需改动。

### 5.2 WebUI（`SessionsPage.razor`）

- 新增「当前可选列表」概念（`_showArchived ? _archivedSessions : _filteredSessions`），
  `IsAllSelected()` / `OnToggleSelectAll()` 改为基于它，普通视图行为不变。
- 归档卡片：
  - 增加 `Checkbox`（勾选，阻止冒泡——归档卡片本身不可点击进入会话）；
  - 「恢复」旁增加「删除」按钮，`Popconfirm` 文案：
    「确定删除已归档会话？该操作不可恢复，将同步删除其子会话。」；
  - 删除走既有 `DeleteSessionsCoreAsync([id])`，成功/失败提示复用。
- 批量栏在归档视图同样显示（已选数、全选、批量删除、清除选择），
  批量删除文案改为「确定删除所选 N 个已归档会话？该操作不可恢复，将同步删除其子会话。」
  （普通视图保持原文案）。
- 保持 `RefreshSessionsAsync` 刷新后清空勾选的既有行为，避免残留选择。

### 5.3 测试

- `tests/primitives/Seeing.Session.Tests/Storage/SessionSummaryAndArchiveTests.cs`：
  - `DeleteAsync` 归档后删除 → 归档文件不存在、`ListArchivedAsync` 为空；
  - 归档后删除 → 组列表/摘要不残留（如现有测试基建允许）。
- `InMemorySessionStore` 归档后删除 → `ListArchivedAsync` 为空。
- WebUI 无组件测试基建，交互靠既有模式复用，不新增 bUnit。

## 6. 风险与边界

- 归档文件删除失败（文件被占用）时，UI 仍提示成功但列表可能残留——本次按「记日志、尽力删除」处理，
  与 `DeleteRefDirectory` 现有策略一致。
- 删除归档组成员时，其余归档成员保持归档；组关系照常保存。
- 归档会话不在活跃缓存中，`RemoveSessionAsync` 的锚点标题回填可能取不到标题（维持原值），无功能影响。

## 7. 留给实现者的决定

- 归档文件删除是否复用 `AtomicFileWrite.TryDelete`（建议复用，保持与其它删除路径一致）；
- 是否顺手把普通视图与归档视图的批量栏文案抽成方法（可选，不为抽象而抽象）。
