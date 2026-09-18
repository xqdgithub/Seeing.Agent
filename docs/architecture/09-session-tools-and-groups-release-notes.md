# 09 会话工具与会话组 Release Notes

**日期：** 2026-09-18
**分支：** `feature/session-tools-and-groups`
**相关模块：** `Seeing.Session`（会话组）、`Seeing.Agent.Tools.Session`（会话工具：list/search/read/trim/handoff）、WebUI 会话窗口投影

---

## 1. 概要

本次收尾聚焦「会话组 + 会话工具」的并发安全与失败回滚，并补齐以下运维语义：

1. **读 API 快照化**：`ISessionGroupManager` 的公开读（`GetGroupAsync` / `GetGroupForSessionAsync` / `ListMembersAsync` / `GetParentAsync` / `ListChildrenAsync`）一律返回克隆/不可变快照，杜绝与组锁内写者并发遍历 `Members` 导致的 `InvalidOperationException` 与脏读；内部写路径仍持锁操作活对象。
2. **工作区切换清缓存**：`ISessionGroupManager.ClearCache()` 新增；`SessionGroupReloadHandler` 在重定位组存储后清空组缓存与会话→组映射，避免旧工作区组继续命中。
3. **交接幂等与回滚**：`session_handoff` 以源会话 `Metadata["handoff_inflight:{CallId}"]` 做重试预检，同一 CallId 仅创建一个后继、仅提交一次；`SessionGroupManager.CreateHandoffSuccessorAsync` 中途失败自清理，杜绝孤儿会话/组成员。
4. **Minor**：`LoopCompleteEvent.Reason` 透传工具 `TurnDirectiveReason`（无则回退 `turn-directive`）；`session_read` 声明 `output.skip` 并钳制 `max_chars`/`limit`；`session_read`/`session_handoff` 授权与幂等细节修正。

---

## 2. 已知边界（本期容忍，非缺陷）

| # | 边界 | 表现 | 影响 / 规避 |
|---|------|------|-------------|
| 1 | **Fork 不随源删除** | 删除源会话（`RemoveSessionAsync`）仅级联删除 `Relation==Child` 子树；`Relation==Fork` 的分支/备份会话保留并提升为锚点 | 备份语义使然：trim/分支备份需在源删除后仍可取回。调用方如需清理，显式删除分支会话 |
| 2 | **`SessionWindowRegistry.IsExecuting` 恒 false** | WebUI 窗口注册表不持权威执行态，该字段始终返回 `false` | 执行态由各窗口自身 handler 渲染（`SessionEventStreamRouter` / Task 卡片聚合），勿以 registry 字段判定执行中 |
| 3 | **`session_trim` 无跨会话参数** | `ParametersSchema` 仅 `mode` / `message_id` / `from_id` / `to_id`，始终作用于**当前会话** | 跨会话裁剪属后续演进；如需裁剪其他会话，先切换/读取目标会话（`session_read` 可跨组读，受授权约束） |
| 4 | **授权工厂缺失时放行** | `SessionToolBase.AuthorizeAsync` 在 `IPermissionAuthorizerFactory` 未注册时返回 `null`（放行） | 兼容未接入授权链的宿主（Core-only / Embed）；生产宿主须注册授权链以获得跨组审批 |
| 5 | **`Kind.Fork` 移除、`SubAgent=2` 保留** | `SessionKind` 现仅 `Root=0` / `SubAgent=2`；分支/备份/交接关系迁至 `SessionRelation`（`SessionGroup` 承载） | 数值 `2` 保持历史兼容（持久化数据反序列化）；禁止再用 `SessionKind` 表达分支，改用 `SessionRelation.Fork` |
| 6 | **同 CallId 重试的元数据残留** | 交接回滚后源会话 `handoff_inflight:{CallId}` 标记不清理；因目标会话已删除，重试会重新创建 | 幂等预检会校验目标会话是否存在，残留标记不会导致卡死；如需整洁可后续加清理钩子 |

---

## 3. 行为差异（同输入不同结果）

1. **`GetGroupAsync` / `GetGroupForSessionAsync` 返回克隆**：调用方对返回对象 `Members` 的修改不再回写缓存/存储（此前会污染活组）。读语义与 `ListMembersAsync` 对齐。
2. **`ListMembersAsync` / `GetParentAsync` / `ListChildrenAsync` 在组锁内快照**：与并发 `AddMemberAsync` / `RemoveMemberAsync` 竞争时不再抛异常，返回一致快照。
3. **`SessionGroupReloadHandler` 构造签名变更**为 `(ISessionGroupStore, ISessionGroupManager)`；宿主经 DI 自动解析，手工构造需补第二参。
4. **`LoopCompleteEvent.Reason`** 现透传工具给出的原因（如 `handoff`），缺省仍为 `turn-directive`。
5. **`session_read` 的 `max_chars`** 上限为 2000（此前无上限，可被 `ToolOutputLimiterDecorator` 二次截断而丢失续读游标）；`limit` 上限 50。

---

## 4. 兼容性说明

- `ISessionGroupManager` 新增成员 `ClearCache()`：自定义实现需补齐（Moq 桩自动返回默认值）。
- 无其他破坏性契约变更；未引入兼容重载或死代码。
