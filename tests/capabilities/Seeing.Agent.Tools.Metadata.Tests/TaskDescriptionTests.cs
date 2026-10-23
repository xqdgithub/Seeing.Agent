using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Hosting.Tools;
using Xunit;

namespace Seeing.Agent.Tools.Metadata.Tests;

/// <summary>
/// `task` 的描述在 warm 前后是两条路径：同步 getter 返回 fallback，
/// 预热后返回带可用子代理列表的动态文本。两者首句必须一致，否则模型看到的路由会漂移。
/// </summary>
public class TaskDescriptionTests
{
    [Fact]
    public async Task FallbackAndWarmed_ShouldShareFirstSentence()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetTaskableAgentsAsync())
            .ReturnsAsync(new[]
            {
                new AgentDefinition { Name = "explore", Description = "探索" },
                new AgentDefinition { Name = "general", Description = "通用" },
            });

        // 构造签名：(ILogger, ISessionManager, ISessionGroupManager, IAgentRegistry, ISessionRuntime, IExecutionEventPublisher)
        // 只有 registry 会被 WarmDescriptionAsync 调用，其余只在执行路径使用。
        var tool = new TaskTool(null!, null!, null!, registry.Object, null!, null!);

        var fallbackFirst = tool.Description.Split('\n')[0].Trim();
        await tool.WarmDescriptionAsync(TestContext.Current.CancellationToken);
        var warmedFirst = tool.Description.Split('\n')[0].Trim();

        warmedFirst.Should().Be(fallbackFirst);
        warmedFirst.Length.Should().BeLessThanOrEqualTo(60);
        tool.Description.Should().Contain("explore").And.Contain("general");
    }
}
