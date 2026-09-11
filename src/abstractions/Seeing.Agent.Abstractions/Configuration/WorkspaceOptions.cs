namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区配置选项（项目级）。
/// 类型位于 Abstractions 程序集，命名空间保持 <c>Seeing.Agent.Configuration</c> 以兼容既有引用。
/// </summary>
public class WorkspaceOptions
{
    /// <summary>
    /// 是否使用全局默认工作区
    /// <para>true: 使用用户级 GlobalWorkspaceRoot</para>
    /// <para>false 或未设置: 使用启动目录（默认）</para>
    /// </summary>
    public bool UseGlobal { get; set; } = false;

    /// <summary>
    /// 项目特定的自定义工作区路径
    /// <para>设置后忽略 UseGlobal，直接使用此路径</para>
    /// </summary>
    public string? CustomPath { get; set; }

    /// <summary>
    /// 工作区文件硬边界：为 true 时，文件工具目标路径须 ∈ WorkspaceRoot ∪ 会话白名单；
    /// 越界由权限通道 Ask，Allow 后写入白名单。默认 false（与历史行为一致）。不约束 bash。
    /// </summary>
    public bool RestrictToWorkspace { get; set; }
}
