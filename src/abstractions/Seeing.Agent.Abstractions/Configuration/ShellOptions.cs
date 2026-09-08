namespace Seeing.Agent.Abstractions.Configuration;

/// <summary>
/// Shell 配置 - 危险命令拦截与 Windows Shell 优先级
/// </summary>
public sealed class ShellOptions
{
    /// <summary>Windows 优先 Shell（按顺序，找不到则顺延）。bash 条目通过 git.exe 推断 git bash 路径</summary>
    public List<string> PreferredShells { get; set; } = new() { "pwsh", "powershell", "bash", "cmd" };

    /// <summary>封禁命令（只保留灾难性）。匹配时大小写不敏感</summary>
    public List<string> BlockedCommands { get; set; } = new()
        { "dd", "mkfs", "format", "shutdown", "reboot", "halt", "poweroff", "init" };

    /// <summary>封禁模式（灾难性 pattern；删除类项由 DangerousCommandGuard 的删除目标检查覆盖）</summary>
    public List<string> BlockedPatterns { get; set; } = new()
    {
        ":(){ :|:& };:",
        "dd if=/dev/zero of=/dev/",
        "chmod 777 /", "chmod -R 777 /",
        "format C:", "format D:",
        "reg delete HKLM",
        "> /etc/", "> /boot/", "> /sys/",
    };

    /// <summary>是否启用危险命令拦截（默认关闭：临时全部放行，待重新设计合理校验后恢复）</summary>
    public bool EnableCommandGuard { get; set; } = false;
}
