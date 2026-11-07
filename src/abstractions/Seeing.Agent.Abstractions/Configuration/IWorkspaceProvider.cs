using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区变更事件参数
/// </summary>
public class WorkspaceChangedEventArgs : EventArgs
{
    /// <summary>旧工作区路径</summary>
    public string OldWorkspace { get; init; } = "";

    /// <summary>新工作区路径</summary>
    public string NewWorkspace { get; init; } = "";
}

/// <summary>
/// 工作区解析来源
/// </summary>
public enum WorkspaceResolutionSource
{
    /// <summary>环境变量 SEEING_WORKSPACE_ROOT</summary>
    EnvironmentVariable,

    /// <summary>项目级自定义路径</summary>
    ProjectCustomPath,

    /// <summary>全局默认工作区</summary>
    GlobalDefault,

    /// <summary>启动目录（默认行为）</summary>
    StartupDirectory,

    /// <summary>手动切换（运行时）</summary>
    ManualSwitch
}

/// <summary>
/// 工作区路径提供者 — 统一管理配置目录的获取逻辑（非执行 cwd）。
/// <para>
/// 目录层级：
/// - 用户级：~/.seeing/（基础配置）
/// - 项目级：{project}/.seeing/（覆盖同名）
/// </para>
/// <para>
/// 类型位于 Abstractions 程序集；实现由 Host / Core 注册，能力包禁止 <c>new</c> 具体实现。
/// 命名空间保持 <c>Seeing.Agent.Configuration</c> 以兼容既有引用。
/// </para>
/// </summary>
public interface IWorkspaceProvider : ISeeingDirectories
{
    /// <summary>工作区是否已选定。未选定时 <see cref="ISeeingDirectories.ProjectSeeingDirectory"/> 抛异常。</summary>
    bool IsResolved { get; }

    /// <summary>已决议的工作区根；未选定时为 <see langword="null"/>。与 <see cref="IsResolved"/> 同时可用。</summary>
    string? ResolvedRoot { get; }

    /// <summary>
    /// 按启动决议设定工作区根（进程内首次选定）。
    /// <para>
    /// 与 <see cref="SetWorkspaceRoot"/> 的区别：本方法用于「还没有根」时建立根，
    /// 保留决议来源（环境变量 / 启动目录 / 命令行参数等），设置页据此展示来源。
    /// </para>
    /// </summary>
    /// <param name="workspaceRoot">工作区根目录。</param>
    /// <param name="source">决议来源。</param>
    void SetInitialWorkspace(string workspaceRoot, WorkspaceResolutionSource source);

    /// <summary>更新工作区根目录（运行时临时切换，不持久化；同时同步进程当前目录）</summary>
    void SetWorkspaceRoot(string workspaceRoot);

    /// <summary>
    /// 获取指定级别的配置目录路径
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <returns>配置目录路径</returns>
    string GetSeeingDirectory(ConfigLevel level);

    /// <summary>
    /// 工作区变更事件
    /// </summary>
    event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;

    /// <summary>启动目录（程序启动时的当前目录，不变）</summary>
    string StartupDirectory { get; }

    /// <summary>当前工作区来源</summary>
    WorkspaceResolutionSource ResolutionSource { get; }

    /// <summary>全局默认工作区（用户级设置）</summary>
    string? GlobalWorkspaceRoot { get; }

    /// <summary>初始化工作区（根据配置解析）</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>设置全局默认工作区（持久化到用户级）</summary>
    Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default);

    /// <summary>设置项目级工作区配置（持久化到项目级）</summary>
    Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default);
}
