namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>解析会话有效工作目录（绝对路径）；会话不存在或无 cwd 返回 null。</summary>
public interface ISessionWorkspaceResolver
{
    /// <summary>返回会话有效工作目录，或 null。</summary>
    string? ResolveWorkingDirectory(string sessionId);
}
