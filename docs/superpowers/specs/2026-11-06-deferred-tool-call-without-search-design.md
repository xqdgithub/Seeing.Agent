# 延迟工具免检索直调 + 目录迁入上下文管道 设计

**日期**：2026-11-06
**状态**：设计已确认，待实施
**关联**：
- `docs/architecture/19-tool-context-budgeting-release-notes.md`（分层暴露）
- `docs/architecture/21-context-source-pipeline-release-notes.md`（上下文源管道，§8.3 预留了本设计的落点）
- `docs/superpowers/specs/2026-10-21-tool-context-economy-research-and-design.md`（P2「目录 delta 注入」未落地）

---

## 0. 一句话

当前分层暴露有两处会击穿前缀缓存：**搜索后 promotion 把工具插进 `tools[]`**，以及**工具目录住在系统提示词里**。
本设计把「搜索即加载」改成「直接调用、错了再给定义」，同时把目录迁入已存在的上下文源管道（`Target = Message` + delta 叙述）。三件事合起来让 **`tools[]` 与系统提示词在一轮会话内恒定**，缓存从头到尾不失效。

---

## 1. 意图

### 1.1 要解决的两个问题

**问题 A：强制检索的代价**

deferred 工具被调用时必须先 `tool_search detail=full`，否则执行层 `Rejected`。而：

- 目录行已带参数签名（`id(path, offset?, limit?)`），模型知道参数名与必填性；
- Skills 与 Agent 提示里已有大量调用示例；
- 冷工具使用频率低，为"可能用错参数"强制所有调用付 2 轮 + 一份常驻 schema，成本与收益严重不匹配。

**问题 B：缓存击穿**

`tool_search` 之后 `Pending → PromotePending → Discovered`，下一轮有两处同时变：

| 变更点 | 位置 | 后果 |
|---|---|---|
| `tools[]` 插入 | `ToolExposureEngine.cs:122-135`（pinned → discovered → 其余 inline → deferred） | 从插入下标起全部位移 |
| 系统提示词目录 | `ToolCatalogSectionContributor`（`Placement => AtEnd`）+ `AgentExecutor.cs:598-600` | 目录含 `### ns (N tools, M shown)`，N/M 变化 → 提示词从该分节起全断 |

系统提示词在最前面，因此这次失效是**近全量**的，不是"从中间某处断"。

### 1.2 关键洞察

**promotion 的唯一作用是"让工具在后续回合可调用"。一旦 deferred 工具可以直接调用，这个作用就消失了。**

也就是说：现在正在付「一次近全量缓存失效 + 每轮常驻 schema token」，去换一个放开直调后**免费获得**的能力。

### 1.3 另一个洞察：历史追加严格优于 `tools[]` 内联

缓存按前缀工作：

| 载体 | 前缀失效 | 后续每轮重发 |
|---|---|---|
| 进 `tools[]` | **一次近全量** | 是 |
| 进历史 `<tool_definition>`（尾部追加） | **零** | 是（但只在真的用到时才有） |

因此「参数错误时才把定义回灌到历史尾部」在缓存维度上**严格优于**「搜索后内联进 `tools[]`」。

---

## 2. 目标 / 非目标 / 反目标

### 目标

1. deferred 工具可直接调用，无需先 `tool_search`。
2. 拒绝原因细分：工具不存在 → 提示检索；工具存在但不可用 → 明确"检索也解决不了"。
3. 参数错误时自动附上完整定义，模型一次往返即可修正。
4. **`tools[]` 在一次会话内恒定**（除非用户主动切换 Agent / 场景）。
5. **系统提示词在一次会话内恒定**；工具目录的运行时变化走增量叙述消息（尾部）。
6. 新增内容跨轮可重建（前缀缓存守门）。

### 非目标

- 不改权限链（`IPermissionAuthorizer`、`DefaultToolPermissionPolicy`、审批流程原样）。
- 不改 `tool_search` 的检索能力与三档 detail（只改它的定位与文案）。
- 不删除 `Pending`/`Discovered` 持久化结构（见 §9 已知边界 1）。
- 不改上下文管道本身（`IContextSource` / `ContextAssembler` 零改动，只新增一个源）。

### 反目标

- **不得**因放开直调而使 `AgentDefinition.AllowedTools` / `DeniedTools` 失效（§3.2）。
- **不得**出现"本轮看到的 definition"与"历史重建的 definition"不一致。
- **不得**在 `AgentExecutor` 里写会话暴露态（单一权威写入者契约）。
- **不得**让 `tool_search` 与"可调用性"仍然耦合（否则问题 B 没解）。

---

## 3. 现状与问题

### 3.1 当前 gate 把两种性质不同的拒绝合并了

`src/spine/Seeing.Agent.Core/Core/AgentExecutor.cs:895-906`：

```csharp
if (context.ToolSettlement is { } settlement
    && !(toolScope?.IsCallable(name) ?? settlement.SendsSchema(name)))
{
    var deferred = toolScope?.IsDeferred(name) ?? settlement.IsDeferred(name);
    var reason = deferred
        ? $"工具 '{name}' 尚未加载。请先用 {ToolIdConventions.SearchToolId} 检索该工具；命中后本次执行内即可直接调用。"
        : $"工具 '{name}' 在本会话当前上下文中不可用。";
    return BuildTerminalEvent(tc, name, context, loopId, ToolCallStatus.Rejected, reason);
}
```

| 分支 | 含义 | 现状 | 应改 |
|---|---|---|---|
| `IsDeferred` | 工具在结算里，只是没下发 schema | 拒绝 + 提示检索 | **放行** |
| 都为 false | 工具不在结算里（被过滤/禁用/模块未挂载） | 拒绝 + 不提示检索 | 保持拒绝，文案细分 |

### 3.2 为什么"不在结算里"必须保持拒绝

**可见集 gate 是当前唯一在执行时强制 `Agent.AllowedTools` / `DeniedTools` 的地方。**

- `ToolManager.FilterSchemasByAgentToolLists`（`ToolManager.cs:91`）**只过滤 schema**，不参与执行。
- `ToolManager.ExecuteAsync`（`ToolManager.cs:612-777`）只检查 `IsToolEnabled`（`:626`）与 `_tools.TryGetValue`（`:638`）；`agent` 参数只用来取 `agent.Name`（`:623`）。
- `PermissionService.cs:528` 的 `context.Policy.AllowedTools` 是 `AgentPermissionPolicy` 的另一个列表，与 `AgentDefinition.AllowedTools` 不是同一份数据。

若一刀切放开，`plan` Agent 的白名单（`BuiltInAgents.cs:180`）会在执行层彻底失效。

### 3.3 文档自相矛盾

`docs/architecture/19` §4 相邻两行：

> 行 43：可调用 = 本轮内联集 ∪ 本执行已加载集
> 行 44：**exposure ≠ permission**：分层只决定"是否进本轮 schema / 目录"，不决定"能否调用"

第一句把 exposure 说成可调用性的决定因素，第二句说不是。本设计统一为：**exposure 只决定"是否随请求下发 schema"，不决定能否调用**；执行层只拦"不在会话结算内"。

### 3.4 目录确实没进管道（你的记忆是对的）

`docs/architecture/21` §8 已知边界第 3 条原文：

> **本期不含工具目录源**：管道为其预留位置（实现 `IContextSource` 即可接入，不改契约）；工具暴露/预算逻辑未改动。

`docs/superpowers/specs/2026-10-21-...md` P2 也写明：

> 目录增量 + 落盘 —— MCP 连接/断开、模块启停 → 目录 **delta 注入**（新增/变更/移除各一段）。

**P2 从未落地。** 现状：`ToolCatalogSectionContributor.Placement => AtEnd`，即系统提示词最后一个分节。

对照已有源：

| 源 | Target | 文件 |
|---|---|---|
| 项目指令 | `Message` | `ProjectInstructionsContextSource.cs:65` |
| 记忆 | `Message` | `MemoryContextSource.cs:104` |
| 技能清单 | `System` | `SkillsContextSource.cs:49` |
| **工具目录** | **（未接入，走系统提示词）** | — |

管道能力已全部就绪：`ContextSourceTarget.Message`、`ContextUpdateEnvelope.Wrap`、`DeltaLedger`、revert 折叠、压缩重锚定、`IContextSourceMessageMetadata`（UI 折叠）。

### 3.5 目录行已带参数签名

`ToolExposureEngine.cs:189`：

```csharp
var signature = $"{id}({DescribeParameters(schema.Function?.Parameters)}): {Truncate(description, lineMaxChars)}";
```

模型看到的是 `delete(path): 删除文件或整个目录…`，参数名与必填性（`?`）已给出，只缺类型、enum、min/max。对扁平工具足够。

### 3.6 全仓没有 JSON Schema 校验

grep `ValidateArguments|ValidateSchema|JsonSchema|SchemaValidator` 仅命中 `JsonSchemaPromptFormatting.cs`（提示词格式化）。

参数校验完全由各工具手写（`DeleteTool.cs:62` 的 `缺少必需参数: path`、`GitCommitTool.cs:58` 抛 `JsonException`、`CronCreateTool` 自解析）。

**因此"参数错误"无法预先判定，只能在执行后得知。** 这决定了 §5.3 必须是"执行失败后附带定义"。

### 3.7 失败有两种出口（必须同时覆盖）

| 出口 | 位置 | 典型工具 |
|---|---|---|
| `ToolResult.Success == false` | `AgentExecutor.cs:974-1013` | `DeleteTool`（返回 `Failure`） |
| 抛异常（被兜住） | `AgentExecutor.cs:956-972` 的 `catch (Exception)` | `GitCommitTool.cs:58` 抛 `JsonException` |

只改前者，主场景（缺参/参数猜错）对一批抛异常的工具完全不生效。

---

## 4. 方案选择

### 4.1 直调策略

| | 做法 | 轮次（对） | 轮次（错） | schema 进上下文 | 结论 |
|---|---|---|---|---|---|
| A | deferred 直接放行 | 1 | 2（裸错误） | 从不 | 复杂工具会反复试错 |
| B | 维持现状（先检索） | 3 | 3 | 必进 | 本设计要改的就是它 |
| C | 拒绝时内嵌定义 | 1 | n/a（先拒） | 只在真需要时 | 拒绝时机不自然 |
| **D** | **A + 失败时附定义** | **1** | **2（带定义）** | **只在真猜错时** | **采纳** |

### 4.2 promotion 策略

| | 搜索后是否 promotion | 缓存失效 | 每轮 token |
|---|---|---|---|
| 1 现状 | 是 | 一次近全量 | +schema/turn |
| **2 永不全量内联** | **否** | **零** | **零** |
| 3 混合 | 仅搜索路径 promotion | 一次 | 仅搜索过的 |

采纳 2。理由见 §1.2 / §1.3：promotion 换来的能力（跨轮可调用）已被 B 分支免费提供；而它的代价（一次近全量失效 + 每轮常驻 schema）没有被替代品覆盖。

### 4.3 目录落点

| | 落点 | 缓存 |
|---|---|---|
| 现状 | 系统提示词 `AtEnd` 分节 | 变化即近全量失效 |
| **采纳** | `IContextSource` + `Target = Message` + delta 叙述 | 初始字节稳定，变化只产生尾部长增量消息 |

**这是管道设计好的用法，不是新发明。** `docs/architecture/21` §8.3 明确"实现 `IContextSource` 即可接入，不改契约"。

---

## 5. 详细设计

### 5.1 Part 1：四分支判定树（放开直调）

替换 `AgentExecutor.cs:895-906`。判定顺序固定，不可颠倒：

```
context.ToolSettlement is { } settlement ?
  ├─ B: settlement.Contains(name)          → 放行（新增；覆盖原 inline 与已加载）
  ├─ C1: !_tools.IsToolEnabled(name)       → 拒绝（已禁用）
  ├─ C2: _tools.GetTool(name) is not null  → 拒绝（已注册但不在本轮结算）
  └─ D: 其余                               → 拒绝（不存在，提示检索）
: （无结算）→ 不拦（现状不变）
```

**为什么可以只用 `settlement.Contains(name)` 代替原来的 `IsCallable`：**

`ToolExposureEngine.Compute:138` 已把 `Hidden` 过滤出 `Tools`，因此 `Tools` 精确等于 Inline ∪ Deferred。而"本执行已加载"的工具在 `Tools` 里仍是 `Deferred` 模式（加载不改 `tools[]`），同样 `Contains`。

`IExecutionToolScope` 从此**不参与**"能否调用"判定，只保留：

| 成员 | 放开后职责 |
|---|---|
| `IsCallable` | 仅表示"本执行内已获得 schema 授权"，用于 §5.3 判定是否需要附定义 |
| `IsDeferred` | 用于生成提示文案 |
| `DeferredTools` | 不变，`tool_search` 的检索语料来源 |
| `TryLoad` / `LoadedIds` | 保留但仅作审计（见 §9 边界 2） |

**大小写必须对齐**：`settlement.Contains` / `SendsSchema` 是 OrdinalIgnoreCase，而 `ToolManager.GetTool` / `ExecuteAsync`（`ToolManager.cs:587`、`:638`）是序号大小写敏感。模型写 `Read` 时会通过 B 分支、再由 ToolManager 返回裸的"工具不存在: Read"。

因此 C 分支的查找必须自带大小写不敏感：

```csharp
// 判定 C2 时不得直接用 _tools.GetTool(name)
var registered = _tools.GetRegisteredTools()
    .Any(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
```

### 5.2 拒绝文案

集中在 `AgentExecutor.ResolveVisibilityRejection(name, context)` 一处：

| 分支 | 判定 | 文案 |
|---|---|---|
| C1 | `!IsToolEnabled` | `工具 '{name}' 已被禁用；可在工具设置中启用后重试。` |
| C2 | 已启用但不在结算 | `工具 '{name}' 在当前 Agent 或会话中不可用；这不是检索可以解决的问题，请直接使用已提供的工具完成任务。` |
| D | 未注册 | `无此工具 '{name}'。` + （`settlement.HasSearchTool` 时）`可用 {SearchToolId} 按关键词检索你要的能力（例如 query="删除文件"）。` |

**C2 必须明确写"检索也解决不了"**：`tool_search` 的语料取自 `settlement.DeferredTools`（`ToolSearchTool.cs:124`），被过滤的工具不在语料里，提示检索只会让模型反复空搜。

**D 的检索提示必须条件化**：Flat 模式下 `tool_search` 被隐藏（`ToolExposureEngine.ResolveMode:173`），不能提示不存在的能力。复用 `settlement.HasSearchTool`（`ISessionToolSchemaResolver.cs:102`）。

审计元数据（与既有 `exposure.loaded` 同族）：

```csharp
Metadata["visibility.reason"] = "disabled" | "not_in_settlement" | "not_found"
```

### 5.3 Part 3：参数错误时附带定义

#### 触发条件（三条全满足）

1. 本次调用在放行前**未下发过 schema**：`!settlement.SendsSchema(name)`
2. **且**本执行内未加载过：`!toolScope.IsCallable(name)`

> 第 2 条是必须的：deferred 工具经 `tool_search detail=full` 加载后，在结算里仍是 `Deferred`，`SendsSchema` 依然为 `false`。若只看第 1 条，会给"刚刚 detail=full 拿到过定义"的工具重复附 2–8KB。

3. **失败**——两条出口都要覆盖：

| 出口 | 处理 |
|---|---|
| `result.Success == false`（`AgentExecutor.cs:974-1013`） | 附定义 |
| `catch (Exception)`（`:956-972`） | 附定义 |

**但要排除纯噪声失败**，否则给超时/重试耗尽/权限拒绝也白附 2–8KB：

```csharp
static bool IsParamLikeFailure(ToolResult r) =>
    !r.Success
    && !(r.Metadata?.ContainsKey("timeout") == true)
    && !string.Equals(r.Title, "重试耗尽", StringComparison.Ordinal);
```

- 权限拒绝（`ToolManager.cs:686-711` 返回的 `Failure`，`Error` 为 `"Permission denied"`）：**不附**。定义帮不上忙。
- 超时（`Metadata["timeout"] == true`）：**不附**。
- 重试耗尽（`Title == "重试耗尽"`）：**不附**。
- 抛异常：**附**（`JsonException` 是典型的缺参/类型错）。

#### 定义来源

本轮冻结快照，不重新结算：

```csharp
var exposed = settlement.Tools.FirstOrDefault(
    t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
var definition = exposed?.Schema;   // FunctionToolSchema，与 ToolSearchTool.GetFullDefinition 同源
```

#### 大小上限

序列化后超过 **8192 字符**则不附，改为：

> `定义过大，请用 {SearchToolId} query='{name}' detail=full 获取。`

防止 `question`（82 行 schema）这类工具把上下文顶爆。

#### 格式

扩展 `ToolResultFormatting.ToModelContent`（`ToolResultFormatting.cs:30`），在 `<error>` 之后加一块：

```xml
<tool_result name="cron_create" status="failed">
<notice>该工具调用未成功；以下为错误信息，不是工具返回的数据。请据此修正后重试。</notice>
<error>缺少必需参数: taskType</error>
<tool_definition>
{"type":"function","function":{"name":"cron_create","description":"…","parameters":{…}}}
</tool_definition>
</tool_result>
```

约束：

- definition 正文用与 `error` 相同的 `EscapeBody`（`:78`，把 `</` 转成 `<\/`），避免提前闭合。
- 新增可选参数 `string? toolDefinition = null`，默认 null → 现有调用点与 `ToolResultFormattingTests` 零改动（测试 8 要求逐字节相等）。
- 放在 `<error>` 之后：notice 说"以下为错误信息"，紧跟错误，符合阅读顺序。

### 5.4 持久化链路（前缀缓存的关键）

`ModelContent` 是计算属性（`MessageEventTypes.cs:334`），跨轮重建走另一条路（`ExecutionRunner.cs:1079`）。若 definition 只存在于当轮事件，历史重建就丢失它 → 下一轮模型看到的历史与本轮不同 → 前缀全废。

| 层 | 改动 |
|---|---|
| `ToolCallEvent`（`MessageEventTypes.cs:290`） | 新增 `public string? ToolDefinition { get; init; }`；`ModelContent` 透传 |
| `SessionToolCall`（`SessionMessage.cs:93`） | 新增 `[JsonPropertyName("tool_definition")] public string? ToolDefinition { get; set; }` |
| `SessionToolCall.Clone()`（`SessionMessage.cs:156-184`，**逐字段枚举**） | **必须显式拷贝**，否则落盘/读回后丢失 |
| `ChatEventTracker.ApplyEvent`（`:112-165`） | `if (toolCall.ToolDefinition != null) existing.ToolDefinition = toolCall.ToolDefinition;` |
| `ExecutionRunner` 历史重建（`:1079`） | 传 `tc.ToolDefinition` |

`ChatEventTracker` 的落盘判定（`:163-165` 的 `isNewToolCall || statusChanged || taskIdFilled`）不需要额外条件：definition 与状态变化同一次事件写入。

### 5.5 Part 1 配套：关闭 promotion

`ToolExposureEngine.ResolveMode`（`:169-179`）当前把 `discovered` 视为 inline。新增配置并默认关闭：

```csharp
// Tools.Exposure.PromoteOnSearch，默认 false
if (IsSearchTool(id) || pinned.Contains(id)) return ToolExposureMode.Inline;
if (cfg.PromoteOnSearch && discovered.Contains(id)) return ToolExposureMode.Inline;
return ToolExposureMode.Deferred;
```

副作用与处置：

| 现象 | 处置 |
|---|---|
| `IToolDiscoveryStore.Add` 仍被 `tool_search` 调用（`ToolSearchTool.cs:174`） | 保留调用：`Pending` 累积但不改变视图。作为观测指标（"模型想加载什么"）。若确认无价值，后续可让 flag 为 false 时跳过写入 |
| `PromotePending`（`ExecutionRunner.cs:766`） | 保留调用，无待生效项时是空操作 |
| `SessionToolExposureState` 的 `Pending`/`Discovered` | 保留（持久化兼容 + `19` §4 的 `Commit` 链路不变） |
| `SchemaSnapshotEvent.DeferredToolCount` | 数值稳定（不再随搜索下降），可用来验证 promotion 确实关闭 |

**同一 Agent / 同一场景内 `tools[]` 恒定**：pinned 是静态配置，`discovered` 不再增长，其余内联集为空。只有用户主动切换 Agent 或场景才会变——那是可接受的显式变化。

### 5.6 Part 2：工具目录迁入上下文源管道

#### 新增 `ToolCatalogContextSource`

| 成员 | 值 |
|---|---|
| 位置 | `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/ToolCatalogContextSource.cs` |
| `Key` | `new(typeof(ToolCatalogContextSource).FullName!)` |
| `Scope` | `ContextSourceScope.Session`（内容随会话结算变化，不跨会话复用） |
| `Target` | `ContextSourceTarget.Message` |
| `ValueSchemaVersion` | `1` |
| `MaxBaselineChars` | `0`（截断由 `ToolExposureEngine` 的 `CatalogBudgetChars` 完成，见 `21` §2.4） |
| 实现接口 | `IContextSource`, `IContextSourceMessageMetadata` |
| DI | `services.AddScoped` + `TryAddEnumerable`（与 `SkillsContextSource` 同法，见 `SkillsModule.cs:111-113`） |

#### 输入来源：扩展 `IContextExecutionScope`

`IContextExecutionScope` 是执行级钉住事实的唯一入口（`IContextExecutionScope.cs:8-14` 明确"宿主在装配之前填充一次"）。新增：

```csharp
/// <summary>
/// 本轮执行的工具暴露视图（结算产物）。装配器与工具看到同一份，执行期间不漂移。
/// 宿主未接入结算时为 null（源按不可用处理，保留上次已叙述内容）。
/// </summary>
SessionToolSchemaResolution? ToolSettlement { get; set; }
```

**时序是成立的**：`ExecutionRunner` 中结算在 `BuildExecutionContextAsync`（`:193`）内完成，`executionScope` 在 `:227-235` 填充，装配在 `:277`。结算早于装配。

#### 读取与渲染

```csharp
ReadAsync:
  settlement = _executionScope.ToolSettlement
  if (settlement is null) → Unavailable          // 不当作"目录已删除"
  canonical = { "namespaces": [{ "name", "tools": [sortedIds] }] }   // 不含描述，保证稳定
  payload   = settlement.CatalogText                                  // 仅本次渲染用，不落盘
  if (CatalogText 为空 && DeferredToolCount == 0) → Removed
  else → Value

RenderInitial(value) → value.Payload as string（即 CatalogText）
Render(previous, current) → ToolCatalogDeltaRenderer.Render(previousCanonical, currentCanonical)
```

**为什么 `canonical` 只放命名空间与 id、不放描述**：描述可能被 `tool.definition` Hook 改写（`ToolManager.cs:430-458`），放进 canonical 会让无关的措辞变化触发叙述。目录正文（`payload`）已经承载描述，只在初始渲染用一次。

#### delta 渲染器

新增 `ToolCatalogDeltaRenderer`，唯一格式化入口（对齐 `SkillCatalogFormatter` 的纪律）：

```
新增：### ns (+3)  tool_a, tool_b, tool_c
移除：### ns (-2)  tool_x, tool_y
变更：元数据变更（命名空间整体出现/消失）→ 该命名空间整段重述
无法表达为 delta → 返回 null（不叙述，保留旧叙述；下一条相关变化再合并）
```

**顺序稳定**：命名空间按 ordinal 排序，工具按 id ordinal 排序。同一 canonical 重复渲染逐字节相等（`IContextSource.cs:5` 的硬要求）。

#### 删除旧落点

| 删除项 | 原因 |
|---|---|
| `ToolCatalogSectionContributor`（`src/hosting/Seeing.Agent.Hosting/Prompts/`） | 目录不再进系统提示词 |
| `HostingServiceCollectionExtensions.cs:97` 的注册 | 同上 |
| `AgentExecutor.cs:598-600`（`promptContext.Sections[PromptSectionNames.ToolCatalog] = ...`） | 同上 |
| `PromptSectionNames.ToolCatalog` | 若无其它消费者则一并移除；否则保留常量并注明"仅上下文源使用" |

**必须先确认 `ToolCatalogSectionContributorTests` 的处理方式**（见 §7）。

`settlement.CatalogText` **保留生成**：它仍是唯一渲染点（`ToolExposureEngine.BuildCatalog`），只是消费方从提示词变成上下文源。

### 5.7 文案：不止目录，提示词与工具描述也要改

§5.2 改了拒绝文案，但驱动"先检索"的还有三处强提示。不改它们，模型行为大概率照旧先检索，目标收益落空：

| # | 位置 | 现状 | 改为 |
|---|---|---|---|
| 1 | `BuiltInAgents.cs:63`、`:330` | "先用 `tool_search` 检索" | "工具不在 `tools` 中也可直接调用（目录已给参数签名）；只有不确定工具名时才用 `tool_search`" |
| 2 | `ToolSearchTool.Description`（`ToolSearchTool.cs:51-55`） | "需要的工具不在 tools 中时先用它" | "按关键词查找工具（不确定工具名时用）；命中 `detail=full` 可取得完整定义" |
| 3 | `ToolSearchTool` 命中行（`:208-212`） | "已加载：本次执行内可直接调用；下一次执行起自动内联" | "已返回完整定义，可直接调用"（`PromoteOnSearch=false` 时**不得**承诺"下一次自动内联"——那是假承诺） |

⚠️ 第 2 项会改变内联 schema 文本（`tool_search` 是 pinned，其 description 进 `tools[]`）。这是一次性的启动成本，不是每轮变化。

⚠️ 核对 `PromptRoutingTests.cs:68`（"提示只允许出现可用工具"）是否受影响。

### 5.8 目录开场白

`ToolExposureEngine.BuildCatalog`（`:295-297`）当前开场白：

> 以下工具当前未内联进本次请求的 schema，**未加载前不能直接调用**。先调用 tool_search 检索…

放开后是**错误承诺**。改为：

> 以下工具当前未内联进本次请求的 schema，但**可以直接调用**——已给出参数名与必填标记，按签名传参即可。若参数不符，错误信息会附带完整定义供你修正。需要提前查看完整定义时用 `tool_search`。

无检索入口时（`searchAvailable == false`）的变体同步改写，删除"否则不能调用"暗示。

---

## 6. 改动清单

### Part 1（放开直调）

| # | 文件 | 改动 | 规模 |
|---|---|---|---|
| 1 | `AgentExecutor.cs:895-906` | 四分支 gate；`ResolveVisibilityRejection`；`BuildVisibilityMetadata` | ~55 行 |
| 2 | `AgentExecutor.cs:974-1013` + `:956-972` | 两条失败出口附 definition；`IsParamLikeFailure` | ~30 行 |
| 3 | `ToolExposureEngine.cs:169-179` | `ResolveMode` 接入 `PromoteOnSearch` | ~5 行 |
| 4 | `SeeingAgentOptions` / `EffectiveExposureConfig` | 新增 `PromoteOnSearch`（默认 false） | ~6 行 |
| 5 | `IExecutionToolScope.cs` | 注释：`IsCallable` 语义收窄 | 注释 |

### Part 3（定义回灌）

| # | 文件 | 改动 |
|---|---|---|
| 6 | `ToolResultFormatting.cs:30` | 新增 `toolDefinition` 可选参数 + `<tool_definition>` 块 |
| 7 | `MessageEventTypes.cs:290` | `ToolCallEvent.ToolDefinition`；`ModelContent` 透传 |
| 8 | `SessionMessage.cs:93` | `SessionToolCall.ToolDefinition` |
| 9 | `SessionMessage.cs:156-184` | `Clone()` 显式拷贝新字段（**易漏**） |
| 10 | `ChatEventTracker.cs:112-165` | 映射 definition |
| 11 | `ExecutionRunner.cs:1079` | 历史重建传 definition |

### Part 2（目录迁管道）

| # | 文件 | 改动 | 规模 |
|---|---|---|---|
| 12 | `ToolCatalogContextSource.cs`（新建） | `IContextSource` + `IContextSourceMessageMetadata` | ~110 行 |
| 13 | `ToolCatalogDeltaRenderer.cs`（新建） | canonical diff → 叙述文本，唯一格式化入口 | ~80 行 |
| 14 | `IContextExecutionScope.cs` | 新增 `ToolSettlement` | ~5 行 |
| 15 | `ContextExecutionScope`（实现） | 实现新属性 | ~2 行 |
| 16 | `ExecutionRunner.cs:227-235` | 填充 `ToolSettlement` | ~2 行 |
| 17 | `HostingServiceCollectionExtensions.cs:97` | 删除 `ToolCatalogSectionContributor` 注册；新增上下文源注册 | ~4 行 |
| 18 | `ToolCatalogSectionContributor.cs` | 删除 | — |
| 19 | `AgentExecutor.cs:598-600` | 删除目录注入 | ~3 行 |

### 文案

| # | 文件 | 改动 |
|---|---|---|
| 20 | `ToolExposureEngine.cs:295-297` | 开场白改写（含无检索入口变体） |
| 21 | `BuiltInAgents.cs:63`、`:330` | 去掉"先用 tool_search" |
| 22 | `ToolSearchTool.cs:51-55`、`:208-212` | 定位改为"发现"，去掉"自动内联"假承诺 |
| 23 | `docs/architecture/19` §4 | 修正行 43/44 矛盾 |

**依赖顺序**：6 → 7 → 8 → 9 → 10 → 11（持久化链路先通）→ 1 → 2 → 3 → 4 → 12 → 13 → 14 → 15 → 16 → 17 → 18 → 19 → 20 → 21 → 22 → 23。

---

## 7. 测试计划

### 必须改写

| 测试 | 现状 | 改为 |
|---|---|---|
| `AgentExecutorToolVisibilityTests.ExecuteAsync_DeferredTool_ShouldRejectAndPointToSearch`（`:42`） | 断言 deferred 被拒 | 断言 **deferred 放行** |
| `ExecuteAsync_ToolAbsentFromSettlement_ShouldRejectWithoutSearchHint`（`:109`） | 未注册，断言不提示 search | 拆两条：未注册 → 提示（D）；已注册不在结算 → 不提示（C2） |
| `ExecuteAsync_LoadDeclaredWithoutCapability_ShouldBeIgnored`（`:86`） | 用"hidden_tool 被拒"证明加载声明被忽略 | **会失去测意**（hidden_tool 反正能调）。改用不在结算的工具作探针 |
| `ToolExposureEngineTests`（`:198-205`） | 断言目录含"未加载前"、"本次执行内即可直接调用" | 改为断言"可以直接调用"且**不含**"未加载前" |
| `ToolCatalogSectionContributorTests`（整文件） | 断言分节贡献者行为 | **随贡献者删除而删除**；如需保留 DI 断言，改为断言上下文源已注册 |

### 必须新增

| # | 测试 | 断言 |
|---|---|---|
| 1 | `DeferredTool_DirectCall_ShouldSucceed` | deferred 直调成功，且 `SendsSchema(name)` 为 false |
| 2 | `DeferredTool_Failure_ShouldAttachDefinition` | `ToolDefinition` 非空，含工具名与 `parameters` 键 |
| 3 | `DeferredTool_ExceptionFailure_ShouldAttachDefinition` | 抛 `JsonException` 的工具也附定义（覆盖 `:956` 出口） |
| 4 | `TimeoutFailure_ShouldNotAttachDefinition` | `Metadata["timeout"]=true` 时不附 |
| 5 | `RetryExhausted_ShouldNotAttachDefinition` | `Title="重试耗尽"` 时不附 |
| 6 | `PermissionDenied_ShouldNotAttachDefinition` | 资源门拒绝时不附 |
| 7 | `InlineTool_Failure_ShouldNotAttachDefinition` | 已下发 schema 时不附 |
| 8 | `LoadedTool_Failure_ShouldNotAttachDefinition` | 经 detail=full 加载（`SendsSchema=false` 但 `IsCallable=true`）时不附 |
| 9 | `OversizedDefinition_ShouldFallbackToSearchHint` | >8192 字符不附，改提示 |
| 10 | `DirectCall_HistoryRebuild_ShouldMatchLiveContent` | 同一 `SessionToolCall` 经两条路径产出**逐字节相等** |
| 11 | `ToolDefinition_ShouldSurviveSaveAndReload` | **经 `SessionManager.SaveAsync` → 读回**后 `ToolDefinition` 仍在（覆盖 `Clone()` 漏字段，见 §9 风险 1） |
| 12 | `DisabledTool_ShouldRejectAsDisabled` / `AgentFilteredTool_ShouldRejectWithoutSearchHint` | C1 / C2 分开 |
| 13 | `NoSearchTool_NotFoundMessage_ShouldNotMentionSearch` | Flat 模式下 D 文案不含检索提示 |
| 14 | `CaseInsensitive_DeferredCall_ShouldNotFallToNotFound` | 模型写 `Read` 时归入 B 或正确的 C/D，不落入裸"工具不存在" |
| 15 | `ToolResultFormattingTests` 增 | definition 在 `<error>` 之后、`</` 被转义、`toolDefinition=null` 时与旧版逐字节相同 |
| 16 | `PromoteOnSearchFalse_ToolsArrayShouldBeStable` | 连续两轮（无 Agent/场景切换）`InlineSchemas` 顺序与内容完全一致 |
| 17 | `ToolCatalogContextSourceTests` | `Unavailable` 时不当作删除；canonical 稳定；`Removed` 时叙述删除 |
| 18 | `ToolCatalogDeltaRendererTests` | 新增/移除/整段重述三种；同一 canonical 渲染逐字节相等 |
| 19 | `CatalogDelta_ShouldBeMessageNotSystemPrompt` | 目录变化产生一条 `context.update` 消息，且系统提示词不变 |

### 回归守门

- `ExecutionRunnerBuildHistoryTests`（历史重建签名改变）
- `ExecutionToolScopeTests`（scope 语义未改，仅注释）
- `SessionToolSchemaResolverExposureTests`、`ToolTokenAccountingTests`（`CatalogText` 仍生成）
- 上下文管道全量测试（新增源不得破坏 `ContextAssembler` 的字节稳定断言）

---

## 8. 风险与已知边界

| # | 风险 | 说明 | 缓解 |
|---|---|---|---|
| 1 | **`Clone()` 漏字段** | `SessionToolCall.Clone()` 逐字段枚举（`SessionMessage.cs:156-184`），漏了就"内存对、读回丢"，测试全绿但线上前缀照断 | 测试 11 强制走 SaveAsync + 读回 |
| 2 | **C/D 判定顺序颠倒** | `GetRegisteredTools()` 含被禁用项，先查注册会把"禁用"说成"不存在" | 顺序固定：`IsToolEnabled` → 注册表 → 未找到 |
| 3 | **大小写不一致** | 结算 ignoreCase，`ToolManager` 序数敏感 | C 分支自带 ignoreCase 查找（§5.1） |
| 4 | **复杂工具反复试错** | `cron_create.schedule.type`、`todowrite.status` 无法从签名猜出 | 失败即附定义；`LoopDetector` 兜底 |
| 5 | **definition 顶爆上下文** | `question` schema 约 3–4KB | 8192 字符硬上限 + 回退检索提示 |
| 6 | **目录首条消息插在会话中段** | `Target=Message` 首次接入时会插一条消息；若会话已有历史，这一次仍断 | 一次性成本，之后靠 delta；新会话无此问题 |
| 7 | **`tool_search` 使用率下降** | 直调成功后再不触发检索 | 期望结果。`DeferredToolCount` 与 `Pending` 累积量仍可观测 |
| 8 | **`AgentDefinition.AllowedTools` 仍是软约束** | 执行层只拦"不在结算内"；若有人把过滤从结算移除，白名单会静默失效 | 本设计不放松任何一层；C2 分支是唯一的运行时守卫 |
| 9 | **`Pending`/`Discovered` 变成只写不读** | promotion 关闭后 `Discovered` 不再影响视图 | 保留结构（持久化兼容）；`PromoteOnSearch=true` 可一键回到旧行为 |
| 10 | **`ToolCatalogSectionContributor` 删除是破坏性变更** | 第三方宿主若自行注册该贡献者会编译失败 | 记入 release notes；常量 `PromptSectionNames.ToolCatalog` 视消费者决定去留 |
| 11 | **Scheduler 旁路行为差异** | 旁路走 `ReadOnly`，不写状态、不叙述（`SchedulerContextAssembly.cs:89-91`）；`Target=Message` 的源在只读模式下不进系统基线，旁路拿不到目录 | 与项目指令/记忆源的现状一致（旁路本来就不注入历史消息）；确认后可接受 |

---

## 9. 验收标准

1. deferred 工具直调成功时，`ChatRequest.Tools` 不含该工具。
2. 参数错误时，模型下一轮能从 `<tool_definition>` 得到完整参数契约，无需再调 `tool_search`。
3. 同一会话连续两轮（无 Agent/场景切换），`ChatRequest.Tools` 顺序与内容**完全一致**。
4. 工具目录变化产生一条 `context.update` 消息，**系统提示词逐字节不变**。
5. 同会话相邻两轮，历史重建出的 tool content 与本轮产出**逐字节相同**；且经 SaveAsync 读回后仍相同。
6. C1/C2/D 三条拒绝文案互不混用；C2 不含 `tool_search` 字样；D 在无检索入口时不含。
7. `dotnet build Seeing.Agent.slnx` = 0 error；`dotnet vstest` 相关程序集全绿。
8. `docs/architecture/19` §4 的 exposure/permission 表述不再自相矛盾。

---

## 10. 交付后观察项

| 指标 | 落点 | 用途 |
|---|---|---|
| `SchemaSnapshotEvent.ToolIds` 的轮间差异 | 已持久化 | 验证 `tools[]` 是否真的恒定 |
| `SchemaSnapshotEvent.DeferredToolCount` | 已持久化 | 稳定 = promotion 确实关闭 |
| `ToolCallEvent` 中 `tool_search` 占比 | 已持久化 | 预期下降 |
| `Metadata["visibility.reason"]` 分布 | 已持久化 | C1 / C2 / D 是否对症 |
| 附 definition 的失败占比 | 已持久化 | §5.3 的实际触发频率与收益 |
| `SessionToolExposureState.Pending` 累积量 | 会话 JSON | 模型想加载什么（检索意图） |

---

## 11. 分阶段落地建议

| 阶段 | 内容 | 独立价值 |
|---|---|---|
| P1 | Part 3（定义回灌链路）+ Part 1 的四分支 gate | 免检索直调生效；缓存问题仍在 |
| P2 | `PromoteOnSearch=false` | `tools[]` 恒定，缓存半解 |
| P3 | Part 2（目录迁管道）+ 文案 | 系统提示词恒定，缓存全解 |

P1 与 P2 可独立验证；P3 依赖 P2（若 promotion 仍开，目录仍会每搜一次就变一次，迁管道收益被抵消）。
