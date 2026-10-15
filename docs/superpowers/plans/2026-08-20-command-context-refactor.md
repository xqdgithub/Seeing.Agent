# 命令上下文重构实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 命令自包含 + 薄上下文重构：删除隐式 Input 变换契约与宿主同步逻辑，skill 命令直接操作会话。

**Architecture:** `CommandContext.Input` 改只读、删 `History` 字段；`ProcessCommandAsync` 删除同步逻辑；`SkillCommands`/`DynamicSkillCommand` 注入 `ISessionManager` 直接写 `session.Messages[^1]`（Role 校验），返回 `NeedsRefresh: true`。

**Tech Stack:** C# / .NET 10 / xUnit / FluentAssertions / Moq

**Spec:** `docs/superpowers/specs/2026-08-20-command-context-refactor-design.md`

---

### Task 1: 契约层变更（Abstractions）

**Files:**
- Modify: `src/Seeing.Agent.Abstractions/Commands/ICommand.cs:109-150`

- [ ] **Step 1: 修改 CommandContext**

```csharp
        /// <summary>当前输入（只读，命令不可修改；输入变换由命令自包含完成）</summary>
        public string Input { get; init; } = "";
```

删除 `History` 属性（含 `using Seeing.Agent.Abstractions.Llm;` 若不再被使用——保留 ChatMessage 引用检查：`CommandMetadata`/`CommandResult` 不使用 ChatMessage，删除 using）。

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/Seeing.Agent.Abstractions -c Debug`
Expected: 0 错误

### Task 2: 宿主层简化（ExecutionJobService）

**Files:**
- Modify: `src/Seeing.Agent.App/Execution/ExecutionJobService.cs:943-986`（ProcessCommandAsync 内）

- [ ] **Step 1: 删除同步逻辑**

将：

```csharp
        // 命令执行前定位本次 user 消息：提交时刚追加的最后一条（会话串行队列内无并发写入）。
        // 显式关联而非事后 LastOrDefault 猜测，避免 SkipUserMessagePersist（未落盘）或压缩后误改旧消息
        SessionMessage? commandUserMessage = null;
        if (session.Messages.Count > 0 && session.Messages[^1].Role == MessageRole.User)
        {
            commandUserMessage = session.Messages[^1];
        }

        var result = await command.ExecuteAsync(cmdContext, cancellationToken);

        // Input 改写契约：命令改写 Input = 本次输入以改写后内容为准（落盘 + 后续历史）。
        // 框架层仅负责把改写同步到本次 user 消息（skill 展开等场景生效的唯一通路）；
        // 本次输入未落盘（SkipUserMessagePersist）时无对应消息，不静默改写旧消息
        if (result.Success && cmdContext.Input != cmdContext.RawInput)
        {
            if (commandUserMessage != null)
            {
                commandUserMessage.Content = cmdContext.Input;
            }
            else
            {
                _logger.LogDebug("命令 {Command} 改写了 Input，但本次输入未落盘，跳过消息同步（仅影响命令上下文）", cmdName);
            }
        }
```

替换为：

```csharp
        var result = await command.ExecuteAsync(cmdContext, cancellationToken);
```

同时从 cmdContext 初始化中删除 `History = context.History,` 行。

- [ ] **Step 2: 构建验证**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 错误

### Task 3: Skill 命令自包含会话操作

**Files:**
- Modify: `src/Seeing.Agent.App/Commands/SkillCommands.cs`

- [ ] **Step 1: 注入 ISessionManager**

```csharp
using Seeing.Session.Core;

    private readonly SkillManager _skillManager;
    private readonly ISessionManager _sessionManager;

    public SkillCommands(SkillManager skillManager, ISessionManager sessionManager)
    {
        _skillManager = skillManager;
        _sessionManager = sessionManager;
    }
```

- [ ] **Step 2: LoadSkill 改为写会话**

将 `context.Input = SkillTemplateProcessor.Process(content, skillArgs, skillName);` 替换为：

```csharp
        // 处理模板并写入本次 user 消息（命令自包含：宿主 finally 统一保存）
        var expanded = SkillTemplateProcessor.Process(content, skillArgs, skillName);
        var session = await _sessionManager.GetOrLoadAsync(context.SessionId, ct);
        // 定位本次 user 消息：串行队列内提交时刚追加；Role 校验防 SkipUserMessagePersist 误改旧消息
        if (session.Messages.Count > 0 && session.Messages[^1].Role == MessageRole.User)
        {
            session.Messages[^1].Content = expanded;
        }

        return Task.FromResult(CommandResult.Ok($"Loaded skill: {skillName}", needsRefresh: true));
```

- [ ] **Step 3: DynamicSkillCommand 同样改造**

```csharp
    private readonly SkillManager _skillManager;
    private readonly ISessionManager _sessionManager;
    private readonly string _skillName;
    private readonly SkillInfo _skillInfo;

    public DynamicSkillCommand(SkillManager skillManager, ISessionManager sessionManager, SkillInfo skillInfo)
    {
        _skillManager = skillManager;
        _sessionManager = sessionManager;
        _skillName = skillInfo.Name;
        _skillInfo = skillInfo;

        Metadata = ...（不变）
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        var content = _skillInfo.Content;
        if (string.IsNullOrEmpty(content))
        {
            return CommandResult.Fail($"Skill content is empty: {_skillName}");
        }

        var expanded = SkillTemplateProcessor.Process(content, context.Arguments ?? "", _skillName);
        var session = await _sessionManager.GetOrLoadAsync(context.SessionId, cancellationToken);
        if (session.Messages.Count > 0 && session.Messages[^1].Role == MessageRole.User)
        {
            session.Messages[^1].Content = expanded;
        }

        return CommandResult.Ok($"Loaded skill: {_skillName}", needsRefresh: true);
    }
```

- [ ] **Step 4: 检查 DynamicSkillCommand 构造调用点**

Run: `rg -rn "new DynamicSkillCommand" src tests`
Expected: 找到构造调用点并补传 `sessionManager`（若从 DI 构造则检查注册处）

- [ ] **Step 5: 构建验证**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 错误

### Task 4: 测试

**Files:**
- Create: `tests/Seeing.Agent.Tests/Commands/SkillCommandsTests.cs`
- Modify: `tests/Seeing.Agent.Tests/Commands/CommandMarkdownTests.cs`（若引用 History 字段）

- [ ] **Step 1: 编写 SkillCommands 测试**

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.App.Commands;
using Seeing.Agent.Core.Skills;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

public class SkillCommandsTests
{
    private static Mock<ISessionManager> CreateSessionManager(SessionData session)
    {
        var mock = new Mock<ISessionManager>();
        mock.Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        return mock;
    }

    [Fact]
    public async Task LoadSkill_WhenExpanded_ShouldRewriteLastUserMessage()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.Messages.Add(SessionMessage.UserMessage("/skill demo"));
        var skill = new SkillInfo { Name = "demo", Content = "skill $1 content", Description = "d" };
        var skillManager = new Mock<SkillManager>();
        var sessionManager = CreateSessionManager(session);

        // Act
        var commands = new SkillCommands(skillManager.Object, sessionManager.Object);
        var context = new CommandContext { SessionId = "s1", Arguments = "arg" };
        var result = await commands.LoadSkill(context);

        // Assert
        result.Success.Should().BeTrue();
        result.NeedsRefresh.Should().BeTrue();
        session.Messages[^1].Content.Should().Contain("skill arg content");
    }

    [Fact]
    public async Task LoadSkill_WhenLastMessageNotUser_ShouldNotRewrite()
    {
        // Arrange
        var session = new SessionData { Id = "s1" };
        session.Messages.Add(SessionMessage.AssistantMessage("old"));
        var skill = new SkillInfo { Name = "demo", Content = "content", Description = "d" };
        var skillManager = new Mock<SkillManager>();
        var sessionManager = CreateSessionManager(session);

        // Act
        var commands = new SkillCommands(skillManager.Object, sessionManager.Object);
        var context = new CommandContext { SessionId = "s1", Arguments = "arg" };
        await commands.LoadSkill(context);

        // Assert
        session.Messages[^1].Content.Should().Be("old");
    }
}
```

（`SkillManager` 为具体类：检查其构造函数是否可 mock——若不可 mock 改用最小真实实例或注入空集合；`SessionMessage.UserMessage/AssistantMessage` 静态工厂确认存在）

- [ ] **Step 2: 运行测试**

Run: `dotnet test tests/Seeing.Agent.Tests --filter "FullyQualifiedName~SkillCommandsTests"`
Expected: 2 通过（若有构造困难，按实际调整）

- [ ] **Step 3: 检查 History 引用清理**

Run: `rg -rn "\.History" tests/Seeing.Agent.Tests src/Seeing.Agent.App -g "*.cs" | rg -v "ChatEventTracker|AppendExecutionHistory|GetHistory"`
Expected: 无 CommandContext.History 残留引用

- [ ] **Step 4: 全量回归**

Run: `dotnet test tests/Seeing.Agent.Tests` + `dotnet test tests/Seeing.Session.Tests`
Expected: 全绿（基线 671 + 81）

### Task 5: 收尾

- [ ] **Step 1: 汇总变更**

Run: `git status --short`
Expected: 列出修改文件（ICommand.cs / ExecutionJobService.cs / SkillCommands.cs / 新测试）

- [ ] **Step 2: 向用户确认后统一提交（用户要求最后统一审查，不在实施中逐次提交）**