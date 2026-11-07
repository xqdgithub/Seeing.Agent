namespace Seeing.Agent.Configuration;

/// <summary>宿主启动方式：决定工作区从哪来。</summary>
public enum WorkspaceLaunchMode
{
    /// <summary>CLI / TUI：工作区就是本次启动目录（或 TUI 的位置参数）。</summary>
    Invocation = 0,

    /// <summary>WebUI / Desktop：可能进入工作区首页。</summary>
    Interactive = 1,

    /// <summary>单独的 Gateway.Server：没有首页。</summary>
    Service = 2,
}

/// <summary>最近工作区快照。不存在的目录由策略层过滤。</summary>
public sealed record WorkspaceHistorySnapshot(IReadOnlyList<string> Recent, string? LastInteractive)
{
    /// <summary>空历史。</summary>
    public static WorkspaceHistorySnapshot Empty { get; } =
        new(Array.Empty<string>(), null);
}

/// <summary>启动决议的输入。全部由宿主在 <c>Host.Build</c> 之前填好。</summary>
public sealed record WorkspaceLaunchInput(
    WorkspaceLaunchMode Mode,
    string StartupDirectory,
    string? ExecutableDirectory,
    string? ExplicitPath,
    string? InheritedEnvironment,
    WorkspaceHistorySnapshot History);

/// <summary>宿主登记的启动请求（单例）。缺失时宿主按 Interactive + 当前目录处理。</summary>
public sealed record WorkspaceLaunchRequest(WorkspaceLaunchInput Input);

/// <summary>决议结果类型。</summary>
public enum WorkspaceLaunchOutcome
{
    /// <summary>已确定工作区。</summary>
    Resolved = 0,

    /// <summary>有界面宿主且没有可用目录：显示工作区首页。</summary>
    Unresolved = 1,

    /// <summary>显式指定的目录无效：进程应启动失败。</summary>
    Failed = 2,
}

/// <summary>
/// 决议结果。<see cref="UpdateRecent"/> / <see cref="UpdateLastInteractive"/> 决定历史怎么写。
/// </summary>
public sealed record WorkspaceLaunchDecision(
    WorkspaceLaunchOutcome Outcome,
    string? WorkspaceRoot,
    WorkspaceResolutionSource Source,
    string? Error,
    bool UpdateRecent,
    bool UpdateLastInteractive)
{
    /// <summary>显式指定的目录无效。</summary>
    public static WorkspaceLaunchDecision Failed(string error) =>
        new(WorkspaceLaunchOutcome.Failed, null, WorkspaceResolutionSource.StartupDirectory, error, false, false);

    /// <summary>有界面宿主没有可用目录。</summary>
    public static WorkspaceLaunchDecision Unresolved() =>
        new(WorkspaceLaunchOutcome.Unresolved, null, WorkspaceResolutionSource.StartupDirectory, null, false, false);
}
