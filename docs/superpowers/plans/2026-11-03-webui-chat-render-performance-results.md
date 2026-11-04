# WebUI 聊天渲染性能 · 实测记录

> 配套计划：`docs/superpowers/plans/2026-11-03-webui-chat-render-performance.md`
> 本文件用于回填改造前后的性能对照数据。

## 采集口径

**这不是自动化基准**，是人工可复现的对照实验。所有数值都在同一台机器、同一份会话数据、同一次运行会话内采集。

### 测试语料

| 代号 | 构造方式 |
|---|---|
| **会话 A（长会话）** | 500+ 消息，其中 ≥3 轮包含多步工具调用（同一 Loop 内 step 0/1/2），最后一条 assistant 回答 ≥3000 字符 |
| **会话 B（多 agent 组）** | 1 个主会话 + 3 个子会话，让 3 个子会话同时执行 |

### 打点方式

在 `SessionWindow.razor` 的 `OnAfterRender` 里（临时改动，采集后移除）：

```csharp
protected override void OnAfterRender(bool firstRender)
{
    if (firstRender && _switchStopwatch is not null)
    {
        _switchStopwatch.Stop();
        Console.WriteLine($"[PERF] first-render: {_switchStopwatch.ElapsedMilliseconds}ms, session={SessionId}, mode={ViewMode}");
        _switchStopwatch = null;
    }
}
```

在 `LoadSessionByIdAsync` / `LoadSummaryOnlyAsync` 入口 `_switchStopwatch = Stopwatch.StartNew();`。

流式期渲染通道数：在 `OnAfterRender`（非 firstRender）里累加计数器，每秒 `Console.WriteLine` 一次。

### 采集矩阵

每组 10 次，记录 P50 / P90。

| # | 场景 | 指标 | 改造前 | 改造后 | 变化 |
|---|---|---|---|---|---|
| 1 | 会话 A 冷进入 | `first-render` ms | 待采集 | 待采集 | |
| 2 | 会话 A 流式 10 秒 | 渲染通道数/秒 | 待采集 | 待采集 | |
| 3 | 会话 A 流式 10 秒 | DevTools `Recalculate Style` + `Layout` 次数 | 待采集 | 待采集 | |
| 4 | 会话 A 流式 10 秒 | DevTools >50ms 长任务个数 | 待采集 | 待采集 | |
| 5 | 会话 B 切换 active ×10 | `first-render` ms P50/P90 | 待采集 | 待采集 | |
| 6 | 会话 B 三子会话并发流式 | 主区点击 → 首帧 ms | 待采集 | 待采集 | |
| 7 | 会话 B 切换 ×10 | Summary 窗口 `OnInitializedAsync` 次数 | 待采集 | 待采集 | |

### 延后说明

Task 0 的原始设计是「先采基线再改」，实际执行改为 **A/B 对比**：Task 1 与 Task 2 完成后，用 `git stash` 回到分支起点采集一组，再 `git stash pop` 采集一组，同机同会话对照。原因见 ledger 的 Task 0 Ruling。

已知局限：
- Blazor Server 的 UI 线程在服务端，浏览器 DevTools 看不到 `ResetFromSession` / RenderTree 的 CPU，只能看 SignalR 下发后的合成与 DOM 时间。因此 #3/#4 是**辅助指标**，#1/#5/#6 才是主指标。
- 「响应 < 300ms」不作为验收阈值 —— 它依赖 LAN/远程 circuit、机器性能与消息体量，跨环境不可比。验收用**相对基线下降 ≥50%**（#5）与**无可感知延迟**（#6）。

## 结果

（待回填）
