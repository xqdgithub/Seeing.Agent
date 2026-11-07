namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// 批量启停渠道子进程（工作区切换与宿主开关共用）。
/// <para>
/// 单独抽出来的理由：渠道子进程有自己的 <c>WorkspaceProvider</c>（构造时按配置路径推断根），
/// 不会跟着宿主进程的 <c>SetWorkspaceRoot</c> 走。切工作区必须真的重启它们。
/// </para>
/// </summary>
public interface IChannelHostController
{
    /// <summary>停止所有正在运行的渠道子进程。</summary>
    Task StopAllAsync(CancellationToken ct = default);

    /// <summary>按当前配置启动所有已启用的渠道子进程。</summary>
    Task StartAllAsync(CancellationToken ct = default);
}
