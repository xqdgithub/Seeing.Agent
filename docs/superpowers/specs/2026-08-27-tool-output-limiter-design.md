# 工具输出统一限制（Tool Output Limiter）设计

2026-08-27 — 装饰器层统一兜底，超限输出落盘到会话 ref 目录，保证完整数据可恢复

## Problem

工具输出过长时会被原样注入 LLM 上下文，带来三类问题：

1. **Token 浪费**：超大输出直接进入 LLM 上下文，占满上下文窗口，推高成本、劣化指令遵循。
2. **覆盖不全**：现有截断分散在各工具内部（`ReadTool` 2000 行/50KB、`GrepTool` 100 条、`GlobTool` 100 个），**第三方工具与 MCP 工具完全无限制**。
3. **恢复无门**：分页（`offset/limit`）仅 `ReadTool` 具备，其余工具一旦截断，完整数据无法取回——`BashTool.cs:309` 甚至存在 bug：只截断 Metadata，`output` 却把**完整未截断**串发给 LLM。

## Current State

### 现有机制盘点

| 位置 | 限制 | 问题 |
|------|------|------|
| `ReadTool` / `FileSystemHelper.ReadFileWithLimit` | 2000 行 / 50KB / 单行 2000 字符 | 分页友好，但仅限 read |
| `BashTool` | `MaxMetadataLength=30K` 仅截 Metadata | **bug**：`Success(description, output, ...)` 的 `output` 传完整未截断串 |
| `GrepTool` / `GlobTool` | 100 条 | 工具内部计数 |
| `McpTool` / 第三方工具 | **无限制** | 输出直接进 LLM |
| `OutputTruncator` 静态类 | 2000 行 / 50KB | 定义了但未被任何工具消费 |

### 主流做法（调研）

| 框架 | 机制 | 超限行为 |
|------|------|---------|
| Claude Agent SDK / Claude Code | `TruncateOutputProcessor` + CLI 双层防护 | Layer1 MCP 按 token（默认 25K）校验；Layer2 通用落盘（50K 字符硬限）→ `<persisted-output>` 标签 + 2KB 预览 + 文件路径 |
| LangChain | `TruncateOutputProcessor`（middleware） | 按 token 截断后发给 LLM |
| OpenAI Agent SDK | `truncate` 处理器 | 消息组装前截断 |

**关键共识**：截断发生在"统一管道层"（装饰器/中间件），而非各工具内部；工具可通过声明（annotations/capabilities）按需覆盖阈值。

### 本项目基础设施（复用）

- **装饰器链**：`ToolDecoratorRegistry` + `ToolDecorator`，`ToolManager.RegisterToolAsync:418` 统一 `Apply()`——覆盖所有工具（含 MCP，经 `McpToolRegistry:73`）
- **能力声明**：`ToolCapabilityKeys`（`timeout.skip`/`timeout.budget`/`cache.*`），预留给扩展键
- **配置热重载**：`IOptionsMonitor<SeeingAgentOptions>`（同 `ToolExecutionTimeout`）
- **会话级白名单**：`IWorkspaceWhitelist`，`SerializingPermissionChannel:44` 对 filesystem.* 自动放行
- **会话存储目录**：`FileSessionStore`（`IRelocatableSessionStore`，工作区切换时重定位）

## Design

### 原则

1. **统一兜底**：截断/落盘决策放在装饰器层，一处生效、覆盖所有工具（内置/第三方/MCP）。
2. **数据不丢**：超限输出**一律落盘**完整内容到会话 ref 目录，不依赖工具分页能力；agent 可通过 read 工具随时读取全文。
3. **显式标记**：任何截断/落盘都有明确的结构化提示（大小、路径、省略量），agent 不会误判数据已完整。
4. **全局一致**：截断后内容在事件流 / UI / Session 存储 / LLM 上下文四处一致，完整内容仅存在于 ref 文件。

### 架构与组件

```
工具执行漏斗（ToolManager → RegisterTool 时 Apply 装饰器链）
注：ToolDecoratorRegistry.Apply 为 current = factory(current)，【后注册者在外层】。
现有链注册顺序 Retry→Timeout→Cache，故现有链从外到内实际为 Cache→Timeout→Retry→工具
（ServiceCollectionExtensions.cs:507-528 的"最外层重试/最内层缓存"注释与实现相反，勿信注释）。
执行顺序（最外层 → 最内层）：
  ToolOutputLimiterDecorator（新增，最后注册=最外层兜底）
    → CachedToolDecorator
      → ToolTimeoutDecorator
        → RetryToolDecorator
          → 真实工具
        │
        ├─ 大小检查（UTF8 字节判定；行数仅记录于 Metadata/预览）
        ├─ 超限 → IToolOutputStore.SaveAsync(全文)          // 以下仅当超限时
        ├─ Output 替换为 <persisted-output> 预览模板（头+尾）
        ├─ Metadata 写入 truncated / outputPath / originalBytes / originalLines
        └─ IWorkspaceWhitelist.Add(sessionId, refDir)
        │
        ▼
  ToolCallEvent.Output（预览）→ 事件流 / Session / LLM 一致
        ▲
  agent 需全文 → read 工具读 {sessionDir}/{sessionId}.ref/*.txt（权限自动放行）
```

#### 新增组件

| 组件 | 位置 | 职责 |
|------|------|------|
| `IToolOutputStore` / `SessionToolOutputStore` | `src/Seeing.Agent/Output/` | 落盘服务：解析 ref 目录、写文件、返回路径 |
| `ToolOutputLimiterDecorator` | `src/Seeing.Agent/Decorators/` | 统一截断/落盘决策（能力感知） |
| `SeeingAgentOptions.ToolOutput` | `SeeingAgentOptions.cs` | 配置节 |
| `ToolCapabilityKeys.OutputSkip` / `OutputMaxBytes` | `Abstractions/Tools/` | `output.skip` / `output.maxBytes` 能力键 |
| `IRelocatableSessionStore.BaseDirectory` | `Seeing.Session/Storage/` | 新增只读属性，暴露当前会话存储目录（仅 FileSessionStore 实现，向后兼容） |

**ref 目录解析**：`Path.Combine(sessionStore.BaseDirectory ?? 默认 ~/.seeing/sessions, $"{sessionId}.ref")`。
- 工作区切换时随 `SessionReloadHandler` 重定位自动跟随；
- 随会话保留、可关联、不丢失；删除会话时联动清理（见文件生命周期）。

### 行为与数据流

```
ToolOutputLimiterDecorator.ExecuteAsync:
  1. result = await Inner.ExecuteAsync(...)                // 执行真实工具
  2. result.Output 为空 → 原样返回                          // 无文本输出
  3. 能力判断（优先于配置）：
       output.skip == "true"      → 原样返回                // 工具自我负责（如内置已分页工具）
       output.maxBytes = N        → 以 N 作为该工具 MaxInlineBytes
  4. size = UTF8 字节数
  5. size ≤ MaxInlineBytes → 原样返回
  6. size > MaxInlineBytes → 落盘 + 替换预览
```

#### 预览模板（超限时）

```
<persisted-output>
输出过长：原始 {bytes/1024}KB（{lines} 行），完整输出已保存到：
  {outputPath}
需要完整内容时，请使用 read 工具读取该文件。

预览（头部 {HeadChars} + 尾部 {TailChars} 字符，中间省略 {omittedChars} 字符）：
{head}
...（省略 {omittedChars} 字符）...
{tail}
</persisted-output>
```

- **头+尾结构**：工具输出的关键信息常在尾部（错误、结果、汇总），故保留头部与尾部，中间省略并给出省略量。
- 内容总长 ≤ Head+Tail 时全展示（无省略）；头/尾各自按字符独立截断（超大单行不被打爆）。
- 大小判定按 **UTF8 字节**（`MaxInlineBytes`/`originalBytes`），预览头尾按**字符**（`PreviewHeadChars`/`PreviewTailChars`）——两者单位不同，勿混用。

#### Metadata 标记

| 键 | 值 |
|----|-----|
| `truncated` | `true` |
| `outputPath` | ref 文件绝对路径 |
| `originalBytes` | 原始输出 UTF8 字节数 |
| `originalLines` | 原始输出行数 |
| `spillFailed` | 落盘失败时 `true`（降级为 inline 截断） |

### 配置

```csharp
public class ToolOutputOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxInlineBytes { get; set; } = 50 * 1024;  // 超此落盘（对齐 FileSystemHelper.MaxBytes）
    public int PreviewHeadChars { get; set; } = 1024;     // 预览头部字符
    public int PreviewTailChars { get; set; } = 1024;     // 预览尾部字符
}
```

- 挂到 `SeeingAgentOptions`，`IOptionsMonitor` 实时读取，支持热重载。

### 现有工具协同

| 工具 | 处理 |
|------|------|
| `ReadTool` | 声明 **`output.skip=true`**（自身已 2000 行/50KB 限制 + 分页，且行号前缀使最终输出可能略超全局阈值——豁免避免冗余落盘） |
| `BashTool` | **修复 bug**：移除内部 output 自截断（`BashTool.cs:302-305,309`），**保留 `MetadataSink` 的 `bash_output`（30KB 流式预览，供 UI）**，移除 `Success` 的 `["output"]` 冗余截断项；完整 output 交由装饰器统一处理 |
| MCP / 第三方 | 无内部限制，由装饰器兜底 |

### 边界与错误处理

| 场景 | 处理 |
|------|------|
| 落盘失败（IO/权限/磁盘满） | **降级为 inline 截断**：头+尾 + `<persisted-output>` 标记"落盘失败"；Metadata `spillFailed=true`；记录日志——agent 至少拿到部分数据 |
| 失败结果（`Success=false`） | 不处理（Error 短小，超长罕见，YAGNI） |
| 无文本输出（仅 Attachments） | 跳过，附件不受影响 |
| 并发工具 | callId 唯一 → 文件名唯一，无竞争 |
| 取消路径 | 落盘用短超时（5s）+ `CancellationToken.None`，避免因取消丢数据 |
| 文件名安全 | sessionId/callId 经 `Path.GetInvalidFileNameChars` 净化 |
| 配置热重载 | `IOptionsMonitor` 实时读取 |
| 装饰器链顺序 | Limiter 作为**最外层兜底**（最后注册），确保看到最终返回结果；不干预重试/超时/缓存语义 |

### 文件生命周期

- **保留**：ref 目录随会话持久化，归档不清理（不丢数据）。
- **清理**：在 **`FileSessionStore.DeleteAsync`**（`:354` 删除 `{sessionId}.json` 处）同步删除 `{sessionId}.ref/`（ref 目录与会话文件同属 `_baseDirectory`，此处联动最自然，且不用侵入 `ISessionStore` 接口）。
- **不自动 TTL**：用户明确要求"不会丢失"，故不做定期清理。

## Testing

| 层级 | 覆盖 |
|------|------|
| 单元（`ToolOutputLimiterDecoratorTests`） | 阈值边界（恰好等于/超 1 字节）、`output.skip` 豁免、`output.maxBytes` 覆盖、失败结果不处理、空输出不处理、头+尾预览格式与省略量、Metadata 标记、落盘失败降级 |
| 单元（`SessionToolOutputStoreTests`） | ref 目录解析（默认/重定位后）、文件名安全、并发写不同 callId |
| 回归（`BashToolTests`） | Bash 超长输出完整传入（不再内部截断），交由装饰器处理 |
| 集成 | 超限输出 → 落盘 → 事件流/Session 存预览 → read 读 ref 文件成功（白名单放行） |

## 非目标（YAGNI）

- 不按 token 估算截断（字符数为主，确定性好、零依赖）。
- 不重写各内置工具的内部限制（保留其自身优化，装饰器兜底）。
- 不做"所有输出都落盘"（仅超限落盘，控制 IO 与文件量）。
- 不处理 `Success=false` 的超长 Error。
- 不新增 MCP `maxResultSizeChars` 协议透传（本项目 MCP 工具结果统一经 ToolResult.Output，由装饰器兜底即可）。
