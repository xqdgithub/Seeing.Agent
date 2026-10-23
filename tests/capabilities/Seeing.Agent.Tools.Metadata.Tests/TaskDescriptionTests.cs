using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Hosting.Tools;
using System.Text.RegularExpressions;
using Xunit;

namespace Seeing.Agent.Tools.Metadata.Tests;

/// <summary>
/// `task` 的描述在 warm 前后是两条路径：同步 getter 返回 fallback，
/// 预热后返回带可用子代理列表的动态文本。两者首句必须一致，否则模型看到的路由会漂移；
/// 动态文本还必须守住描述总长预算（子代理数量不可控）。
/// </summary>
public class TaskDescriptionTests
{
    private static TaskTool CreateTool(params AgentDefinition[] agents)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetTaskableAgentsAsync()).ReturnsAsync(agents);
        // 构造签名：(ILogger, ISessionManager, ISessionGroupManager, IAgentRegistry, ISessionRuntime, IExecutionEventPublisher)
        // 只有 registry 会被 WarmDescriptionAsync 调用，其余只在执行路径使用。
        return new TaskTool(null!, null!, null!, registry.Object, null!, null!);
    }

    [Fact]
    public async Task FallbackAndWarmed_ShouldShareFirstSentence()
    {
        var tool = CreateTool(
            new AgentDefinition { Name = "explore", Description = "探索" },
            new AgentDefinition { Name = "general", Description = "通用" });

        var fallbackFirst = tool.Description.Split('\n')[0].Trim();
        await tool.WarmDescriptionAsync(TestContext.Current.CancellationToken);
        var warmedFirst = tool.Description.Split('\n')[0].Trim();

        warmedFirst.Should().Be(fallbackFirst);
        warmedFirst.Length.Should().BeLessThanOrEqualTo(60);
        tool.Description.Should().Contain("explore").And.Contain("general");
    }

    [Fact]
    public async Task WarmedDescription_ShouldKeepDescriptionRules_ForLargeRegistry()
    {
        var agents = Enumerable.Range(0, 60)
            .Select(i => new AgentDefinition
            {
                Name = $"agent{i:00}",
                Description = new string('描', 80),
            })
            .ToArray();
        var tool = CreateTool(agents);

        await tool.WarmDescriptionAsync(TestContext.Current.CancellationToken);
        var text = tool.Description;

        text.Length.Should().BeLessThanOrEqualTo(700, "动态列表不能突破描述总长预算");
        Regex.IsMatch(text, "[\u4e00-\u9fff]").Should().BeTrue();
        text.Split('\n').Should().NotContain(l => l.TrimStart().StartsWith("## "));
        text.Split('\n')[0].Trim().Length.Should().BeLessThanOrEqualTo(60);
    }
}
