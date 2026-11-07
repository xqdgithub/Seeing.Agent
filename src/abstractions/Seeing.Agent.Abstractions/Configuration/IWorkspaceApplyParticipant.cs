namespace Seeing.Agent.Configuration;

/// <summary>一次工作区切换的上下文。</summary>
/// <param name="OldRoot">切换前的工作区根；首次选定为 null。</param>
/// <param name="NewRoot">切换后的工作区根。</param>
public sealed record WorkspaceApplyContext(string? OldRoot, string NewRoot);

/// <summary>
/// 工作区切换的参与者：能力包在这里停掉依赖项目路径的运行时，
/// 并在根变更与重载完成后按新根重新拉起。
/// <para>
/// 顺序保证：所有 <see cref="OnApplyingAsync"/> 在改根之前跑完，
/// 所有 <see cref="OnAppliedAsync"/> 在一轮重载成功之后跑。
/// </para>
/// </summary>
public interface IWorkspaceApplyParticipant
{
    /// <summary>参与者标识（用于日志与错误信息）。</summary>
    string ComponentId { get; }

    /// <summary>改根之前：停掉跑在旧根上的东西。失败视为切换失败。</summary>
    Task OnApplyingAsync(WorkspaceApplyContext context, CancellationToken ct);

    /// <summary>重载成功后：按新根重新启动。失败记日志，不视为切换失败。</summary>
    Task OnAppliedAsync(WorkspaceApplyContext context, CancellationToken ct);
}
