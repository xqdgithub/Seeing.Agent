# 定时任务出站推送到 Gateway Channel（QQ）— 设计方案

**日期:** 2026-07-19  
**状态:** 已实施（方案 B）  
**背景:** 定时任务 `dispatch.target.channel=qq` 已配置，执行 success，但仅写入本地 Session；QQ 群收不到。根因是 **Scheduler 与 QQ HTTP 客户端分属不同进程**，且现有 Dispatcher 不会按 channel 出站。  
**约束:** 默认不 git commit；不改 QQ 入站主路径；先做 QQ MVP，企微可复用同一协议。

---

## 1. 目标

定时任务（`taskType=text|agent`）在配置了 `dispatch.target` 后，结果能：

1. 继续写入目标 Session（本地可查）  
2. **主动推送到对应 Channel 远端**（首期：QQ 群 / C2C）

### 非目标

- 不在本变更实现企微出站（协议预留即可）  
- 不改 Cron / jobs.json schema（沿用现有 `dispatch.target`）  
- 不做富媒体定时推送（首期仅文本）  
- 不依赖「用户刚发过消息」的被动 `msg_id` 窗口（允许无 `msg_id` 的主动发送）

---

## 2. 现状与约束

| 进程 | 持有能力 |
|------|----------|
| WebUI（Scheduler + Gateway Server） | `IScheduledJobDispatcher`、`SessionManager`、`GatewayConnectionManager` |
| Channel Host（QQ） | `QQHttpApiClient`、QQ WS 入站、`IGatewayConnection` 客户端 |

`GatewayScheduleDispatcher` 今日行为：写 Session + `PushEvent` 给 **已 Subscribe 该 session 的 WS 连接**。  
QQ Channel **不会**常驻 Subscribe 定时任务会话 → 事件无人接收；且即使收到，也未映射为 QQ `SendText`。

因此：**不能在 WebUI 进程内直接调 QQ API**；必须经 Gateway↔Channel Host 协议把出站请求交给 QQ 进程。

---

## 3. 方案对比

| | A. WebUI 直连 QQ API | B. Gateway `ChannelOutbound` 帧（推荐） | C. Channel 常驻 Subscribe Session |
|--|--|--|--|
| 做法 | Scheduler 读 qq.json 自己发 | Dispatcher 发 WS 帧；Channel Host 收帧后 `SendText` | Channel 对每个 qq session 长订，把 ChatEvent 转 QQ |
| 进程边界 | 破坏「凭证只在 Channel」 | 符合现有拆分 | 勉强符合 |
| 复杂度 | 低但架构错 | 中 | 高（订阅生命周期难） |
| 多 Channel | 差 | 好（按 channelId 路由） | 一般 |
| 与现配置 | 可用 | **完全兼容** `dispatch.target.channel/sessionId` | 需额外订阅策略 |

**推荐 B。**

---

## 4. 推荐架构

```
AgentJob / HeartbeatJob
    │
    ▼
IScheduledJobDispatcher（最终以 Gateway 侧为准）
    │
    ├─ 1. EnsureSession + AddMessage（本地）
    ├─ 2. PushEvent ChatEvent（可选：WebUI 订阅可见）
    └─ 3. 若 request.Channel 非空
           → GatewayConnectionManager.PushChannelOutbound(channel, payload)
                │
                ▼
           已 RegisterChannel(channelId) 的 WS 连接（QQ Channel Host）
                │
                ▼
           QQChannelOutboundHandler
                │  sessionId → ParsedQQMessage（反解）
                └─ QQHttpApiClient.SendTextAsync（无 msg_id 亦可）
```

### 4.1 协议扩展

新增 `GatewayWsFrameType.ChannelOutbound`（名称可定为 `channel.outbound`）。

**Server → Channel Host（单向推送，可带 requestId 供后续 ACK，MVP 可无 ACK）：**

```json
{
  "type": "ChannelOutbound",
  "id": "optional-request-id",
  "payload": {
    "channel": "qq",
    "sessionId": "qq_group_CF4097...",
    "text": "要学习了",
    "source": "scheduler.cron",
    "jobId": "job-6fc18292",
    "userId": null
  }
}
```

**Channel Host → Server（连接后注册，MVP 必做）：**

```json
{
  "type": "ChannelRegister",
  "payload": { "channel": "qq" }
}
```

`GatewayConnectionManager` 增加：

- `RegisterChannel(connectionId, channelId)`
- `PushChannelOutbound(channelId, payload)` → 发给该 channel 的全部注册连接（通常 1 个）

无注册连接时：打 Warning，Dispatch 仍算 Session 写入成功，但 `DispatchResult` 可带 `partial` 信息或记日志（MVP：日志 Warning + Success，避免任务反复失败；可选后续改为 Fail）。

**决策（明确）：MVP 在无 Channel 在线时：Session 写入成功；出站失败只 Warning，不把整次 Job 标为 Fail。** 配置里可后续加 `dispatch.requireChannel`（本版不做）。

### 4.2 SessionId → QQ 目标反解

利用现有 `QQSessionResolver` 约定（无需先改 Session.Context）：

| sessionId 形态 | MessageType | 目标字段 |
|----------------|-------------|----------|
| `qq_group_{groupOpenId}` | group | GroupOpenId |
| `qq_group_{groupOpenId}_{member}` | group | GroupOpenId + SenderOpenId |
| `qq_{senderOpenId}` | c2c | SenderOpenId |
| `qq_channel_{channelId}` | guild | ChannelId |

实现：`QQSessionTargetParser.TryParse(sessionId, out ParsedQQMessage stub)`（`MsgId` 置空）。

主动发送：现有 `BuildTextMessageBody` 在无 `msg_id` 时不写入该字段，符合主动消息路径。

**局限：** 若未来 sessionId 被 Rotate（`GenerateRotatedSessionId`）带时间后缀，反解需剥离后缀或改存 Context。  
**MVP：** 仅支持标准前缀、未 rotate 的 id（用户当前 `qq_group_CF40…` 可用）。Rotate 会话出站列入后续。

### 4.3 Dispatcher 组合与 DI

WebUI 当前顺序：

1. `AddSeeingScheduler()` → 注册 `Session + Log` Composite  
2. `AddSeeingGatewayServer()` → **覆盖**为仅 `GatewayScheduleDispatcher`

保持 Gateway 覆盖策略，但增强 `GatewayScheduleDispatcher`：

1. 写 Session（不变）  
2. `PushEvent`（不变，供 WebUI）  
3. **新增：** 若 `request.Channel` 非空 → `PushChannelOutbound`

`request.Channel` 为空：行为与今日一致（仅 Session + 可选 WS session 订阅）。

`AddSeeingScheduler` 与 Gateway 的覆盖关系在注释中写清，避免再引入双 Dispatcher 分裂。

### 4.4 Channel Host / QQ 侧

1. WS 连接成功后发送 `ChannelRegister { channel: "qq" }`  
2. 接收循环识别 `ChannelOutbound`：  
   - `payload.channel` 必须为 `qq`（否则忽略）  
   - `TryParse(sessionId)` 失败 → 打 Error 日志  
   - 成功 → `SendTextAsync(stub, text)`（走现有 fallback）  
3. 不把出站文本再次 Submit 给 Agent（避免环）

可选：出站成功后也可往 Session 写一条 system 标记（**MVP 不做**，Scheduler 已写 assistant 消息）。

### 4.5 任务配置契约（用户侧，无变更）

必填（text 任务推 QQ）：

```json
{
  "taskType": "text",
  "text": "要学习了",
  "dispatch": {
    "target": {
      "channel": "qq",
      "sessionId": "qq_group_<group_openid>"
    }
  }
}
```

必填运行时条件：

- Gateway `Enabled`  
- QQ Channel Host 在线且已 Register  
- `qq.json` AppId/Secret 有效  

与现状配置兼容；用户当前 `jobs.json` **无需改字段**。

---

## 5. 错误处理

| 场景 | 行为 |
|------|------|
| Channel 未注册 / 离线 | Warning 日志；Job 仍 Success（Session 已写） |
| sessionId 无法反解 | Error 日志；不出站 |
| QQ API 失败 | Channel Host Error 日志；不重试（MVP）；不反向 Fail Job |
| `channel` 非 qq（如 wecom） | Server 推送后无 handler 或 Host 忽略；Warning |

---

## 6. 测试

| 测试 | 要点 |
|------|------|
| `QQSessionTargetParser` | group / c2c / channel / 非法 id |
| `GatewayConnectionManager` | Register + PushChannelOutbound 路由 |
| `GatewayScheduleDispatcher` | channel 空不推 outbound；channel=qq 时调用 Push |
| QQ Host handler（单测 mock HTTP） | 收到帧后调用 Send |

手工：启 WebUI + QQ Channel → 触发 `job-6fc18292` → 群收到「要学习了」。

---

## 7. 实施顺序

1. 协议：`ChannelRegister` / `ChannelOutbound` 帧 + Manager API  
2. `GatewayScheduleDispatcher` 接出站  
3. `QQSessionTargetParser` + Channel Host 注册与处理  
4. 测试 + 手工验收  

---

## 8. 成功标准

- [x] 协议 `ChannelRegister` / `ChannelOutbound` + Dispatcher / QQ Bridge 已落地
- [x] `QQSessionTargetParser` / InboundParser 单测通过
- [ ] 手工：重启 WebUI + QQ Channel 后，现有 `jobs.json` 群收到定时文本
- [x] Channel 离线时任务仍 Success，且有 Warning 日志路径
- [x] 入站 QQ 对话路径未改主逻辑

---

## 9. 后续（不做）

- `dispatch.requireChannel` 强制失败  
- Rotate sessionId / Context 持久化路由表  
- 出站 ACK / 重试队列  
- WeCom `ChannelOutbound` handler  
- 定时任务富媒体
