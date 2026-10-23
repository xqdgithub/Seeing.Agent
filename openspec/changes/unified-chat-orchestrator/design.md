## Context

### 当前架构问题

`
WebUI (Session.razor)              Gateway (GatewayOrchestrator)
    Session 管理 (分散)                Session 管理 (统一)
    History 构建 (内联重复)            History 构建 (封装)
    事件处理      事件跟踪
    直接调用 AgentRouter              通过 Orchestrator 调用

问题：两套重复逻辑，数据流不一致，Skill 命令展开后 UI 不同步
`

### 约束条件
- 不修改 Seeing.Session（基础层，最小依赖）
- Gateway 入口签名保持兼容
- 支持多调用方：WebUI、Gateway、CLI（未来）
- 不引入循环依赖

## Goals / Non-Goals

**Goals:**
- 创建统一的 ChatOrchestrator 入口，所有调用方共用
- Session 管理统一在服务层，UI 层只负责渲染
- 命令预处理集成到入口层
- 消除 WebUI 和 Gateway 之间的重复代码

**Non-Goals:**
- 不修改 Seeing.Session 的结构
- 不修改 Gateway 的外部 API 签名
- 不重构 AgentExecutor 核心逻辑
- 不实现 CLI/TUI 客户端（仅预留接口）

## Decisions

### D1: 新建 Seeing.Agent.App 项目

**选择**: 新建独立项目作为统一入口层

**原因**:
- 避免 Seeing.Agent 核心库膨胀
- 避免循环依赖（Gateway → Agent → Session）
- 清晰的职责边界：Agent（核心能力）vs App（编排层）

**替代方案**:
- 扩展 Seeing.Agent：违反核心库最小依赖原则
- 扩展 Seeing.Agent.Gateway：Gateway 应该是适配层，不是核心

### D2: 入口签名设计

**原则**: 入口参数最小化，只包含外部容易提供的数据。

```
IChatOrchestrator.ExecuteAsync(
    string sessionId,        // 会话 ID
    ChatInput input,         // 用户输入（text + attachments）
    ChatOptions? options,    // 可选配置（agent/model/mode）
    CancellationToken ct)
-> IAsyncEnumerable<ChatEvent>
```

**原因**:
- `sessionId`：调用方必须知道操作哪个会话
- `input`：用户发送的内容（文本 + 附件）
- `options`：可选配置，使用默认值

**不应该从外部传入的参数**（内部获取）:
- `IServiceProvider`：DI 构造函数注入
- `AgentDefinition`：从 AgentRegistry 根据 agentId 获取
- `History`：从 SessionManager 加载会话历史
- `WorkspaceRoot`：从 IWorkspaceProvider 获取
- `IPermissionChannel`：DI 注入或内部创建
- `ParentSessionId`、`IsTopLevel`、`IsBackground`：内部调用时设置

### D2.1: 分层设计（公开接口 vs 内部上下文）

```
┌─────────────────────────────────────────────────────────────────┐
│  公开接口 (IChatOrchestrator)                                    │
│  ExecuteAsync(sessionId, input, options)                        │
│  - 最小化参数，外部容易提供                                       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  内部构建 (ChatExecutionContext)                                 │
│  - SessionManager.LoadAsync(sessionId) → History                │
│  - AgentRegistry.GetOrCreate(agentId) → Agent                   │
│  - IWorkspaceProvider.WorkspaceRoot                             │
│  - PermissionChannel (从 DI 或创建)                              │
│  - 其他内部状态                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  内部执行 (ExecuteInternalAsync)                                 │
│  - 命令预处理                                                    │
│  - Agent 执行                                                    │
│  - 事件发送                                                      │
│  - 子代理调用 (使用 CreateSubAgentContext)                       │
└─────────────────────────────────────────────────────────────────┘
```

**ChatExecutionContext 内部字段**:
- `SessionId`, `Agent`, `History`
- `WorkingDirectory`, `WorkspaceRoot`
- `PermissionChannel`, `PermissionContext`
- `ParentSessionId`, `IsTopLevel`, `IsBackground`（子代理用）
- `ChannelId`, `UserId`（Gateway 用）

### D3: 命令类型枚举

`
CommandType.Default  // 透传给 Agent
CommandType.System   // 执行后返回（/new, /clear, /help）
CommandType.Skill    // 替换 History 后继续执行
`

**原因**:
- 与现有 CommandCategory 正交（Category 用于帮助分组，Type 用于执行行为）
- 简单明了，三种行为

### D4: 事件驱动 UI 更新

`
ChatOrchestrator
    ↓ 修改 Session
    ↓ 发送 ChatEvent
UI Adapter
    ↓ 监听 ChatEvent
    ↓ 刷新 UI
`

**原因**:
- UI 层不直接修改 Session
- 通过事件通知 UI 刷新
- 保持 UI 层简单（只渲染）

## Architecture

`
┌─────────────────────────────────────────────────────────────────┐
│                         Seeing.Agent.App                         │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                    ChatOrchestrator                        │  │
│  │  - Session 管理                                            │  │
│  │  - 命令预处理                                              │  │
│  │  - Agent 调用                                              │  │
│  │  - 事件发送                                                │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                               │
          ┌────────────────────┼────────────────────┐
          ▼                    ▼                    ▼
   ┌─────────────┐      ┌─────────────┐      ┌─────────────┐
   │    WebUI    │      │   Gateway   │      │   CLI/TUI   │
   │   Adapter   │      │   Adapter   │      │   Adapter   │
   │ (渲染 Session)│    │ (WebSocket) │      │  (未来)     │
   └─────────────┘      └─────────────┘      └─────────────┘
`

## Risks / Trade-offs

### R1: 迁移风险
**风险**: WebUI 和 Gateway 同时迁移，可能引入回归
**缓解**: 分阶段迁移，先 Gateway 适配，再 WebUI 重构

### R2: 循环依赖风险
**风险**: Seeing.Agent.App 依赖多个项目，可能产生循环
**缓解**: 保持单向依赖：App → Agent → Session

### R3: 性能影响
**风险**: 新增一层抽象，可能影响性能
**缓解**: 抽象层仅做编排，无额外开销

### T1: 事件流复杂度
**权衡**: 事件流 vs 直接返回
**选择**: 事件流更适合多客户端场景，复杂度可接受

## Migration Plan

### Phase 1: 创建 Seeing.Agent.App
- 定义 IChatOrchestrator 接口
- 定义 ChatInput/ChatOptions/ChatEvent 模型
- 实现 ChatOrchestrator（从 Gateway 提取逻辑）
- 实现内部组件（EventTracker, ExecutionQueue）

### Phase 2: Gateway 适配
- GatewayOrchestrator 改为调用 ChatOrchestrator
- 保持 GatewayRequest/GatewayEvent 兼容

### Phase 3: WebUI 重构
- Session.razor 使用 ChatOrchestrator
- 删除 SimulateAgentResponseAsync 重复逻辑
- 简化 EventStreamHandler

### Phase 4: 命令处理
- 添加命令预处理入口
- 实现系统命令
- 实现 Skill 命令

### 回滚策略
- 每个阶段独立可回滚
- Gateway 保持原接口，内部切换
- WebUI 可保留原逻辑作为 fallback

## Open Questions

1. **ChatEvent 与 IMessageEvent 的关系**：
   - 选项 A：ChatEvent 映射到 IMessageEvent
   - 选项 B：ChatEvent 替代 IMessageEvent
   - 建议：选项 A，保持兼容

2. **命令注册时机**：
   - 选项 A：应用启动时注册
   - 选项 B：Skill 发现时动态注册
   - 建议：选项 A，启动时注册
