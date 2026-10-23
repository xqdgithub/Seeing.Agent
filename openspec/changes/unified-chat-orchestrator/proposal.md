## Why

当前架构存在 Session 管理分散的问题：WebUI 和 Gateway 各自实现了一套类似的逻辑（History 构建、事件跟踪、模型选择），导致数据流不一致。当 Skill 命令修改 History 后，UI 层已添加的原始命令与服务层展开后的内容不同步，暴露了架构的根本问题——入口职责不清。

需要一个统一的 ChatOrchestrator 入口，让所有调用方（WebUI、Gateway、CLI）都通过同一入口处理 Session 和 Agent 执行，UI 层只负责渲染 Session 变化。

## What Changes

### 新增
- **Seeing.Agent.App** 项目：统一入口层
- **IChatOrchestrator** 接口：最小化入口签名 `ExecuteAsync(sessionId, input, options)`
- **ChatInput/ChatOptions**：用户输入和可选配置模型
- **ChatExecutionContext**：内部执行上下文（不对外暴露）
- **ChatOrchestrator** 实现：统一管理 Session、命令预处理、Agent 执行、事件发送
- **命令处理**：在入口层预处理系统命令和 Skill 命令

### 重构
- **GatewayOrchestrator**：改为 ChatOrchestrator 适配层
- **Session.razor**：删除重复逻辑，使用 ChatOrchestrator
- **EventStreamHandler**：简化为 UI 状态管理

### 移除
- SimulateAgentResponseAsync 中的内联逻辑（~200 行）
- BuildHistoryFromSessionMessages 重复实现
- UI 层的 HandleBuiltInCommand 逻辑

## Capabilities

### New Capabilities
- `chat-orchestrator`: 统一的 Agent 执行入口，管理 Session 生命周期、命令预处理、Agent 调用、事件发送
- `command-processing`: 命令预处理能力，支持系统命令（执行后返回）和 Skill 命令（修改 History 后继续执行）

### Modified Capabilities
- `gateway-protocol`: Gateway 改为 ChatOrchestrator 适配层，入口签名不变，内部实现改为调用 ChatOrchestrator

## Impact

### 新增项目
- `src/Seeing.Agent.App/`：统一入口层，依赖 Seeing.Agent, Seeing.Agent.Acp, Seeing.Session

### 修改项目
- `src/Seeing.Agent.Gateway/`：GatewayOrchestrator 改为适配层
- `samples/Seeing.Agent.WebUI/`：Session.razor, EventStreamHandler, PromptInput.razor

### 新增文件
- `ChatOrchestrator.cs`, `IChatOrchestrator.cs`
- `Models/ChatInput.cs`, `Models/ChatOptions.cs`, `Models/ChatEvent.cs`
- `Internal/ChatEventTracker.cs`, `Internal/ChatExecutionQueue.cs`
- `Commands/CommandType.cs`, `Commands/BuiltIn/*.cs`, `Commands/SkillCommand.cs`

### API 变化
- 新增 `IChatOrchestrator.ExecuteAsync(sessionId, input, options)` 接口
- Gateway 入口签名保持兼容，内部重构
