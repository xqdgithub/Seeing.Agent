# tool_search 真实 Provider 联调清单

**目的:** 验证"检索命中后本次执行内可直接调用"在真实模型/网关上成立（自动化测试只覆盖本地行为）。
**前置:** 本清单不产出 token 收益数字；收益需按 `baseline.md` 流程另行记录。

## 1. 准备

1. 在 `~/.seeing/seeing.json` 或 `.seeing/seeing.json` 设置：

```json
{
  "SeeingAgent": {
    "Tools": { "Exposure": { "PolicyMode": "Tiered" } }
  }
}
```

2. 确认可用工具数 ≥ 30（触发分层），且 `tools[]` 里应包含默认钉选集（16 个）+ `tool_search`。
3. 选择一个未经钉选的工具作为目标（例如 `memory_search` 或某个 MCP 工具）。

## 2. 用例

向模型发送（或在 Agent 的系统提示里加入）明确指令：

> 先调用 `tool_search`，使用 `detail=full` 查询 `<目标工具>`；拿到定义后**立即**调用它完成一次无害操作，不要询问我。

## 3. 断言（逐条记录）

| # | 断言 | 期望 |
|---|---|---|
| 1 | 第一次与第二次 LLM 请求的 `tools[]` 快照 | **完全相同**（本次不换 schema） |
| 2 | 第二次请求的 assistant 消息 | 出现 `name=<目标工具>` 的 tool_call |
| 3 | HTTP 状态 | 200，无 `unknown function` / `invalid tool` 报错 |
| 4 | `ToolCallComplete(<目标工具>)` | 状态不是 `Rejected` |
| 5 | 该工具调用的 `ToolCallEvent.Metadata` | 存在 `exposure.loaded`（值是命中的 id 数组） |
| 6 | 再发一条新用户消息后 | 目标工具出现在 `tools[]` 的 inline 集合（`SchemaSnapshotEvent.InlineToolCount` 增加或 `ToolIds` 含之） |

## 4. 失败判定与处置

- **模型不调用未声明工具**：属模型服从性问题。先把 `tool_search` 的结果文案与目录文案当作已生效；若仍不调用，记录模型与 provider，评估是否启用 Plan B。
- **provider 拒绝未声明函数名**：记录报错原文与 provider，**启用 Plan B**（轮内重算 schema）：
  - 代价：每次发现触发一次前缀缓存写入，并放弃"单轮 schema 不变"不变量；
  - 需要同步修改 `SchemaSnapshotEvent` 的同源契约（每步快照）。

## 5. 记录

联调完成后，把 provider / 模型 / 通过项 / 失败项写回本文件末尾（不要写收益百分比，除非有真实基线对比数据）。
