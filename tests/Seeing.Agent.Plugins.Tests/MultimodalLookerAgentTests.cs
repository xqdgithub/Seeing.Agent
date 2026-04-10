using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Plugins.Agents;
using Xunit;

namespace Seeing.Agent.Plugins.Tests;

/// <summary>
/// Multimodal-Looker Agent 单元测试
/// </summary>
public class MultimodalLookerAgentTests
{
    private readonly Mock<ILogger<MultimodalLookerAgent>> _loggerMock;
    private readonly MultimodalLookerAgent _agent;

    public MultimodalLookerAgentTests()
    {
        _loggerMock = new Mock<ILogger<MultimodalLookerAgent>>();
        _agent = new MultimodalLookerAgent(_loggerMock.Object);
    }

    [Fact]
    public void Constructor_WithLogger_CreatesInstance()
    {
        // 验证构造函数能正常创建实例
        var agent = new MultimodalLookerAgent(_loggerMock.Object);
        agent.Should().NotBeNull();
        agent.Should().BeOfType<MultimodalLookerAgent>();
    }

    [Fact]
    public void Name_ReturnsMultimodalLooker()
    {
        // 验证 Agent 名称正确
        _agent.Name.Should().Be("multimodal-looker");
    }

    [Fact]
    public void Mode_ReturnsSubAgent()
    {
        // 验证 Agent 模式为子代理
        _agent.Mode.Should().Be(AgentMode.SubAgent);
    }

    [Fact]
    public void AllowedTools_ContainsRead()
    {
        // 验证允许的工具列表包含 read
        _agent.AllowedTools.Should().Contain("read");
        _agent.AllowedTools.Should().HaveCount(1);
    }

    [Fact]
    public void DeniedTools_ContainsWriteTools()
    {
        // 验证禁止的工具列表包含写入和执行类工具
        _agent.DeniedTools.Should().Contain("write");
        _agent.DeniedTools.Should().Contain("edit");
        _agent.DeniedTools.Should().Contain("bash");
        _agent.DeniedTools.Should().Contain("task");
        _agent.DeniedTools.Should().HaveCount(4);
    }

    [Fact]
    public void SystemPrompt_ContainsUseWhen()
    {
        // 验证系统提示词包含使用场景说明
        var prompt = _agent.SystemPrompt;
        prompt.Should().NotBeNullOrEmpty();
        
        // 检查关键的使用场景内容
        prompt.Should().Contain("何时使用你");
        prompt.Should().Contain("媒体文件");
        prompt.Should().Contain("提取特定信息");
        prompt.Should().Contain("描述图像");
        prompt.Should().Contain("视觉内容");
    }

    [Fact]
    public void SystemPrompt_ContainsWorkMode()
    {
        // 验证系统提示词包含工作模式说明
        var prompt = _agent.SystemPrompt;
        prompt.Should().NotBeNullOrEmpty();
        
        // 检查关键的工作模式内容
        prompt.Should().Contain("你如何工作");
        prompt.Should().Contain("文件路径");
        prompt.Should().Contain("goal");
        prompt.Should().Contain("提取信息");
        prompt.Should().Contain("节省上下文");
    }

    [Fact]
    public void SystemPrompt_ContainsResponseRules()
    {
        // 验证系统提示词包含响应规则
        var prompt = _agent.SystemPrompt;
        prompt.Should().NotBeNullOrEmpty();
        
        // 检查响应规则内容
        prompt.Should().Contain("响应规则");
        prompt.Should().Contain("直接返回");
        prompt.Should().Contain("无开场白");
        prompt.Should().Contain("匹配请求的语言");
    }

    [Fact]
    public void MaxSteps_ReturnsOne()
    {
        // 验证最大迭代步骤为 1（单次分析）
        _agent.MaxSteps.Should().Be(1);
    }

    [Fact]
    public void Description_ContainsMultimodalLooker()
    {
        // 验证描述包含关键信息
        _agent.Description.Should().Contain("媒体文件");
        _agent.Description.Should().Contain("PDF");
        _agent.Description.Should().Contain("图像");
        _agent.Description.Should().Contain("Multimodal-Looker");
    }

    [Fact]
    public void SystemPrompt_ContainsNotUseWhen()
    {
        // 验证系统提示词包含不应使用的场景说明
        var prompt = _agent.SystemPrompt;
        prompt.Should().NotBeNullOrEmpty();
        
        // 检查不应使用的场景内容
        prompt.Should().Contain("何时不应使用你");
        prompt.Should().Contain("源代码");
        prompt.Should().Contain("纯文本");
        prompt.Should().Contain("后续需要编辑");
    }
}