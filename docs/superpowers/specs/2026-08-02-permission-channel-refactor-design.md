# 权限通道重构设计

**日期:** 2026-08-02
**状态:** 审批通过，待实施

---

## 概述

重构权限系统，将分散的双重权限通道（`ToolContext.AskPermission` + `IPermissionChannel`）统一为单一的 `IPermissionChannel`，新增会话级权限记忆能力，支持目录级文件路径权限自动匹配。

### 核心目标

1. **统一权限入口** — 移除 `ToolContext.AskPermission`，所有权限请求通过 `IPermissionChannel.RequestAsync()` 完成
2. **会话级决策记忆** — 用户可选择"本次会话始终允许/拒绝"，后续同类型请求自动应用，无需重复询问
3. **目录级文件权限** — 工作区外文件访问支持目录级授权，一次批准整个目录，不再逐个文件询问
4. **Agent 主动扩展工作区** — 新增 `add_workspace_path` 工具，Agent 可请求将路径加入当前会话的工作区白名单

### 关键约束

- **仅会话级** — 不引入持久化层，所有运行时记忆会话结束即失效，防止会话攻击
- **向后兼容** — `SerializingPermissionChannel` 串行装饰器保留

---

## 接口设计

### IPermissionChannel（重构后）

```csharp
public interface IPermissionChannel
{
    /// <summary>
    /// 统一的权限请求入口。替代原有 RequestToolPermissionAsync / RequestWritePermissionAsync 等方法。
    /// </summary>
    Task<PermissionChannelResult> RequestAsync(PermissionRequest request, CancellationToken ct = default);
}
```

### PermissionRequest（扩展）

```csharp
public class PermissionRequest
{
    /// <summary>权限类别："tool.execute", "filesystem.write", "filesystem.read", "network.fetch" 等</summary>
    public string PermissionKind { get; set; } = string.Empty;

    /// <summary>目标资源标识（工具名、文件路径、URL 等）</summary>
    public string? Resource { get; set; }

    /// <summary>辅助匹配模式列表（文件路径场景使用）</summary>
    public List<string> Patterns { get; set; } = new();

    /// <summary>上下文元数据</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>当前会话 ID，用于记忆范围隔离</summary>
    public string? SessionId { get; set; }
}
```

### PermissionChannelResult

```csharp
public class PermissionChannelResult
{
    /// <summary>最终决策</summary>
    public PermissionChannelAction Action { get; set; }

    /// <summary>匹配的记忆条目（有记忆命中时非 null）</summary>
    public PermissionMemoryEntry? MatchedMemory { get; set; }

    /// <summary>拒绝原因</summary>
    public string? Reason { get; set; }

    public static PermissionChannelResult Allowed(PermissionMemoryEntry? memory = null)
        => new() { Action = PermissionChannelAction.Allow, MatchedMemory = memory };

    public static PermissionChannelResult Denied(string reason, PermissionMemoryEntry? memory = null)
        => new() { Action = PermissionChannelAction.Deny, Reason = reason, MatchedMemory = memory };
}

public enum PermissionChannelAction
{
    Allow = 0,
    Deny = 1,
}
```

---

## 会话级记忆模块

### IPermissionMemory

```csharp
public interface IPermissionMemory
{
    /// <summary>查询记忆：按 kind + resource 匹配</summary>
    PermissionMemoryEntry? Match(string permissionKind, string? resource, string sessionId);

    /// <summary>写入记忆</summary>
    void Remember(string sessionId, PermissionMemoryEntry entry);

    /// <summary>遗忘指定资源的记忆</summary>
    void Forget(string sessionId, string? resource);

    /// <summary>清除整个会话的记忆</summary>
    void ClearSession(string sessionId);
}
```

### PermissionMemoryEntry

```csharp
public class PermissionMemoryEntry
{
    /// <summary>唯一标识</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>权限类别</summary>
    public string PermissionKind { get; set; } = string.Empty;

    /// <summary>目标资源（工具名 / 文件路径 / 目录路径 / 通配符模式）</summary>
    public string? Resource { get; set; }

    /// <summary>决策：Allow / Deny</summary>
    public PermissionMemoryAction Action { get; set; }

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PermissionMemoryAction
{
    Allow = 0,
    Deny = 1,
}
```

### SessionPermissionMemory（默认实现）

```csharp
public class SessionPermissionMemory : IPermissionMemory
{
    // ConcurrentDictionary<string, List<PermissionMemoryEntry>> 按 SessionId 分组
    // 匹配规则：精确匹配优先、目录前缀匹配次之
}
```

**匹配规则（文件路径场景）：**

1. **精确文件匹配** — `Resource` 与请求路径完全一致
2. **目录前缀匹配** — 请求路径以记忆的目录 `Resource` 为前缀
3. **通配符匹配** — `Resource` 包含 `*`/`**` 通配符，与请求路径 wildcard 匹配

**匹配规则（非文件场景）：**

- `Resource` 与请求的 `PermissionKind` + `Resource` 精确匹配

---

## 决策模型

### 工具/网络类请求的决策选项

| 选项 | 行为 |
|------|------|
| **允许一次** | 仅本次通过，不写入记忆 |
| **本次会话始终允许** | 写入会话记忆，后续同 kind+resource 自动通过 |
| **拒绝一次** | 仅本次拒绝，不写入记忆 |
| **本次会话始终拒绝** | 写入会话记忆，后续自动拒绝 |

### 文件系统类请求的额外选项

| 选项 | 行为 |
|------|------|
| **允许此文件（本次会话）** | 仅该精确路径生效 |
| **允许此目录（本次会话）** | 该目录及其子目录下所有文件生效 |

### 文件权限询问时的展示信息

向用户展示时应包含：
- 操作类型（读取/写入/执行）
- 目标文件路径
- 是否在工作区外
- 所有可用的决策选项

---

## 新增工具：add_workspace_path

```
工具名:     add_workspace_path
参数:       path (string) — 要加入工作区白名单的路径
权限类别:   "filesystem.workspace_extend"
行为:       触发权限询问 → 用户确认 → 路径写入会话级 PermissionMemory
效果:       该路径及其子目录下所有文件操作自动免询问（仅当前会话有效）
```

Agent 可以主动调用此工具来预先申请路径权限，避免后续逐个文件询问。

---

## 架构变更

### 变更前（现状）

```
Tool.ExecuteAsync
    │
    ├─ context.AskPermission()          ← 工具层独立权限通道
    │
    └─ (AgentExecutor 另有 I PermissionChannel 入口检查)
```

### 变更后

```
Tool.ExecuteAsync
    │
    ├─ _channel.RequestAsync(request)
    │     │
    │     ├─ IPermissionMemory.Match(sessionId, kind, resource)
    │     │     ├─ Allow → 返回 Allowed
    │     │     └─ Deny → 返回 Denied
    │     │
    │     └─ 未命中 → 询问 UI/用户
    │           │
    │           ├─ 一次决策 → 直接返回
    │           └─ 会话级决策 → Remember() → 返回
    │
    └─ 执行工具 / 拒绝
```

### 具体变更清单

| 变更项 | 路径 | 操作 |
|--------|------|------|
| `IPermissionChannel` 接口重定义 | `Core/Interfaces/IPermissionChannel.cs` | **修改** |
| `PermissionRequest` 扩展 | `Core/Interfaces/ITool.cs` 或新建文件 | **修改** |
| `ToolContext.AskPermission` 字段 | `Core/Interfaces/ITool.cs` | **移除** |
| `BuiltInToolBase.AskPermissionAsync` | `Tools/BuiltIn/BuiltInToolBase.cs` | **修改** |
| `BuiltInToolBase.CheckPathWithinWorkspaceAsync` | `Tools/BuiltIn/BuiltInToolBase.cs` | **修改** |
| `IPermissionMemory` + `SessionPermissionMemory` | `Core/Permission/` | **新增** |
| `BlazorPermissionChannel` | `samples/WebUI/Services/` | **修改** |
| `GatewayPermissionChannel` | `Gateway/Permission/` | **修改** |
| `SerializingPermissionChannel` | `Core/Permission/` | **保留不修改** |
| `DynamicPermissionChannel` | `Core/Permission/` | **评估是否仍需要** |
| `add_workspace_path` 工具 | `Tools/BuiltIn/FileSystem/` | **新增** |
| 所有内置工具（Read/Write/Edit/Glob/Grep/Bash/WebFetch 等） | `Tools/BuiltIn/` | **修改** |
| `AgentExecutor` | `Core/` | **修改**（PermissionChannel 调用方式统一） |
| DI 注册 | `Extensions/` | **修改** |

---

## 接口边界

### IPermissionChannel 实现者职责

- 内嵌 `IPermissionMemory` 实例
- `RequestAsync` 第一步检查记忆，命中直接返回
- 未命中则触发 UI 交互（不同实现的交互方式不同：Blazor UI / Gateway 远程 / 控制台）
- 用户选择"会话级"决策时调用 `Remember()`

### IPermissionMemory 职责

- 纯数据存储 + 匹配逻辑，不涉及 UI
- 按 SessionId 隔离
- 支持目录前缀匹配（文件路径场景）

### 工具层职责

- 构建 `PermissionRequest`，填充 `PermissionKind`、`Resource`、`Patterns`
- 调用 `_channel.RequestAsync()` 获取决策
- 根据 `PermissionChannelResult.Action` 决定是否继续执行

---

## 兼容性

- `PermissionService` / `PermissionCache` / `PermissionRuleEntry` 体系保持不变
- `SerializingPermissionChannel` 装饰器保留，继续确保权限请求串行
- 与现有 `DenyAllPermissionChannel` 的交互：记忆模块对 DenyAll 场景无影响（因为永远不会询问用户）
