# 可读 JSON 序列化统一设计

**日期：** 2026-11-06
**状态：** 待评审
**分支：** master

---

## 1. 意图与原因

### 现状问题

项目里所有 JSON 输出默认走 `System.Text.Json` 的默认编码器，它会把**所有非 ASCII 字符**转义成 `\uXXXX`，顺带把 `<` `>` `&` `'` 也转义。

实际落盘证据（本机 `~/.seeing`）：

| 文件 | 现象 |
|---|---|
| `sessions/ses_*.json` | `"title": "\u65B0\u4F1A\u8BDD"` |
| `session-groups/*.json` | 同上 |
| `sessions/_archive/*.json.gz`（141 KB 样例） | 内含 **17125 处** `\uXXXX` |

这带来三类实际损失：

1. **可读性**：用户直接打开会话/配置 JSON 看到的是不可读的转义串，排障需要额外解码。
2. **体积**：一个中文字符 UTF-8 占 3 字节，转义后是 6 字节字符（`\u4E2D`），文本型 JSON 接近翻倍；归档会话体积受影响最明显。
3. **模型可见的 JSON**：`ToolSearchTool`、`SystemOneTool`、`QuestionTool` 等把 JSON 直接喂给模型。模型看到的是 `\u67E5\u8BE2` 而不是「查询」，既浪费 token 也增加理解成本。TUI 层甚至为此专门写了反解逻辑（`TuiRendererTests` 断言输出中不得出现 `\u67E5`）——这是在为默认编码器的副作用打补丁。

只有 6 处显式设了编码器（`ToolSearchTool`、`SystemOneTool`、`ReflectionJournal`、`ExecutionUsageLedger`、`CanonicalJson`、WebUI 的 `ToolCallDisplayFormatter`），其余全部走默认。

### 本次要达成的效果

**全项目源内产生的 JSON 一律中文原样输出**，通过**单一工具**统一，不再散落 `new JsonSerializerOptions {...}`。

### 非目标

- 不改变反序列化行为（`.NET` 的 `JsonSerializer` 读 `\uXXXX` 和原样中文是等价的，旧文件继续可读）。
- 不改命名策略、不新增驼峰/忽略 null 等语义变化（**只换编码器，其他设置逐处保持原样**）。
- 不迁移/重写已有的历史归档文件（旧文件保持原样，新写入的才用新格式）。

---

## 2. 约束

### C1：统一工具必须放在依赖地板

`Seeing.Session` 是原语层，**不引用 `Seeing.Agent.Abstractions`**（依赖方向是 Abstractions → Seeing.Session）。而产出最多转义 JSON 的正是 `FileSessionStore` / `FileSessionGroupStore`。

| 候选落点 | 可用性 | 结论 |
|---|---|---|
| `Seeing.Agent.Abstractions` | `Seeing.Session` 用不到 → 会话文件仍然转义 | ✗ 不满足「统一」 |
| 新建原语工程 `Seeing.Serialization` | 干净，但要新增 csproj + 给约 50 个工程加引用 | ✗ 成本过高 |
| **`Seeing.Session`（现有地板）** | 所有工程（直接或经 Abstractions/Core 传递）均可见 | ✓ 采用 |

### C2：编码器选择

实测（.NET 10）三种候选对 `中文标题 <b>"引号"</b> & é` 的输出：

| 方案 | 中文 | `<` `>` `&` | `"` |
|---|---|---|---|
| 默认 | `\u4E2D\u6587` | `\u003C` | `\u0022` |
| **`UnsafeRelaxedJsonEscaping`** | 原样 | **原样** | `\"`（合法 JSON 转义） |
| `TextEncoderSettings(All)` + 禁 `<>&` | 原样 | `\u003C` | **`\u0022`** |

第三条看似「既可读又安全」，但会把引号变成 `\u0022`，产出 `"key": "\u0022引号\u0022"` 这种反常形式，且对模型更不利。**采用 `UnsafeRelaxedJsonEscaping`**：

- 中文、日文、韩文、拉丁扩展字符原样输出。
- `"` `\` 与控制字符仍按 JSON 规范转义 → **不产生非法 JSON**。
- U+2028/U+2029 仍被转义 → 不会破坏 JS 字符串字面量。

### C3：`<` `>` `&` 原样输出的安全前提（已核查）

「JSON 被内联进 HTML/`<script>`」是唯一会让放开 `<>` 变危险的位置。核查结果：

- `samples/Seeing.Agent.WebUI/Pages/_Host.cshtml` 中**没有**任何把 C# 侧 JSON 注入 `<script>` 的代码。
- Blazor 的 `@_paramJsonDisplay` 输出进 `<pre>`，razor 自动 HTML 编码；`@((MarkupString)...)` 的输入全部来自 Markdown 渲染，不来自本次改动的 JSON 序列化产物。
- 现存 Markdown 渲染路径：`MemoryRenderCache` 与 TUI 已 `.DisableHtml()`；`MessageComponentBase.MarkdownPipeline` 未禁用原始 HTML —— 但它的输入是模型回复正文，**不是** JSON 序列化结果，与本改造正交（作为独立风险在 §7 记录）。

**结论：本次放开 `<>` 不引入新的注入面。**

### C4：不可改动的位置（签名字节）

下列位置的输出直接参与 HMAC / 内容哈希，改编码会让已落盘或已计算的签名失效：

| 位置 | 用途 |
|---|---|
| `Abstractions/Permissions/AgentPermissionPolicy.cs` — `ComputeHash` / `Sign` / `VerifySignature` | ContentHash + HMAC 签名载荷 |
| `Core/Core/Permission/AgentPermissionPolicyExtensions.cs` — `ComputeHash` | 策略合并后的 ContentHash |
| `Core/Core/Permission/PermissionIntegrity.cs` — `ComputeIntegrityHash` | 完整性链 HMAC（递归含 ParentHash） |

这三处显式使用 `SeeingJson.Signature`（**保持 CLR 默认编码器**）并加注释说明「字节稳定性优先，勿改为可读编码器」。

### C5：不改变既有 opt-in 行为

`CanonicalJson` 已经是 `UnsafeRelaxedJsonEscaping`。它的注释明确要求「非 ASCII 原样 UTF-8」以保证字节稳定的 diff 键。改造后它改为复用 `SeeingJson.ReadableEncoder`，**行为不变**（`CanonicalJsonTests` 继续守护）。

---

## 3. 方案

### 3.1 新增单一工具

`src/primitives/Seeing.Session/Serialization/SeeingJson.cs`

```csharp
namespace Seeing.Serialization;

/// <summary>
/// 全项目统一 JSON 序列化选项。
/// <para>可读选项：非 ASCII（中文等）原样输出，仅转义 JSON 规范要求的字符
/// （引号、反斜杠、控制字符、U+2028/U+2029）。</para>
/// <para>签名选项：保持 CLR 默认编码器，用于 HMAC / 内容哈希的字节稳定输入。</para>
/// </summary>
public static class SeeingJson
{
    /// <summary>可读编码器（非 ASCII 原样）。CanonicalJson 等自定义写出器复用。</summary>
    public static JavaScriptEncoder ReadableEncoder { get; }

    /// <summary>可读、无缩进。</summary>
    public static JsonSerializerOptions Readable { get; }

    /// <summary>可读、缩进（落盘与界面展示）。</summary>
    public static JsonSerializerOptions ReadableIndented { get; }

    /// <summary>签名/哈希专用：默认编码器，字节稳定，勿用于普通输出。</summary>
    public static JsonSerializerOptions Signature { get; }

    /// <summary>
    /// 派生可读选项：以 <paramref name="indented"/> 决定缩进，其余设置由
    /// <paramref name="configure"/> 追加（调用方原样迁移既有配置）。
    /// </summary>
    public static JsonSerializerOptions Create(
        bool indented = false,
        Action<JsonSerializerOptions>? configure = null);
}
```

**关键设计点：`Create` 只预置 `Encoder`，不预置任何其他设置。** 这样每处迁移都是「原配置 + 换编码器」，输出形状（命名策略、忽略 null、转换器）完全不变，把回归面压到最小。

### 3.2 迁移分类

三类处理，逐处判定：

| 类别 | 用哪个 | 典型位置 |
|---|---|---|
| **落盘**（人读的文件） | `Create(indented: true, ...)` | 会话/分组/归档、`seeing.json`、`providers.json`、`mcp.json`、调度历史、反思日志、用量账本、技能状态、渠道目录 |
| **协议与上下文**（网络、模型可见、元数据） | `Create(...)` 或 `Readable` | OpenAI/Anthropic/TypeSafe 请求体、QQ/WeCom/Gateway WS 帧、工具输出（todo/question/tool_search/systemone）、`execution_history`、`context.baseline`、`inbox.conversation` |
| **签名/哈希** | `Signature` | §2 C4 三处 |

### 3.3 改动清单（按程序集）

已逐文件核对，共 **55 处** 生产代码调用点：

**primitives**
- `Seeing.Session/Storage/FileSessionStore.cs`（3 处 Serialize + options）
- `Seeing.Session/Storage/FileSessionGroupStore.cs`
- `Seeing.Session/Management/SessionArchiver.cs`、`SessionSharer.cs`

**abstractions**
- `Permissions/AgentPermissionPolicy.cs`（3 处 → `Signature`，不改行为）
- `Llm/LlmModels.cs`（`ToString` 拼装，→ `Readable`）

**spine / Core**
- `Configuration/UnifiedConfigManager.cs`（options + 落盘 `ToJsonString`）
- `Tools/ToolManager.cs`（禁用工具名单落盘）
- `Context/ContextBaselineStore.cs`、`Context/Sources/ProjectInstructionsContextSource.cs`
- `Context/CanonicalJson.cs`（改用共享 `ReadableEncoder`，行为不变）
- `Core/AgentExecutor.cs`（工具定义回灌）、`Core/Permission/AgentPermissionPolicyExtensions.cs`（→ `Signature`）
- `Core/Permission/PermissionIntegrity.cs`（→ `Signature`）

**hosting**
- `Execution/SessionRuntime.cs`、`Execution/ExecutionRunner.cs`、`Execution/ChatEventTracker.cs`
- `Execution/ToolExposure/ToolExposureEngine.cs`
- `Tools/TodoWriteTool.cs`

**capabilities**
- Llm：`Llm.OpenAI/Clients/OpenAiHttpHelper.cs`、`Llm.Anthropic/Clients/AnthropicClient.cs`（3 处）、`Llm.TypeSafe/Clients/TypeSafeHttpHelper.cs`、`Llm.ModelCatalog.Builtin/BuiltinCatalogDocument.cs` + `BuiltinCapabilitySource.cs`
- Tools：`Tools.Web`（WebSearch/CodeSearch 的 MCP 请求）、`Tools.Question`、`Tools.ToolDiscovery/ToolSearchTool.cs`（去掉本地 options，改共享）、`Tools.SystemOne/SystemOneTool.cs`（同上）
- `Mcp`：`Configuration/McpConfigPersistence.cs`（2 处）、`McpClientManager.cs`、`OAuth/McpOAuthStorage.cs`、`OAuth/McpOAuthClientRegistrar.cs`、`OAuth/McpOAuthClientRegistrationStore.cs`
- `Scheduler`：`Persistence/JsonScheduleRepository.cs`（2 处）、`Jobs/JobDataMapExtensions.cs`、`Management/ScheduleManager.cs`、`Tools/CronScheduleParser.cs`
- `Skills/SkillWriter.cs`、`Skills/Catalog/SkillCatalogCore.cs`
- `Reflection/Core/ReflectionJournal.cs`、`ExecutionUsageLedger.cs`（去掉本地 encoder，改共享）
- `Comms/Directories/JsonChannelDirectoryStore.cs`

**gateway**
- `Seeing.Gateway/Protocol/GatewayWsFrame.cs`、`Seeing.Gateway.Client/GatewayJsonOptions.cs`
- `Seeing.Gateway.QQ`（2 处）、`Seeing.Gateway.WeCom`（`WeComWsFrame.cs`、`WeComSessionTracker.cs`、2 处 Connection）
- `Seeing.Agent.Gateway`（`GatewayEndpoints.cs`、`AdminEndpoints.cs`、`ChannelHostConfigStore.cs`）

**plugs/providers**（5 个配置存储）
- `DeepSeek` / `MiMo` / `OpenCodeZen` / `SiliconFlow` / `TypeSafeApi` 的 `*ConfigStore.cs`

**samples**
- WebUI：`Helpers/ToolCallDisplayFormatter.cs`、`Services/GatewayClientConfigService.cs`、`Components/McpJsonEditor.razor`、`Components/McpToolList.razor`、`Models/TodoViewModel.cs`
- Cli：`Commands/ConfigCommand.cs`（2 处）、`Services/InstanceRegistry.cs`、`Services/OneShotOutput.cs`
- Tui：`Services/TuiEventInterpreter.cs`

> 只改 `Encoder` 语义的调用点（如 `Kind` → `Create(o => o.Converters.Add(...))`）保持原有 `Converters` / `PropertyNamingPolicy` / `WriteIndented` 逐项迁移，不顺手改其他设置。

### 3.4 防再犯：架构不变量测试

新增 `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/JsonOptionsInvariantTests.cs`：

- 扫描 `src/`、`plugs/`、`samples/` 下的 `*.cs`，断言 `new JsonSerializerOptions` **只允许出现在**：
  1. `serialization/SeeingJson.cs`（工具本体）
  2. 显式豁免清单（若将来确有必须自定义编码器的场景，需在清单里写明理由）
- 失败信息给出文件与行号，指向 `SeeingJson`。
- 与现有 `ExecutionWorldTests`（工具程序集禁 `File.WriteAllText`）同一风格，属于「单向门」防漂移。

### 3.5 测试

**新增**
- `tests/primitives/Seeing.Session.Tests/Serialization/SeeingJsonTests.cs`
  - 中文字符串原样输出（不含 `\u4E2D`）
  - 引号输出为 `\"` 而非 `\u0022`（保证可读性一致）
  - 控制字符与 U+2028 仍被转义（合法性）
  - `Signature` 输出保持 `\uXXXX`（字节稳定性护栏，防止有人顺手机改）
  - `Create(indented: true)` 产出缩进且中文原样
- `Seeing.Session.Tests` 增加：会话标题含中文 → 落盘文件文本 `Should().Contain("新会话")` 且 `NotContain("\\u")`
- 兼容性用例：手写一份含 `\uXXXX` 的旧会话 JSON → `Deserialize` 正常（不回归）

**既有用例调整**
- `CanonicalJsonTests`：应全部继续通过（行为未变）。
- `ReflectionJournalTests`、`TuiRendererTests` 里 `NotContain("\\u4f1a")` 这类断言现在测的是「下游解码逻辑没被绕过」，改造后仍然有意义，保留。

---

## 4. 交付物

| # | 交付 | 位置 |
|---|---|---|
| 1 | 统一工具 | `src/primitives/Seeing.Session/Serialization/SeeingJson.cs` |
| 2 | 55 处调用点迁移 | 见 §3.3 |
| 3 | 架构不变量测试 | `tests/spine/Seeing.Agent.Invariants.Tests/Invariants/JsonOptionsInvariantTests.cs` |
| 4 | 行为测试 | `tests/primitives/Seeing.Session.Tests/Serialization/SeeingJsonTests.cs` + 会话落盘可读用例 |
| 5 | 规范条目 | `docs/architecture/04-development-standards.md`：禁止裸 `new JsonSerializerOptions` |
| 6 | 豁免登记 | `docs/architecture/06-compliance-audit.md`：`SeeingJson` 为何落在 `Seeing.Session` |
| 7 | Release Notes | `docs/architecture/31-readable-json-release-notes.md` |

---

## 5. 风险与缓解

| 风险 | 影响 | 缓解 |
|---|---|---|
| **R1 签名字节漂移** | 权限策略签名/完整性哈希失配 | §2 C4 三处强制 `Signature`；加护栏测试断言 `Signature` 仍输出 `\uXXXX` |
| **R2 某处 JSON 被内联进 `<script>`** | XSS 面 | 已核查 `_Host.cshtml` 无此用法（§2 C3）；改造后重新全量 grep `new MarkupString` / `<script` 交叉确认 |
| **R3 字符数假设变化** | 截断/预算逻辑按 `string.Length` 计（`MaxDefinitionChars`、`ToolOutputLimiter`、token 估算） | 可读化后中文长度变小（6→1 字符），**做得更多而不是更少**，方向安全；但需在测试里确认截断仍按预期工作 |
| **R4 静态 options 被写后复用** | `JsonSerializerOptions` 首次使用后变只读，再改抛 `InvalidOperationException` | 所有静态字段用 `Create(...)` 一次性构造完成，禁止「拿到实例后再改」 |
| **R5 遗漏某个调用点** | 该处仍转义 | §3.4 不变量测试做全量扫描兜底 |
| **R6 协议对端拒绝非 ASCII** | QQ/WeCom/OpenAI 接口异常 | 这些接口都以 `charset=utf-8` 传 `StringContent`，JSON 规范允许原样 UTF-8；若个别对端异常，单点回退 `Signature` 选项并在此文档登记 |

**回滚**：改造集中在 `SeeingJson` 的编码器定义。若线上出现对端兼容问题，把 `ReadableEncoder` 换回 `JavaScriptEncoder.Default` 即整体回滚，调用点无需改动。

---

## 6. 遗留与后续（非本次范围）

- `MessageComponentBase.MarkdownPipeline` 未 `.DisableHtml()`，与 `MemoryRenderCache`/TUI 不一致。属既有渲染安全问题，建议单独立项。
- 历史归档 `*.json.gz` 不重写；如需瘦身可另做一次性迁移工具。

---

## 7. 已决策的取舍（请评审时确认）

1. **工具落点放在 `Seeing.Session` 而非 `Abstractions`**：被 §2 C1 的依赖地板倒逼。命名空间取 `Seeing.Serialization`（横切语义），程序集寄居原语层。若你更希望「新建 `Seeing.Serialization` 原语工程」，改动量是 50+ 个 csproj 引用。
2. **编码器采用 `UnsafeRelaxedJsonEscaping`**，`<>` 一起放开。备选的「禁 `<>`」方案会把引号变成 `\u0022`，更不可读，故弃用。
3. **签名/哈希三处不跟随**：以字节稳定为先。
