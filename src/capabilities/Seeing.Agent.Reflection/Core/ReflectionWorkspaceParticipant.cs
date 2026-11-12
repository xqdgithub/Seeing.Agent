using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 工作区切换参与者：把复盘产物根目录重新对齐到新工作区。
/// <para>
/// <b>为什么需要它</b>：复盘产物（journal / 用量账本 / 记录页读取根）跟着工作区走，
/// 而它是个单例。没有参与者时，切到工作区 B 之后新复盘仍写进 A 的
/// <c>.seeing/reflection</c>：记录页看不到、预算也记在旧项目上。
/// 会话存储有 <c>SessionReloadHandler</c> 做同样的事，本类是它的复盘版。
/// </para>
/// <para>
/// 只做重定位，没有需要「停掉再拉起」的运行时——因此
/// <see cref="OnAppliedAsync"/> 与 <see cref="OnApplyingAsync"/> 都用同一次对齐：
/// 在改根之后、重载成功之后各对齐一次是幂等的，也让「首次选定」与「再次切换」共用一条路径。
/// </para>
/// </summary>
public sealed class ReflectionWorkspaceParticipant : IWorkspaceApplyParticipant
{
    private readonly ReflectionStorageRoot _root;
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<ReflectionWorkspaceParticipant>? _logger;

    public ReflectionWorkspaceParticipant(
        ReflectionStorageRoot root,
        IWorkspaceProvider workspace,
        ILogger<ReflectionWorkspaceParticipant>? logger = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _logger = logger;
    }

    /// <inheritdoc />
    public string ComponentId => "reflection";

    /// <inheritdoc />
    /// <remarks>改根之前不做任何事：产物目录是纯路径，没有跑在旧根上的运行时。</remarks>
    public Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct)
    {
        Align();
        return Task.CompletedTask;
    }

    /// <summary>按当前工作区重算产物目录（幂等）。</summary>
    public void Align()
    {
        try
        {
            if (_root.AlignToWorkspace(_workspace))
            {
                _logger?.LogInformation("复盘产物目录已跟随工作区：{Directory}", _root.Path);
            }
        }
        catch (Exception ex)
        {
            // 对齐失败只影响产物落点，不能让它推翻整个工作区切换。
            _logger?.LogWarning(ex, "复盘产物目录对齐工作区失败（继续使用原目录）：{Directory}", _root.Path);
        }
    }
}
