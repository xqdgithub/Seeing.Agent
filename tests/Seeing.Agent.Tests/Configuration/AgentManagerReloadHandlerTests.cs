using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class AgentManagerReloadHandlerTests : IDisposable
{
    private readonly string _tempRoot;

    public AgentManagerReloadHandlerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "seeing-agent-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 清理失败忽略（文件锁等）
        }
    }

    /// <summary>在指定工作区写一个 Agent MD 配置文件</summary>
    private static string CreateAgentMd(string workspaceRoot, string agentName, string description)
    {
        var dir = Path.Combine(workspaceRoot, ".seeing", "agents", agentName);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "AGENT.md");
        var content = $"---\nname: {agentName}\ndescription: {description}\nmode: Primary\n---\n# {description}\n";
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public async Task 工作区切换_重新应用MD覆盖()
    {
        // Arrange: 构造 AgentManager + 两个工作区目录，各放不同 AGENT.md
        var workspaceA = Path.Combine(_tempRoot, "ws-a");
        var workspaceB = Path.Combine(_tempRoot, "ws-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        var userDir = Path.Combine(_tempRoot, "user");

        CreateAgentMd(workspaceA, "builtin", "来自工作区A");
        CreateAgentMd(workspaceB, "builtin", "来自工作区B");

        var store = new Mock<IAgentStore>();
        var runtime = new Mock<IAgentRuntimeManager>();
        var workspace = new Mock<IWorkspaceProvider>();

        var builtIn = new AgentDefinition { Name = "builtin", Description = "内置定义", Mode = AgentMode.Primary };
        store.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<AgentDefinition> { builtIn });
        store.Setup(x => x.GetAsync(It.IsAny<string>())).ReturnsAsync((AgentDefinition?)null);

        // 用可变的 currentRoot 模拟工作区切换（ProjectSeeingDirectory 随工作区变化）
        var currentRoot = workspaceA;
        workspace.Setup(x => x.ProjectSeeingDirectory).Returns(() => Path.Combine(currentRoot, ".seeing"));
        workspace.Setup(x => x.UserSeeingDirectory).Returns(userDir);

        var manager = new AgentManager(
            NullLogger<AgentManager>.Instance,
            store.Object,
            runtime.Object,
            workspace.Object,
            builtInAgents: new[] { builtIn });

        var handler = new AgentManagerReloadHandler(manager);

        // Act: 切换到新工作区后触发重载
        currentRoot = workspaceB;
        await handler.ReloadAsync(
            new WorkspaceChange { OldWorkspace = workspaceA, NewWorkspace = workspaceB },
            CancellationToken.None);

        // Assert: agentStore 中代理来自新工作区 MD
        store.Verify(x => x.RegisterAsync(It.Is<AgentDefinition>(a =>
            a.Name == "builtin" && a.Description == "来自工作区B")), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ChangeTypes_声明订阅工作区变更()
    {
        // Arrange
        var manager = CreateManager(Path.Combine(_tempRoot, "ws-a"));
        var handler = new AgentManagerReloadHandler(manager);

        // Assert
        handler.ChangeTypes.Should().Contain(typeof(WorkspaceChange));
        handler.ComponentId.Should().Be("agent-md");
    }

    [Fact]
    public async Task 非工作区信号_不触发重载()
    {
        // Arrange: 工作区有 Agent MD，但触发的是配置变更信号
        var workspaceA = Path.Combine(_tempRoot, "ws-a");
        Directory.CreateDirectory(workspaceA);
        CreateAgentMd(workspaceA, "builtin", "来自工作区A");

        var store = new Mock<IAgentStore>();
        var runtime = new Mock<IAgentRuntimeManager>();
        var workspace = new Mock<IWorkspaceProvider>();

        var builtIn = new AgentDefinition { Name = "builtin", Description = "内置定义", Mode = AgentMode.Primary };
        store.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<AgentDefinition> { builtIn });
        store.Setup(x => x.GetAsync(It.IsAny<string>())).ReturnsAsync((AgentDefinition?)null);
        workspace.Setup(x => x.ProjectSeeingDirectory).Returns(() => Path.Combine(workspaceA, ".seeing"));
        workspace.Setup(x => x.UserSeeingDirectory).Returns(Path.Combine(_tempRoot, "user"));

        var manager = new AgentManager(
            NullLogger<AgentManager>.Instance,
            store.Object,
            runtime.Object,
            workspace.Object,
            builtInAgents: new[] { builtIn });

        var handler = new AgentManagerReloadHandler(manager);

        // Act: 触发配置变更信号（类型不匹配，应被忽略）
        await handler.ReloadAsync(
            new ConfigChange { ChangedSections = new[] { "Agents" } },
            CancellationToken.None);

        // Assert: 未重新应用 MD 覆盖（构造时的内置注册除外）
        store.Verify(x => x.RegisterAsync(It.Is<AgentDefinition>(a => a.Description == "来自工作区A")), Times.Never);
    }

    private AgentManager CreateManager(string workspaceRoot)
    {
        Directory.CreateDirectory(workspaceRoot);

        var store = new Mock<IAgentStore>();
        var runtime = new Mock<IAgentRuntimeManager>();
        var workspace = new Mock<IWorkspaceProvider>();

        var builtIn = new AgentDefinition { Name = "builtin", Description = "内置定义", Mode = AgentMode.Primary };
        store.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<AgentDefinition> { builtIn });
        store.Setup(x => x.GetAsync(It.IsAny<string>())).ReturnsAsync((AgentDefinition?)null);
        workspace.Setup(x => x.ProjectSeeingDirectory).Returns(() => Path.Combine(workspaceRoot, ".seeing"));
        workspace.Setup(x => x.UserSeeingDirectory).Returns(Path.Combine(_tempRoot, "user"));

        return new AgentManager(
            NullLogger<AgentManager>.Instance,
            store.Object,
            runtime.Object,
            workspace.Object,
            builtInAgents: new[] { builtIn });
    }
}
