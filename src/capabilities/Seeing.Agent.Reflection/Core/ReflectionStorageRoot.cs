using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 复盘产物根目录：日志、用量账本与记录页读的同一个目录。
/// <para>
/// <b>为什么是可变的单例而不是构造时算好的字符串</b>：它跟着工作区走
/// （<c>{workspace}/.seeing/reflection</c>）。原先在 DI 工厂里一次性算好，
/// 切到工作区 B 之后 journal / 账本 / 记录页仍然写读 A 的目录——
/// 会话存储有 <c>SessionReloadHandler</c> 重定位，复盘没有等价物，于是
/// 「新工作区的复盘记录」永远看不到、预算也记在旧工作区。
/// </para>
/// <para>
/// 工作区未选定时退回临时目录（有界面宿主停在首页的合法状态）；
/// 首次选定后由 <see cref="ReflectionWorkspaceParticipant"/> 重新对齐。
/// </para>
/// </summary>
public sealed class ReflectionStorageRoot
{
    private readonly ILogger<ReflectionStorageRoot>? _logger;
    private readonly object _gate = new();
    private string _path;

    /// <summary>创建目录持有者。</summary>
    /// <param name="path">初始目录。</param>
    /// <param name="logger">日志（可选）。</param>
    public ReflectionStorageRoot(string path, ILogger<ReflectionStorageRoot>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("复盘产物目录不能为空", nameof(path));

        _path = path;
        _logger = logger;
    }

    /// <summary>当前生效的产物根目录（每次现算，不缓存调用方快照）。</summary>
    public string Path
    {
        get
        {
            lock (_gate)
                return _path;
        }
    }

    /// <summary>按当前工作区重算目录。工作区未选定时保持原值（可能是临时目录）。</summary>
    /// <param name="workspace">工作区提供者。</param>
    /// <returns>是否发生了变更。</returns>
    public bool AlignToWorkspace(IWorkspaceProvider workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        // ProjectSeeingDirectory 在未选定时是占位目录：不能把占位当真实工作区钉死。
        if (!workspace.IsResolved)
            return false;

        return SetPath(System.IO.Path.Combine(workspace.ProjectSeeingDirectory, "reflection"));
    }

    /// <summary>直接指定目录（首次选定与切换共用）。</summary>
    /// <param name="path">目标目录。</param>
    /// <returns>是否发生了变更。</returns>
    public bool SetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        lock (_gate)
        {
            if (string.Equals(_path, path, StringComparison.Ordinal))
                return false;

            _logger?.LogInformation("复盘产物目录已切换：{Old} → {New}", _path, path);
            _path = path;
            return true;
        }
    }

    /// <summary>工作区未选定时的回退目录（仅无宿主场景 / 启动早期）。</summary>
    public static string TempFallback
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seeing-reflection");
}
