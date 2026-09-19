using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;

namespace Seeing.Agent.Core.Tools.Session.Tests;

/// <summary>
/// 会话工具测试夹具：真实 SessionManager + SessionGroupManager + 内存/文件存储。
/// </summary>
internal sealed class SessionToolTestHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(
        Path.GetTempPath(), "seeing-session-tools-" + Guid.NewGuid().ToString("N"));

    public InMemorySessionStore SessionStore { get; } = new();
    public FileSessionGroupStore GroupStore { get; }
    public SessionManager Sessions { get; }
    public SessionGroupManager Groups { get; }

    public SessionToolTestHarness()
    {
        GroupStore = new FileSessionGroupStore(Dir);
        Sessions = new SessionManager(store: SessionStore, logger: NullLogger<SessionManager>.Instance);
        var forker = new SessionForker(NullLogger<SessionForker>.Instance, Sessions);
        Groups = new SessionGroupManager(Sessions, GroupStore, forker, NullLogger<SessionGroupManager>.Instance);
    }

    /// <summary>创建并注册一个根会话（未入组）。</summary>
    public SessionData CreateRoot(string? title = null, string partition = "p1")
    {
        var session = Sessions.Create(partitionId: partition, selectedAgent: "build");
        if (title is not null)
            session.Title = title;
        Sessions.Register(session);
        return session;
    }

    /// <summary>创建根会话并入组（锚点）。</summary>
    public async Task<SessionData> CreateRootGroupedAsync(string? title = null, string partition = "p1")
    {
        var session = CreateRoot(title, partition);
        await Groups.EnsureForSessionAsync(session.Id);
        return session;
    }

    /// <summary>向会话追加一条带确定性 ID 的消息。</summary>
    public async Task<SessionMessage> AddMessageAsync(
        string sessionId,
        string role,
        string content,
        string? reasoning = null,
        int step = 0,
        bool isSummary = false,
        List<SessionToolCall>? toolCalls = null,
        string? toolName = null)
    {
        var message = new SessionMessage
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Role = role,
            Content = content,
            ReasoningContent = reasoning,
            Step = step,
            IsSummary = isSummary,
            ToolCalls = toolCalls,
            ToolName = toolName,
            CreatedAt = DateTime.UtcNow,
        };
        await Sessions.AddMessageAsync(sessionId, message);
        return message;
    }

    public void Dispose()
    {
        if (Directory.Exists(Dir))
            Directory.Delete(Dir, true);
    }
}

/// <summary>记录最近一次权限请求的桩授权器工厂。</summary>
internal sealed class StubPermissionAuthorizerFactory : IPermissionAuthorizerFactory
{
    private readonly PermissionEffect _decision;

    public PermissionRequest? LastRequest { get; private set; }
    public string? LastCreateSessionId { get; private set; }

    public StubPermissionAuthorizerFactory(PermissionEffect decision = PermissionEffect.Allow)
    {
        _decision = decision;
    }

    public IPermissionAuthorizer Create(string sessionId, SessionAutoApprove? @override = null)
    {
        LastCreateSessionId = sessionId;
        return new Authorizer(this, sessionId);
    }

    private sealed class Authorizer : IPermissionAuthorizer
    {
        private readonly StubPermissionAuthorizerFactory _owner;

        public Authorizer(StubPermissionAuthorizerFactory owner, string sessionId)
        {
            _owner = owner;
            SessionId = sessionId;
        }

        public string SessionId { get; }

        public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
        {
            _owner.LastRequest = request;
            return Task.FromResult(new PermissionResolution
            {
                RequestId = request.RequestId ?? "req-1",
                SessionId = request.SessionId,
                CallId = request.CallId,
                Decision = _owner._decision,
                Reason = _owner._decision == PermissionEffect.Allow ? "允许" : "权限被拒绝",
            });
        }
    }
}

/// <summary>记录最近一次请求并返回预设决策的直接授权器（用于 context.PermissionAuthorizer 注入测试）。</summary>
internal sealed class RecordingPermissionAuthorizer : IPermissionAuthorizer
{
    private readonly PermissionEffect _decision;

    public RecordingPermissionAuthorizer(PermissionEffect decision = PermissionEffect.Allow)
    {
        _decision = decision;
        SessionId = "context-session";
    }

    public PermissionRequest? LastRequest { get; private set; }

    public string SessionId { get; }

    public Task<PermissionResolution> AuthorizeAsync(PermissionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new PermissionResolution
        {
            RequestId = request.RequestId ?? "req-ctx",
            SessionId = request.SessionId,
            CallId = request.CallId,
            Decision = _decision,
            Reason = _decision == PermissionEffect.Allow ? "允许" : "权限被拒绝",
        });
    }
}

/// <summary>记录提交参数并返回预设结果的执行提交桩。</summary>
internal sealed class StubExecutionSubmitter : IExecutionSubmitter
{
    private readonly ExecutionSubmitResult _result;

    public string? LastSessionId { get; private set; }
    public ChatInput? LastInput { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public string? LastCancelledExecutionId { get; private set; }
    public int SubmitCount { get; private set; }

    public StubExecutionSubmitter(ExecutionSubmitResult result)
    {
        _result = result;
    }

    public Task<ExecutionSubmitResult> SubmitAsync(
        string sessionId,
        ChatInput input,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        SubmitCount++;
        LastSessionId = sessionId;
        LastInput = input;
        LastOptions = options;
        return Task.FromResult(_result);
    }

    public Task<bool> CancelAsync(string executionId, CancellationToken cancellationToken = default)
    {
        LastCancelledExecutionId = executionId;
        return Task.FromResult(true);
    }

    public Task<int> CancelBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task WaitForExecutionAsync(string executionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<int> CancelAllInFlightAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

/// <summary>极简 IServiceProvider（测试用，仅按类型注册实例）。</summary>
internal sealed class StubServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _map = new();

    public StubServiceProvider Add<T>(T instance) where T : notnull
    {
        _map[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType) =>
        _map.TryGetValue(serviceType, out var value) ? value : null;
}
