# 可读 JSON 序列化统一 Release Notes

**日期：** 2026-11-06
**分支：** master
**设计规格：** `docs/superpowers/specs/2026-11-06-readable-json-serialization-design.md`

---

## 1. 一句话

全项目 JSON 输出统一改走 `Seeing.Serialization.SeeingJson`：**中文（及所有非 ASCII）原样输出**，不再出现 `\u65B0\u4F1A\u8BDD` 这类转义串；参与 HMAC / 内容哈希的序列化显式豁免，保持字节稳定。

---

## 2. 为什么

`System.Text.Json` 默认编码器会把所有非 ASCII 字符转义成 `\uXXXX`，并顺带转义 `<` `>` `&`。改造前的实际影响：

| 问题 | 证据（改造前本机 `~/.seeing`） |
|---|---|
| 落盘不可读 | `sessions/ses_*.json` → `"title": "\u65B0\u4F1A\u8BDD"` |
| 体积膨胀 | 归档 `*.json.gz` 内 **17125 处** `\uXXXX`（单文件 141 KB） |
| 模型看到转义 | `tool_search` / `system_one` / `question` 输出喂给模型的是 `\u67E5\u8BE2` 而非「查询」；TUI 层曾为此专门写反解补丁（`TuiRendererTests` 断言不得出现 `\u67E5`） |

全项目仅 6 处显式设过编码器，其余 55 处 `JsonSerializer.Serialize(...)` 全走默认。

---

## 3. 改了什么

### 3.1 新增单一工具

`src/primitives/Seeing.Session/Serialization/SeeingJson.cs`（命名空间 `Seeing.Serialization`）

| 成员 | 用途 |
|---|---|
| `ReadableEncoder` | 可读编码器（`UnsafeRelaxedJsonEscaping`），供自定义写出器复用 |
| `Readable` | 可读、无缩进。协议载荷 / 模型可见 JSON / 元数据 |
| `ReadableIndented` | 可读、缩进。落盘文件与界面展示 |
| `Signature` | **默认编码器**（保持 `\uXXXX`）。仅限 HMAC / 内容哈希 |
| `Create(indented, configure)` | 派生：只预置编码器，其余设置由调用方原样传入 |

**落点为什么在 `Seeing.Session`**：`Seeing.Session` 是依赖地板（`Abstractions` → `Seeing.Session`，不反向）。产出最多转义 JSON 的 `FileSessionStore` 恰恰在最低层——若把工具放 `Abstractions`，会话落盘就用不上，统一即失效。

### 3.2 迁移范围（55 处）

- **primitives**：会话/分组/归档/分享存储
- **abstractions**：权限策略（→ `Signature`）、`LlmModels.ToString`
- **Core**：统一配置、禁用工具名单、上下文基线、项目指令源、工具定义回灌、`CanonicalJson`（改用共享编码器，行为不变）
- **Hosting**：`SessionRuntime`、`ExecutionRunner`、`ChatEventTracker`、`ToolExposureEngine`、`TodoWriteTool`
- **capabilities**：Llm（OpenAI / Anthropic / TypeSafe / BuiltinCatalog）、Tools（Web / Question / ToolSearch / SystemOne）、Mcp（配置 + OAuth）、Scheduler、Skills、Reflection、Comms
- **gateway**：Gateway WS、WeCom、QQ、Gateway 端点与通道配置
- **plugs/providers**：5 个 Provider 配置存储
- **samples**：WebUI（工具卡片、Gateway 配置、MCP 编辑器、Workspace 历史、Todo）、CLI、TUI

### 3.3 签名/哈希豁免（关键）

以下位置**必须**保持默认编码器，否则已落盘或已计算的签名会静默失效：

| 位置 | 用途 |
|---|---|
| `Abstractions/Permissions/AgentPermissionPolicy.cs` — `ComputeHash` / `Sign` / `VerifySignature` | `ContentHash` + HMAC 签名载荷 |
| `Core/Core/Permission/AgentPermissionPolicyExtensions.cs` — `ComputeHash` | 策略合并后的 `ContentHash` |
| `Core/Core/Permission/PermissionIntegrity.cs` — `ComputeIntegrityHash` | 完整性链 HMAC（递归含 `ParentHash`） |

---

## 4. 行为变化

| 项 | 改造前 | 改造后 |
|---|---|---|
| 会话标题落盘 | `"\u65B0\u4F1A\u8BDD"` | `"新会话"` |
| `<` `>` `&` | `\u003C` `\u003E` `\u0026` | 原样 |
| `"` `\` 控制字符 U+2028/U+2029 | 转义 | **仍转义**（保证 JSON 合法） |
| HMAC / ContentHash 输入 | `\uXXXX` | **不变** |
| 读旧文件（含 `\uXXXX`） | 正常 | **正常**（读侧行为未变） |

**不改变**：命名策略、忽略 null、枚举转换器等任何既有设置——每处只换了编码器。

---

## 5. 验证

- 新增 `SeeingJsonTests`（8 项）：中文原样 / 引号用 `\"` 而非 `\u0022` / 控制字符仍转义 / 往返一致 / 缩进变体 / **`Signature` 仍输出 `\uXXXX`**（护栏）/ `Create` 保留调用方设置
- 新增 `SessionStoreReadableJsonTests`（2 项）：中文标题落盘可读 + 旧 `\uXXXX` 文件仍能读回
- 新增 `JsonOptionsInvariantTests`（2 项）：扫描 `src`/`plugs`/`samples`，禁止裸 `new JsonSerializerOptions`；锚定 `SeeingJson` 必须位于 `Seeing.Session`
- 实机验证：临时目录写入中文标题会话 → 文件内无任何 `\u`、`<b>` 与 `&` 原样、读回一致
- 回归：`Seeing.Session.Tests` 295 通过、`Seeing.Agent.Tests` 1610 通过、`Hosting.Tests` 104、`Reflection.Tests` 108、`Tui.Tests` 617、`Gateway.Tests` 94，均 0 失败

**既有失败（非本次引入）**：`ToolViewSingleSourceTests.No_One_Should_Enumerate_Tool_Registry_To_Decide_Availability_Outside_Resolver` 在改造前即失败（已用 `git stash` 对基线复现），原因是 `AgentExecutor` 调用 `GetRegisteredTools()` 而测试允许清单未包含该文件。

---

## 6. 已知边界

- **历史归档不重写**：`~/.seeing/sessions/_archive/*.json.gz` 保持原样，仅新写入使用新格式。如需瘦身可另做一次性迁移。
- **`MessageComponentBase.MarkdownPipeline` 未 `.DisableHtml()`**：与 `MemoryRenderCache`/TUI 不一致，属既有渲染风险，与本次 JSON 编码器改造正交，建议单独立项。本次放开 `<>` 不引入新注入面——`_Host.cshtml` 无任何 C# JSON 注入 `<script>`，Blazor `@` 输出自动 HTML 编码。
- **个别对端兼容**：QQ/WeCom/OpenAI/Anthropic 均以 `charset=utf-8` 传输，JSON 规范允许原样 UTF-8。若某对端异常，该单点回退 `SeeingJson.Signature` 并在本文登记。

---

## 7. 回滚

改造收敛于一处：把 `SeeingJson.ReadableEncoder` 换回 `JavaScriptEncoder.Default` 即整体回滚，调用点无需改动。
