using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Hooks;
using Seeing.Agent.Plugins.Agents;
using Xunit;

namespace Seeing.Agent.Plugins.Tests;

/// <summary>
/// MetisAgent 单元测试
/// </summary>
public class MetisAgentTests
{
    private readonly Mock<ILogger<MetisAgent>> _loggerMock;
    private readonly Mock<IHookManager> _hookManagerMock;

    public MetisAgentTests()
    {
        _loggerMock = new Mock<ILogger<MetisAgent>>();
        _hookManagerMock = new Mock<IHookManager>();
    }

    /// <summary>
    /// 测试：使用 Logger 创建实例
    /// </summary>
    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        // Arrange & Act
        var agent = new MetisAgent(_loggerMock.Object);

        // Assert
        agent.Should().NotBeNull();
        agent.Name.Should().Be("metis");
    }

    /// <summary>
    /// 测试：使用 HookManager 创建实例
    /// </summary>
    [Fact]
    public void Constructor_WithHookManager_CreatesInstance()
    {
        // Arrange & Act
        var agent = new MetisAgent(_loggerMock.Object, _hookManagerMock.Object);

        // Assert
        agent.Should().NotBeNull();
        agent.Name.Should().Be("metis");
    }

    /// <summary>
    /// 测试：Name 属性返回 "metis"
    /// </summary>
    [Fact]
    public void Name_ReturnsMetis()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.Name.Should().Be("metis");
    }

    /// <summary>
    /// 测试：Mode 属性返回 SubAgent
    /// </summary>
    [Fact]
    public void Mode_ReturnsSubAgent()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.Mode.Should().Be(AgentMode.SubAgent);
    }

    /// <summary>
    /// 测试：AllowedTools 包含只读工具
    /// </summary>
    [Fact]
    public void AllowedTools_ContainsReadOnlyTools()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.AllowedTools.Should().Contain("read");
        agent.AllowedTools.Should().Contain("grep");
        agent.AllowedTools.Should().Contain("glob");
        agent.AllowedTools.Should().Contain("lsp_*");
        agent.AllowedTools.Should().Contain("ast_grep_*");
    }

    /// <summary>
    /// 测试：DeniedTools 包含写入和执行工具
    /// </summary>
    [Fact]
    public void DeniedTools_ContainsWriteTools()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.DeniedTools.Should().Contain("write");
        agent.DeniedTools.Should().Contain("edit");
        agent.DeniedTools.Should().Contain("bash");
        agent.DeniedTools.Should().Contain("task");
        agent.DeniedTools.Should().Contain("apply_patch");
    }

    /// <summary>
    /// 测试：SystemPrompt 不为空
    /// </summary>
    [Fact]
    public void SystemPrompt_IsNotEmpty()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.SystemPrompt.Should().NotBeNullOrEmpty();
        agent.SystemPrompt!.Length.Should().BeGreaterThan(1000);
    }

    /// <summary>
    /// 测试：SystemPrompt 包含意图分类部分
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsIntentClassification()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.SystemPrompt.Should().Contain("意图分类");
        agent.SystemPrompt.Should().Contain("第 0 阶段");
        agent.SystemPrompt.Should().Contain("重构");
        agent.SystemPrompt.Should().Contain("从头构建");
        agent.SystemPrompt.Should().Contain("中等任务");
        agent.SystemPrompt.Should().Contain("协作");
        agent.SystemPrompt.Should().Contain("架构");
        agent.SystemPrompt.Should().Contain("研究");
    }

    /// <summary>
    /// 测试：SystemPrompt 包含 QA/验收标准指令
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsQADirectives()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.SystemPrompt.Should().Contain("QA/验收标准指令");
        agent.SystemPrompt.Should().Contain("零用户干预原则");
        agent.SystemPrompt.Should().Contain("可执行命令");
    }

    /// <summary>
    /// 测试：MaxSteps 返回 1
    /// </summary>
    [Fact]
    public void MaxSteps_ReturnsOne()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.MaxSteps.Should().Be(1);
    }

    /// <summary>
    /// 测试：Description 不为空并包含关键信息
    /// </summary>
    [Fact]
    public void Description_IsNotEmptyAndContainsKeywords()
    {
        // Arrange
        var agent = new MetisAgent(_loggerMock.Object);

        // Act & Assert
        agent.Description.Should().NotBeNullOrEmpty();
        agent.Description.Should().Contain("预规划");
        agent.Description.Should().Contain("Metis");
    }
}