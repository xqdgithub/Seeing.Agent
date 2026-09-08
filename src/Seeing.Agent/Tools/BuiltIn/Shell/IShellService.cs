namespace Seeing.Agent.Tools.BuiltIn.Shell;

/// <summary>
/// Shell 服务 — 跨平台 Shell 选择与命令准备。
/// </summary>
public interface IShellService
{
    string SelectShell();
    string PrepareCommand(string shell, string command);
    string BuildArguments(string shell, string preparedCommand);
    string GetShellName(string shell);
}
