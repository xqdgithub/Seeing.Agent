using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Permission;

/// <summary>基于 ISessionManager 内存会话数据解析会话有效工作目录。</summary>
public sealed class SessionWorkspaceResolver : ISessionWorkspaceResolver
{
    private readonly ISessionManager _sessions;

    /// <summary>注入会话管理器。</summary>
    public SessionWorkspaceResolver(ISessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <inheritdoc />
    public string? ResolveWorkingDirectory(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var cwd = _sessions.Get(sessionId)?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd))
            return null;

        try
        {
            return Directory.Exists(cwd) ? Path.GetFullPath(cwd) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}
