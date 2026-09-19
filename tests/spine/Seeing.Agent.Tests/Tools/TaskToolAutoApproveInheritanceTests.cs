using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Scheduling;
using Seeing.Agent.Hosting.Tools;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// TaskTool 审批策略继承：子会话不冻结父会话三态到执行级 Override，
/// 由 EffectivePermissionPolicy 沿父链实时解析（父会话切换即时作用于子代理）。
/// </summary>
public class TaskToolAutoApproveInheritanceTests
{
    [Theory]
    [InlineData(SessionAutoApprove.Disabled)]
    [InlineData(SessionAutoApprove.Enabled)]
    [InlineData(SessionAutoApprove.FollowGlobal)]
    public async Task ExecuteAsync_NewChild_ShouldNotFreezeParentAutoApprove(SessionAutoApprove parentAutoApprove)
    {
        var fixture = new Fixture(parentAutoApprove);

        var result = await fixture.Tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "find auth",
                subagent_type = "explore"
            }),
            new ToolContext { SessionId = fixture.ParentId, CallId = "call-new" });

        result.Success.Should().BeTrue();
        fixture.SubmittedOptions.Should().NotBeNull();
        fixture.SubmittedOptions!.AutoApprove.Should().Be(SessionAutoApprove.FollowGlobal);
    }

    [Theory]
    [InlineData(SessionAutoApprove.Disabled)]
    [InlineData(SessionAutoApprove.Enabled)]
    [InlineData(SessionAutoApprove.FollowGlobal)]
    public async Task ExecuteAsync_ResumeTask_ShouldNotFreezeParentAutoApprove(SessionAutoApprove parentAutoApprove)
    {
        var fixture = new Fixture(parentAutoApprove);

        var result = await fixture.Tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                description = "explore auth",
                prompt = "continue",
                subagent_type = "explore",
                task_id = fixture.Child.Id
            }),
            new ToolContext { SessionId = fixture.ParentId, CallId = "call-resume" });

        result.Success.Should().BeTrue();
        fixture.SubmittedOptions.Should().NotBeNull();
        fixture.SubmittedOptions!.AutoApprove.Should().Be(SessionAutoApprove.FollowGlobal);
    }

    private sealed class Fixture
    {
        public string ParentId { get; } = "parent-1";
        public SessionData Parent { get; }
        public SessionData Child { get; }
        public Mock<ISessionManager> SessionManager { get; } = new();
        public Mock<ISessionGroupManager> GroupManager { get; } = new();
        public Mock<IAgentRegistry> AgentRegistry { get; } = new();
        public Mock<IAgentLoopScheduler> LoopScheduler { get; } = new();
        public Mock<IExecutionStatusProvider> StatusProvider { get; } = new();
        public Mock<IExecutionSubmitter> Submitter { get; } = new();
        public Mock<IExecutionEventPublisher> EventPublisher { get; } = new();
        public ChatOptions? SubmittedOptions { get; private set; }
        public TaskTool Tool { get; }

        public Fixture(SessionAutoApprove parentAutoApprove)
        {
            Parent = new SessionData
            {
                Id = ParentId,
                Kind = SessionKind.Root,
                SelectedAgent = string.Empty,
                AutoApprove = parentAutoApprove,
                Metadata = new Dictionary<string, string>()
            };
            Child = new SessionData
            {
                Id = "child-1",
                Kind = SessionKind.SubAgent,
                SelectedAgent = "explore",
                SelectedModel = string.Empty,
                Metadata = new Dictionary<string, string>()
            };

            var agentDef = new AgentDefinition
            {
                Name = "explore",
                Mode = AgentMode.SubAgent,
                Runtime = AgentRuntime.Native,
                Description = "explore agent"
            };

            SessionManager.Setup(s => s.GetOrLoadAsync(ParentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Parent);
            SessionManager.Setup(s => s.Get(Child.Id)).Returns(Child);
            SessionManager.Setup(s => s.LoadAsync(It.IsAny<string>())).ReturnsAsync((SessionData?)null);
            SessionManager.Setup(s => s.AddMessageAsync(
                    It.IsAny<string>(),
                    It.IsAny<SessionMessage>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            SessionManager.Setup(s => s.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

            GroupManager.Setup(g => g.CreateChildAsync(
                    ParentId,
                    agentDef.Name,
                    It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<SessionPermissionRule>>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(Child);
            GroupManager.Setup(g => g.GetParentAsync(Child.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(ParentId);
            GroupManager.Setup(g => g.ListChildrenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SessionData> { Child });

            AgentRegistry.Setup(r => r.GetAgentAsync("explore")).ReturnsAsync(agentDef);

            LoopScheduler.Setup(l => l.IsLoopBusy(It.IsAny<string>())).Returns(false);

            StatusProvider.Setup(p => p.GetOverview(It.IsAny<string>()))
                .Returns(new SessionExecutionOverview());

            Submitter.Setup(s => s.SubmitAsync(
                    It.IsAny<string>(),
                    It.IsAny<ChatInput>(),
                    It.IsAny<ChatOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, ChatInput, ChatOptions?, CancellationToken>(
                    (_, _, options, _) => SubmittedOptions = options)
                .ReturnsAsync(ExecutionSubmitResult.Succeeded("exec-1"));
            Submitter.Setup(s => s.WaitForExecutionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Tool = new TaskTool(
                NullLogger<TaskTool>.Instance,
                SessionManager.Object,
                GroupManager.Object,
                AgentRegistry.Object,
                LoopScheduler.Object,
                Submitter.Object,
                StatusProvider.Object,
                EventPublisher.Object);
        }
    }
}
