# LLM 指数退避重试设计

- **日期**：2026-09-18
- **状态**：待用户审阅
- **关联**：
  - `src/abstractions/Seeing.Agent.Abstractions/Llm/ProviderConfig.cs`
  - `src/abstractions/Seeing.Agent.Abstractions/Llm/LlmRetryPolicy.cs`
  - `src/spine/Seeing.Agent.Core/Llm/RetryLlmClientDecorator.cs`
  - `src/spine/Seeing.Agent.Core/Llm/ProviderManager.cs`
  - `src/spine/Seeing.Agent.Core/Llm/ModelConfigManager.cs`
  - `src/spine/Seeing.Agent.Core/Llm/ConfiguredLlmProvider.cs`
- **背景**：连接 LLM 时若出现网络错误，现有重试为固定 3 次、间隔 1s/2s，约 3 秒即放弃；瞬时网络抖动易直接终止对话。需要改为指数退避、单次封顶、总时长预算控制的弹性重试。

---

## 1. 目标 / 非目标

**目标**

1. 退避序列为 `0.5, 1, 2, 4, 8, 10, 10, 10…` 秒：指数增长，单次封顶 10 秒。
2. 默认以"累计重试等待预算 ≈ 120 秒"作为停止条件（约 16 次尝试）。
3. 参数可按 Provider 配置，沿用现有 `.seeing/seeing.json` 热重载管道。
4. 非流式与流式（未产出任何 chunk 前）行为一致。
5. 保留 `max_retries` 作为可选次数上限，非破坏性。

**非目标**

- 不重试确定性错误（4xx/认证/参数错误），沿用 `LlmRetryPolicy.IsRetryable` 判定。
- 不改流式"已产出 chunk 后不再重试"的既有约束。
- 不重试工具调用（Tool 装饰器链另属体系）。
- 不做全局重试 options，不新增 WebUI 配置页字段。
- 不把请求本身耗时计入预算（避免与 5 分钟请求超时耦合）。

---

## 2. 现状

唯一重试落点为 `RetryLlmClientDecorator` 内的私有 `RetryingLlmClient`：

- 延迟：`1s × 2^(attempt-1)`，**无封顶**（`RetryLlmClientDecorator.cs:71-73, 112-114`）。
- 次数：`ProviderConfig.MaxRetries`，默认 3（`ProviderConfig.cs:86`）。
- 判定：`LlmRetryPolicy.IsRetryable` —— `HttpRequestException`（非 400/401/403/404/422）可重试，覆盖连接层网络错误。
- 重试元数据写入 `LlmCallContext.Items`（`retry.attempt` / `retry.willRetry` / `retry.maxRetries`），由 `LlmService.FireChatOnError`（`LlmService.cs:247-262`）消费。
- 装饰器经 `ILlmClientDecorator` 注册（`ServiceCollectionExtensions.cs:663`），`Order = 100`。

**结论**：`ProviderInfo.MaxRetries` / `ILlmProvider.MaxRetries` 不参与实际重试逻辑，仅作展示，UI 无消费方（WebUI 中 `MaxRetries` 仅用于 `LlmRetryExhaustedException` 文案）。

---

## 3. 配置模型（方案 A：扩展 `ProviderConfig`）

`ProviderConfig` 新增三项并调整一项：

| JSON 键 | 属性 | 类型 | 默认 | 说明 |
|---|---|---|---|---|
| `retry_base_delay` | `RetryBaseDelayMs` | int | `500` | 首次退避（毫秒） |
| `retry_max_delay` | `RetryMaxDelayMs` | int | `10000` | 单次退避上限（毫秒） |
| `retry_total_budget` | `RetryTotalBudgetMs` | int | `120000` | 单次 LLM 调用内累计重试等待预算（毫秒） |
| `max_retries` | `MaxRetries` | int | **`3` → `0`** | `<=0` 表示不限次数（仅受预算/取消约束）；`>0` 为次数上限 |

- 均用 `[JsonPropertyName(...)]` snake_case，与现有字段一致。
- 非法值（`<=0`）在装饰器侧归一为默认值，避免除零/负数延迟。
- `ProviderConfig.GetConfigSchema()` 当前返回 `null`，配置只能经 `.seeing/seeing.json` 写入，无 UI schema 变更。

---

## 4. 退避与停止算法（`LlmRetryPolicy` 新增纯函数）

在 `LlmRetryPolicy`（Abstractions，零副作用、便于单测）新增：

```csharp
// attempt 为已失败尝试序号（1-based）
public static TimeSpan ComputeDelay(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    => TimeSpan.FromMilliseconds(Math.Min(
        baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
        maxDelay.TotalMilliseconds));

// elapsed 仅累计重试等待；budget 为总等待预算
public static bool ShouldRetry(
    int attempt, TimeSpan elapsed, TimeSpan nextDelay, int maxRetries, TimeSpan budget)
{
    if (maxRetries > 0 && attempt >= maxRetries) return false;
    return elapsed + nextDelay <= budget;
}
```

**默认序列**：`0.5, 1, 2, 4, 8, 10, 10, 10…` 秒（`base=500ms`, `cap=10s`）。

**默认预算停止点**：累计等待 `0.5→1.5→3.5→7.5→15.5→25.5→35.5→45.5→55.5→65.5→75.5→85.5→95.5→105.5→115.5s`；下一次需 `115.5+10=125.5 > 120`，停止。共 **16 次尝试 / 15 次等待 / 约 115 秒**。

- 预算按**单次调用内累计重试等待**计算，失败调用之间相互独立。
- 用户取消（`OperationCanceledException` 且 token 已请求）不重试，`Task.Delay` 与判定均观察 `cancellationToken`。

---

## 5. `RetryingLlmClient` 改造

- `Wrap` 从 `ProviderConfig` 读取 4 项，归一化后构建不可变 `LlmRetrySettings(baseDelay, maxDelay, budget, maxRetries)` 记录，传给 `RetryingLlmClient`。
- 删除静态常量 `s_retryDelay`，全部改用 `LlmRetrySettings`。
- 抽公共逻辑：`GetNextRetry(attempt, elapsed)` 返回 `(TimeSpan nextDelay, bool canRetry)`，`CompleteAsync` 与 `CompleteStreamAsync` 共用。
- `elapsed` 为方法内局部累计变量（不依赖墙钟，测试无需 fake time）。
- 每次调用成功即结束；`willRetry=false` 在成功/终止前写入 `Items`。
- 日志字段扩展：`NextDelayMs`、`ElapsedMs`、`BudgetMs`、`Attempt/MaxRetries`。
- `LlmCallContext.Items` 保留现有三键，另加 `retry.nextDelayMs`（`LlmRetryPolicy.NextDelayItemKey`）供上层/UI 显示。

流式约束不变：一旦已 `yield` 任一 chunk，捕获异常后不再重试。

---

## 6. 必须同步的克隆 / 映射点

以下位置需同步新增字段，否则热重载丢配置或重建判定失效：

| 文件 | 位置 | 改动 |
|---|---|---|
| `ProviderManager.cs` | `CloneConfig` | 复制 3 个新字段 |
| `ProviderManager.cs` | `RequiresRebuild` | 3 个新字段参与比较 |
| `ModelConfigManager.cs` | `CloneProviderConfig` | 复制 3 个新字段 |
| `ConfiguredLlmProvider.cs` | `CloneConfig` | 复制 3 个新字段 |
| `ConfiguredLlmProvider.cs` | `LoadConfigAsync` | 输出 3 个新字段 |
| `ConfiguredLlmProvider.cs` | `ApplyValues` | 解析 3 个新字段 |

> `MaxRetries` 默认值调整仅改 `ProviderConfig` 的初始化值，无需改克隆点。

---

## 7. 测试

**新增 `LlmRetryPolicyTests`**

- `ComputeDelay` 序列：attempt 1..6 → 0.5/1/2/4/8/10s，且第 7 次仍 10s（封顶）。
- `ShouldRetry`：`maxRetries <= 0` 仅看预算；`elapsed + nextDelay <= budget` 边界（等于预算可重试、超出不可）；`maxRetries > 0` 时次数上限优先。

**改造 `RetryLlmClientDecoratorTests`**

- `Wrap` 辅助改为构造含 `RetryBaseDelayMs = 1`（或更小）与显式预算的 `ProviderConfig`，避免真实等待。
- 保留既有四个用例语义，断言不变（`MaxRetries` item 现取决于配置）。
- 新增：
  - 预算耗尽后用尽重试并抛出**原始异常**（非包装异常）。
  - `max_retries > 0` 时次数上限生效。
  - 封顶后间隔不再增长（可通过 `retry.nextDelayMs` item 断言）。

> 测试不得依赖真实 500ms/10s 延迟，统一注入毫秒级参数。

---

## 8. 兼容与影响

- **默认行为变化**：默认由"3 次固定 / 约 3 秒"改为"预算制 / 约 115 秒"。显式配置 `max_retries` 可恢复次数约束。
- `ProviderInfo.MaxRetries` / `ILlmProvider.MaxRetries` 可能返回 `0`（不限），已确认无 UI 消费方，不展示为"0 次"。
- 扩展 Provider（`plugs/providers/*`）自行返回 `MaxRetries => 3`，不经过本装饰器，不受影响。
- 不改变 `LlmRetryExhaustedException` 与 `EventStreamHandler` 文案路径。
- 不改 DI 注册、不改全局选项、不改 WebUI。

---

## 9. 示例配置（`.seeing/seeing.json`）

```json
{
  "Providers": {
    "openai": {
      "type": "openai",
      "baseURL": "https://api.openai.com/v1",
      "apiKey": "sk-...",
      "timeout": 300000,
      "max_retries": 0,
      "retry_base_delay": 500,
      "retry_max_delay": 10000,
      "retry_total_budget": 120000
    }
  }
}
```

---

## 10. 开放问题

- 无（默认值经用户确认：`base=500ms`、`cap=10s`、预算 `120s`、`max_retries` 默认 `0`）。
