## Context

当前 `PromptInput.razor` 组件实现了基本的命令自动完成功能，当用户输入 `/` 时显示候选列表。但候选列表仅来源于 `AvailableTools` 和 `AvailableSkills` 两个参数，缺少内置命令和用户自定义命令的支持。

命令系统已有的基础设施：
- `ICommandRegistry` - 命令注册表接口
- `CommandDiscovery.DiscoverFromMarkdownAsync()` - Markdown 命令解析
- `CommandDispatcher` - 命令分发执行
- `CommandMetadata` - 命令元数据定义

需要在 WebUI 层面扩展候选列表来源，并实现命令的动态可用性检查。

## Goals / Non-Goals

**Goals:**
- 在命令自动完成候选列表中添加内置命令
- 支持加载项目级和用户级自定义 Markdown 命令
- 根据会话状态动态禁用不可用的命令
- 处理自定义命令与内置命令的同名冲突
- 按优先级排序候选列表

**Non-Goals:**
- 不实现全局快捷键监听（仅显示快捷键提示）
- 不修改底层 `ICommandRegistry` 和 `CommandDispatcher` 的实现
- 不实现命令的持久化存储（使用现有文件系统）

## Decisions

### 1. 命令来源架构

**决策**: 采用多 Provider 模式，在 UI 层组合多个命令来源

**理由**: 
- 保持与现有 `AvailableTools`、`AvailableSkills` 参数模式一致
- 避免修改底层命令系统，降低影响范围
- 便于后续扩展新的命令来源

**备选方案**:
- 在 `ICommandRegistry` 中注册所有命令 → 影响范围大，需要修改核心模块
- 在 Session.razor 中硬编码命令列表 → 不利于维护和测试

### 2. 命令优先级排序

**决策**: 使用整数优先级字段，数值越小优先级越高

```
Priority 0: 项目级自定义命令
Priority 1: 用户级自定义命令  
Priority 2: 内置命令
Priority 3: 工具
Priority 4: 技能
Priority 99: 冲突禁用的命令
```

**理由**: 简单直观，便于扩展新类型命令

### 3. 冲突处理策略

**决策**: 内置命令优先，冲突的自定义命令标记为禁用但仍显示

**理由**:
- 内置命令行为确定，避免用户意外覆盖
- 显示冲突命令让用户知晓存在同名命令
- 用户可通过重命名自定义命令解决冲突

### 4. 可用性检查位置

**决策**: 在 `PromptInput.razor` 组件中通过 `CommandAvailabilityChecker` 服务检查

**理由**:
- 可用性依赖于 UI 状态（如 `SessionState`）
- 避免将 UI 状态传递到核心命令模块
- 便于在组件中显示禁用原因

## Risks / Trade-offs

| 风险 | 缓解措施 |
|------|----------|
| 大量自定义命令影响加载性能 | 采用懒加载，首次打开下拉框时加载 |
| 命令分类标签显示拥挤 | 使用紧凑样式，必要时省略 |
| 可用性状态不同步 | 在 `HandleInputChange` 时重新检查 |
| 快捷键提示与实际不符 | 文档明确仅显示提示，不实现监听 |

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                      Session.razor                              │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  OnInitializedAsync:                                       │ │
│  │    1. LoadCustomCommandsAsync() → _customCommands          │ │
│  │    2. LoadToolsAndSkillsAsync() → _availableTools/Skills   │ │
│  └────────────────────────────────────────────────────────────┘ │
│                              │                                   │
│                              ▼                                   │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  <PromptInput                                              │ │
│  │      AvailableCustomCommands="_customCommands"             │ │
│  │      AvailableTools="_availableTools"                      │ │
│  │      AvailableSkills="_availableSkills"                    │ │
│  │      OnBuiltInCommand="HandleBuiltInCommand" />            │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│                      PromptInput.razor                          │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  LoadCommands():                                           │ │
│  │    1. Add CustomCommands (check conflict with BuiltIn)     │ │
│  │    2. Add BuiltInCommands (check availability)             │ │
│  │    3. Add Tools                                            │ │
│  │    4. Add Skills                                           │ │
│  │    5. Sort by Priority, then Name                          │ │
│  └────────────────────────────────────────────────────────────┘ │
│                              │                                   │
│                              ▼                                   │
│  ┌────────────────────────────────────────────────────────────┐ │
│  │  SelectCommand(cmd):                                       │ │
│  │    if cmd.IsDisabled → show warning                        │ │
│  │    if cmd.IsBuiltIn → OnBuiltInCommand.InvokeAsync()       │ │
│  │    else → insert to InputText                              │ │
│  └────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```
