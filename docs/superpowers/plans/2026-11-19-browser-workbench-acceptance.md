# 浏览器工作台验收清单（共享宿主 + 统一渲染管线）

对应设计规格：[`docs/superpowers/specs/2026-11-19-browser-workbench-process-and-render-design.md`](../specs/2026-11-19-browser-workbench-process-and-render-design.md) §8（19 条）。

列含义：

- **自动化**：已有测试钉住这条标准（写测试类名）；空 = 没有自动化，只能手工判定。
- **本次证据**：本次改造（任务 B4）实际执行过的内容。`已跑(自动)` / `已跑(桩)` / `未执行`，未执行的一律写明原因。

> 诚实声明：本次执行环境**没有 GUI 浏览器可交互**（无法打开工作台、无法用 DevTools 看消息、无法肉眼判定「文字是否变清晰」），
> 也没有并行启动第二/第三个宿主（另一条并行工作线正在改 `Engine/` 与测试辅助）。
> 因此**所有视觉类标准都标为未执行**，只提交能在无 GUI 下取得真实证据的部分：
> 真实浏览器的帧率/进程用例 + Node 桩驱动**发布版** `browser-workbench.js` 的行为断言。

---

## 一、进程与多实例

| # | 标准 | 自动化（测试） | 本次证据 | 备注 |
|---|------|---------------|---------|------|
| 1 | 桌面 + TUI + WebUI 同时各开不同网页，三路 screencast 各 ≥25fps | `BrowserMultiWindowFrameRateTests`（2 路，真实浏览器） | `已跑(自动)`：2 路同时 ≥25fps 通过（9s）。三路未测 | 三宿主要同时起三套宿主进程，本次未做 |
| 2 | 关闭/强杀一个 UI 实例，其他页面继续可用、帧率不掉 | — | `未执行` | 需两个 UI 实例；仅有「两页同时出帧」的等价证据（第 1 条） |
| 3 | 客户端断开不关闭浏览器；第二客户端仍能连 | `BrowserLifetimeTests`、`BrowserEngineConnectTests` | `已跑(自动)`：48 项通过 | |
| 4 | 强杀宿主，5 秒内 Chromium 整棵树退出；下次调用自动重拉 | `BrowserProcessHostTests`（Job/退出） | `已跑(自动)`：14 项通过（真实浏览器，`SEEING_BROWSER_TESTS=1`） | 手工 `Get-Process msedge` 未做 |
| 5 | 浏览器崩溃 → 宿主退出 → 客户端重拉成功 | `BrowserProcessHostTests`、`BrowserHostSmokeTests` | `已跑(自动)` | |
| 6 | 同一 profile 一个宿主；不同 profile 各自独立 | `BrowserHostLocatorTests`、`BrowserProfileLockRecoveryTests` | `已跑(自动)` | |
| 7 | UI 常开不用浏览器 → 锁释放 → 超 `HostIdleSeconds` 宿主退出；登录态保留 | `BrowserLifetimeTests`（空闲判定） | `部分` | 未真等一个空闲周期；可把 `Browser.HostIdleSeconds` 降到 20s 手工复核 |
| 8 | 客户端崩溃 → 锁释放、页面被回收，无孤儿 | `BrowserClientReclaimerTests`（真实宿主 + 手工造 lock/pages.json，双向断言） | `已跑(自动)`：死客户端 target 被关、活客户端 target 与锁文件不受影响 | 用「无主锁文件」模拟崩溃客户端，未真的杀 UI 进程 |
| 9 | 迁移：旧 PID 存活 → 拒绝；已死 → 清理并启动 | `BrowserHostLocatorTests` | `已跑(自动)` | |
| 10 | 版本不匹配 → 明确提示重启浏览器 | `BrowserHostLocatorTests`、`BrowserRestartSignatureTests` | `已跑(自动)` | |
| 11 | 宿主日志含启动参数、endpoint、连接、退出原因 | `BrowserHostSmokeTests`、`BrowserProcessHostTests` | `已跑(自动)` | |

## 二、渲染

| # | 标准 | 自动化（测试） | 本次证据 | 备注 |
|---|------|---------------|---------|------|
| 12 | 桌面与 WebUI 行为一致 | — | `未执行` | 桌面宿主未起 |
| 13 | 静止 300ms 后画布尺寸 = `round(viewportWidth × frameScale)` | 协议侧 `BrowserFrameProtocolTests`、`BrowserViewportResolutionTests`；前端侧 Node 桩 | `已跑(自动)` + `已跑(桩)`：`800×1.25 / 600×1.25 → 1000×750` | 真浏览器的 `canvas.width` 需 DevTools 肉眼确认（未做） |
| 14 | 滚动后 still 帧内容与当前视口一致 | `BrowserScreencastPipelineTests`（still 单飞/降级/超时，不含画面内容） | `未执行` | 内容一致性只能肉眼或像素比对，需要 GUI |
| 15 | 流重建（epoch）后画面继续更新 | `BrowserScreencastPipelineTests.RestartingStream_ShouldIncreaseEpochAndKeepStateAndFrameHeaderInSync`；前端侧 Node 桩 | `已跑(自动)`：重建后 epoch 严格变大，`state.epoch` 与下一帧帧头一致（同源）；前端「epoch 变化后 seq=1 仍被接受」由桩覆盖 | 真浏览器里拖面板触发重启仍未做（mock CDP 帧） |
| 16 | 拖拽面板无跳变/闪烁/黑帧 | — | `未执行` | 视觉判定，需要 GUI；代码层面「backing 只在变化时赋值」由第 13 条断言覆盖 |
| 17 | 帧带宽下降 ≥25%；滚动 ≥25fps | `BrowserMultiWindowFrameRateTests`（帧率下限） | `部分`：帧率 ≥25fps 已通过；带宽未做新旧对比 | 结构性收益成立：base64 去掉（-25%）+ 每帧少一层 JSON 包装 |
| 18 | still 失败 2s 内清在途，之后仍能请求 | 前端侧 Node 桩 | `已跑(桩)`：不回应静帧，2.2s 后自动补发一次（rev 更新） | |
| 19 | 同一 page 双流各自出帧（不是单例）；产品约束仍为每客户端只开自己的流 | `BrowserMultiWindowFrameRateTests`（两页两路） | `已跑(自动)`：两路均 ≥25fps | |

---

## 已有自动化证据（本次已实际运行）

```
dotnet vstest ...Browser.Tests.dll --TestCaseFilter:"BrowserFrameProtocolTests|BrowserViewportResolutionTests|BrowserScreencastPipelineTests"
  已通过: 25
dotnet vstest ... --TestCaseFilter:"BrowserLifetimeTests|BrowserProcessHostTests|BrowserHostLocatorTests|BrowserOptionsBindingTests|BrowserPageFactoryTests"
  已通过: 48，已跳过: 1（ProcessHost 真实浏览器用例）
SEEING_BROWSER_TESTS=1 dotnet vstest ... --TestCaseFilter:"BrowserMultiWindowFrameRateTests"
  已通过: 1（9s，两路各 ≥25fps）
SEEING_BROWSER_TESTS=1 dotnet vstest ... --TestCaseFilter:"BrowserProcessHostTests"
  已通过: 14
```

**Node 桩（22 项断言，驱动发布版 `browser-workbench.js`）**：
二进制头解析、坏帧（长度/版本/种类/头不完整）拒收、重复/回退流帧丢弃、epoch 重置、
静帧 `rev` 仲裁（`rev == lastFlowSeq` 绘制 1:1、`rev < lastFlowSeq` 丢弃且不推进 `lastFlowSeq`）、
`backing = round(viewport × frameScale)`、帧缩放到 backing、`contain` 显示尺寸、
静帧 200ms 去抖、在途不重发、过期静帧触发补请求、超时 2s 后补一次、`highres` 不请求静帧、
`resize` 只发面板尺寸 + dpr。

---

## 待补的手工验收（需要 GUI）

1. `dotnet run --project samples/Seeing.Agent.WebUI`，打开工作台 → 浏览器面板，输入网址。
2. DevTools → Network → WS → 该连接 → Messages：
   - 画面帧必须是 **Binary**（旧版是 Text + JSON，前端会 `JSON.parse` 失败并丢弃）；
   - 静止 200ms~1s 后出现一条 `{type:'hires', rev:N}` 上行；
   - 滚动时上行只有 `wheel`，静帧只应在**停下后**出现一次。
3. DevTools → Elements → canvas：`width/height` = `round(viewportWidth × frameScale)`；
   拖动面板时 CSS 尺寸连续变化，而 `width/height` 只在视口真正变化时跳一次（不逐帧变）。
4. 滚到长页面中部停手：文字应从「略糊」变清晰，内容与滚动位置一致。
5. 关闭工作台标签再打开：画面继续更新，不需要重启浏览器。
6. 125% DPI：点击按钮/滑块命中位置正确（无 1.25 倍偏移）。
7. `Browser.HighResStream=true` 重启浏览器：`state.mode=highres`、上行没有 `hires`、画面为物理像素。
8. 两个 WebUI 端口各开不同页面：两路都持续出帧，互不顶掉。
