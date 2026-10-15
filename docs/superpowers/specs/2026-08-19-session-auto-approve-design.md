# 会话级自动批准设计

**日期:** 2026-08-19
**状态:** 已确认

## 背景

当前「自动批准」是纯全局开关（`SeeingAgentOptions.Permission.AutoApproveAll`），优先级最高——一旦全局开启，会覆盖 WebUI 的交互式确认弹窗。缺少会话级别的独立控制能力。

## 目标

在输入组件附近增加会话级「自动批准」开关，以会话级别为准，默认跟随全局值；全局开关与三态开关状态需可见，便于判断。

## 三态语义

| 枚举值 | 含义 |
|--------|------|
| `FollowGlobal` | 跟随全局 `AutoApproveAll`（默认） |
| `Enabled` | 强制自动批准（本会话） |
| `Disabled` | 强制交互式确认（本会话，即使全局开也弹窗） |

## 设计

### ① 数据模型（`Seeing.Session` 原语层）

- 新增枚举（与 `SessionData` 同命名空间）：

  ```csharp
  public enum SessionAutoApprove
  {
      FollowGlobal = 0,  // 跟随全局（默认）
      Enabled = 1,       // 强制自动批准
      Disabled = 2       // 强制交互式确认
  }
  ```

- `SessionData` 新增字段，并在 `Clone()` 中复制：

  ```csharp
  public SessionAutoApprove AutoApprove { get; set; } = SessionAutoApprove.FollowGlobal;
  ```

### ② App 层（`Seeing.Agent.App`）

- `ChatOptions` 新增字段：

  ```csharp
  public SessionAutoApprove AutoApprove { get; init; } = SessionAutoApprove.FollowGlobal;
  ```

- `ExecutionJobService.ResolvePermissionChannel` 增加参数 `SessionAutoApprove autoApprove`，最终通道选择优先级：

  ```
  ① Enabled       → DefaultPermissionChannel.AutoApproveInstance（强制自动批准）
  ② Disabled      → callerChannel（WebUI 交互式）；无 caller 则 DenyAll
  ③ FollowGlobal  → 现有逻辑（全局 AutoApproveAll → callerChannel → DenyAll）
  ```

- `BuildExecutionContextAsync` 调用处传入 `record.Options?.AutoApprove ?? SessionAutoApprove.FollowGlobal`。

- 现有「全局 AutoApproveAll 覆盖 caller channel」的行为仅保留在 `FollowGlobal` 分支内，语义不变。

### ③ WebUI（`Seeing.Agent.WebUI`）

- `PromptInput.razor`（footer 重构）：
  - 删除帮助文字（`prompt-help-text` span）；`HelpText` 参数保留（移动端 placeholder 仍使用）
  - footer 布局：左侧 Token 预算（`ChatTokenStatusBar`），右侧 `Segmented<SessionAutoApprove>`（`Size="Small"`）
  - Token 预算：桌面端显示于左（维持 `BudgetStatus.MaxTokens > 0` 条件），移动端隐藏
  - Segmented 三段文案（`AppState.IsMobile` 区分）：
    - 桌面端完整：`跟随全局（开）` / `跟随全局（关）` / `自动批准` / `需确认`
    - 移动端精简：`全局·开` / `全局·关` / `自动` / `确认`，完整语义放 Tooltip
  - 移动端 footer 仅显示 Segmented（Token 预算隐藏）
  - 「跟随全局」段 Tooltip 说明「当前全局自动批准：已开启/已关闭」
  - 新增参数：`SessionAutoApprove AutoApprove`、`EventCallback<SessionAutoApprove> OnAutoApproveChanged`、`bool GlobalAutoApprove`、`bool AutoApproveDisabled`
- `Session.razor`：
  - 注入 `IOptionsMonitor<SeeingAgentOptions>`，读取 `CurrentValue.Permission?.AutoApproveAll ?? false` 作为 `GlobalAutoApprove`
  - **热更**：`OnInitializedAsync` 中 `_globalAutoApproveSubscription = optionsMonitor.OnChange(...)`，回调里更新 `_globalAutoApprove` 并 `InvokeAsync(StateHasChanged)`；在 `Dispose()` 释放订阅（`SeeingAgentOptionsMonitor.OnChange` 已支持热重载通知，`UnifiedConfigManager.ConfigChanged` 驱动）
  - 双向绑定 `SessionState.CurrentSession.AutoApprove`；切换时写回并 `SessionManager.SaveAsync` 持久化
  - `OnSubmitMessage` 中设置 `ChatOptions.AutoApprove = SessionState.CurrentSession.AutoApprove`
  - 子会话（`IsSubAgentView`）禁用开关（`AutoApproveDisabled = true`）

### ④ 数据流

```
打开会话 → 读 SessionData.AutoApprove → Segmented 显示
切换     → 写回 CurrentSession.AutoApprove → SaveAsync 持久化
提交     → ChatOptions.AutoApprove → ResolvePermissionChannel 定最终通道
热更     → UnifiedConfigManager.ConfigChanged → SeeingAgentOptionsMonitor.OnChange
           → 更新 GlobalAutoApprove 显示（跟随全局会话下次执行由后端 CurrentValue 实时生效）
```

### ⑤ 错误处理

- 持久化失败沿用现有 `SaveAsync` 异常处理（`MessageService.Error`）
- 无新增错误路径

### ⑥ 测试

- `ResolvePermissionChannel`：三态 × 全局开关 × caller 有无 组合单测（`Seeing.Agent.Tests`）
- `SessionData` 序列化 + `Clone()` 往返测试（`Seeing.Session.Tests`）
- 回归现有权限测试

## 非目标

- 不改 Gateway/WeCom/QQ 的 `AutoApproveLowRisk` 策略
- 不新增配置节；会话级状态只存 `SessionData`
