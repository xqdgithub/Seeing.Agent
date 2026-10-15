# QQ Channel 能力对齐缺口 — 支持方案

**日期:** 2026-07-19  
**状态:** 草案（待审阅后实施）  
**前置:** MVP + C 档核心（出站图 / 文本 fallback / 引用 / 语音 ASR / WS 稳健性）已落地  
**对照:** QwenPaw `app/channels/qq/`  
**约束:** 不引入 OneBot；不改企微协议；默认不 git commit（除非用户要求）

---

## 1. 目标

在现有 `Seeing.Gateway.QQ` 上补齐与 QwenPaw QQ **仍未对齐**的能力，使官方 Bot Channel 在富媒体出站、Ack、交互卡片扩展、健康检查等方面达到可运营的 C 档完整度。

### 非目标

- 流式逐 token 推送到 QQ（QQ 被动回复模型仍以整段 HTTP 为主）
- Gateway 全通道统一 Outbound ContentPart 大重构（可局部扩展，不为 QQ 单独重做协议）
- 真机联调自动化（保留人工验收清单）

---

## 2. 差距清单（相对 QwenPaw）

| ID | 能力 | QwenPaw | Seeing 现状 | 优先级 | 建议批次 |
|----|------|---------|-------------|--------|----------|
| G1 | C2C/群出站 **视频 / 文件 / 语音** | `file_type` 2/3/4 + upload | 仅图片 `file_type=1` | P0 | A |
| G2 | 出站媒体来源：URL / 本地路径 / base64 | `send_content_parts` | 仅 HTTP(S) URL（文本标记提取） | P0 | A |
| G3 | 文本标记扩展：`[Video:]` / `[File:]` | 有 Image；媒体也可走 parts | 仅 `[Image:]` + markdown 图 | P0 | A |
| G4 | 频道/DM **本地** `file_image` multipart | `_send_guild_image_file_async` | 仅 `image` URL | P1 | B |
| G5 | Ack **异步化**（不阻塞入站处理） | `_schedule_ack` fire-and-forget | 同步 `await SendText` | P1 | B |
| G6 | 卡片 **分发器**（可注册多种 card） | `QQCardHandler` registry | 仅硬编码权限键盘 | P1 | B |
| G7 | Channel **health** | `health_check()` | 仅 `IsConnected` | P2 | C |
| G8 | 入站 video 独立类型 | `VideoContent` | video → `GatewayFileContentPart` | P2 | C |
| G9 | Gateway 出站结构化媒体事件 | parts 直出 | 仅累积文本 | P2 | C（可选） |
| G10 | 真机验收矩阵 | 人工 | 未做 | P0 验收 | D |

---

## 3. 决策

| 议题 | 选择 | 理由 |
|------|------|------|
| 出站媒体入口 | **双轨**：① 文本标记抽取（兼容当前 Agent 纯文本终态）；② `SendMediaAsync` API 供 Bridge/卡片直接调用 | Gateway 终态仍以文本为主；不强行等协议升级 |
| 本地/data URL | C2C/群：读文件或 decode → `file_data` base64 上传；频道：multipart `file_image` | 对齐 QwenPaw |
| 音频 file_type | `.amr/.silk/.slk` → `3`（voice）；其它音频 → `4`（file） | 对齐 QQ 语义 |
| 卡片扩展 | 抽出 `IQQCardKind` + `QQCardDispatcher`；现有权限卡迁入为首个 Kind | 避免再硬编码；为后续审批卡留钩子 |
| Health | `IChannelHealth`（可选实现）+ ChannelHost/诊断日志；**不**强制改 `IChannelBridge` 破坏 WeCom | 最小侵入 |
| Gateway Video part | **可选**：新增 `GatewayVideoContentPart`；未做前 video 继续落 File | 避免阻塞 A/B |
| Ack | 入站后 `Task.Run`/`_ = Send…` + 异常仅 Debug | 缩短感知延迟，失败不影响主路径 |

---

## 4. 架构（目标态）

```
Agent 终态文本
    │
    ▼
QQOutboundComposer
    ├─ Extract markers: Image / Video / File
    ├─ Split text chunks
    └─ Build OutboundPart[] { Text | Media }
            │
            ▼
QQHttpApiClient
    ├─ SendTextWithFallback (已有)
    ├─ UploadMedia (url | file_data) + SendMedia msg_type=7   ← C2C/群
    └─ SendGuildImageUrl | SendGuildImageFile (multipart)     ← 频道/DM

入站:
  Attachments → QQMediaFetcher → GatewayContentPart
  INTERACTION → QQCardDispatcher → IQQCardKind.Handle

运维:
  QQWebSocketClient.IsConnected → IChannelHealth / Host 探针
```

### 关键文件（拟）

| 文件 | 职责 |
|------|------|
| `QQOutboundComposer.cs` | 标记解析、媒体类型推断、分片编排 |
| `QQMediaUpload.cs` / 扩 `QQHttpApiClient` | URL/base64/本地上传、guild file_image |
| `Cards/IQQCardKind.cs` | 卡片契约 |
| `Cards/QQCardDispatcher.cs` | 按 message_type / action prefix 路由 |
| `Cards/PermissionCardKind.cs` | 从现有 `QQPermission*` 迁入 |
| `QQChannelHealth.cs` | 健康快照 |
| `QQChannelBridge.cs` | Ack 异步；Composer；Dispatcher |

---

## 5. 分批交付

### 批次 A — 出站富媒体完整化（P0）

1. `QQOutboundComposer`：解析 `[Image:]` / `[Video:]` / `[File:]` / markdown 图；推断 `file_type`。
2. `UploadMediaAsync` 支持 `url` **或** `file_data`(+`file_name`)。
3. `SendReplyAsync` 对 video/file/audio 走与图片相同的 C2C/群管道。
4. 频道/DM：非图片媒体 → 降级为「链接已省略」文本提示或附文件名说明（QQ 频道无通用 rich-media upload）；图片仍 URL/`file_image`。
5. 单测：标记解析、file_type 映射、upload body 形状。

**完成标准：** Agent 回复含 `[Video: https://…]` / `[File: …]` 时，C2C/群可发出对应 `msg_type=7`；失败有日志且不阻断文本。

### 批次 B — 体验与扩展（P1）

1. 频道/DM 本地图：`multipart/form-data` 字段 `file_image` + 可选 `msg_id`。
2. Ack：配置了 `AckMessage` 时 fire-and-forget，不 `await` 阻塞 Submit。
3. `QQCardDispatcher` + `PermissionCardKind`；Bridge Interaction 只调 Dispatcher。
4. 单测：Dispatcher 前缀路由；Ack 不阻塞（可用同步上下文/计数器测调度）。

**完成标准：** 本地路径图片可在频道发出；权限卡走注册表；Ack 失败不影响主对话。

### 批次 C — 运维与入站细化（P2）

1. `IChannelHealth` / `QQChannelHealth.GetSnapshot()`：`enabled` / `wsConnected` / `lastReadyAt` / `identifyFailCount`。
2. ChannelHost 启动日志或简易 `/health`（若 Host 已有诊断端点则挂接，否则仅日志 + 接口）。
3. 入站 video：优先 `GatewayFileContentPart`（mime video/*）；若引入 `GatewayVideoContentPart` 则同步改 Fetcher。
4. （可选）扩展 `GatewayAssistantReplyCollector` 识别 Message 事件中的媒体 URL 字段——**仅当 Gateway 事件契约已有字段时再做**。

### 批次 D — 真机验收

手工矩阵（见下）；通过后更新 `progress.md` 与 README。

---

## 6. 真机验收矩阵（批次 D）

| # | 场景 | 期望 |
|---|------|------|
| 1 | C2C 纯文本（含 URL） | fallback 后可送达 |
| 2 | C2C `[Image: https]` | 图文分离发送 |
| 3 | 群 `[Video: https]` | 视频气泡 |
| 4 | 群 `[File: https]` + filename | 文件气泡 |
| 5 | 频道 markdown 图 URL | `image` 字段成功 |
| 6 | 频道本地 png（若提供路径） | `file_image` 成功 |
| 7 | 引用消息 | Agent 输入含 `[quoted message:…]` |
| 8 | 语音 + ASR | 文本入站，无需 SILK |
| 9 | 权限键盘 批准/拒绝 | interaction ack + Gateway RespondPermission |
| 10 | 断网重连 / InvalidSession | 可恢复或降级 intent |
| 11 | AckMessage 开启 | 先 Ack 再长任务，Ack 失败不杀主流程 |

---

## 7. 风险与缓解

| 风险 | 缓解 |
|------|------|
| QQ 沙箱与正式对 file_type 行为差异 | 验收同时跑 sandbox；失败降级为文本说明 |
| 大文件超限 | 沿用 `MaxMediaBytes`；超限跳过并记 Warning |
| data: URL 内存峰值 | 单文件上限 + 流式读本地文件再 base64 |
| 卡片注册表与 PermissionState 生命周期 | Kind 无状态；状态仍在 `QQPermissionState` |
| Gateway 无结构化出站媒体 | 批次 A 用标记协议；批次 C 再评估事件扩展 |

---

## 8. 实施计划入口

详细任务拆解见：

`docs/superpowers/plans/2026-07-19-qq-channel-parity-gaps.md`

建议执行顺序：**A → B → D(部分) → C → D(全量)**。批次 C 不阻塞 P0 验收中的 C2C/群富媒体项。
