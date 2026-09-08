using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Filesystem;
using Seeing.Agent.Acp.Permission;
using Seeing.Agent.Acp.Terminal;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Acp.Client;

/// <summary>
/// 按 backend 创建 <see cref="SeeingAcpClient"/>。
/// </summary>
public sealed class SeeingAcpClientFactory
{
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly AcpPermissionBridge _permissionBridge;
    private readonly AcpFileSystemBridge _fileSystemBridge;
    private readonly AcpTerminalBridge _terminalBridge;
    private readonly IOptionsMonitor<AcpOptions> _options;
    private readonly ILoggerFactory _loggerFactory;

    public SeeingAcpClientFactory(
        IAcpBackendRegistry backendRegistry,
        AcpPermissionBridge permissionBridge,
        AcpFileSystemBridge fileSystemBridge,
        AcpTerminalBridge terminalBridge,
        IOptionsMonitor<AcpOptions> options,
        ILoggerFactory loggerFactory)
    {
        _backendRegistry = backendRegistry;
        _permissionBridge = permissionBridge;
        _fileSystemBridge = fileSystemBridge;
        _terminalBridge = terminalBridge;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public SeeingAcpClient Create(
        string backendId,
        IAcpUpdateSink? updateSink = null,
        AcpPermissionContext? permissionContext = null,
        string? workingDirectory = null)
    {
        var backend = _backendRegistry.GetBackend(backendId);
        return new SeeingAcpClient(
            backend,
            _permissionBridge,
            _fileSystemBridge,
            _terminalBridge,
            _options,
            _loggerFactory.CreateLogger<SeeingAcpClient>(),
            updateSink,
            permissionContext,
            workingDirectory);
    }
}
