using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Comms.Directories;

/// <summary>
/// 工作区切换参与者：让通联子系统整块切到新工作区。
/// <para><b>为什么要参与</b>：目录文件路径由工作区推导
/// （<c>{项目}/.seeing/comms/directory.json</c>）。切换工作区后路径变了，
/// 而 <see cref="JsonChannelDirectoryStore"/> 与 <see cref="ChannelDirectoryService"/>
/// 都是单例、内存里留着上一个工作区的对话、屏蔽名单、回复锚点与频控滑窗——
/// 不重置就会出现「新工作区显示旧工作区的会话」「新工作区的回复锚着旧工作区的消息」，
/// 更糟的是下一次写入会把旧条目合并进新工作区的文件。</para>
/// <para><b>不认具体类型</b>：本参与者只枚举 <see cref="ICommsWorkspaceScopedState"/>，
/// 谁持有工作区级状态谁自己清理。新增一处状态时无需改这里，
/// 也不会因为外层要知道内部字段而破坏封装。</para>
/// </summary>
public sealed class CommsWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly IReadOnlyList<ICommsWorkspaceScopedState> _scopedStates;
    private readonly ILogger<CommsWorkspaceParticipant> _logger;

    /// <summary>创建参与者。</summary>
    public CommsWorkspaceParticipant(
        IEnumerable<ICommsWorkspaceScopedState> scopedStates,
        ILogger<CommsWorkspaceParticipant> logger)
    {
        _scopedStates = scopedStates.ToArray();
        _logger = logger;
    }

    /// <inheritdoc/>
    public string ComponentId => "comms";

    /// <inheritdoc/>
    /// <remarks>目录是纯内存+文件，切换前没有「跑在旧根上的东西」需要停。</remarks>
    public Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// 单个状态重置失败只记日志、不影响其它状态，也不让切换失败：
    /// 正确性本身不依赖这里（各状态的读写路径都会自行对齐到当前工作区），
    /// 这一步只是让切换后的第一帧界面看不到旧数据。
    /// </remarks>
    public Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        foreach (var state in _scopedStates)
        {
            try
            {
                state.ResetForNewWorkspace();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工作区切换：通联状态 {State} 重置失败", state.GetType().Name);
            }
        }

        _logger.LogInformation("工作区切换：通联目录已按新工作区重置: {Root}", context.NewRoot);
        return Task.CompletedTask;
    }
}
