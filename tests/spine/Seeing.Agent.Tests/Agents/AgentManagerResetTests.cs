using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Agents;

/// <summary>
/// 工作区切换后 Agent 的项目级 MD 覆盖必须从零重建：
/// 旧工作区纯 MD 创建的 agent 消失，用户级同名覆盖保留。
/// 判定依据只能是 <c>_configCache</c> 的 <c>Project:</c> 键——不允许新增记账字段。
/// </summary>
public class AgentManagerResetTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "agent-manager-reset-" + Guid.NewGuid().ToString("N"));

    public AgentManagerResetTests() => Directory.CreateDirectory(_tempRoot);

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

    /// <summary>在指定 .seeing/agents 目录下写一个 Agent MD 配置。</summary>
    private static void WriteAgentMd(string agentsRoot, string name, string description)
    {
        var dir = Path.Combine(agentsRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "AGENT.md"),
            $"---\nname: {name}\ndescription: {description}\nmode: Primary\n---\n# {description}\n");
    }

    private AgentManager CreateManager(
        AgentStore store,
        Mock<IWorkspaceProvider> workspace,
        Func<string> currentRoot)
    {
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(() => Path.Combine(currentRoot(), ".seeing"));
        workspace.Setup(w => w.UserSeeingDirectory).Returns(Path.Combine(_tempRoot, "user", ".seeing"));

        var runtime = new Mock<IAgentRuntimeManager>();
        runtime.Setup(r => r.InitializeAsync()).Returns(Task.CompletedTask);

        return new AgentManager(
            NullLogger<AgentManager>.Instance,
            store,
            runtime.Object,
            workspace.Object);
    }

    [Fact]
    public async Task 切换工作区_旧项目级纯MD_Agent消失_用户级覆盖保留()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspaceA = Path.Combine(_tempRoot, "ws-a");
        var workspaceB = Path.Combine(_tempRoot, "ws-b");
        var userAgentsRoot = Path.Combine(_tempRoot, "user", ".seeing", "agents");

        // 用户级：shared（同名用户覆盖）+ useronly（只在用户级存在）
        WriteAgentMd(userAgentsRoot, "shared", "用户级描述");
        WriteAgentMd(userAgentsRoot, "useronly", "仅用户级");
        // 工作区 A：alpha 是纯项目级 agent；shared 另有项目级覆盖
        WriteAgentMd(Path.Combine(workspaceA, ".seeing", "agents"), "alpha", "A的项目agent");
        WriteAgentMd(Path.Combine(workspaceA, ".seeing", "agents"), "shared", "A覆盖描述");
        // 工作区 B：没有任何项目级 agent 配置

        var store = new AgentStore(NullLogger<AgentStore>.Instance);
        var workspace = new Mock<IWorkspaceProvider>();
        var currentRoot = workspaceA;
        var manager = CreateManager(store, workspace, () => currentRoot);

        // 启动期应用 A 的覆盖 → _configCache 出现 Project: 键
        await manager.StartAsync(ct);
        (await store.GetAsync("alpha")).Should().NotBeNull("工作区 A 的项目级 agent 应当已生效");
        (await store.GetAsync("shared"))!.Description.Should().Be("A覆盖描述");

        // Act: 切到工作区 B
        currentRoot = workspaceB;
        await new AgentManagerReloadHandler(manager).ReloadAsync(
            new WorkspaceChange { OldWorkspace = workspaceA, NewWorkspace = workspaceB },
            ct);

        // Assert
        (await store.GetAsync("alpha")).Should().BeNull("旧工作区纯 MD 创建的 agent 必须消失");
        (await store.GetAsync("shared"))!.Description.Should().Be("用户级描述", "同名用户级覆盖不能被一起删掉");
        (await store.GetAsync("useronly"))!.Description.Should().Be("仅用户级");
    }
}
