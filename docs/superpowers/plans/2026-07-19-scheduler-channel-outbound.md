# Scheduler Channel Outbound Implementation Plan

> **For agentic workers:** 按任务顺序实施。用户约束：**不要 git commit**。

**Goal:** 定时任务 `dispatch.target.channel=qq` 时，经 Gateway WS 推送到 QQ Channel Host 并主动发群/私聊消息。

**Architecture:** `GatewayScheduleDispatcher` → `PushChannelOutbound` → 已 `ChannelRegister` 的 Channel Host → `QQSessionTargetParser` + `SendTextAsync`。

**Tech Stack:** .NET 10, Gateway WS 协议, QQ HTTP API

**Spec:** `docs/superpowers/specs/2026-07-19-scheduler-channel-outbound-design.md`

## Global Constraints

- 不改 jobs.json schema
- Channel 离线：Session 仍成功，出站 Warning
- 无 msg_id 主动发送
- 不 commit

---

### Task 1: 协议帧与 Payload

**Files:**
- Modify: `src/Seeing.Gateway/Protocol/GatewayWsFrameType.cs`
- Modify: `src/Seeing.Gateway/Protocol/GatewayWsFrame.cs`（如需 payload 记录类；或新建 Models 文件）
- Modify: `src/Seeing.Gateway.Client/GatewayWsFrameReader.cs`（InboundParser）
- Modify: `src/Seeing.Gateway/IGatewayConnection.cs`（GatewayInbound 扩展可选）

- [ ] 增加 `ChannelRegister`、`ChannelOutbound`
- [ ] Payload: `GatewayChannelRegisterPayload`、`GatewayChannelOutboundPayload`
- [ ] 单元测试序列化/解析

---

### Task 2: GatewayConnectionManager + WS Handler

**Files:**
- Modify: `src/Seeing.Agent.Gateway/Core/GatewayConnectionManager.cs`
- Modify: `src/Seeing.Agent.Gateway/Endpoints/GatewayWebSocketHandler.cs`
- Modify: `src/Seeing.Agent.Gateway/Scheduling/GatewayScheduleDispatcher.cs`

- [ ] RegisterChannel / Unregister 清理 / PushChannelOutbound（返回是否投递）
- [ ] Handler 处理 ChannelRegister
- [ ] Dispatcher：Channel 非空时 PushChannelOutbound；无连接 Warning

---

### Task 3: Client 收帧 + QQ 反解与发送

**Files:**
- Modify: `src/Seeing.Gateway.Client/WebSocketGatewayClient.cs`
- Create: `src/Seeing.Gateway.QQ/QQSessionTargetParser.cs`
- Modify: `src/Seeing.Gateway.QQ/QQChannelBridge.cs`
- Test: parser + dispatcher 相关测试

- [ ] Connect 后发 ChannelRegister
- [ ] ReceiveLoop 对 ChannelOutbound 触发回调（不进 Subscribe 队列抢占）
- [ ] Bridge 处理回调 → Parse → SendTextAsync

---

### Task 4: 回归

- [ ] 相关测试通过
- [ ] grep / 编译 Gateway + QQ + Client
- [ ] 更新 spec 状态

---
