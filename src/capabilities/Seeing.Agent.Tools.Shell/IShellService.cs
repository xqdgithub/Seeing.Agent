namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// Shell 服务 — 跨平台 Shell 选择与命令准备。
/// </summary>
public interface IShellService
{
    string SelectShell();
    string PrepareCommand(string shell, string command);

    /// <summary>
    /// 拼出交给 <c>ProcessStartInfo.Arguments</c> 的原始命令行。仅适用于 cmd / powershell。
    /// POSIX shell 必须使用 <see cref="BuildArgumentList"/>。
    /// </summary>
    string BuildArguments(string shell, string preparedCommand);

    /// <summary>
    /// POSIX shell 的 argv。<see langword="null"/> 表示继续走 <see cref="BuildArguments"/>。
    /// Linux 上运行时会按空白拆开 <c>Arguments</c>，单引号不会成组，因此不能把脚本包进 <c>-c '…'</c>。
    /// </summary>
    IReadOnlyList<string>? BuildArgumentList(string shell, string preparedCommand) => null;

    string GetShellName(string shell);
}
