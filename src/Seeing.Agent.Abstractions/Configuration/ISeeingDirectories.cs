namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置目录路径契约（用户级 / 项目级 <c>.seeing</c>）。
/// 能力包应依赖此接口而非主库 <c>IWorkspaceProvider</c>。
/// </summary>
public interface ISeeingDirectories
{
    /// <summary>用户级 .seeing 目录路径</summary>
    string UserSeeingDirectory { get; }

    /// <summary>项目级 .seeing 目录路径</summary>
    string ProjectSeeingDirectory { get; }
}
