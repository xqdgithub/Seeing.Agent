# QQ Channel 能力对齐缺口 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 补齐 Seeing.Gateway.QQ 相对 QwenPaw 仍未对齐的出站富媒体、Ack 异步、卡片分发器、频道本地图与健康检查。

**Architecture:** 在现有 `QQHttpApiClient` / Bridge 上增加 `QQOutboundComposer`（标记→媒体编排）与可注册的 `QQCardDispatcher`；C2C/群统一 upload+`msg_type=7`；频道图片走 URL 或 `file_image` multipart；Ack 改为 fire-and-forget。

**Tech Stack:** .NET 10、xUnit、现有 Seeing.Gateway.QQ、对照 QwenPaw `qq/channel.py` + `qq/cards/`。

**Spec:** `docs/superpowers/specs/2026-07-19-qq-channel-parity-gaps-design.md`

## Global Constraints

- 不做 OneBot；不改企微协议。
- **禁止 git commit / push**，除非用户明确要求。
- 保持已修复的文本协议：C2C/群 `msg_type`+`msg_seq`；频道/DM 无 seq；Markdown 无顶层 `content`。
- 出站媒体默认入口仍为终态**文本标记**（Gateway 尚未普遍下发结构化媒体事件）。
- 音频：`.amr/.silk/.slk` → `file_type=3`；其它音频 → `4`；图片 `1`；视频 `2`；文件 `4`。
- 单测放在 `tests/Seeing.Gateway.QQ.Tests`；改完跑 `dotnet test tests/Seeing.Gateway.QQ.Tests`。

---

## File map

| 文件 | 职责 |
|------|------|
| `src/Seeing.Gateway.QQ/QQOutboundComposer.cs` | 新建：标记解析 + 出站部件列表 |
| `src/Seeing.Gateway.QQ/QQTextSanitizer.cs` | 扩展 Video/File 标记；或委托 Composer |
| `src/Seeing.Gateway.QQ/Connection/QQHttpApiClient.cs` | file_data 上传、多 file_type、guild multipart |
| `src/Seeing.Gateway.QQ/Cards/IQQCardKind.cs` | 新建 |
| `src/Seeing.Gateway.QQ/Cards/QQCardDispatcher.cs` | 新建 |
| `src/Seeing.Gateway.QQ/Cards/PermissionCardKind.cs` | 迁入权限卡 |
| `src/Seeing.Gateway.QQ/QQPermission.cs` | 瘦身：策略/状态保留，键盘构建迁 Kind |
| `src/Seeing.Gateway.QQ/QQChannelBridge.cs` | Composer、Ack 异步、Dispatcher |
| `src/Seeing.Gateway.QQ/QQChannelHealth.cs` | 新建（批次 C） |
| `src/Seeing.Gateway.QQ/Connection/QQWebSocketClient.cs` | 暴露健康字段（批次 C） |
| `tests/Seeing.Gateway.QQ.Tests/*` | 各任务测试 |

---

## 批次 A — 出站富媒体（P0）

### Task 1: QQOutboundComposer（标记 → 部件）

**Files:**
- Create: `src/Seeing.Gateway.QQ/QQOutboundComposer.cs`
- Modify: `src/Seeing.Gateway.QQ/QQTextSanitizer.cs`（Image 逻辑可复用或内聚到 Composer）
- Test: `tests/Seeing.Gateway.QQ.Tests/QQOutboundComposerTests.cs`

**Models（建议）：**

```csharp
public enum QQOutboundMediaKind { Image = 1, Video = 2, Voice = 3, File = 4 }

public sealed record QQOutboundMediaItem(
    QQOutboundMediaKind Kind,
    string Source,          // http(s) | file path | data: url
    string? FileName = null);

public sealed record QQOutboundPayload(
    string CleanText,
    IReadOnlyList<QQOutboundMediaItem> Media);
```

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Compose_ShouldExtractImageVideoFileMarkers()
{
    var raw = "说明 [Image: https://a/x.png] [Video: https://a/v.mp4] [File: https://a/d.pdf]";
    var payload = QQOutboundComposer.Compose(raw);
    payload.Media.Should().HaveCount(3);
    payload.Media[0].Kind.Should().Be(QQOutboundMediaKind.Image);
    payload.Media[1].Kind.Should().Be(QQOutboundMediaKind.Video);
    payload.Media[2].Kind.Should().Be(QQOutboundMediaKind.File);
    payload.CleanText.Should().Contain("说明");
    payload.CleanText.Should().NotContain("https://");
}

[Fact]
public void ResolveFileType_VoiceExt_ShouldBeAudio()
{
    QQOutboundComposer.ResolveMediaType(QQOutboundMediaKind.File, "a.silk")
        .Should().Be(3);
}
```

- [ ] **Step 2: 运行测试确认失败**

```bash
dotnet test tests/Seeing.Gateway.QQ.Tests --filter QQOutboundComposer
```

- [ ] **Step 3: 实现 Composer（Image/Video/File 标记 + markdown 图；ResolveMediaType）**
- [ ] **Step 4: 测试通过**
- [ ] **Step 5: 不 commit（除非用户要求）**

### Task 2: Upload 支持 file_data + 多 file_type 发送

**Files:**
- Modify: `src/Seeing.Gateway.QQ/Connection/QQHttpApiClient.cs`
- Test: `tests/Seeing.Gateway.QQ.Tests/QQMediaUploadBodyTests.cs`（纯函数测 body 构造更佳）

- [ ] **Step 1: 抽出 `BuildUploadMediaBody(fileType, url?, fileData?, fileName?)` 并写测试**

```csharp
[Fact]
public void BuildUploadMediaBody_UrlMode_ShouldSetUrl()
{
    var body = QQHttpApiClient.BuildUploadMediaBody(1, url: "https://x", fileData: null, fileName: null);
    body["file_type"].Should().Be(1);
    body["url"].Should().Be("https://x");
    body["srv_send_msg"].Should().Be(false);
}

[Fact]
public void BuildUploadMediaBody_DataMode_ShouldSetFileData()
{
    var body = QQHttpApiClient.BuildUploadMediaBody(4, url: null, fileData: "YWJj", fileName: "a.bin");
    body["file_data"].Should().Be("YWJj");
    body["file_name"].Should().Be("a.bin");
    body.Should().NotContainKey("url");
}
```

- [ ] **Step 2: 实现 Upload（URL 或读本地/data URL→base64）；`SendImagesAsync` 泛化为 `SendMediaItemsAsync`**
- [ ] **Step 3: `SendReplyAsync` 改用 `QQOutboundComposer.Compose`，按 Media 列表发送**
- [ ] **Step 4: 频道/DM：仅 Image 走现有路径；Video/File 追加一行提示文本到 cleanText（或单独再发一条说明），避免静默丢弃**
- [ ] **Step 5: `dotnet test tests/Seeing.Gateway.QQ.Tests` 全绿

---

## 批次 B — Ack / 本地图 / 卡片分发（P1）

### Task 3: 频道/DM `file_image` multipart

**Files:**
- Modify: `QQHttpApiClient.cs` — `SendGuildImageFileAsync(target, filePath, msgId, ct)`
- Test: 可用 `HttpMessageHandler` stub 断言 `MultipartFormDataContent` 含 `file_image`（若成本高，则测「路径存在时选择 file 分支」的编排单元）

- [ ] **Step 1: 实现 multipart POST（Authorization: QQBot；字段 `file_image`、可选 `msg_id`）**
- [ ] **Step 2: `SendMediaItemsAsync`：guild/dm + 本地路径图片 → file_image；http(s) → `image` 字段**
- [ ] **Step 3: 测试或手工清单项写入 spec 验收表

### Task 4: Ack 异步化

**Files:**
- Modify: `QQChannelBridge.cs`

- [ ] **Step 1: 将 Ack 块改为：**

```csharp
if (!string.IsNullOrWhiteSpace(_options.AckMessage))
{
    var ackTarget = parsed;
    var ackText = _options.AckMessage!;
    _ = Task.Run(async () =>
    {
        try
        {
            await _qqHttp.SendTextAsync(ackTarget, ackText, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QQ ack message failed");
        }
    });
}
```

- [ ] **Step 2: 确认主路径立即 `SubmitAsync`，不等待 Ack**
- [ ] **Step 3: 跑现有 QQ 测试（行为无断言则仅回归）**

### Task 5: QQCardDispatcher

**Files:**
- Create: `Cards/IQQCardKind.cs`, `Cards/QQCardDispatcher.cs`, `Cards/PermissionCardKind.cs`
- Modify: `QQPermission.cs`, `QQChannelBridge.cs`, `Extensions/QQServiceCollectionExtensions.cs`

**接口草图：**

```csharp
public interface IQQCardKind
{
    string Name { get; }
    string ActionDataPrefix { get; }
    Task<bool> TryHandleInteractionAsync(JsonElement d, CancellationToken ct);
}

// 出站权限卡仍由 Bridge 在 Permission 事件时调用 PermissionCardKind.SendPromptAsync(...)
```

- [ ] **Step 1: 测试 Dispatcher 按 prefix 路由 allow/deny**
- [ ] **Step 2: 迁入 PermissionCardKind；Bridge Interaction 只调 Dispatcher**
- [ ] **Step 3: DI 注册；全测试通过**

---

## 批次 C — Health / 入站 video（P2）

### Task 6: Health 快照

**Files:**
- Create: `QQChannelHealth.cs`
- Modify: `QQWebSocketClient.cs`（`LastReadyAt`, `IdentifyFailCount` 只读属性）
- Modify: ChannelHost（若有）启动后 Debug 打快照；或实现可选 `IChannelHealth`

- [ ] **Step 1: 定义 `QQChannelHealthSnapshot` record**
- [ ] **Step 2: WS 在 READY 时更新时间戳**
- [ ] **Step 3: 单测可用假时钟/直接设属性（若需 InternalsVisibleTo）**

### Task 7: 入站 video mime → 更明确的 File/未来 Video part

**Files:**
- Modify: `QQMediaFetcher` — `video/*` 明确走 File（Name 保留）；文档注明可选 GatewayVideo

- [ ] **Step 1: 测试 content_type=video/mp4 生成 File part 且 mime 正确**
- [ ] **Step 2: 实现与测试通过**

---

## 批次 D — 验收

### Task 8: 真机矩阵与文档

**Files:**
- Modify: `.superpowers/sdd/progress.md`
- Modify: `src/Seeing.Gateway.QQ` README（若有）或 Gateway README QQ 节
- Spec 验收表勾选

- [ ] **Step 1: 按 spec §6 矩阵在 sandbox/正式各跑一轮**
- [ ] **Step 2: 记录失败项与降级行为**
- [ ] **Step 3: 更新 progress ledger**

---

## 执行顺序建议

1. Task 1 → 2（批次 A，可合并一个工作单元）
2. Task 3 → 4 → 5（批次 B）
3. 穿插 Task 8 中与 A/B 相关的真机项
4. Task 6 → 7（批次 C）
5. Task 8 收尾

每任务结束后：`dotnet test tests/Seeing.Gateway.QQ.Tests`。
