# cron conversation 目标与出站绑定选择器设计

**日期：** 2026-10-17
**相关模块：** `Seeing.Agent.Scheduler`（DispatchTarget / JobDataKeys / AgentJob / HeartbeatJob）、`Seeing.Agent.Comms`（ChannelDeliveryScheduleDispatcher）、`Seeing.Agent.Abstractions.Channels`（ConversationInfo / IChannelDirectory）、`samples/Seeing.Agent.WebUI`（SessionToolbar / JobEditorModal / ConversationPicker）

---

## 1. 问题

1. **出站绑定手填无来源**：WebUI 会话工具栏「编辑出站绑定」Modal 要求手填 `ChannelId` / `Kind` / `ExternalId` / `Title`，用户完全不知道这些值从哪里获取。而数据其实已在主程序 `IChannelDirectory`（每条 QQ/WeCom 消息经 WS submit 时服务端 `RegisterInbound` 登记；`list_conversations` agent 工具即此数据源）。
2. **cron 推送目标只有会话**：`JobEditorModal` 投递配置「目标 Session」必填（默认 `main`）；`ChannelDeliveryScheduleDispatcher` 按会话 `ChannelRoute` 出站，**会话无绑定时静默跳过**（Debug 日志）——手工建的 cron 会话永远不推送到 QQ/WeCom 对话。

## 2. 决策

- **conversation 是一等投递目标**：`Job.Dispatch.Target` = 会话（**可选**）+ conversation（**可选**）。会话只是执行留档载体，conversation 才是推送路由（延续「对话是路由，sessionId 只是名字」决策）。
- **无会话仅 conversation：每次触发新建会话**。cron 每轮独立上下文，避免粘性会话历史无限膨胀。
- **指定会话无绑定：自动挂上 conversation**（复用 `ConversationInfo.ToSessionRoute()` 语义，等价入站回填）。
- **出站优先级：任务 conversation > 会话 `ChannelRoute`**。
- **选择器与 `list_conversations` 工具同源**：WebUI 注入 `IChannelDirectory`，不新增数据通道。

## 3. 目标矩阵

| SessionId | Conversation | 执行会话 | 出站推送 |
|-----------|--------------|----------|----------|
| 有 | 有 | 指定会话（无 `ChannelRoute` 时自动挂上该 conversation） | 任务 conversation |
| 无 | 有 | **每次触发新建** Root 会话 `cron_{jobId}_{yyyyMMddHHmmss}`（Title `⏰ {jobName} {触发时间}`）+ 挂上 conversation | 任务 conversation |
| 有 | 无 | 指定会话 | 会话 `ChannelRoute`（现状；无绑定跳过） |
| 无 | 无 | `main`（现状） | 会话 `ChannelRoute`（现状） |

`AgentJob` 与 `HeartbeatJob` 共用同一解析逻辑。

## 4. 数据模型与链路变更

### 4.1 Scheduler 模型

- `DispatchTarget`（`SchedulerModels.cs`）：新增 `ConversationSpec? Conversation`（ChannelId / Kind / ExternalId / Title 四字段纯字符串模型，与 `ConversationInfo` 平面镜像——JSON 配置模型不引契约枚举）。
- `JobDataKeys`：新增 4 个平面键 `ConversationChannelId` / `ConversationKind` / `ConversationExternalId` / `ConversationTitle`（Quartz JobDataMap 平面字符串，与 SessionId 同风格）。
- `ScheduleManager`：`BuildJobData` / 回读映射补 conversation 四键（缺省 null）。

### 4.2 触发时解析（AgentJob / HeartbeatJob 共享助手）

```
ResolveTargetAsync(data, sessionId, ct):
    conversation = ReadConversation(data)          // 四键齐（ChannelId+Kind+ExternalId）才生效
    if sessionId 显式配置:
        session = EnsureSessionAsync(sessionId)
        if conversation != null && session.ChannelRoute 为空:
            session.ChannelRoute = conversation.ToSessionRoute()   // 挂上
        return session.Id
    if conversation != null:
        session = CreateSessionAsync($"cron_{jobId}_{yyyyMMddHHmmss}", title: "⏰ {jobName}")
        session.ChannelRoute = conversation.ToSessionRoute()
        return session.Id
    return "main"
```

- 新会话创建经 `ISessionManager.CreateAsync`（Kind=Root）；挂绑后 `SaveAsync` 落盘。
- 仅会话 + conversation 且会话已有其他绑定：**不覆盖**（first-write-wins，与入站回填 `TryBackfillChannelRoute` 一致）。

### 4.3 出站优先级（ChannelDeliveryScheduleDispatcher）

契约层 `DispatchRequest`（`Abstractions.Scheduling`）新增可选强类型 `DispatchConversation`（record：ChannelId / Kind / ExternalId / Title 纯字符串——Scheduler 填充、Comms 消费，不入 Metadata 杂键）。出站目标解析：

```
route = request.Conversation（任务级） ?? session.ChannelRoute（会话级，现状）
```

任务级 conversation 直接构造 `DeliveryTarget`，不再因会话无绑定而跳过。

### 4.4 WebUI 选择器（共享组件）

- 新组件 `Components/Shared/ConversationPicker.razor`：
  - 注入 `IChannelDirectory`，打开时拉取全部 channel 的 `ListConversationsAsync`（跳过 `OptedOut`）；
  - AntDesign `Select` + 搜索（按 Title / ExternalId / ChannelId 过滤）；选项显示 `Title（channelId · kind · ExternalId）`；
  - 选中后经回调把 4 字段回填到宿主表单。
- `SessionToolbar.razor` 出站绑定 Modal：顶部加选择器，选中自动填 4 字段；**保留手填**（目录为空 / 未登记场景兜底）；全部留空仍为清除绑定。
- `JobEditorModal.razor` 投递配置：目标 Session 改**可选**；加 conversation 选择器 + 手填展开；提示语改为目标矩阵。

## 5. 测试

| 套件 | 内容 |
|------|------|
| Scheduler.Tests | 解析矩阵：配置会话+conversation 挂绑（含已有绑定不覆盖）/ 仅 conversation 新建会话+挂绑 / 仅会话 / 都无 main；JobDataMap 四键往返 |
| Comms.Tests | dispatcher 出站优先级：任务 conversation（无会话绑定也推）/ 无任务 conversation 走会话绑定（现状不回归） |
| WebUI | 手工验证：出站绑定点选回填；cron 任务仅选 conversation → 触发后新会话出现且 QQ 收到推送 |

## 6. 已知边界

- **目录数据在主程序进程内存**（启动 `LoadIntoAsync` 载入 + WS `RegisterInbound` 增量）；ChannelHost 与服务端双写同一 JSON 是既有机制，不在本次范围。
- **仅 conversation 的 cron 每次触发新建会话** → 会话列表积累（Title 前缀 `⏰` 识别；用户已确认接受）。
- **conversation 选中后渠道掉线**：出站仍走 `ChannelDeliveryScheduleDispatcher` 既有失败日志（`ChannelUnavailable` 无重试，既有边界）。
- HeartbeatJob 的 `Target`（main/last/inbox）与 conversation 组合：conversation 优先于 last/inbox 的推送目标语义，会话仍按 Target 解析（仅出站目标变更）。
