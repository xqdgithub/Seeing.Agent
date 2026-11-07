using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

/// <summary>解析 CLI 的工作区根目录，供服务启动与 TUI 子进程共用。</summary>
internal static class CliWorkspace
{
    /// <summary>
    /// CLI 的工作区 = 本次启动目录。
    /// <para>
    /// 不再优先读 <c>SEEING_WORKSPACE_ROOT</c>：CLI 是「工具」，每次调用都可能在不同目录，
    /// shell 里残留的同名变量会让一次调用指向两个工作区（父进程决议 vs 子进程环境）。
    /// 需要显式指定请用 <c>--workspace</c> 或 TUI 的位置参数。
    /// </para>
    /// </summary>
    public static string Resolve() => Directory.GetCurrentDirectory();

    /// <summary>子进程启动信息：工作目录与环境里的工作区都写成同一个已决议目录。</summary>
    internal static void PinWorkspace(ProcessStartInfo psi, string workspaceRoot)
    {
        psi.WorkingDirectory = workspaceRoot;
        psi.Environment["SEEING_WORKSPACE_ROOT"] = workspaceRoot;
    }
}
