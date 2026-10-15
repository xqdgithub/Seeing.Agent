# Session Title Ensuring — Design Spec

**Date:** 2026-08-04  
**Status:** 已写入工作区，待用户审阅（按约定不 git commit）  
**Scope:** 自动会话标题生成（执行环旁路 + 直接 LLM，不作为 Agent / 插件）

---

## 1. Problem

现有 `TitleGenerationService` 意图正确（首条用户消息后生成标题），但无法可靠工作：

1. **DI 懒加载**：工厂内才 `HookManager.Register`，而无人解析该服务 → Hook 从未挂上。
2. **依赖方向错误**：`ITitleGenerationService` 放在独立 `Seeing.Session` 包，实现却依赖 `AgentExecutor` / `IAgentRegistry`。
3. **过重路径**：用完整 Agent 执行环跑一步标题，易踩权限 / 路由 / ACP 分流。
4. **`title` Agent 暴露**：即使 `IsHidden`，仍进入 Agent 管理体系，不符合「系统旁路」定位。
5. **UI 无感知**：`SetTitleAsync` 改 Title 并落盘，但不发执行层事件；WebUI 页头难以实时刷新。长标题页头也缺截断样式。

参考实现：

| 项目 | 触发 | 形态 |
|------|------|------|
| OpenCode | Prompt 循环 `step===1` fork `ensureTitle` | 隐藏 title agent + small model |
| QwenPaw | 建 chat 时 `asyncio.create_task` | 直接 LLM + compare-and-set |

二者都**不是插件**；标题是会话生命周期副作用。

---

## 2. Goals & Non-Goals

### Goals

- 首条真实用户消息后，后台生成简洁标题并写入 Session。
- 失败静默：保留占位标题，不影响主对话。
- 模型：`TitleGeneration.Model` 可配；未配则用会话当前模型。
- 触发：主对话执行环内 fork（对齐 OpenCode），覆盖 WebUI / Gateway / 凡走 `ExecutionJobService` 的入口。
- 直接 `ITextCompletion` 一次 completion（`maxTokens: 32`，非默认 2048）；**删除** BuiltIn `title` Agent。
- 标题硬上限 **≤15 字**（prompt + 请求侧 MaxTokens + 清洗兜底）。
- WebUI：标题变更后页头 / 侧栏 / 列表可更新；展示层 ellipsis + Tooltip 全文。

### Non-Goals

- 不做插件 / `IExtension`。
- 不在 `Seeing.Session` 内依赖 LLM。
- 不保留 / 不恢复 `title` Agent。
- 首版不做失败自动重试风暴（单次尝试即可）。
- 不在非执行路径（纯改元数据）强制生成标题。

---

## 3. Architecture

### 3.1 Placement

```
Seeing.Agent.App / ExecutionJobService
  │
  ├─ 写入用户消息
  ├─ 启动主 Agent 执行
  └─ fire-and-forget → ISessionTitleEnsuring.TryEnsureAsync
                              │
                              ▼
                   Seeing.Agent / SessionTitleEnsuring
                     ILlmService + ISessionManager + Options
                              │
                              ▼
                   SessionManager.SetTitleAsync
                     + SessionEvent
                     + SessionTitleChangedEvent → UI
```

| 层 | 负责 | 不负责 |
|----|------|--------|
| Session | 存 Title、默认标题约定、`SetTitleAsync`、Session 事件 | 调 LLM、标题策略 |
| Agent | `ISessionTitleEnsuring`、固定 prompt、清洗、LLM 调用 | Agent 列表、完整执行环 |
| App/Execution | 合适时机调用 `TryEnsureAsync`、转发标题变更事件 | 拼 prompt / 选模型细节 |
| WebUI | 占位标题创建、监听标题事件、展示截断 | 自行触发生成 |

### 3.2 Why not Hook / Plugin

- Hook：`session.updated` 语义过宽；DI 注册易漏；Session↔Agent 桥接脆弱（现状已证明）。
- Plugin：标题是核心 UX，不是可选扩展；开关用配置 `Enabled` 即可。

---

## 4. Components

### 4.1 `ISessionTitleEnsuring`

```csharp
public interface ISessionTitleEnsuring
{
    /// <summary>
    /// 若满足条件则生成并写入标题；否则 no-op。
    /// 失败不抛到调用方（内部吞掉并打日志）。
    /// </summary>
    /// <returns>实际写入的标题；未写入则 null</returns>
    Task<string?> TryEnsureAsync(
        string sessionId,
        string userMessage,
        string? fallbackModelId,
        CancellationToken cancellationToken = default);
}
```

实现类：`SessionTitleEnsuring`（Singleton），放在 `Seeing.Agent`（例如 `Services/` 或 `Core/Sessions/`）。

依赖（仅 Agent / Session 边界内）：

- `ILlmService`
- `ISessionManager`
- `IOptionsMonitor<SeeingAgentOptions>`（读 `TitleGeneration`）
- `ILogger<SessionTitleEnsuring>`

**不**依赖 `IExecutionEventPublisher`（属 App 层）。事件由 `ExecutionJobService` 根据返回值发布，见 §5.3。

### 4.2 `TitleGenerationOptions`

挂在 `SeeingAgentOptions`：

```json
{
  "TitleGeneration": {
    "Enabled": true,
    "Model": null,
    "TimeoutSeconds": 15
  }
}
```

| 字段 | 含义 | 默认 |
|------|------|------|
| `Enabled` | 总开关 | `true` |
| `Model` | 专用模型 ID；空则用 `fallbackModelId`（会话当前模型） | `null` |
| `TimeoutSeconds` | 单次生成超时 | `15` |

### 4.3 `TitlePrompts`（静态）

固定 system prompt，内容迁自原 `CreateTitleAgent`，规则改为：

- 只输出标题，无解释
- 与用户消息同语言
- **≤15 字**
- 无 emoji、无工具名、无引号包裹

### 4.4 删除清单

- `BuiltInAgents.CreateTitleAgent` 及注册
- `Seeing.Agent.Services.TitleGenerationService`
- `Seeing.Session.Management.ITitleGenerationService`
- `ServiceCollectionExtensions` 中 TitleGeneration 的 Hook 工厂注册

---

## 5. Data Flow

### 5.1 Trigger（ExecutionJobService）

在 `ProcessExecutionAsync` 中，用户消息已写入 Session、主执行即将开始（或刚开始）时：

```csharp
_ = EnsureTitleFireAndForgetAsync(record.SessionId, userText, session.SelectedModel);

// 局部辅助：不阻塞主路径
async Task EnsureTitleFireAndForgetAsync(string sid, string text, string? model)
{
    try
    {
        var title = await titleEnsuring.TryEnsureAsync(sid, text, model, CancellationToken.None);
        if (!string.IsNullOrEmpty(title))
        {
            _eventPublisher.Publish(sid, new SessionTitleChangedEvent
            {
                SessionId = sid,
                Title = title
            });
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Title ensure failed: {SessionId}", sid);
    }
}
```

不 `await` 主路径（调用方用 `_ =`）。所有经 `ExecutionJobService.SubmitAsync` 的入口（WebUI、Gateway 等）自动覆盖。

### 5.2 Ensure 内部步骤

1. 读配置；`Enabled == false` → return  
2. `Get(sessionId)`；不存在 → return  
3. 条件（全部满足）：
   - `session.Kind == SessionKind.Root` 且 `ParentSessionId` 为空（Fork / SubAgent 不生成；`BranchSessionAsync` 清掉 Parent 的独立 Root 可以生成）
   - `IsDefaultTitle(session.Title)`
   - 真实用户消息数 == 1（排除 `Metadata["synthetic"]`）
   - `userMessage` 非空
4. 解析模型：`options.Model` 非空 ? 该值 : `fallbackModelId`；仍空则打日志 return  
5. 截断用户输入（约 500–1000 字符）  
6. `ILlmService` 一次 completion（system = TitlePrompts，user = 截断消息），带 `TimeoutSeconds`  
7. `CleanTitle` → 空则 return  
8. **CAS**：再读 session；若标题已非默认 → return  
9. `SetTitleAsync(sessionId, cleaned)`  
10. `return cleaned`（由 JobService 发 `SessionTitleChangedEvent`）

### 5.3 UI 事件

**补齐缺口**：`SessionManager.SetTitleAsync` 在改 Title + Save 后，发布 Session 层 `SessionEventType.Updated`（与 `AddMessageAsync` 对齐）。

**执行流事件**：由 `ExecutionJobService` 在 `TryEnsureAsync` 返回非空标题后发布：

```csharp
public record SessionTitleChangedEvent : IMessageEvent
{
    public required string SessionId { get; init; }
    public required string Title { get; init; }
    // Timestamp / Type 等按现有 IMessageEvent 约定
}
```

放在 `Seeing.Agent.App.Events`（与 `SessionUpdatedEvent` 同级）。

WebUI `Session.razor`（及侧栏 / Sessions 列表按需）：

- 收到事件且 `SessionId` 匹配 → 更新 `CurrentSession.Title`（若非同一引用则赋值）→ `StateHasChanged`
- **不要**因此走完整 `ResetTimelineFromSession`

### 5.4 展示截断

| 层 | 规则 |
|----|------|
| 生成 / 落库 | prompt ≤15；`CleanTitle` **硬截断到 15 个字符**，落库字符串本身不加 `...`（展示层负责省略号） |
| 展示 | CSS：`max-width` + `ellipsis` + `nowrap`；`Tooltip` 显示全文。覆盖：会话页头、侧栏、Sessions 列表 |

页头现有 `.session-title` 缺 ellipsis，实现时补齐。

### 5.5 默认标题判定

至少识别：

- 以 `Session ` 开头（`SessionData.Create` 默认）
- `新会话`（WebUI `CreateSessionAsync("新会话")`）
- `New Session`
- null / whitespace

---

## 6. Error Handling & Concurrency

| 情况 | 行为 |
|------|------|
| LLM 失败 / 超时 / 空输出 | Warning 日志；保留占位标题；主对话不受影响 |
| 生成中用户手动改名 | CAS：写前再查默认标题，已改则跳过 |
| 主执行取消 | 标题任务使用**独立** `CancellationTokenSource(TimeoutSeconds)`，不绑主执行 CT |
| 进程退出 | 可丢弃未完成标题任务 |
| 并发双触发 | 条件「仅 1 条真实用户消息」+ CAS，最多一次有效写入 |

---

## 7. WebUI Requirements

1. 标题变更事件到达后，当前会话页头即时更新。  
2. 侧栏会话项、Sessions 列表在可见时同步新标题（同源 Session 数据或同一事件）。  
3. 长标题不撑开布局：ellipsis；悬停 Tooltip 看全文。  
4. 手动「重命名会话」仍优先；自动生成不得覆盖用户已设标题。

---

## 8. Testing

1. **单元** `ShouldEnsure` / 条件矩阵（禁用、Fork、非默认标题、消息数 ≠1）。  
2. **单元** `CleanTitle`：15 字截断、去 think、取首行、去引号。  
3. **单元** CAS：标题已非默认时不调用写 / 不覆盖。  
4. **组件** `TryEnsureAsync` mock `ILlmService` → `SetTitle` + 发出 `SessionTitleChangedEvent`。  
5. **手工** WebUI：首条消息后页头更新；超长展示 ellipsis + Tooltip。

---

## 9. Migration / Cleanup

1. 删除 title Agent 与旧 TitleGeneration 服务 / 接口 / DI。  
2. 确认 Agent 管理页不再出现 `title`。  
3. 已有会话：仅默认标题 + 后续若再满足条件才生成（不回填历史会话）。  
4. 文档 / AGENTS.md 中若提及 title Agent，改为 TitleEnsuring 旁路说明（实现阶段顺带，非阻塞）。

---

## 10. Decisions Log

| 决策 | 选择 | 理由 |
|------|------|------|
| 失败行为 | 静默保留占位 | 对齐 OpenCode / QwenPaw；不挡主路径 |
| 模型 | 可配，否则会话当前模型 | 用户明确要求；不做强制 small model |
| 触发点 | ExecutionJobService fork | 覆盖所有执行入口；语义清晰 |
| 调用形态 | 直接 LLM，删 title Agent | 避免 Agent 列表暴露；去掉 Executor 复杂度 |
| 长度 | ≤15 字 | 用户明确要求（原 50 过长） |
| UI 事件 | `SessionTitleChangedEvent` | 避免误用 `SessionUpdatedEvent` 整页重建 |
| 插件化 | 不做 | 核心 UX，配置开关足够 |

---

## 11. Open Questions (resolved)

- ~~失败时行为~~ → 静默保留占位  
- ~~模型策略~~ → 可配，否则会话模型  
- ~~触发点~~ → 执行环  
- ~~Agent vs LLM~~ → 直接 LLM，删 Agent  
- ~~标题长度~~ → ≤15 字  
- ~~是否 git commit 本 spec~~ → 用户要求全程不提交；本文件仅写入工作区
