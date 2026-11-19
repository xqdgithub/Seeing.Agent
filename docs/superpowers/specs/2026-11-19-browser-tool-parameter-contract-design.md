# 浏览器工具参数契约统一（2026-11-19）

## 意图

把 `browser_wait_for` 上验证过的参数纪律推广到全部 12 个 `browser_*` 工具：
**入参是契约，错了就说出来，不猜、不夹、不回落默认**。

## 判据（三条）

1. **按 schema 校验入参**：类型不符或越界 → 明确失败，注入值必须可解释；不 `Math.Clamp`、不回落默认。
2. **参数校验先于状态检查**：参数错与「该会话还没打开页面」无关，报最可执行的那一条。
3. **单位进名字、真源只有一处**：数值参数带单位后缀；默认值与范围集中定义，工具 schema 与执行层共用。

## 发现的问题

### P1 — 静默做反（布尔/类型）

`BrowserJson.ReadInt` / `ReadBool` 对「类型不符」和「未提供」都返回 `null`，调用方无法区分：

| 参数 | 输入 | 旧行为 | 后果 |
|---|---|---|---|
| `browser_type.clear` | `"false"` | 回落 `true` | **清空用户已有内容**（破坏性） |
| `browser_type.submit` | `"true"` | 回落 `false` | 回车没发，模型以为提交了 |
| `browser_eval.awaitPromise` | `"false"` | 回落 `true` | 可能挂到超时 |
| `browser_screenshot.fullPage` | `"true"` | 回落 `false` | 只截了可视区 |
| `clickCount` / `maxElements` / `amount` / `delayMs` | 字符串或超范围 | 回落默认或夹取 | 参数「看起来生效了」 |

### P2 — 静默夹取

`browser_scroll.amount` 的 `-100` 被 `Math.Clamp(amount <= 0 ? 600 : amount, 1, 20000)` 翻译成
**向同一方向滚 600px** —— 方向错误，不是精度损失。其余（5000→500、2000→500、5→3）属同类。

### P3 — 一致性与真源

- 参数校验排在 `TryGetPage` 之后：缺 `text` 又没页面时报「还没打开页面」，模型会去开页面而不是改参数。
- 默认值/范围在工具层与引擎层各写一份（`600`、`1..3`、`0..500`），改一处漏一处就漂移。
- `browser_scroll` 的 metadata 记请求值而非实际值。

## 方案

- `BrowserJson` 增加与 `TryReadMilliseconds` 同语义的严格读取器：
  `TryReadInt(name, min, max, out int?)`、`TryReadBool(name, out bool?)`；旧字段统一走 `RejectObsoleteField`。
- 参数校验统一提到 `TryGetPage` 之前。
- `amount` → **`amountPx`**（旧名明确拒绝并给示例）；`timeout` → `timeoutMs` 已在上一轮完成。
- `BrowserInputLimits` 集中默认值与范围，工具 schema 与引擎共用。
- 12 个工具统一声明 `timeout.budget`：`open=130000`、`wait_for=130000`、`eval=120000`、其余 `60000`。
  背景：`SeeingAgentOptions.ToolExecutionTimeout` 默认 `null`，此前除 `wait_for` 外的工具**完全没有超时上限**，
  一次卡住的 CDP 调用会一直占住会话互斥。
- 引擎层的夹取保留为最后防线（工具层已保证合法），`amount <= 0 ? 600` 这类隐藏默认删除。

## 非目标

- 不改各工具的权限粒度（仍是「一个动作一个工具」）。
- 不改 `browser_snapshot` 的默认/上限值（200/500），只改校验方式。

## 验证

- `BrowserToolParameterContractTests`：布尔/整数类型不符、越界、旧字段改名、参数先行、全工具超时预算。
- `BrowserToolsTests`：共享参数需含 `ref`（因为校验提前），定位冲突直接报冲突。
- 全量 `Seeing.Agent.Tools.Browser.Tests`（含真浏览器）：331 通过 / 0 失败。
