using Acp.Messages;
using Acp.Transport;
using Acp.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Client;

/// <summary>
/// 带桥接注入的 ACP 子进程客户端。
/// </summary>
public sealed class SeeingAcpClient : SubprocessClient
{
    private readonly AcpPermissionBridge _permissionBridge;
    private readonly AcpFileSystemBridge _fileSystemBridge;
    private readonly AcpTerminalBridge _terminalBridge;
    private readonly ILogger<SeeingAcpClient> _clientLogger;
    private readonly string _defaultWorkingDirectory;

    private IAcpUpdateSink? _updateSink;
    private AcpPermissionContext? _permissionContext;
    private string _workingDirectory;

    public SeeingAcpClient(
        AcpBackendDescriptor backend,
        AcpPermissionBridge permissionBridge,
        AcpFileSystemBridge fileSystemBridge,
        AcpTerminalBridge terminalBridge,
        IOptions<SeeingAgentOptions> options,
        ILogger<SeeingAcpClient>? logger = null,
        IAcpUpdateSink? updateSink = null,
        AcpPermissionContext? permissionContext = null,
        string? workingDirectory = null)
        : base(
            backend.Command,
            backend.Args.ToArray(),
            BuildOptions(backend, options, logger))
    {
        _permissionBridge = permissionBridge;
        _fileSystemBridge = fileSystemBridge;
        _terminalBridge = terminalBridge;
        _clientLogger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SeeingAcpClient>.Instance;
        _defaultWorkingDirectory = workingDirectory ?? backend.WorkingDirectory ?? Environment.CurrentDirectory;
        _workingDirectory = _defaultWorkingDirectory;
        _updateSink = updateSink;
        _permissionContext = permissionContext;
    }

    /// <summary>
    /// 为当前请求绑定流式 sink、权限上下文与工作目录（租约复用时每次 Run 调用）。
    /// </summary>
    public void ConfigureForRequest(
        IAcpUpdateSink? updateSink,
        AcpPermissionContext? permissionContext,
        string? workingDirectory = null)
    {
        _updateSink = updateSink;
        _permissionContext = permissionContext;
        _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? _defaultWorkingDirectory
            : workingDirectory;
    }

    /// <inheritdoc />
    public override Task<RequestPermissionResponse> RequestPermissionAsync(
        IEnumerable<PermissionOption> options,
        string sessionId,
        ToolCallUpdate toolCall,
        CancellationToken cancellationToken = default)
    {
        using var _ = _permissionContext != null ? _permissionBridge.Push(_permissionContext) : null;
        return _permissionBridge.HandleAsync(sessionId, toolCall, options, cancellationToken);
    }

    /// <inheritdoc />
    public override Task SessionUpdateAsync(
        string sessionId,
        SessionUpdate update,
        CancellationToken cancellationToken = default)
    {
        _clientLogger.Log(
            AcpSessionUpdateLogging.GetLogLevel(update),
            "ACP client received session/update acpSession={AcpSessionId} kind={Kind}",
            sessionId,
            AcpSessionUpdateLogging.Describe(update));

        return _updateSink?.OnSessionUpdateAsync(sessionId, update, cancellationToken)
            ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<ReadTextFileResponse> ReadTextFileAsync(
        string path,
        string sessionId,
        int? limit = null,
        int? line = null,
        CancellationToken cancellationToken = default)
    {
        return _fileSystemBridge.ReadTextFileAsync(path, sessionId, _workingDirectory, limit, line, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<WriteTextFileResponse?> WriteTextFileAsync(
        string content,
        string path,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return _fileSystemBridge.WriteTextFileAsync(content, path, sessionId, _workingDirectory, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<CreateTerminalResponse> CreateTerminalAsync(
        string command,
        string sessionId,
        List<string>? args = null,
        string? cwd = null,
        List<EnvVariable>? env = null,
        int? outputByteLimit = null,
        CancellationToken cancellationToken = default)
    {
        return _terminalBridge.CreateTerminalAsync(
            command,
            sessionId,
            cwd ?? _workingDirectory,
            args,
            env,
            cancellationToken);
    }

    /// <inheritdoc />
    public override Task<TerminalOutputResponse> TerminalOutputAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
        => _terminalBridge.TerminalOutputAsync(sessionId, terminalId, cancellationToken);

    /// <inheritdoc />
    public override Task<ReleaseTerminalResponse?> ReleaseTerminalAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
        => _terminalBridge.ReleaseTerminalAsync(sessionId, terminalId, cancellationToken);

    /// <inheritdoc />
    public override Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
        => _terminalBridge.WaitForTerminalExitAsync(sessionId, terminalId, cancellationToken);

    /// <inheritdoc />
    public override Task<KillTerminalCommandResponse?> KillTerminalAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
        => _terminalBridge.KillTerminalAsync(sessionId, terminalId, cancellationToken);

    private static SubprocessClientOptions BuildOptions(
        AcpBackendDescriptor backend,
        IOptions<SeeingAgentOptions> options,
        ILogger? logger)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            WorkingDirectory = backend.WorkingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var (key, value) in backend.Environment)
            startInfo.Environment[key] = value;

        return new SubprocessClientOptions
        {
            StartInfo = startInfo,
            Logger = logger,
            RequestTimeout = options.Value.Acp.RequestTimeout,
            DefaultStartTimeout = Configuration.AcpOptionsDefaults.DefaultStartTimeout,
            DefaultStopTimeout = Configuration.AcpOptionsDefaults.DefaultStopTimeout
        };
    }
}
