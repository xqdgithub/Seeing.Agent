using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

/// <summary>
/// 锁定 <see cref="AgentManager.GetTaskableAgentsAsync"/> 的过滤语义：
/// 委托列表只应包含「未禁用 + 非 Primary + Native 运行时 + 未隐藏」的 Agent。
/// </summary>
public class TaskableAgentsTests
{
    /// <summary>
    /// 隐藏（IsHidden=true）的 Agent 不得出现在 task 工具的可用子代理类型列表中；
    /// 隐藏的 SubAgent（如 reflection）与非隐藏的 All（如 build）作为对照组。
    /// </summary>
    [Theory]
    [InlineData("build", AgentMode.All, false)]          // 可见的 All → 应出现
    [InlineData("summary", AgentMode.All, true)]         // 隐藏的 All → 不应出现
    [InlineData("reflection", AgentMode.SubAgent, true)] // 隐藏的 SubAgent → 不应出现
    public async Task GetTaskableAgents_Should_Exclude_Hidden(
        string name, AgentMode mode, bool hidden)
    {
        // Arrange
        var manager = await BuildManagerWithAsync(new AgentDefinition
        {
            Name = name,
            Mode = mode,
            IsHidden = hidden,
            Runtime = AgentRuntime.Native,
            IsNative = true
        });

        // Act
        var result = await manager.GetTaskableAgentsAsync();

        // Assert
        result.Any(a => a.Name == name).Should().Be(!hidden);
    }

    /// <summary>
    /// 新增的隐藏过滤不应影响既有过滤条件：Disabled 与 Primary 仍被排除。
    /// </summary>
    [Fact]
    public async Task GetTaskableAgents_Should_Keep_Existing_Filters()
    {
        // Arrange
        var manager = await BuildManagerWithAsync(
            new AgentDefinition { Name = "visible-sub", Mode = AgentMode.SubAgent, IsNative = true },
            new AgentDefinition { Name = "disabled-sub", Mode = AgentMode.SubAgent, Disabled = true, IsNative = true },
            new AgentDefinition { Name = "primary-agent", Mode = AgentMode.Primary, IsNative = true });

        // Act
        var names = (await manager.GetTaskableAgentsAsync()).Select(a => a.Name).ToList();

        // Assert
        names.Should().Contain("visible-sub");
        names.Should().NotContain("disabled-sub");
        names.Should().NotContain("primary-agent");
    }

    /// <summary>
    /// 用真实 <see cref="AgentStore"/> 构造 <see cref="AgentManager"/>，其余依赖给 Mock，
    /// 以便锁定真实过滤行为而非桩语义。
    /// </summary>
    private static async Task<AgentManager> BuildManagerWithAsync(params AgentDefinition[] agents)
    {
        var store = new AgentStore(NullLogger<AgentStore>.Instance);
        foreach (var agent in agents)
        {
            await store.RegisterAsync(agent);
        }

        return new AgentManager(
            NullLogger<AgentManager>.Instance,
            store,
            new Mock<IAgentRuntimeManager>().Object,
            new WorkspaceProvider(Path.GetTempPath()));
    }
}
