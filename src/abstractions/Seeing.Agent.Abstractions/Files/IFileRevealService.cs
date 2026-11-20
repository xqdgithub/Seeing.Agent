namespace Seeing.Agent.Abstractions.Files;

/// <summary>
/// 在宿主操作系统的文件管理器中定位/打开一个路径。
/// <para>
/// 由宿主实现（WebUI / 桌面端）：组件库只负责触发；无此能力的宿主不注册，
/// UI 应据此灰置入口，而不是点击后无反应。
/// </para>
/// </summary>
public interface IFileRevealService
{
    /// <summary>当前宿主是否支持在文件管理器中定位（远程/容器部署通常不支持）。</summary>
    bool CanReveal { get; }

    /// <summary>在文件管理器中选中文件（目录则打开该目录）。路径必须是绝对路径。</summary>
    Task RevealAsync(string absolutePath, CancellationToken ct = default);
}
