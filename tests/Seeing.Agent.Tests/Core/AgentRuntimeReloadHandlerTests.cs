using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Core;

public class AgentRuntimeReloadHandlerTests
{
    [Fact]
    public async Task AgentModels变更_通过Handler重新应用模型()
    {
        var (handler, agent) = CreateHandlerWithAgent();

        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "AgentModels" } });

        agent.Model.Should().NotBeNull();
        agent.Model!.ModelId.Should().Be("modelA");
    }

    [Fact]
    public async Task 全量重载_重新应用AgentModels()
    {
        var (handler, agent) = CreateHandlerWithAgent();

        await handler.ReloadAsync(new ConfigChange { ChangedSections = Array.Empty<string>() });

        agent.Model.Should().NotBeNull();
        agent.Model!.ModelId.Should().Be("modelA");
    }

    [Fact]
    public async Task 不相关配置节变更_不重新应用模型()
    {
        var (handler, agent) = CreateHandlerWithAgent();

        await handler.ReloadAsync(new ConfigChange { ChangedSections = new[] { "Skills" } });

        agent.Model.Should().BeNull();
    }

    private static (AgentRuntimeReloadHandler Handler, AgentDefinition Agent) CreateHandlerWithAgent()
    {
        var agent = new AgentDefinition { Name = "agent1" };
        var agents = new List<AgentDefinition> { agent };

        var agentStore = new Mock<IAgentStore>();
        agentStore.Setup(s => s.GetAllAsync()).ReturnsAsync(agents);

        var options = new SeeingAgentOptions
        {
            AgentModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["agent1"] = "modelA"
            }
        };
        var optionsMonitor = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(options);

        var manager = new AgentRuntimeManager(
            NullLogger<AgentRuntimeManager>.Instance,
            agentStore.Object,
            optionsMonitor.Object,
            new Mock<IConfigSectionStore>().Object,
            new Mock<IModelManager>().Object,
            new Mock<IProviderRegistry>().Object);

        return (new AgentRuntimeReloadHandler(manager), agent);
    }
}
