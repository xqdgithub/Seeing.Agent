namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 配置目录路径契约（用户级 / 项目级 <c>.seeing</c>）。
/// 仅需目录路径时可依赖此窄接口；需要工作区切换/解析时用 <see cref="Seeing.Agent.Configuration.IWorkspaceProvider"/>。
/// </summary>
public interface ISeeingDirectories
{
    /// <summary>用户级 .seeing 目录路径</summary>
    string UserSeeingDirectory { get; }

    /// <summary>项目级 .seeing 目录路径</summary>
    string ProjectSeeingDirectory { get; }
}
