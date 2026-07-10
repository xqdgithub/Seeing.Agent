using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Xunit;
using AcpRunResult = Seeing.Agent.Acp.Execution.AcpRunResult;

namespace Seeing.Agent.Acp.Tests;

public class AcpToolTests
{
    [Fact]
    public async Task ExecuteAsync_MissingBackend_ShouldFail()
    {
        var tool = CreateTool(runner: null);
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "test task",
            prompt = "do work",
            backend = "missing"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext { SessionId = "sess-1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("missing");
    }

    [Fact]
    public async Task ExecuteAsync_SyncRun_ShouldReturnTaskResultFormat()
    {
        var runner = new Mock<IAcpSessionRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcpRunResult { Text = "done", Success = true });

        var sessionManager = new FakeSessionManager();
        var tool = CreateTool(runner.Object, sessionManager);

        var args = JsonSerializer.SerializeToElement(new
        {
            description = "sync task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext { SessionId = "parent-sess" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("<task_result>");
        result.Output.Should().Contain("done");
        result.Output.Should().Contain("task_id:");
    }

    private static AcpTool CreateTool(IAcpSessionRunner? runner, FakeSessionManager? sessionManager = null)
    {
        runner ??= Mock.Of<IAcpSessionRunner>();
        sessionManager ??= new FakeSessionManager();

        var registry = CreateBackendRegistry(new SeeingAgentOptions
        {
            Acp = new AcpOptions
            {
                Enabled = true,
                Backends = new Dictionary<string, AcpBackendConfig>
                {
                    ["opencode"] = new() { Command = "opencode" }
                }
            }
        });
        
        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(".");
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(Path.GetTempPath());
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(Path.GetTempPath());

        return new AcpTool(
            NullLogger<AcpTool>.Instance,
            runner,
            registry,
            new ContentBlockMapper(),
            Options.Create(new SeeingAgentOptions { Acp = new AcpOptions { Enabled = true } }),
            sessionManager ?? new FakeSessionManager(),
            workspaceMock.Object);
    }

    private static AcpBackendRegistry CreateBackendRegistry(SeeingAgentOptions options)
    {
        var workspaceMock = new Mock<IWorkspaceProvider>();
        workspaceMock.Setup(w => w.WorkspaceRoot).Returns(".");
        workspaceMock.Setup(w => w.UserSeeingDirectory).Returns(Path.GetTempPath());
        workspaceMock.Setup(w => w.ProjectSeeingDirectory).Returns(Path.GetTempPath());

        var configManager = new UnifiedConfigManager(
            workspaceMock.Object,
            NullLogger<UnifiedConfigManager>.Instance);

        // Set the Acp options
        configManager.GetSeeingAgentOptions().Acp = options.Acp;

        return new AcpBackendRegistry(configManager, NullLogger<AcpBackendRegistry>.Instance);
    }
}

public sealed class FakeSessionManager : ISessionManager
{
    private readonly Dictionary<string, SessionData> _sessions = new();

    public SessionData Create(string? partitionId = null, string? selectedAgent = null)
    {
        var session = SessionData.Create(partitionId, selectedAgent);
        _sessions[session.Id] = session;
        return session;
    }

    public Task<SessionData> EnsureSessionAsync(string id, string? selectedAgent = null, string? partitionId = null) =>
        Task.FromResult(_sessions.TryGetValue(id, out var session) ? session : Create(partitionId, selectedAgent));

    public SessionData? Get(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public bool Delete(string id) => _sessions.Remove(id);

    public void Register(SessionData session) => _sessions[session.Id] = session;

    public IReadOnlyList<SessionData> List() => _sessions.Values.ToList();

    public Task SaveAsync(string id) => Task.CompletedTask;

    public Task<SessionData?> LoadAsync(string id) =>
        Task.FromResult(Get(id));

    public IReadOnlyList<SessionMessage> Compress(string id) => Array.Empty<SessionMessage>();

    public Task AddMessageAsync(string sessionId, SessionMessage message, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<SessionData> ForkAsync(string sessionId, string? atMessageId = null, string? label = null, CancellationToken ct = default) =>
        Task.FromResult(Create());

    public Task<bool> ArchiveAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<string> ShareAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult("share-id");

    public Task<bool> RevertAsync(string sessionId, string messageId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<SessionMetadata>> ListAllAsync(string? partitionId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SessionMetadata>>(Array.Empty<SessionMetadata>());

    public Task SetTitleAsync(string sessionId, string title, CancellationToken ct = default) =>
        Task.CompletedTask;
}
