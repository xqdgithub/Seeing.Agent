# Token 预算管理集成设计

> **Status:** Draft
> **Created:** 2025-01-15
> **Related:** `2025-01-15-token-budget-management-design.md`（基础模块已实现）

## 概述

本文档描述如何将已实现的 Token 预算管理模块集成到 AgentExecutor 执行流程中，包括：
1. AgentExecutor 预算检查集成
2. LLM 摘要压缩策略（SummarizingStrategy）
3. 混合压缩策略（HybridStrategy）
4. UI 实时更新机制

## 目标

- 在 LLM 请求前后自动检查和管理 Token 预算
- 提供多种压缩策略以适应不同场景
- 通过现有事件流机制实时更新 UI
- 遵循项目统一配置架构

## 非目标

- 不修改现有的 Token 计算逻辑
- 不引入新的通信协议（如 SignalR）
- 不在 appsettings.json 中添加业务配置

## 架构设计

### 整体架构

```
┌─────────────────────────────────────────────────────────────────┐
│                        AgentExecutor                             │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────────┐  │
│  │ LoopStart   │───▶│ LLM Request │───▶│ LoopComplete        │  │
│  └─────────────┘    └─────────────┘    └─────────────────────┘  │
│         │                  │                      │              │
│         ▼                  ▼                      ▼              │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │                    Hook Manager                              ││
│  │  • chat.before_start  → BudgetCheckHook (压缩+预算检查)      ││
│  │  • chat.after_complete → BudgetUpdateHook (更新统计)        ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Token Budget Services                         │
│  ┌──────────────────┐  ┌──────────────────┐  ┌────────────────┐ │
│  │ ITokenBudget     │  │ ICompression     │  │ ICompression   │ │
│  │ Manager          │  │ Trigger          │  │ StrategyFactory│ │
│  └──────────────────┘  └──────────────────┘  └────────────────┘ │
│                              │                                   │
│                              ▼                                   │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │         Compression Strategies (ICompressionStrategy)        ││
│  │  ┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐ ││
│  │  │SlidingWindow    │ │ Summarizing     │ │ Hybrid          │ ││
│  │  │ TokenStrategy   │ │ Strategy        │ │ Strategy        │ ││
│  │  │ (已实现)        │ │ (新增)          │ │ (新增)          │ ││
│  │  └─────────────────┘ └─────────────────┘ └─────────────────┘ ││
│  └──────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Event Stream (IMessageEvent)                │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │  BudgetStatusEvent, BudgetWarningEvent, CompactionEvent      ││
│  │  (前端 ChatTokenStatusBar 订阅并实时渲染)                     ││
│  └──────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

### 核心流程

1. `chat.before_start` → 检查 `PendingCompaction` 标记，执行压缩（如需要）
2. LLM 循环正常执行
3. `chat.after_complete` → 计算新预算状态，更新 Session，发送 `BudgetStatusEvent`

## 组件设计

### 1. 压缩策略

#### 1.1 SummarizingStrategy（LLM 摘要策略）

使用当前会话的 LLM 生成历史摘要。

**流程：**
```
输入: messages[0..n] (需要压缩的历史消息)
输出: [系统提示, 摘要消息, 最近 k 条消息]

步骤:
1. 保留第一条消息（系统提示）
2. 构建摘要提示词，要求 LLM 总结 messages[1..n-k] 的关键信息
3. 保留最近 k 条消息不压缩
4. 返回压缩后的消息列表
```

**摘要提示词模板：**
```
请将以下对话历史压缩为简洁的摘要，保留关键信息：
- 用户的主要请求
- 助手完成的关键操作
- 重要的上下文信息（文件路径、决策结果等）

对话历史：
{messages}

摘要格式：
【用户请求】...
【已完成的操作】...
【重要上下文】...
```

**关键实现点：**
- 复用 `ILlmService` 进行摘要生成
- 使用 `AgentDefinition.Model` 指定的模型
- 摘要失败时返回 `CompressionResult.Failed`

#### 1.2 HybridStrategy（混合策略）

组合 SlidingWindow 和 Summarizing 策略，提供更可靠的压缩。

**决策逻辑：**
```
流程:
1. 先尝试 SlidingWindowTokenStrategy
2. 检查压缩后 token 是否低于目标阈值（config.SlidingWindowKeepTokens）
3. 若仍超限，调用 SummarizingStrategy
4. 若仍超限，返回失败

目标阈值定义:
- SlidingWindow 阶段: config.SlidingWindowKeepTokens（默认 20000 tokens）
- Summarizing 阶段: config.SummaryTargetTokens（默认 4000 tokens）

决策树:
┌─────────────────────────────────────────────────┐
│ SlidingWindow 压缩后仍超目标阈值?                │
│   ├─ 是 → 尝试摘要压缩                           │
│   │      └─ 仍超 SummaryTargetTokens? → Failed  │
│   └─ 否 → 返回 SlidingWindow 结果                │
└─────────────────────────────────────────────────┘
```

### 2. Hook 集成

#### 2.1 BudgetCheckHook（chat.before_start）

在 LLM 请求前检查并执行待处理的压缩。

```csharp
public class BudgetCheckHook : ISyncHook
{
    public string HookPoint => HookRegistry.ChatBeforeStart;
    public int Priority => 100; // 高优先级，最先执行
    
    public HookResult Execute(HookContext context)
    {
        var session = context.Get<SessionData>("session");
        var agent = context.Get<AgentDefinition>("agent");
        
        // 1. 检查 PendingCompaction 标记
        if (session.PendingCompaction)
        {
            // 2. 执行压缩
            var result = _compressionService.CompressAsync(session, agent);
            
            // 3. 清除标记
            session.PendingCompaction = false;
            
            // 4. 发送 CompactionEvent
            context.Events.Add(new CompactionEvent { ... });
        }
        
        return HookResult.Continue();
    }
}
```

#### 2.2 BudgetUpdateHook（chat.after_complete）

在 LLM 响应后更新预算状态。

```csharp
public class BudgetUpdateHook : IAsyncHook
{
    public string HookPoint => HookRegistry.ChatAfterComplete;
    
    public async Task<HookResult> ExecuteAsync(HookContext context)
    {
        var session = context.Get<SessionData>("session");
        var agent = context.Get<AgentDefinition>("agent");
        
        // 1. 计算当前 token 分布
        var breakdown = _budgetManager.CalculateBreakdown(session, ...);
        
        // 2. 获取有效配置
        var config = _configResolver.Resolve(session.BudgetConfig, agent.BudgetConfig, _globalConfig);
        
        // 3. 检查预算状态
        var status = _budgetManager.CheckBudget(session, config, breakdown.Total);
        
        // 4. 根据状态设置 PendingCompaction
        if (status.Level >= BudgetLevel.Critical)
        {
            session.PendingCompaction = true;
        }
        
        // 5. 发送 BudgetStatusEvent
        context.Events.Add(new BudgetStatusEvent { ... });
        
        return HookResult.Continue();
    }
}
```

### 3. 新增事件类型

```csharp
// 预算状态更新事件
public class BudgetStatusEvent : IMessageEvent
{
    public string Type => MessageEventType.BudgetStatus;
    public string SessionId { get; set; }
    public int CurrentTokens { get; set; }
    public int MaxTokens { get; set; }
    public double UsagePercentage { get; set; }
    public BudgetLevel Level { get; set; }
    public TokenBreakdownResponse? Breakdown { get; set; }
}

// 压缩执行事件
public class CompactionEvent : IMessageEvent
{
    public string Type => MessageEventType.Compaction;
    public string SessionId { get; set; }
    public string Strategy { get; set; }
    public int TokensBefore { get; set; }
    public int TokensAfter { get; set; }
    public int MessagesRemoved { get; set; }
    public bool Success { get; set; }
}

// 预算警告事件
public class BudgetWarningEvent : IMessageEvent
{
    public string Type => MessageEventType.BudgetWarning;
    public string SessionId { get; set; }
    public string Message { get; set; }
    public BudgetLevel Level { get; set; }
}
```

### 4. UI 实时更新

扩展 `ChatTokenStatusBar.razor` 以订阅 `IMessageEvent` 流：

```
┌─────────────────────────────────────────────────────────────────┐
│                    Frontend (Blazor WebUI)                       │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │  ChatTokenStatusBar.razor                                   ││
│  │  ┌─────────────────────────────────────────────────────────┐││
│  │  │ [订阅 IMessageEvent 流]                                  │││
│  │  │  • BudgetStatusEvent → 更新进度条和统计                  │││
│  │  │  • CompactionEvent  → 显示压缩通知                       │││
│  │  │  • BudgetWarningEvent → 显示警告提示                     │││
│  │  └─────────────────────────────────────────────────────────┘││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

**ChatTokenStatusBar 扩展：**
```razor
@implements IAsyncDisposable

@code {
    [Parameter] public string SessionId { get; set; }
    [Parameter] public IObservable<IMessageEvent>? EventStream { get; set; }
    
    private IDisposable? _eventSubscription;
    private BudgetStatusResponse _status = new();
    
    protected override void OnInitialized()
    {
        if (EventStream != null)
        {
            _eventSubscription = EventStream
                .Where(e => e.SessionId == SessionId)
                .Subscribe(OnEventReceived);
        }
    }
    
    private void OnEventReceived(IMessageEvent evt)
    {
        switch (evt)
        {
            case BudgetStatusEvent status:
                _status = MapToResponse(status);
                break;
            case CompactionEvent compaction:
                ShowCompactionNotification(compaction);
                break;
            case BudgetWarningEvent warning:
                ShowWarningNotification(warning);
                break;
        }
        InvokeAsync(StateHasChanged);
    }
}
```

### 5. 配置集成

#### 5.1 配置层级

```
配置层级（优先级从高到低）：
┌─────────────────────────────────────────────────────────────────┐
│ 1. Session 级别 (SessionData.BudgetConfig)                      │
│    → 单个会话的覆盖配置                                          │
├─────────────────────────────────────────────────────────────────┤
│ 2. Agent 级别 (AgentDefinition.BudgetConfig)                    │
│    → Agent 定义中的默认配置                                      │
├─────────────────────────────────────────────────────────────────┤
│ 3. 项目级 ({WorkspaceRoot}/.seeing/seeing.json)                 │
│    → TokenBudget 节                                             │
├─────────────────────────────────────────────────────────────────┤
│ 4. 用户级 (~/.seeing/seeing.json)                               │
│    → TokenBudget 节（全局默认）                                  │
└─────────────────────────────────────────────────────────────────┘
```

#### 5.2 配置模型扩展

```csharp
// SeeingAgentOptions.cs 中添加
public class SeeingAgentOptions
{
    // 现有属性...
    
    /// <summary>Token 预算全局配置</summary>
    public TokenBudgetOptions TokenBudget { get; set; } = new();
}

// 新增：Token 预算配置选项
public class TokenBudgetOptions
{
    /// <summary>默认最大上下文 Token 数</summary>
    public int? MaxContextTokens { get; set; }
    
    /// <summary>警告阈值</summary>
    public ThresholdOptions WarningThreshold { get; set; } = new() { Percentage = 80 };
    
    /// <summary>压缩阈值</summary>
    public ThresholdOptions CompactionThreshold { get; set; } = new() { Percentage = 90 };
    
    /// <summary>压缩策略类型</summary>
    public CompactionStrategyType CompactionStrategy { get; set; } = CompactionStrategyType.SlidingWindow;
    
    /// <summary>滑动窗口保留 Token 数</summary>
    public int SlidingWindowKeepTokens { get; set; } = 20000;
    
    /// <summary>摘要目标 Token 数</summary>
    public int SummaryTargetTokens { get; set; } = 4000;
    
    /// <summary>是否启用自动压缩</summary>
    public bool AutoCompactionEnabled { get; set; } = true;
}

public class ThresholdOptions
{
    public int? Percentage { get; set; }
    public int? AbsoluteTokens { get; set; }
}
```

#### 5.3 配置节注册

```csharp
// UnifiedConfigManager.BuildSectionRegistry() 中添加
["TokenBudget"] = new("TokenBudget", "seeing.json", ConfigScope.Both, 
    typeof(TokenBudgetOptions), displayName: "Token 预算配置", displayOrder: 16),
```

#### 5.4 配置文件示例

```json
// .seeing/seeing.json
{
  "SeeingAgent": {
    "TokenBudget": {
      "MaxContextTokens": 128000,
      "WarningThreshold": { "Percentage": 80 },
      "CompactionThreshold": { "Percentage": 90 },
      "CompactionStrategy": "Hybrid",
      "SlidingWindowKeepTokens": 20000,
      "SummaryTargetTokens": 4000,
      "AutoCompactionEnabled": true
    }
  }
}
```

### 6. DI 依赖关系

```
┌─────────────────────────────────────────────────────────────────┐
│                     Dependency Injection                         │
│                                                                  │
│  ┌─────────────────┐      ┌─────────────────┐                   │
│  │ BudgetCheckHook │      │BudgetUpdateHook │                   │
│  └────────┬────────┘      └────────┬────────┘                   │
│           │                        │                             │
│           ▼                        ▼                             │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │              ICompressionService (新增)                     ││
│  │  • CompressAsync(session, agent)                           ││
│  │  • 选择策略、执行压缩、返回结果                              ││
│  └─────────────────────────────────────────────────────────────┘│
│                          │                                       │
│                          ▼                                       │
│  ┌─────────────────────────────────────────────────────────────┐│
│  │          ICompressionStrategyFactory                        ││
│  │  • GetStrategy(CompactionStrategyType) → ICompressionStrategy│
│  └─────────────────────────────────────────────────────────────┘│
│                          │                                       │
│           ┌──────────────┼──────────────┐                       │
│           ▼              ▼              ▼                       │
│  ┌───────────────┐ ┌───────────────┐ ┌───────────────┐          │
│  │SlidingWindow  │ │ Summarizing   │ │ Hybrid        │          │
│  │ TokenStrategy │ │ Strategy      │ │ Strategy      │          │
│  └───────────────┘ └───────────────┘ └───────────────┘          │
└─────────────────────────────────────────────────────────────────┘
```

## 错误处理

### 错误场景处理

| 场景 | 处理方式 | 影响 |
|------|----------|------|
| **压缩后仍超限** | 发送 `BudgetWarningEvent`，设置 `PendingCompaction=true`，下次请求前再次尝试 | 不中断对话 |
| **LLM 摘要失败** | 回退到 `SlidingWindow` 策略 | 降级处理 |
| **所有策略失败** | 发送 `ErrorEvent`，提示用户手动清理对话 | 中断当前请求 |
| **预算配置无效** | 使用默认配置，记录警告日志 | 不影响功能 |
| **Hook 执行超时** | 记录日志，跳过预算检查继续执行 | 保证可用性 |

### 压缩结果处理流程

```
CompressByTokenBudget 返回结果
        │
        ▼
┌───────────────────┐
│ Success == true?  │
│   且 TokensAfter  │
│   < 目标阈值?     │
└─────────┬─────────┘
          │
    ┌─────┴─────┐
    ▼           ▼
   是          否
    │           │
    ▼           ▼
 清除标记    尝试下一策略
            (Hybrid 场景)
                │
                ▼
          所有策略失败?
                │
          ┌─────┴─────┐
          ▼           ▼
         是          否
          │           │
          ▼           ▼
    发送错误事件   清除标记
```

## 测试策略

### 单元测试

| 组件 | 测试重点 |
|------|----------|
| `SummarizingStrategy` | Mock LLM 返回摘要，验证消息列表正确构建 |
| `HybridStrategy` | SlidingWindow 失败时调用 Summarizing，两者都失败返回错误 |
| `BudgetCheckHook` | `PendingCompaction=true` 时触发压缩 |
| `BudgetUpdateHook` | 计算状态、设置标记、发送事件 |
| `CompressionService` | 策略选择、配置解析、错误处理 |

### 集成测试

| 场景 | 验证点 |
|------|--------|
| 完整压缩流程 | 从超限 → 触发 Hook → 执行压缩 → 发送事件 |
| 多策略回退 | Hybrid 策略在 SlidingWindow 失败后调用 Summarizing |
| 事件流订阅 | 前端组件收到 `BudgetStatusEvent` 并正确渲染 |

### 性能测试

| 指标 | 目标 |
|------|------|
| 预算检查耗时 | < 50ms（无压缩时） |
| SlidingWindow 压缩 | < 100ms（1000 条消息） |
| Summarizing 压缩 | 取决于 LLM 响应时间 |

## 向后兼容性

| 现有功能 | 影响 |
|----------|------|
| `AgentExecutor` | 无破坏性变更，仅通过 Hook 扩展 |
| `SessionData` | 已有 `BudgetConfig` 和 `PendingCompaction` 属性 |
| `ICompressionStrategy` | 已有 `CompressByTokenBudget` 方法 |
| `ChatTokenStatusBar` | 扩展而非重写，保持现有参数兼容 |
| 配置文件 | 新增可选配置项，无必填项 |

## 文件结构

```
src/
├── Seeing.TokenBudget/
│   ├── Services/
│   │   ├── ICompressionService.cs          # 新增：压缩服务接口
│   │   ├── CompressionService.cs           # 新增：压缩服务实现
│   │   └── ICompressionStrategyFactory.cs  # 新增：策略工厂接口
│   ├── Hooks/
│   │   ├── BudgetCheckHook.cs              # 新增
│   │   └── BudgetUpdateHook.cs             # 新增
│   └── Extensions/
│       └── TokenBudgetServiceExtensions.cs # 扩展
│
├── Seeing.Session/Compression/Strategies/
│   ├── SummarizingStrategy.cs              # 新增
│   └── HybridStrategy.cs                   # 新增
│
├── Seeing.Agent/Core/Events/
│   ├── BudgetStatusEvent.cs                # 新增
│   ├── CompactionEvent.cs                  # 新增
│   └── BudgetWarningEvent.cs               # 新增
│
├── Seeing.Agent/Configuration/
│   └── SeeingAgentOptions.cs               # 扩展：添加 TokenBudgetOptions
│
└── samples/Seeing.Agent.WebUI/Components/Token/
    └── ChatTokenStatusBar.razor            # 扩展：订阅事件流
```

## 开关控制

用户可选择禁用自动压缩：

```json
// .seeing/seeing.json
{
  "SeeingAgent": {
    "TokenBudget": {
      "AutoCompactionEnabled": false
    }
  }
}
```

或按 Agent 级别禁用：
```csharp
// Agent 定义
agent.BudgetConfig = new TokenBudgetConfig 
{ 
    AutoCompactionEnabled = false 
};
```

## 里程碑

1. **M1: 压缩策略实现** - SummarizingStrategy + HybridStrategy
2. **M2: Hook 集成** - BudgetCheckHook + BudgetUpdateHook
3. **M3: 事件类型** - BudgetStatusEvent + CompactionEvent + BudgetWarningEvent
4. **M4: 配置集成** - TokenBudgetOptions + UnifiedConfigManager 注册
5. **M5: UI 扩展** - ChatTokenStatusBar 事件订阅
6. **M6: 集成测试** - 端到端流程验证