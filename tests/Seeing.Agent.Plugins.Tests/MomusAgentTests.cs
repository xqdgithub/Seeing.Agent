using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Plugins.Agents;
using Xunit;

namespace Seeing.Agent.Plugins.Tests;

/// <summary>
/// MomusAgent 单元测试
/// </summary>
public class MomusAgentTests
{
    private readonly Mock<ILogger<MomusAgent>> _loggerMock;
    private readonly MomusAgent _agent;

    public MomusAgentTests()
    {
        _loggerMock = new Mock<ILogger<MomusAgent>>();
        _agent = new MomusAgent(_loggerMock.Object);
    }

    /// <summary>
    /// 测试：构造函数应正确创建实例
    /// </summary>
    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        // Arrange & Act
        var agent = new MomusAgent(_loggerMock.Object);

        // Assert
        agent.Should().NotBeNull();
        agent.Should().BeAssignableTo<MomusAgent>();
    }

    /// <summary>
    /// 测试：Name 属性应返回 "momus"
    /// </summary>
    [Fact]
    public void Name_ReturnsMomus()
    {
        // Assert
        _agent.Name.Should().Be("momus");
    }

    /// <summary>
    /// 测试：Mode 属性应返回 SubAgent
    /// </summary>
    [Fact]
    public void Mode_ReturnsSubAgent()
    {
        // Assert
        _agent.Mode.Should().Be(AgentMode.SubAgent);
    }

    /// <summary>
    /// 测试：Description 属性应返回正确的描述
    /// </summary>
    [Fact]
    public void Description_ReturnsCorrectDescription()
    {
        // Assert
        _agent.Description.Should().Be("计划审查者，用于评估工作计划的清晰度和可执行性");
    }

    /// <summary>
    /// 测试：MaxSteps 属性应返回 1（只读咨询）
    /// </summary>
    [Fact]
    public void MaxSteps_ReturnsOne()
    {
        // Assert
        _agent.MaxSteps.Should().Be(1);
    }

    /// <summary>
    /// 测试：AllowedTools 应包含只读工具
    /// </summary>
    [Fact]
    public void AllowedTools_ContainsReadOnlyTools()
    {
        // Arrange
        var allowedTools = _agent.AllowedTools;

        // Assert
        allowedTools.Should().Contain("read");
        allowedTools.Should().Contain("grep");
        allowedTools.Should().Contain("glob");
        allowedTools.Should().HaveCount(3);
    }

    /// <summary>
    /// 测试：DeniedTools 应包含写入和执行工具
    /// </summary>
    [Fact]
    public void DeniedTools_ContainsWriteTools()
    {
        // Arrange
        var deniedTools = _agent.DeniedTools;

        // Assert
        deniedTools.Should().Contain("write");
        deniedTools.Should().Contain("edit");
        deniedTools.Should().Contain("bash");
        deniedTools.Should().Contain("task");
        deniedTools.Should().HaveCount(4);
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含批准倾向（Approval Bias）
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsApprovalBias()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("批准倾向");
        systemPrompt.Should().Contain("有疑问时，批准");
        systemPrompt.Should().Contain("80% 清晰的计划就足够了");
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含决策框架
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsDecisionFramework()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("OKAY");
        systemPrompt.Should().Contain("REJECT");
        systemPrompt.Should().Contain("最多");
        systemPrompt.Should().Contain("阻塞问题");
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含检查项
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsCheckItems()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("参考验证");
        systemPrompt.Should().Contain("可执行性检查");
        systemPrompt.Should().Contain("QA 场景可执行性");
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含反模式说明
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsAntiPatterns()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("反模式");
        systemPrompt.Should().Contain("不是阻塞项");
        systemPrompt.Should().Contain("阻塞项发现者");
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含输出格式说明
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsOutputFormat()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("[OKAY]");
        systemPrompt.Should().Contain("[REJECT]");
        systemPrompt.Should().Contain("总结");
        systemPrompt.Should().Contain("阻塞问题");
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含输入验证规则
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsInputValidation()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("输入验证");
        systemPrompt.Should().Contain(".sisyphus/plans/*.md");
        systemPrompt.Should().Contain("有效输入");
        systemPrompt.Should().Contain("无效输入");
    }

    /// <summary>
    /// 测试：SystemPrompt 应包含最终提醒
    /// </summary>
    [Fact]
    public void SystemPrompt_ContainsFinalReminders()
    {
        // Arrange
        var systemPrompt = _agent.SystemPrompt;

        // Assert
        systemPrompt.Should().NotBeNull();
        systemPrompt.Should().Contain("批准倾向");
        systemPrompt.Should().Contain("最多");
        systemPrompt.Should().Contain("OKAY");
        systemPrompt.Should().Contain("REJECT");
    }
}