# 上下文源管道（Context Source Pipeline）设计 v2

- 日期：2026-10-24
- 状态：待终审
- 范围：通用管道 + 项目指令源（Message 落点）+ 技能源（含技能目录单一真相源）
- **不纳入**：工具目录（理由见 §2 非目标；机制为其留位，接入时无需改管道）
- 取代并删除：`2026-10-24-skill-catalog-single-source-design.md`（内容并入 §11）

> v2 相对 v1 的变化来自两轮独立审查（架构补丁审计 + 可行性对抗验证）。结论：v1 的框架契约需要重做，技能目录部分方向正确。本文按根因重写契约形状，不再叠加旋钮。

## 1. 背景

会进入模型上下文、且随运行时变化的内容，目前各用各的办法，没有统一语义：

| 内容 | 现状 | 问题 |
|------|------|------|
| 项目指令（AGENTS.md 等） | `InstructionManager` 指纹 + 变更时注入 user 消息 | 机制只服务一种内容；压缩后永久丢失（见 §9.3） |
| 技能清单 | 每次请求从内存字典渲染进系统提示词 | 无热更新；`ConcurrentDictionary` 顺序每次请求可能不同；安装后不生效 |
| 技能正文 | 发现时读入 `SkillInfo.Content` | 改文件不生效 |
| 技能状态 | 内存字典 + `DynamicSkillCommand` 副本 + `AcpDynamicSkillCommand` 副本 + UI 列表副本 | 四份真相；UI"刷新"是假刷新；`embedded://` 前缀当类型系统 |

两类失败同时存在：**正确性**（装了不生效、改了不起作用、压缩后指令消失）与**成本**（系统提示词位于请求最前，任变化都使整段历史的前缀缓存失效）。

## 2. 目标 / 非目标 / 反目标

### 目标

1. 通用、可扩展的更新机制：新内容类型只需实现一个源并注册，**管道零改动**。
2. 基线字节稳定 → 内容变化不击穿前缀缓存；变化只产生一条增量叙述。
3. 每种内容一个权威状态；删除有显式语义；不依赖"猜"（类型、层级、落点均显式）。
4. 技能：批量安装后**下一条用户消息**可用；UI 与运行时同源；写入收口。
5. 修掉压缩后项目指令丢失的既有 bug。

### 非目标

- **工具目录本期不接入**。理由（审查结论）：它是"低频、单调、有界"的事实（每会话一次升级 + 每次 `tool_search` 新发现一次），纳入后必须把预算算法（分组保底、最短优先轮转、`shown` 计数）改造成"源提供的选择策略"，并重定义被 `TokenBudgetManager` 消费的 `CatalogTokens`——收益小于风险。机制预留位置（同一 `IContextSource`），后续接入不改管道。
- 不做远程内容目录同步。
- 不改工具 schema 结算（`SessionToolSchemaResolver` 仍是唯一出口）、不改权限与执行级可调用集。
- 不做跨进程强一致。

### 反目标

- 同一事实两条产出路径（含"没有基线就回退渲染"）。
- 字符串前缀/魔法键当类型系统或隐藏 API。
- 读取失败被当成"内容已删除"。
- 由框架猜测调用方意图（层级、落点、是否叙述）。
- 持久化的派生状态可以静默漂移。

## 3. 参考与本地资产

**OpenCode V2**（源码为证）：系统提示词 = `[agent.system, initial]`，`initial` 从**会话存储的基线值**渲染（`InstructionState.initial` 注释：*"Renders the epoch baseline shown at the start of every model request"*）；变化渲染为**增量消息**（新增 / 变更 / 删除 "must not be used" + supersede）；每源按 sha256 存 per-session；压缩推进 epoch；源按"越共享越靠前"排序。

**本仓库已有同构资产**：`IInstructionManager` + `InstructionFingerprintStore` + `SessionMetadataKeys.InstructionFingerprints`（指纹 → 只注变更 → 存会话元数据 → fork 复制 / ClearMessages 清除）。本文把它抽象成平台能力。

## 4. 适用范围（运行时矩阵）

| 运行时 | 是否参与协议 | 行为 |
|---|---|---|
| 主链（`ExecutionRunner`，含子代理/子会话） | 是 | 完整：读源 → diff → 叙述 → 提交状态 |
| 一次性旁路（`Scheduler` / `Heartbeat`） | **只读模式** | 渲染当前值作为本次请求的基线，**不写状态、不叙述、不推进**。它们自建消息列表（`AgentJob.cs:316-350`），会话历史不参与本轮输入，若写状态会造成"已叙述但模型没看到"的丢失更新 |
| ACP passthrough | 否 | `AcpPassthroughExecutor` 不构建系统提示词（`AcpPassthroughExecutor.cs:82-95`），无落点；跳过且不写状态，文档明示不支持 |
| 极简宿主（未接入 assembler） | 否 | 不改任何现有行为（源不被装配），Release Notes 说明 |

`EnsureFreshAsync` 因此有两个显式模式：`Persist`（默认）与 `ReadOnly`。不是两条实现路径，而是同一装配函数的两种提交策略。

## 5. 契约（Abstractions，零实现）

```csharp
public enum ContextSourceScope { User = 0, Project = 1, Session = 2 }
public enum ContextSourceTarget { System = 0, Message = 1 }

/// <summary>键由源类型派生（如 typeof(SkillsContextSource).FullName），不做魔法字符串；
/// 状态里同时记录值 schema 版本，改名/改形状都有明确迁移路径。</summary>
public readonly record struct ContextSourceKey(string Value)
{
    public static ContextSourceKey For<T>() where T : IContextSource
        => new(typeof(T).FullName!);
}

/// <summary>Initial = 从未建立过基线；Value = 有值；Unavailable = 读取暂不可用（保留旧值）；
/// Removed = 明确消失（必须叙述"不可再使用"）。</summary>
public enum ContextValueState { Initial, Value, Unavailable, Removed }

/// <summary>
/// Canonical：规范化 JSON，仅用于 diff 与持久化（不含必须渲染的正文）。
/// Payload：本次渲染所需的完整输入（如指令正文），**不持久化**；读取时重新取得。
/// Payload 为 null 时禁止重新渲染该源（只能重放已存文本）。
/// </summary>
public sealed record ContextSourceValue(
    ContextValueState State,
    JsonElement? Canonical,
    object? Payload);

public interface IContextSource
{
    ContextSourceKey Key { get; }
    ContextSourceScope Scope { get; }

    /// <summary>值 schema 版本：形状变化时递增。不匹配 → 丢弃该源状态并按新形状重建（一次性全量叙述）。</summary>
    int ValueSchemaVersion { get; }

    /// <summary>渲染落点。System = 系统提示词基线；Message = 历史消息。</summary>
    ContextSourceTarget Target { get; }

    /// <summary>基线文本上限（字符，0 = 不限）。截断发生在渲染内部，截断结果即存库文本。</summary>
    int MaxBaselineChars { get; }

    Task<ContextSourceValue> ReadAsync(CancellationToken ct);

    /// <summary>首次渲染。previous 为 Initial 时表示"从未建立"。</summary>
    string RenderInitial(ContextSourceValue value);

    /// <summary>
    /// 唯一的变化渲染动词。previous.State == Initial 表示"在此之前没有基线"。
    /// 返回 null 表示"该源的变化不需要叙述"，此时**不推进**该源已叙述值（禁止静默丢更新）。
    /// 措辞由源决定（例如工具目录的"已升级为内联"与"已下线"是不同语义）。
    /// </summary>
    string? Render(ContextSourceValue previous, ContextSourceValue current);
}
```

框架入口（同文件，接口无实现）：

```csharp
public enum ContextAssemblyMode { Persist, ReadOnly }

public sealed record ContextAssemblyResult(
    string SystemBaseline,                 // 直接可用的系统提示词附加文本（May be empty）
    string? HistoryBaseline,               // Target=Message 且需要落历史时的初始渲染
    string? DeltaText,                     // 需追加的增量叙述（Persist 模式且确有变化时）
    IReadOnlyList<string> ChangedSourceKeys,
    IReadOnlyList<string> Issues);

public interface IContextAssembler
{
    Task<ContextAssemblyResult> EnsureFreshAsync(
        SessionData session, ContextAssemblyMode mode, string? insertBeforeMessageId,
        CancellationToken ct);

    /// <summary>请求构建时的系统提示词附加文本（从已存状态读取，字节稳定）。不读源、不算 diff。</summary>
    string BuildSystemBaseline(SessionData session);
}
```

契约里**只有这两个动词**。没有 `AdvanceEpochAsync`、没有 `Rewind`：生命周期由 §9 的状态机承担，避免"留着但禁止调用"的双路径。

源读取所需的执行期事实（技能快照）**不进契约**：`ExecutionRunner` 每轮 `CreateScope` 后注册 scoped 端口，源按需注入。这样第四个源需要新的执行期事实时，加自己的 scoped 端口即可，不动 `ContextSourceValue`/`IContextSource`/装配签名。

```csharp
/// <summary>每轮执行作用域内注册：本轮钉住的技能快照。</summary>
public interface ISkillCatalogPin { SkillCatalogSnapshot Snapshot { get; } }
```

## 6. 状态模型

per-session 状态序列化为 JSON，存 `SessionData.Metadata[SessionMetadataKeys.ContextBaseline]`（`Dictionary<string,string>`，与 `execution_history`、指令指纹同型；文件存储整对象序列化，无大小限制）：

```jsonc
{
  "stateVersion": 1,                 // 状态结构版本，升级时整体丢弃重建
  "pending": ["Compaction"],         // 待处理的生命周期事件（位集合，可合并，不互相覆盖）
  "sources": {
    "Seeing.Agent.Skills.SkillsContextSource": {
      "valueSchemaVersion": 2,
      "baselineText": "## Skills\n...",   // 已渲染基线；字节锁与重放的唯一来源
      "baselineValues": { },              // canonical JSON（不含 Payload）
      "currentValues":  { },              // 已向模型叙述过的值
      "baselineMessageId": null,          // Target=Message 时初始渲染所在消息
      "deltaLedger": [                    // 每条已叙述增量的快照，用于精确回退
        { "messageId": "msg_...", "values": { } }
      ]
    }
  }
}
```

- **只持久化 canonical + 已渲染文本**；`Payload` 每次读取重新取得，不落盘（避免"指令正文存两遍"与"存了却不用于渲染"的自相矛盾）。
- 只存紧凑字段：技能 `id/name/description`；指令 `path/hash/mtime`。指令正文属于 `Payload`。
- `deltaLedger` 解决回退：回退到两条增量之间时，取**仍存活的最晚一条**账本条目作为 `currentValues`，只补叙后续差异。单值 `lastEmittedMessageId` 会重复叙述，已废弃。
- 新增元数据键 `context.baseline`；`SessionData.ClearMessages()` 清除它；`SessionForker` **不复制它**（分支属新 epoch，见 §9.5）；`SessionMetadataKeys` 补一条"会话派生元数据"的约定，原语层只按键名处理，不引入上层语义。

## 7. 装配与注入时序

**主链（`Persist`）**

落点固定为 `ExecutionRunner` 的「命令处理完成之后、`BuildHistoryFromSession` 之前」（现 `ExecutionRunner.cs:227-230`，即 `if (!commandConsumedExecution)` 分支内）。不放在结算之后立即执行，原因：`/clear`、`/compact` 会短路本轮，提前装配会把"状态说已叙述、消息随即被清除/替换"变成既有事实。

1. `CreateScope` → 注册 `ISkillCatalogPin`（装配前**捕获一次**快照）等 scoped 端口。
2. `EnsureFreshAsync(session, Persist, batch.FirstUserMessageId)`：
   - 处理 `pending` 生命周期事件（§9）；
   - 并行读全部源；`Unavailable` 不参与 diff；
   - canonical JSON（键排序、无空白、数字/转义规则写死）→ sha256 比对；
   - 有变化 → 对每个源调用 `Render`；返回 null 的源**不推进**其 `currentValues`；
   - 合成**一条**增量消息（`<context-update>` 信封 + 源键元数据），插入 `batch.FirstUserMessageId` 之前；
   - 更新 `currentValues` 与 `deltaLedger`。
3. 提交：消息与状态都写在同一个 `SessionData` 实例上，由本轮既有的一次 `SaveAsync` 落盘（文件存储是整对象写入）。**原子性表述如实**：最坏情况是"重复叙述一条"，不存在"丢叙述"；不宣称不存在的多操作事务。

**请求构建**：`AgentContext.ContextBaseline = BuildSystemBaseline(session)`；`AgentExecutor` 透传到 `PromptContext.ContextBaseline`；`PromptBuilder` 在**变量替换与 `Trim` 之后**追加（见 §8.2）。

**空 `SystemPrompt` 的代理**：`AgentExecutor.cs:583` 目前整块跳过 `PromptBuilder`，导致基线丢失。改为：`agent.SystemPrompt` 为空但基线非空时，直接把基线作为 `SystemPrompt` 发给模型。

**事件顺序**：`ExecutionStartedEvent`（`:155`）早于装配点，UI 在该事件时刻看到的是"尚未插入增量"的会话。这是既有行为（现在指令注入也在其后），不作为回归；UI 依赖消息事件渲染，不依赖 Started 快照。

**失败路径变化**：今天指令注入发生在结算之前，因此"结算失败"时代码仍留下了指令；迁移后结算/命令失败则不装配、不叙述。这是有意的行为变化（执行没能发生，就不该宣称已经告知模型）。

## 8. 硬不变量

1. **规范化哈希**：canonical JSON（键排序、无空白、数字与转义规则固定，附 golden 测试），基于值不基于渲染。
2. **字节稳定（正确的观测点）**：基线文本在"变量替换之后"追加；源渲染文本中的 `{{...}}` 字面量不得被替换（追加发生在替换之后，天然满足）。测试锁定：同一执行内两次 `BuildSystemBaseline` 逐字节相等；同一快照重复渲染逐字节相等。
3. **版本语义二分**：
   - `ValueSchemaVersion` 或 `stateVersion` 不匹配 → **丢弃该源状态**，按新形状重建并全量叙述（一次预期的缓存失效）；
   - 同版本内重渲染结果与 `baselineHash` 不一致 → 沿用已存文本 + 告警（防"渲染不确定"）。
   - 两者不共用一把锁：意图变更走重建，不确定性走保留。
4. **截断即存库**：`MaxBaselineChars` 由源在渲染内部执行，截断后的文本就是持久化与哈希的对象；框架不再二次截断（否则临界点每轮都告警）。
5. **三态语义**：`Unavailable` 保留旧值、不叙述；`Removed` 必须渲染显式"不可再使用"；读取异常映射为 `Unavailable` 并记入 `Issues`。源不得复用会吞异常的辅助（见 §10.1）。
6. **有 diff 必有叙述**：`Render` 返回 null 时该源不推进已叙述值（下次仍会重试），杜杜绝"检测到变化但静默丢弃"。
7. **确定性顺序**：源按 `Scope` → `Key`（ordinal）排序；源内条目排序由源保证并锁定测试。
8. **单飞**：装配以 `SessionData` 上的乐观并发（版本号 CAS，交给 `ISessionManager.UpdateSessionAsync`）保证同一会话不被并发推进；不新造第二把会话级锁。
9. **钉住一致**：`SkillsContextSource` 读 `ISkillCatalogPin`，`skill` 工具解析也读同一个对象；禁止源内自读 `SkillCatalog.Current`。
10. **提交如实**：见 §7 步骤 3。

## 9. 生命周期（状态机，无隐藏 API）

`pending` 是**位集合**（可合并、不互相覆盖），由下一次 `Persist` 装配消费：

| 事件 | 写入点 | 处理 |
|---|---|---|
| 压缩（自动 / `/compact` / TokenBudget API） | `CompressionService.CompressAsync`（三条入口的唯一汇聚点；`CompactionRunner` 只覆盖其中两条，`TokenBudgetApi.cs:69` 直连 `CompressionService`） | 置 `Compaction`：`baselineValues := currentValues`，清 `deltaLedger`；`Target=Message` 的源在摘要之后**重放**初始渲染（修掉压缩后项目指令消失的既有 bug） |
| `/clear` | `SessionData.ClearMessages()` 清除元数据键 | 无状态 → 下次全量渲染（不需要 pending 标记，清除即足够） |
| 工作区切换 | 现有 `WorkspaceChange` 处理链 | 置 `WorkspaceChanged`：根集合变化 → 全量重算 canonical，差异按 §8.6 叙述 |
| revert | 无需写入 | 装配时按 `deltaLedger` 中仍存活的最后一条折叠 `currentValues`；初始渲染消息若被回退掉则重放 |
| fork | `SessionForker` 不复制该元数据键 | 新 epoch：下次装配全量渲染（旧增量在复制来的历史里属于过去的真实状态，不冲突） |
| 值 schema 升级 | `ValueSchemaVersion` 不匹配 | 丢弃该源状态重建 |

## 10. 内置源

### 10.1 项目指令源（`ProjectInstructionsContextSource`）

- `Target = Message`，`Scope = Project`。
- 读取：复用 `InstructionDiscovery` 的扫描，但**必须拿到逐路径结果**（`Loaded` / `Missing` / `Unreadable`）。现有 `TryReadAsync` 把所有异常吞成 `null`（`InstructionDiscovery.cs:120-143`），会让"文件被占用"被渲染成"指令已删除"——违反 §8.5。做法：在 Core 侧为指令源新增逐路径结果端口（`IInstructionFileReader`），发现器复用它；吞异常的那条路径不再被源使用。
- `Canonical`：`[{path, sha256, mtime}]`（排序后）。
- `Payload`：`[{path, content}]`（正文，仅本次渲染用）。
- 渲染：沿用 `ProjectInstructionsRenderer` 的信封与措辞（`<project-instructions>` + `<file>` 块），保证与今天一致。
- 与今天的行为差异（唯一一处）：**压缩后按存库文本重放**（今天会永久消失）。Release Notes 明示这是修复。
- `SkipInstructionInject` 语义：只跳过本源（`ChatOptions.SkipInstructionInject` 的唯一消费者 `ExecutionRunner.cs:125` 改为传给 assembler 的跳过集合，强类型，不用字符串键）。

### 10.2 技能源（`SkillsContextSource`）

- `Target = System`，`Scope = Project`（技能清单属于项目视角），`ValueSchemaVersion = 1`。
- 读 `ISkillCatalogPin.Snapshot`；`Canonical` = `[{id, name, description}]`（按 id ordinal 排序）；无 description 的条目不参与渲染。
- 渲染：`## Skills` 标题 + 一行使用说明 + 条目。**基线拥有整个 `## Skills` 区块**，因此 `SkillManager` 不再作为 `IPromptSectionContributor` 注册；若某个 agent 模板里已有 `## Skills` 标题，迁移时删除该占位（避免两个同名标题）。
- 变化叙述：新增 / 变更 / 删除（删除写明 "must not be used"）。
- 清单文本只有一个格式化入口 `SkillCatalogFormatter`：基线、`/skill`（无参数列表）、`/skills` 命令、`skill` 工具的错误提示（`SkillTool.cs:98-102`）全部复用它。

## 11. 技能目录（单一真相源）

### 11.1 问题（已核实）

四份状态（`SkillManager._skillInfos`、`DynamicSkillCommand` 内 `SkillInfo`、`AcpDynamicSkillCommand` 的 name/desc、`SkillsPage._allSkills`）；正文发现时缓存（`SkillManager.cs:278`）；`DiscoverSkillsAsync` 先 `Clear` 再填（`:216-219`）存在空窗；`RefreshSkillsAsync` 靠 `embedded://` 前缀保留注册技能（`:211-228`）；同名冲突静默覆盖（`:283-289`）；UI"刷新"只读内存（`SkillsPage.razor:197-207`）；`SkillLoader.LoadAsync` 切工作区时不清旧技能（`SkillLoader.cs:36-50`，只有 `ReloadAsync` 清）。

### 11.2 契约

```csharp
public enum SkillSource { Registered, ProjectFile, UserFile }

public sealed record SkillCatalogEntry(SkillInfo Info, SkillSource Source, string? ProviderId);
public sealed record SkillConflict(string Name, SkillSource Winner, string WinnerOrigin,
                                   SkillSource Loser, string LoserOrigin);
public sealed record SkillLoadIssue(string Path, string Reason);

public sealed record SkillCatalogSnapshot(
    long Version,
    IReadOnlyList<SkillCatalogEntry> Entries,
    IReadOnlySet<string> DisabledNames,
    IReadOnlyDictionary<string, ConfigLevel> DisabledScopes,  // 每技能被哪一级禁用（供 UI 显示）
    IReadOnlyList<SkillConflict> Conflicts,
    IReadOnlyList<SkillLoadIssue> Issues);

// 查询在接口上（Abstractions 零实现纪律）
public interface ISkillCatalog
{
    SkillCatalogSnapshot Current { get; }
    IDisposable Subscribe(Action<SkillCatalogSnapshot> onChanged);
    SkillCatalogEntry? Find(string name);
    bool IsEnabled(string name);

    Task<SkillCatalogSnapshot> EnsureFreshAsync(CancellationToken ct = default);   // 指纹未变则直接返回
    Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken ct = default);       // 强制重建
    Task<bool> RegisterAsync(SkillInfo info, string providerId, CancellationToken ct = default);
    Task<bool> UnregisterAsync(string name, CancellationToken ct = default);
}
```

`ISkillManager` **收缩为只读**：保留 `GetSkillInfo` / `GetAllSkillInfos` / `GetSkillDirectories` / `GetAllSkillInfosIncludingDisabled`；`AddSearchDirectory`、`DiscoverSkillsAsync`、`RegisterEmbeddedSkill`、`Unregister` 标记 `[Obsolete(error: true)]` 并指向 catalog（消除"类型系统不拦的第二条写路径"）。`SkillInfo.DirectoryPath`（Abstractions 内的实现，且引入 `System.IO`）迁移为 `SkillCatalogEntry.DirectoryPath`（由实现侧计算）。

### 11.3 单一写入者与触发

- `SkillCatalogCore`（`Seeing.Agent.Skills` 内，`ISkillCatalog` 唯一实现）：注册通道 + 磁盘通道两条输入，**插件注册优先**；同名文件不进入可调用集合，生成 `SkillConflict`（warning + UI 可见，不静默）。文件通道内部优先级显式：`<ws>/.seeing/skills` > `<ws>/.agents/skills` > `<ws>/skills` > `~/.seeing/skills` > `~/.agents/skills`。
- 刷新单飞（进程级 `SemaphoreSlim`，多会话并发执行需要）；构建新快照后 `Volatile.Write` 原子替换；**不再有 clear-then-fill**。
- 触发：`FileSystemWatcher`（每根一个，400ms 去抖；根不存在时监听最近存在的祖先，创建后重挂）**归 `SkillsModule.Activate/Deactivate` 管理**（`SkillLoader` 只是 `IComponentLoader`，无 `Dispose`，不承担生命周期）；执行开始前额外做一次廉价指纹校验（路径 + 长度 + `LastWriteTimeUtc`，同秒同长度编辑可能延迟一次，作为已知边界写入文档）。
- `SkillLoader.LoadAsync/ReloadAsync` 合并为一条幂等路径：解析根 → `RefreshAsync`。修掉切工作区不清旧技能的 bug。

### 11.4 写入收口（`ISkillWriter`）

```csharp
public sealed record SkillDraft(string Name, string Description, string Content,
                                IReadOnlyDictionary<string, string>? Metadata = null);
public sealed record SkillWriteResult(bool Success, string? Error, SkillCatalogEntry? Entry);

public interface ISkillWriter
{
    Task<SkillWriteResult> CreateAsync(SkillDraft draft, ConfigLevel level, bool overwrite, CancellationToken ct = default);
    /// <summary>原样写入 SKILL.md（保留未知 frontmatter）；用于在线安装的"仅 Markdown"分支。</summary>
    Task<SkillWriteResult> WriteMarkdownAsync(string name, string rawContent, ConfigLevel level, bool overwrite, CancellationToken ct = default);
    Task<SkillWriteResult> UpdateAsync(string name, SkillDraft draft, CancellationToken ct = default);
    Task<SkillWriteResult> DeleteAsync(string name, CancellationToken ct = default);
    Task<SkillWriteResult> InstallZipAsync(string name, Stream zip, ConfigLevel level, bool overwrite,
                                           string? subdirectoryPrefix, CancellationToken ct = default);
    Task<SkillWriteResult> SetEnabledAsync(string name, bool enabled, ConfigLevel level, CancellationToken ct = default);
}
```

- 只允许写文件通道；目标是注册通道技能 → 直接拒绝（"该技能由插件提供"）。
- 路径安全（根内、名称正则、禁 `..`）、原子写（临时目录 → `Directory.Move`）、`skill-state.json` 带写闸（现无，`SkillManager.cs:469`）。
- `SetEnabledAsync`：**层级由调用方显式给出**（UI 用当前生效层级预选，用户可改）；启用时清除**所有**层级的禁用项（修掉"双层级禁用一个都清不掉"的静默 bug）。生效判定保持并集语义。
- 写成功后 `RefreshAsync` 再返回条目，保证"写方法返回时状态已一致"。
- 在线安装保留现有"解析 → 预览 → 下载"流程（`OnlineSkillParserAggregator`）；writer 只接最终字节，网络与解析不进写服务。

### 11.5 消费方改造

| 消费方 | 改为 |
|---|---|
| `skill` 工具 | 读 `ToolContext.SkillSnapshot`（新增字段；`ToolContext.WithCancellationToken` 用 `MemberwiseClone`，自动透传） |
| `/skill`、动态命令 | 只持名字，执行时按名从快照解析；不再持 `SkillInfo` 副本 |
| 命令注册 | 订阅 catalog 事件按名 diff 注册/注销（顺带修掉"被禁用技能的命令永远注销不掉"：现 `UnregisterDynamicSkillCommands` 用的是只含启用项的 `GetAllSkillInfos()`） |
| `AcpModule` 命令 | 同上，订阅事件 |
| `SkillsPage` | 订阅事件，渲染 `Current`；"刷新"按钮 → `RefreshAsync`；展示 `Conflicts`/`Issues`/来源（读 `SkillCatalogEntry.Source`，删除 `StartsWith("embedded://")` 嗅探） |
| `SkillCreatePage` / `SkillDetailPage` | 全部经 `ISkillWriter`，页面不再碰文件系统 |

## 12. 提示词接线

- `AgentContext` 新增 `string? ContextBaseline`、`SkillCatalogSnapshot? SkillSnapshot`。
- `ToolContext` 新增同名字段 `SkillCatalogSnapshot? SkillSnapshot`。
- `PromptContext` 新增 `string? ContextBaseline`。
- `PromptBuilder.BuildAsync`：分节注入 → 变量替换 → `Trim` → **最后**追加 `ContextBaseline`。
- `AgentExecutor.BuildRequestAsync`：`agent.SystemPrompt` 为空但基线非空 → 基线即系统提示词（修掉 §7 的空提示词丢失）。
- 子代理经 `TaskTool` → `ISessionRuntime.DeliverAsync`，各自有独立执行与独立快照，无需额外接线。

## 13. 删除 / 退役清单

- `InstructionManager` 的指纹双轨逻辑（保留 `IInstructionManager` 为薄适配器，内部委托 assembler；签名改为返回 `ContextAssemblyResult` 的投影）。
- `SessionMetadataKeys.InstructionFingerprints` 退役（读取路径不再使用；不做读路径清理，避免"读时写会话"）。
- `SkillManager` 内的字典、`RefreshSkillsAsync`、`embedded://` 相关分支；`RegisterSkill`/`RegisterEmbeddedSkill` 实现迁移到 catalog。
- `DynamicPromptBuilder`（`{{skills}}`/`{{tools}}` 占位符渲染，当前无引用）——与"单一渲染点"冲突，删除。
- `SkillsPage` 的本地列表缓存与路径嗅探。
- `SkillCreatePage` 的文件写入代码。

## 14. 测试计划

**管道（Core.Tests）**

1. 无变化不产生消息、不推进状态；有变化只产生一条增量。
2. `Unavailable` 保留旧值不叙述；`Removed` 渲染 "must not be used"；读取异常不映射为 Removed。
3. `Render` 返回 null 时不推进已叙述值（下次重试）。
4. 字节稳定：同执行两次 `BuildSystemBaseline` 逐字节相等；同一快照重复渲染相等；内容含 `{{timestamp}}` 字面量不被替换。
5. 版本二分：`ValueSchemaVersion` 变化 → 重建并全量叙述；同版本渲染不一致 → 保留旧文本 + 告警。
6. 截断：临界点不再每轮告警（截断文本即存库）。
7. 回退：回退到两条增量之间，取存活的最晚账本条目，不重复不遗漏。
8. fork：状态不复制，分支首次装配全量渲染；不产生重复叙述。
9. 压缩：`Compaction` 位被消费一次；`Target=Message` 源在摘要后重放。
10. `ReadOnly` 模式：不写状态、不叙述、返回当前渲染。
11. 并发：同一会话并发装配只推进一次（CAS），不同会话可并行。

**源**

12. 项目指令：迁移前后渲染文本与今天一致；文件不可读时不叙述删除；压缩后仍可见（新行为）。
13. 技能：清单按 id 排序；新增/改名/改描述/删除四种叙述；注册优先 + 冲突可见；改正文后下一条消息生效；UI 订阅后自动更新。

**端到端**

14. 批量安装 3 个技能 → 系统提示词基线字节不变、出现一条增量、`skill` 工具可加载。
15. 长会话多轮安装 → 基线不变（缓存不失效）。
16. `Scheduler`/`Heartbeat` 运行不推进状态（不产生"已叙述未送达"）。
17. `ExecutionRunner` 构造函数变更涉及的 7 个测试文件、`ExecutionRunnerInstructionTests`（断言由 `IInstructionManager` 改为 `IContextAssembler`）一并更新。

**运行方式**：本环境 MTP 发现不到测试，统一 `dotnet build` + `dotnet vstest`（见 AGENTS.md）。watcher 测试用真实延时 + 临时目录（仓库无虚拟时钟包）。

## 15. 行为变更与兼容性

| 变更 | 影响 | 说明 |
|---|---|---|
| 压缩后项目指令重新可见 | 修复既有 bug | 今天压缩后永久消失 |
| 技能安装/改正文下一条消息生效 | 新能力 | — |
| 插件技能同名优先于文件技能 | 之前由执行顺序决定 | 显式规则 + 冲突可见 |
| `ISkillManager` 写面 `[Obsolete(error:true)]` | 破坏性（源码级） | Release Notes 明示替代 API |
| `ISkillWriter.SetEnabledAsync` 需显式层级 | 破坏性（新增契约） | UI 同步改造 |
| `SkillManager` 不再是提示词分节贡献者 | 提示词结构变化（`## Skills` 由基线提供） | 若 agent 模板有同名占位需删除 |
| 结算失败时不再注入指令增量 | 行为变化 | 执行未发生则不应宣称已告知 |
| `/help` 等短路命令不再注入指令 | 行为变化（无实际影响） | 它们不调用模型 |
| `SkillInfo.DirectoryPath` 迁移 | 破坏性（罕见使用） | 移出 Abstractions |
| 新增 `context.baseline` 元数据键 | 纯增量 | Clear 清除、fork 不复制 |

## 16. 实施顺序（一次交付）

1. 契约 + `ContextAssembler` + 状态机 + 管道测试（1–11）。
2. 项目指令源迁移 + 压缩重放 + `IInstructionManager` 适配器（12）。
3. 技能目录（快照/事件/原子替换/优先级/冲突）+ 模块级 watcher + 指纹（13 前半）。
4. 技能源接入 + 提示词接线 + 执行级钉住（13 后半、14）。
5. 写收口 + UI 三页 + 命令按名解析 + ACP 订阅（15）。
6. 删除清单（§13）+ 全量测试。
7. 文档：`docs/architecture/20-context-source-pipeline-release-notes.md`、`README`/`AGENTS.md` 索引、`04-development-standards.md` 反模式（禁多渲染点、禁前缀嗅探、禁绕过 catalog 写状态）。

## 17. 决议（终版）

- **D1** 装配点固定在命令处理之后、构史之前（`ExecutionRunner.cs:227-230`）。
- **D2** 工具目录不纳入（§2）；管道为其预留位置，接入不改契约。
- **D3** 运行时分三档：主链 `Persist`；Scheduler/Heartbeat `ReadOnly`（不推进状态）；ACP 不支持。契约放 Abstractions，实现在 Core，`AddSeeingCore` 注册。
- **D4** 生命周期用「`pending` 位集合 + `deltaLedger`」，删除 `AdvanceEpochAsync`/`Rewind`/`lastEmittedMessageId`；压缩钩在 `CompressionService.CompressAsync`。
- **D5** 执行期事实用 scoped 端口（`ISkillCatalogPin`），不塞进 `ContextReadContext`。
- **D6** 状态只存 canonical + 已渲染文本；`Payload` 不落盘；只用 `SessionData.Metadata`，不做旁挂文件。
- **D7** 项目指令 `Target=Message`（保持既有落点）+ 压缩后重放（修 bug）；`Target=System` 保留为框架能力。
- **D8** `ISkillManager` 收缩为只读；写面 obsolete；`ISkillWriter` 承担全部写入，层级显式，启用清所有层级。
- **D9** 叙述协议单动词 `Render` + `Initial` 状态；返回 null 不推进。
- **D10** `Abstractions` 零实现：查询挂接口；`SkillInfo.DirectoryPath` 移出。
- **D11** 技能清单一处格式化（`SkillCatalogFormatter`），基线拥有整个 `## Skills` 区块。
- **D12** 附录文档删除，只保留本文（git 历史可回溯）。

## 18. 留给实现者

- 增量消息 `{{...}}` 信封措辞与 UI 折叠样式。
- `MaxBaselineChars` 默认值（建议技能 8k、指令 32k）。
- watcher 去抖窗口（建议 400ms）与缓冲溢出处理。
- canonical JSON 的规范化实现（JCS 或自写 + golden 测试）。
- `deltaLedger` 的裁剪策略（跟随消息存活，上限条数）。
