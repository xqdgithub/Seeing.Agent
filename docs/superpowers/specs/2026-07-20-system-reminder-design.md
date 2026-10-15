# System Reminder 消息模板设计

**日期:** 2026-07-20  
**状态:** 已批准（待实现）  
**范围:** 通用模板系统；首批接入 Job/Cron 与后台 Task 完成/失败回调

---

## 问题

系统会把非用户主动输入的内容（定时 Job、后台 Task 完成回调等）以 `role=user` 写入会话并发给模型。若不加标记，模型会误判为用户刚发的消息。

需要：

1. 发给后端/模型时仍可为 `user` 消息  
2. 内容上明确区分「系统提醒」与「用户主动输入」  
3. 配置页只存任务裸文本，不存包裹后的 XML  
4. Session 持久化包裹后的完整文本  
5. UI 用专用样式展示，并支持切换原文

---

## 决策摘要

| 项 | 选择 |
|----|------|
| 信封格式 | XML 标签（方案 A） |
| 说明语 vs 任务正文 | `<notice>` 与 `<task>` 分离 |
| 持久化 | Session 存包裹后全文 |
| 配置 | 只存 `<task>` 裸文本，运行时 Wrap |
| UI | 专用提醒气泡；默认显示 task 正文；可切换原文 |
| 首批接入 | `job`（cron/heartbeat）+ `task`（completed/failed） |

---

## 1. 标签结构

### 1.1 Job / Cron

```text
<system-reminder source="job" kind="cron">
<notice>
定时任务已触发。以下内容是系统注入的指令，不是用户刚刚输入的消息。请按 <task> 中的文本执行。
</notice>
<task>
每小时检查一次构建状态并汇总。
</task>
</system-reminder>
```

### 1.2 Task 完成

```text
<system-reminder source="task" kind="completed" task-id="abc123">
<notice>
后台任务已完成。以下是任务结果摘要；请根据结果决定是否继续，这不是用户刚刚输入的消息。
</notice>
<task>
任务 #abc123 已成功完成。

输出：
构建通过，3 个测试失败已修复。
</task>
</system-reminder>
```

### 1.3 各段职责

| 部分 | 职责 | 提供者 |
|------|------|--------|
| `<system-reminder>` | 标记整段为系统提醒；属性 `source` / `kind` / 可选 `task-id` | 框架 |
| `<notice>` | 固定说明语（按 source+kind 选择） | 框架内置文案 |
| `<task>` | 真实业务正文 | 配置 / Job prompt / Task 输出 |

### 1.4 属性约定

| 属性 | 首批取值 | 说明 |
|------|----------|------|
| `source` | `job`, `task` | 触发域；后续可扩展 |
| `kind` | `cron`, `heartbeat`, `completed`, `failed` | 具体类型 |
| `task-id` | 可选 | Task 回调时填写 |

未知 `source`/`kind` 仍允许 Wrap：notice 使用通用文案；UI 徽章原样显示属性值。

---

## 2. API / 渲染器

### 2.1 位置

```
src/Seeing.Agent/Core/Reminders/
  SystemReminder.cs           # 常量、source/kind 约定
  SystemReminderRenderer.cs   # Wrap / TryParse
  SystemReminderNotices.cs    # 按 source+kind 的 notice 文案
```

放在 `Seeing.Agent` 核心层；`Seeing.Session` 不引入业务依赖。

### 2.2 类型与 API

```csharp
public static class SystemReminder
{
    public const string Tag = "system-reminder";
}

public sealed record SystemReminderParts(
    string Source,
    string Kind,
    string Notice,
    string Task,
    string? TaskId = null,
    string Raw = "");

public static class SystemReminderRenderer
{
    /// <summary>配置裸文本 → 包裹后的 user content</summary>
    public static string Wrap(string taskBody, string source, string kind, string? taskId = null);

    /// <summary>从 content 解析；非 reminder 返回 false</summary>
    public static bool TryParse(string content, out SystemReminderParts parts);
}
```

### 2.3 Session 写入约定

```csharp
SessionMessage.UserMessage(SystemReminderRenderer.Wrap(prompt, "job", "cron"))
    .WithMetadata("reminder", true)
    .WithMetadata("reminder_source", "job")
    .WithMetadata("reminder_kind", "cron");
```

- `Content`：权威数据（带标签全文）  
- `Metadata`：UI 快速识别路径；缺失时回退 `TryParse`  
- 配置页 / 调度 JSON：**永不**调用 Wrap，只存裸 `taskBody`

### 2.4 首批接入点

| 来源 | 位置 | Wrap 参数 |
|------|------|-----------|
| Cron / Agent Job | `AgentJob` → History / Dispatch `UserInput` | `source=job`, `kind=cron` |
| Heartbeat | `HeartbeatJob` | `source=job`, `kind=heartbeat` |
| Task 完成/失败注入主会话 | Task 完成后续跑的 user 消息 | `source=task`, `kind=completed\|failed`, `task-id=...` |

不接入：配置字段、idle resume 空消息（除非后续扩展）。

---

## 3. UI 展示与原文切换

### 3.1 识别

`role == "user"` 且（`Metadata["reminder"] == true` **或** `TryParse` 成功）→ 系统提醒气泡。

### 3.2 默认视图（摘要）

- 顶栏：来源徽章（`source` · `kind`）+ 「原文」切换  
- 正文：仅 `parts.Task`  
- 不展示 `<notice>`（面向模型）  
- 视觉：弱于用户气泡（左侧色条 + 浅底），与右侧/强对比用户气泡区分

### 3.3 原文视图

- 显示完整 `parts.Raw`（或原始 `Content`）  
- 按钮文案：`原文` ↔ `摘要`  
- 切换状态按消息本地保存，不必持久化

### 3.4 组件落点

| 改动 | 说明 |
|------|------|
| 新增 `SystemReminderMessageItem.razor` | 专用样式 + 切换 |
| `MessageList.razor` | user 分支先判 reminder |
| ViewModel 可选 `IsSystemReminder` / `ReminderParts` | 列表转换时解析一次 |

首批完整样式：WebUI 样本。TUI/Spectre 可文本前缀降级（如 `[reminder:job/cron]`）。

---

## 4. 边界与错误处理

| 场景 | 行为 |
|------|------|
| `taskBody` 为空 | 仍允许 Wrap；UI 显示「（无任务正文）」 |
| 正文含结束标签 | Wrap 时将 `</` 替换为 `<\/`（仅作用于 taskBody）；Parse 取 `<task>` 内文本后再把 `<\/` 还原为 `</` |
| 旧 Session 无标签消息 | `TryParse` 失败 → 普通 user |
| Metadata 有但 Content 损坏 | 以 `TryParse` 为准；失败降级普通 user + Debug 日志 |
| Compaction | 当普通 user 文本参与压缩，不剥标签 |
| `Wrap` 时 source/kind 为空 | 抛 `ArgumentException` |
| `TryParse` 格式不完整 | 返回 `false`，不抛异常 |

---

## 5. 测试要点

1. `Wrap` → `TryParse` 往返：`Task` / `Source` / `Kind` / `TaskId` 一致  
2. 正文含特殊字符 / 伪嵌套标签时信封不被破坏  
3. `AgentJob` / Task 完成路径：Session 为包裹文本；配置仍为裸文本  
4. UI：reminder 走专用组件；原文/摘要切换正确  
5. 普通 user 消息行为不变

---

## 6. 明确不做（YAGNI）

- 多语言 notice  
- 用户自定义 notice 模板  
- Gateway 出站时剥标签（后续另定）  
- 除 Job/Task 外的全量系统注入（idle resume、权限提醒等）——模板已通用，接入后续再加

---

## 7. 数据流

```
配置/Job prompt (裸文本)
        │
        ▼
SystemReminderRenderer.Wrap(source, kind, taskId?)
        │
        ▼
SessionMessage (role=user, Content=包裹全文, Metadata.reminder=true)
        │
        ├──────────────► LLM History（原样 Content）
        │
        └──────────────► UI
                           ├─ 默认：徽章 + Task 正文
                           └─ 切换：完整 Raw
```
