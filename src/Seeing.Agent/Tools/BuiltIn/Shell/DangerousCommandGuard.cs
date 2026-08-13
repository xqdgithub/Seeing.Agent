using Seeing.Agent.Configuration;

namespace Seeing.Agent.Tools.BuiltIn.Shell;

/// <summary>
/// 危险命令检测器 - 基于 <see cref="ShellOptions"/> 的可配置命令安全检测。
/// 只拦截灾难性操作，普通删除与绝对路径重定向不再拦截。
/// </summary>
internal static class DangerousCommandGuard
{
    /// <summary>检查命令是否危险。安全返回 null，否则返回拒绝原因。</summary>
    public static string? Check(string command, ShellOptions options)
    {
        if (string.IsNullOrWhiteSpace(command)) return "空命令";
        if (options == null) return null;
        if (!options.EnableCommandGuard) return null;

        var trimmed = command.Trim().ToLowerInvariant();

        // 管道：curl/wget | bash/sh 等
        if (trimmed.Contains("|"))
        {
            foreach (var part in trimmed.Split('|'))
            {
                var pt = part.Trim();
                if ((pt.StartsWith("bash") || pt.StartsWith("sh") ||
                     pt.StartsWith("zsh") || pt.StartsWith("powershell")) &&
                    !pt.Contains("-c"))
                    return "禁止使用管道将输出传递给 Shell 解释器";
            }
        }

        // 命令替换 $() / 反引号 与网络请求组合
        if (trimmed.Contains("$(") || trimmed.Contains("`"))
        {
            if (trimmed.Contains("curl") || trimmed.Contains("wget") ||
                trimmed.Contains("nc ") || trimmed.Contains("ncat"))
                return "禁止命令替换与网络请求组合使用";
        }

        // 封禁模式（灾难性）
        foreach (var pattern in options.BlockedPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (trimmed.Contains(pattern.ToLowerInvariant()))
                return $"禁止执行危险命令模式: {pattern}";
        }

        // 封禁命令（大小写不敏感）
        var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var cmdName = Path.GetFileName(firstWord);
        if (options.BlockedCommands.Contains(cmdName, StringComparer.OrdinalIgnoreCase))
            return $"禁止执行危险命令: {cmdName}";

        return null;
    }
}
