# WebUI 排队条交互与外观

**日期:** 2026-11-17  
**状态:** 已定稿（方案 B · 紧凑排队条）

## 问题

1. 会话运行中用户消息进入排队；输入框已空时再按 Enter，仍会插入一条重复排队消息，且无法撤销。
2. 现有虚线 chip 条 + 黄色「中断并执行」条视觉粗糙，缺少单条/批量撤销与「发送到截止点」能力。

## 决策

| 项 | 选择 |
|---|---|
| UI 形态 | 紧凑卡片条（方案 B），替换虚线 chip + 黄条 |
| 空提交 | 无文本且无附件 → 不 `DeliverAsync`、不 Toast |
| 单条撤销 | 每行 `×` → 按 messageId 丢弃该条未读 |
| 发送到此 | 该条及之前立刻中断并消费；之后继续排队等下一轮 |
| 全部撤销 | 现有 `DiscardPendingAsync` |
| 中断并发送 | 现有 `InterruptAsync`（整队，等于今天的「中断并执行」） |
| 说明 tip | 不常驻；界面只留控件 |

## 外观

- 顶栏：`排队中 · N` +「全部撤销」+「中断并发送」
- 行：序号圆点 · 单行截断摘要（完整文案 `title`）·「发送到此」· `×`
- 无时钟图标、无虚线底、无黄条、无说明文案
- 仅在有待处理排队时显示（位于输入区上方）

## 架构

```
PendingQueueBar (WebUI，SessionWindow 输入区上方)
  ×              → ISessionRuntime.DiscardPendingMessageAsync(sessionId, messageId)
  全部撤销        → ISessionRuntime.DiscardPendingAsync(sessionId)
  中断并发送      → ISessionRuntime.InterruptAsync(sessionId)
  发送到此        → ISessionRuntime.InterruptThroughAsync(sessionId, throughMessageId)
```

### Runtime 语义

- **单删 / 全部撤销**：与消费循环同锁，**从会话物理删除**未读消息（不留 `consumed` 残骸），不启动执行；刷新 `PendingInputCount`。任意宿主（WebUI/CLI）与 LLM 历史都看不到这些消息；时间线对已删 id 做 pending 修剪。
- **发送到此**（`InterruptThroughAsync`）：
  1. 校验 `throughMessageId` 属于当前未读排队，否则 no-op；
  2. 将截止点之后的未读打上搁置元数据 `inbox.held=true`（`IsPendingInboxMessage` 视其为不可消费）；
  3. 若有活跃执行则硬停当前轮（同 `InterruptAsync`）；若已空闲则只唤醒/确保消费循环；
  4. 本轮只消费未搁置未读（≤ 截止点）；
  5. 本批取走并标记 consumed 后，清除其余消息的 `inbox.held`，使其回到普通排队，等下一空闲轮。
- **空提交**：`PromptInput` / `OnSubmitMessage` 双闸；修掉空框仍入队的实际路径（残留状态或重复回调）。

投影仍走现有 `MessageTimelineStore.PendingMessages`；操作后 `ReconcileTimelineFromSession`。

## 非目标

- 不编辑排队正文、不拖拽排序、不做相同内容去重
- 其他宿主（Tui / Gateway）不跟本轮 UI；契约先落在 `ISessionRuntime`，供日后复用

## 测试

- 空文本无附件不落盘、不增 Pending
- 单删后 Pending 计数与列表一致
- 发送到此：只消费 ≤ 截止点；之后仍 pending
- 全部撤销清空；中断并发送消费整队
- InterruptThrough 后下一空闲轮能消费曾被搁置的消息
