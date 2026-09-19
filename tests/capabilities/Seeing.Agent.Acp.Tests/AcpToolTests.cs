using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Acp.Configuration;
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

    [Fact]
    public async Task ExecuteAsync_SyncRun_ShouldPassthroughTypedMetadata()
    {
        AcpRunRequest? captured = null;
        var runner = new Mock<IAcpSessionRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()))
            .Callback<AcpRunRequest, IAcpUpdateSink, CancellationToken>((req, _, _) => captured = req)
            .ReturnsAsync(new AcpRunResult { Text = "done", Success = true });

        var tool = CreateTool(runner.Object);

        var args = JsonSerializer.SerializeToElement(new
        {
            description = "meta task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext { SessionId = "parent-sess" });

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.ParentContext.Should().NotBeNull();
        captured.ParentContext!.Metadata[AgentContextKeys.ParentSessionIdKey].Should().Be("parent-sess");
        captured.ParentContext.Metadata[AgentContextKeys.TaskDescriptionKey].Should().Be("meta task");
        captured.ParentContext.Metadata[AgentContextKeys.AcpBackendKey].Should().Be("opencode");
    }

    [Fact]
    public async Task ExecuteAsync_WithAuthorizerFactory_ShouldPassAcpAgentName()
    {
        var runner = new Mock<IAcpSessionRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcpRunResult { Text = "done", Success = true });

        var factory = new CapturingPermissionAuthorizerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionAuthorizerFactory>(factory);
        using var sp = services.BuildServiceProvider();

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp
        });

        result.Success.Should().BeTrue();
        factory.CapturedSessionId.Should().Be("parent-sess");
        factory.LastRequest.Should().NotBeNull();
        factory.LastRequest!.AgentName.Should().Be("acp-opencode");
        factory.LastRequest.Resource.Should().Be("acp");
        factory.LastRequest.PermissionKind.Should().Be("tool.execute");
        factory.LastRequest.SessionId.Should().Be("parent-sess");
        factory.LastRequest.CallId.Should().Be("call-1");
        factory.LastRequest.RequireInteraction.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_DeniedPermission_ShouldFailWithReason()
    {
        var runner = new Mock<IAcpSessionRunner>();
        var factory = new CapturingPermissionAuthorizerFactory
        {
            Decision = PermissionEffect.Deny,
            Reason = "权限被拒绝"
        };
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionAuthorizerFactory>(factory);
        using var sp = services.BuildServiceProvider();

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("权限被拒绝");
        runner.Verify(
            r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutAuthorizerFactory_ShouldFallbackAndRun()
    {
        var runner = new Mock<IAcpSessionRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcpRunResult { Text = "done", Success = true });

        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp
        });

        result.Success.Should().BeTrue();
        runner.Verify(
            r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ContextAuthorizer_ShouldBePreferredOverFactory()
    {
        var runner = new Mock<IAcpSessionRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcpRunResult { Text = "done", Success = true });

        var factory = new CapturingPermissionAuthorizerFactory { Decision = PermissionEffect.Allow };
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionAuthorizerFactory>(factory);
        using var sp = services.BuildServiceProvider();

        var contextAuthorizer = new CapturingPermissionAuthorizer(PermissionEffect.Allow);

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp,
            PermissionAuthorizer = contextAuthorizer
        });

        result.Success.Should().BeTrue();
        contextAuthorizer.LastRequest.Should().NotBeNull();
        contextAuthorizer.LastRequest!.Resource.Should().Be("acp");
        factory.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ContextAuthorizerDeny_ShouldFailWithoutCallingFactory()
    {
        var runner = new Mock<IAcpSessionRunner>();
        var factory = new CapturingPermissionAuthorizerFactory { Decision = PermissionEffect.Allow };
        var services = new ServiceCollection();
        services.AddSingleton<IPermissionAuthorizerFactory>(factory);
        using var sp = services.BuildServiceProvider();

        var contextAuthorizer = new CapturingPermissionAuthorizer(PermissionEffect.Deny, "上下文拒绝");

        var tool = CreateTool(runner.Object, new FakeSessionManager());
        var args = JsonSerializer.SerializeToElement(new
        {
            description = "perm task",
            prompt = "hello",
            backend = "opencode"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext
        {
            SessionId = "parent-sess",
            CallId = "call-1",
            Services = sp,
            PermissionAuthorizer = contextAuthorizer
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("上下文拒绝");
        factory.LastRequest.Should().BeNull();
        runner.Verify(
            r => r.RunAsync(It.IsAny<AcpRunRequest>(), It.IsAny<IAcpUpdateSink>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AcpTool CreateTool(IAcpSessionRunner? runner, FakeSessionManager? sessionManager = null)
    {
        runner ??= Mock.Of<IAcpSessionRunner>();
        sessionManager ??= new FakeSessionManager();

        var registry = CreateBackendRegistry(new AcpOptions
        {
            Enabled = true,
            Backends = new Dictionary<string, AcpBackendConfig>
            {
                ["opencode"] = new() { Command = "opencode" }
            }
        });
        
        var world = Mock.Of<IExecutionWorld>(w => w.Cwd == ".");

        return new AcpTool(
            NullLogger<AcpTool>.Instance,
            runner,
            registry,
            new ContentBlockMapper(),
            Mock.Of<IOptionsMonitor<AcpOptions>>(m => m.CurrentValue == new AcpOptions { Enabled = true }),
            sessionManager ?? new FakeSessionManager(),
            world);
    }

    private static AcpBackendRegistry CreateBackendRegistry(AcpOptions acp)
    {
        var options = Mock.Of<IOptionsMonitor<AcpOptions>>(m => m.CurrentValue == acp);
        return new AcpBackendRegistry(options, NullLogger<AcpBackendRegistry>.Instance);
    }
}

public sealed class FakeSessionManager : ISessionManager
{
    private readonly Dictionary<string, SessionData> _sessions = new();

    public SessionData Create(string? partitionId = null, string? selectedAgent = null, string? scenario = null)
    {
        var session = SessionData.Create(partitionId, selectedAgent, scenario);
        _sessions[session.Id] = session;
        return session;
    }

    public Task<SessionData> EnsureSessionAsync(
        string id,
        string? selectedAgent = null,
        string? partitionId = null,
        string? scenario = null) =>
        Task.FromResult(_sessions.TryGetValue(id, out var session)
            ? session
            : Create(partitionId, selectedAgent, scenario));

    public SessionData? Get(string id) =>
        _sessions.TryGetValue(id, out var session) ? session : null;

    public bool Delete(string id) => _sessions.Remove(id);

    public void Register(SessionData session) => _sessions[session.Id] = session;

    public IReadOnlyList<SessionData> List() => _sessions.Values.ToList();

    public Task SaveAsync(string id) => Task.CompletedTask;

    public Task FlushAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<SessionData?> LoadAsync(string id) =>
        Task.FromResult(Get(id));

    public Task AddMessageAsync(string sessionId, SessionMessage message, CancellationToken ct = default) =>
        Task.CompletedTask;

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

    public Task SetAutoApproveAsync(string sessionId, SessionAutoApprove value, CancellationToken ct = default)
    {
        var session = Get(sessionId);
        if (session != null)
            session.AutoApprove = value;
        return Task.CompletedTask;
    }

    public Task SetModelAsync(string sessionId, string modelId, CancellationToken ct = default)
    {
        var session = Get(sessionId);
        if (session != null)
            session.SelectedModel = modelId;
        return Task.CompletedTask;
    }

    public Task SetThinkingEffortAsync(string sessionId, string? thinkingEffort, CancellationToken ct = default)
    {
        var session = Get(sessionId);
        if (session != null)
            session.SelectedThinkingEffort = thinkingEffort?.Trim() ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<SessionData> GetOrLoadAsync(string sessionId, CancellationToken ct = default)
    {
        var session = Get(sessionId);
        if (session != null) return Task.FromResult(session);
        throw new InvalidOperationException($"Session not found: {sessionId}");
    }

    public Task<SessionData> UpdateSessionAsync(string sessionId, Action<SessionData> updateAction, CancellationToken ct = default)
    {
        var session = Get(sessionId);
        if (session == null) throw new InvalidOperationException($"Session not found: {sessionId}");
        updateAction(session);
        return Task.FromResult(session);
    }

    public Task SaveAndNotifyAsync(string sessionId, bool persist = true, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<SessionData>> LoadAllFromStorageAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SessionData>>(List());
}
