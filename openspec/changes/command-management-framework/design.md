## Context

### 当前状态

Seeing.Agent 框架已有完整的命令系统骨架：

| 组件 | 文件 | 职责 |
|------|------|------|
| `ICommand` | `Commands/ICommand.cs` | 命令接口，定义 `Metadata` 和 `ExecuteAsync` |
| `CommandMetadata` | `Commands/ICommand.cs` | 命令元数据（名称、描述、分类等） |
| `CommandContext` | `Commands/ICommand.cs` | 命令执行上下文 |
| `CommandRegistry` | `Commands/CommandRegistry.cs` | 命令注册表，存储和查询命令 |
| `CommandDiscovery` | `Commands/Discovery/CommandDiscovery.cs` | 反射发现，扫描 `[Command]` 注解 |
| `CommandDispatcher` | `Commands/CommandDispatcher.cs` | 命令分发，解析输入并执行 |
| `CommandService` | `Commands/CommandService.cs` | Hook 集成，触发 `command.execute.before` |

### 约束

- **最小改动原则** - 不引入新抽象层，不破坏现有接口
- **复用现有模式** - YAML frontmatter 解析参考 `AgentDiscovery` 和 `SkillManager`
- **线程模型** - 命令注册在启动时完成，运行时只读

### 利益相关者

- **Session 用户** - 使用斜杠命令交互
- **TUI 开发者** - 命令面板显示 Agent 归属
- **Extension 开发者** - 提供 Markdown 命令目录

---

## Goals / Non-Goals

### Goals

1. **Markdown 命令发现** - 从 `.seeing/commands/**/*.md` 和 `.agents/commands/**/*.md` 发现命令
2. **模板参数支持** - 支持 `{{1}}`、`{{2}}`、`{{arguments}}` 占位符
3. **Agent 关联** - 命令可指定 `Agent` 属性，执行时传递到上下文
4. **YAML frontmatter 解析** - 支持 `name`、`description`、`aliases`、`category`、`agent` 字段
5. **向后兼容** - 现有代码无需修改

### Non-Goals

1. **命令热重载** - 文件变更后自动更新命令（后续考虑）
2. **复杂模板引擎** - 不支持条件、循环、默认值等高级语法
3. **命令命名空间** - 不支持 `ext:command` 格式
4. **线程安全注册** - 保持现有设计，启动时注册、运行时只读

---

## Decisions

### D1: 属性放置位置

**决策**: 将 `Template` 和 `Agent` 放入 `CommandMetadata`，`TargetAgent` 放入 `CommandContext`。

**理由**:
- `Template` 和 `Agent` 是命令的元数据，与 `Name`、`Description` 性质相同
- `TargetAgent` 是运行时传递字段，属于上下文

**替代方案**: 
- 在 `ICommand` 接口上新增属性 → 破坏接口契约
- 使用 `Metadata.Data` 字典 → 类型不安全

### D2: 模板语法

**决策**: 使用简单的字符串替换语法 `{{1}}`、`{{arguments}}`。

**理由**:
- 实现简单（约 10 行代码）
- 满足 90% 的使用场景
- 无需引入模板引擎依赖

**不支持**:
- 命名参数 `{{name}}`
- 默认值 `{{count|10}}`
- 条件/循环

**替代方案**:
- Mustache/Liquid 模板引擎 → 过度设计
- `$1`/`$ARGUMENTS` 语法 → 与 Shell 变量冲突

### D3: Markdown 命令实现方式

**决策**: 使用内部类 `MarkdownCommand` 包装，放入 `CommandDiscovery.cs`。

**理由**:
- 与现有 `ReflectedCommand` 并列，保持文件组织一致
- 内部类不暴露，减少 API 表面
- 复用现有 `FrontmatterRegex` 正则

**替代方案**:
- 独立文件 `MarkdownCommand.cs` → 增加文件数量
- 实现 `ICommandProvider` 接口 → 过度抽象

### D4: Agent 切换逻辑

**决策**: `CommandDispatcher` 仅传递 `TargetAgent`，不执行切换。

**理由**:
- 切换 Agent 涉及会话状态，属于上层职责
- `CommandDispatcher` 保持单一职责（分发）
- `CommandService` 或调用方根据 `TargetAgent` 决定是否切换

**调用方处理**:
```csharp
var result = await dispatcher.HandleAsync(input, context, ct);
if (!string.IsNullOrEmpty(context.TargetAgent))
{
    await agentManager.SwitchAgentAsync(context.TargetAgent);
}
```

---

## Risks / Trade-offs

| 风险 | 影响 | 缓解措施 |
|------|------|----------|
| **模板注入** | 用户输入包含 `{{` 导致意外替换 | 文档说明：调用方需确保输入安全；后续可添加转义语法 |
| **YAML 解析失败** | frontmatter 格式错误导致命令无法加载 | 捕获异常，记录警告，跳过该文件 |
| **命令名冲突** | Markdown 命令与现有命令同名 | 遵循现有行为：后注册覆盖，日志警告 |
| **Agent 切换失败** | 指定的 Agent 不存在 | 调用方负责验证，`CommandDispatcher` 不处理 |

---

## Migration Plan

### 部署步骤

1. **代码合并** - 合并 PR 后无特殊部署要求
2. **默认目录** - 自动扫描 `.seeing/commands/` 和 `.agents/commands/`
3. **扩展集成** - 扩展可通过 `CommandDiscovery` 注册额外目录

### 回滚策略

- 所有改动是增量式的
- 不修改现有接口签名
- 可直接回滚代码，不影响现有功能

---

## Open Questions

1. **Q: 是否需要支持命令目录配置？**
   - 当前方案硬编码扫描目录
   - 可通过 `CommandDiscovery` 构造函数注入目录列表
   - **决策**: 保持简单，硬编码默认目录，后续按需扩展

2. **Q: 模板中是否需要转义语法？**
   - 当前不支持转义 `{{` 和 `}}`
   - 如果用户输入包含这些字符，会产生意外替换
   - **决策**: 首版不支持，收集反馈后决定
