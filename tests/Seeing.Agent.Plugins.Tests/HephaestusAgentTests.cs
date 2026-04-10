using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Hooks;
using Seeing.Agent.Plugins.Agents;
using Xunit;

namespace Seeing.Agent.Plugins.Tests;

/// <summary>
/// HephaestusAgent 单元测试
/// </summary>
public class HephaestusAgentTests
{
    private readonly Mock<ILogger<HephaestusAgent>> _loggerMock;
    private readonly Mock<IHookManager> _hookManagerMock;

    public HephaestusAgentTests()
    {
        _loggerMock = new Mock<ILogger<HephaestusAgent>>();
        _hookManagerMock = new Mock<IHookManager>();
    }

    /// <summary>
    /// 测试：使用 Logger 创建实例
    /// </summary>
    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        // Act
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Assert
        agent.Should().NotBeNull();
        agent.Should().BeAssignableTo<HephaestusAgent>();
    }

    /// <summary>
    /// 测试：使用 Logger 和 HookManager 创建实例
    /// </summary>
    [Fact]
    public void Constructor_WithLoggerAndHookManager_CreatesInstance()
    {
        // Act
        var agent = new HephaestusAgent(_loggerMock.Object, _hookManagerMock.Object);

        // Assert
        agent.Should().NotBeNull();
        agent.Should().BeAssignableTo<HephaestusAgent>();
    }

    /// <summary>
    /// 测试：Name 属性返回 "hephaestus"
    /// </summary>
    [Fact]
    public void Name_ReturnsHephaestus()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var name = agent.Name;

        // Assert
        name.Should().Be("hephaestus");
    }

    /// <summary>
    /// 测试：Mode 属性返回 SubAgent
    /// </summary>
    [Fact]
    public void Mode_ReturnsSubAgent()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var mode = agent.Mode;

        // Assert
        mode.Should().Be(AgentMode.SubAgent);
    }

    /// <summary>
    /// 测试：MaxSteps 属性返回 50
    /// </summary>
    [Fact]
    public void MaxSteps_Returns50()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var maxSteps = agent.MaxSteps;

        // Assert
        maxSteps.Should().Be(50);
    }

    /// <summary>
    /// 测试：DeniedTools 属性为空（无限制）
    /// </summary>
    [Fact]
    public void DeniedTools_IsEmpty()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var deniedTools = agent.DeniedTools;

        // Assert
        deniedTools.Should().BeEmpty();
    }

    /// <summary>
    /// 测试：AllowedTools 属性为空（允许所有工具）
    /// </summary>
    [Fact]
    public void AllowedTools_IsEmpty()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var allowedTools = agent.AllowedTools;

        // Assert
        allowedTools.Should().BeEmpty();
    }

    /// <summary>
    /// 测试：Description 属性包含预期内容
    /// </summary>
    [Fact]
    public void Description_ContainsExpectedContent()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var description = agent.Description;

        // Assert
        description.Should().Contain("自主");
        description.Should().Contain("深度");
        description.Should().Contain("工作者");
    }

    /// <summary>
    /// 测试：SystemPrompt 包含身份标识
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsIdentity()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var systemPrompt = agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("资深工程师");
        systemPrompt.Should().Contain("身份");
    }

    /// <summary>
    /// 测试：SystemPrompt 包含执行循环
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsExecutionLoop()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var systemPrompt = agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("执行循环");
        systemPrompt.Should().Contain("探索");
        systemPrompt.Should().Contain("规划");
        systemPrompt.Should().Contain("决策");
        systemPrompt.Should().Contain("执行");
        systemPrompt.Should().Contain("验证");
    }

    /// <summary>
    /// 测试：SystemPrompt 包含禁止提问内容
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsForbiddenQuestions()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var systemPrompt = agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("不要问");
        systemPrompt.Should().Contain("直接做");
    }

    /// <summary>
    /// 测试：SystemPrompt 包含探索层次
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsExplorationHierarchy()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var systemPrompt = agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("探索层次");
        systemPrompt.Should().Contain("最后手段");
    }

    /// <summary>
    /// 测试：SystemPrompt 包含 Todo 纪律
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsTodoDiscipline()
    {
        // Arrange
        var agent = new HephaestusAgent(_loggerMock.Object);

        // Act
        var systemPrompt = agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("Todo");
        systemPrompt.Should().Contain("不可协商");
        systemPrompt.Should().Contain("多步骤");
    }
}