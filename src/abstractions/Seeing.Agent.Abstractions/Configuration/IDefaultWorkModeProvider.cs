namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// 进程默认工作模式（Scenario）提供者：新建 root session 时应物化到 <c>session.Scenario</c>。
/// </summary>
public interface IDefaultWorkModeProvider
{
    /// <summary>进程默认工作模式名；未配置时返回 null。</summary>
    string? GetDefaultScenario();
}
