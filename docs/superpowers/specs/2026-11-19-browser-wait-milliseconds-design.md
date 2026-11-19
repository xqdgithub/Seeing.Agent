# 浏览器等待参数改用毫秒（2026-11-19）

## 意图

`browser_wait_for` 的时长参数改为**毫秒**表达，并修掉「`kind=networkidle` 必然睡满预算」的实现缺陷。
起因是一次真实调用：`{"kind":"networkidle","timeout":15000}` 表现为「一直在等待」。

## 根因（两条，必须同时修）

1. **单位与静默夹取**：`timeout` 名为秒，实际 `Math.Clamp(15000, 1, 300)` = **300 秒**。
   工具又声明了 `timeout.skip`，全局 `ToolExecutionTimeout` 拦不住它。
2. **networkidle 永远判不到空闲**：`TryWaitOnceAsync` 把观察窗口 `IdleTime` 与强制超时
   `Timeout` 都设成同一片长（200ms），两者同时到点，空闲条件无法成立；外层只能靠
   「预算用尽 → 按就绪处理」收场。

实测（真实 Edge + 本地空闲页，同一时刻）：

| 调用 | 实测 | 结果 |
|---|---|---|
| 封装 `WaitForAsync(NetworkIdle, 3s)` | 3013 ms | `BudgetExhausted=true` |
| 原生 `WaitForNetworkIdleAsync(Timeout=3000, IdleTime=500)` | 512 ms | 正常空闲 |

## 方案（已选定：A + timeoutMs）

**A. 一次调用，交给 PuppeteerSharp 原生的 `Timeout` + `CancellationToken`**

- `WaitForSelectorAsync` / `WaitForFunctionAsync` / `WaitForNetworkIdleAsync` 均传
  `Timeout = timeoutMs`、`CancellationToken = ct`（25.x 的 `WaitForOptions` 原生支持，取消会真正中止等待）。
- 删除 200ms 分片、`TryWaitOnceAsync`、以及「取消异常必须排在超时判定之前」的顺序 hack。
- networkidle 固定 `IdleTime = 500`，预算用尽时返回 `ReadyAfterBudget`（上层文案注明）。

被否掉的方案：

- **B. 保留分片，只把 IdleTime 改成 slice/2**：仍有两套超时语义并存，selector/text 每片重建 CDP 等待，精度差。
- **C. 自己轮询 DOM / 统计在飞请求**：重写库已有能力，还要注入脚本，收益为零。

## 参数契约

| 工具 | 参数 | 范围 | 说明 |
|---|---|---|---|
| `browser_wait_for` | `timeoutMs` | 50–120000，默认 30000 | 所有 kind 的预算上限 |
| `browser_wait_for` | `durationMs` | 50–120000，缺省= timeoutMs | 仅 `kind=timeout`，且不得超过 `timeoutMs` |
| `browser_open` | `timeoutMs` | 1000–120000，缺省用 `BrowserOptions.OperationTimeout` | 导航超时 |

- **越界一律失败**，提示单位与范围，不再 `Math.Clamp`。
- 旧字段 `timeout` / `duration` 出现时明确拒绝并给出改名示例（不做静默兼容：有单位的名字才没有二义）。
- 工具能力从 `timeout.skip` 改为 `timeout.budget = 130000`（自身上限 120s + 收尾开销），保留外层兜底。

## 非目标

- 不改 `waitUntil`（导航等待策略）的枚举与语义。
- 不改 `BrowserOptions.DefaultTimeoutSeconds`（配置项，沿用秒；它只作 `browser_open` 未给 `timeoutMs` 时的兜底）。
- 不为 `networkidle` 增加自动重试或自动降级到 `timeout`。

## 验证

- `BrowserWaitForToolTests`：schema 只暴露毫秒字段、越界失败、旧字段被拒、能力改为 `timeout.budget`。
- `BrowserWaitNetworkIdleTests`（真实浏览器）：
  - 空闲页 + 10s 预算 → `BudgetExhausted=false` 且耗时 < 5s；
  - 持续请求页 → `BudgetExhausted=true`。
- 全量 `Seeing.Agent.Tools.Browser.Tests`：301 通过 / 0 失败 / 11 跳过。
