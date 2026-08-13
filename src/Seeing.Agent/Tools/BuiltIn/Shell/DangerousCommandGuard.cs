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

        var trimmed = command.Trim();
        var lower = trimmed.ToLowerInvariant();

        // 管道：curl/wget | bash/sh 等
        if (lower.Contains("|"))
        {
            foreach (var part in lower.Split('|'))
            {
                var pt = part.Trim();
                if ((pt.StartsWith("bash") || pt.StartsWith("sh") ||
                     pt.StartsWith("zsh") || pt.StartsWith("powershell")) &&
                    !pt.Contains("-c"))
                    return "禁止使用管道将输出传递给 Shell 解释器";
            }
        }

        // 命令替换 $() / 反引号 与网络请求组合
        if (lower.Contains("$(") || lower.Contains("`"))
        {
            if (lower.Contains("curl") || lower.Contains("wget") ||
                lower.Contains("nc ") || lower.Contains("ncat"))
                return "禁止命令替换与网络请求组合使用";
        }

        // 封禁模式（子串匹配，用于明确灾难性片段）
        foreach (var pattern in options.BlockedPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (lower.Contains(pattern.ToLowerInvariant()))
                return $"禁止执行危险命令模式: {pattern}";
        }

        // 命令名封禁（剥离 sudo/env 前缀）
        var cmdName = GetCommandName(lower);
        if (cmdName != null && options.BlockedCommands.Contains(cmdName, StringComparer.OrdinalIgnoreCase))
            return $"禁止执行危险命令: {cmdName}";

        // 删除目标检查：rm/rmdir/del 递归删除根/家/当前目录/盘符根（对管道各段分别检查）
        foreach (var segment in lower.Split('|'))
        {
            var deleteTarget = CheckDestructiveDelete(segment);
            if (deleteTarget != null)
                return $"禁止递归删除根/家/当前目录: {deleteTarget}";
        }

        return null;
    }

    /// <summary>提取命令名（剥离 sudo/env 前缀，处理带路径的可执行文件）</summary>
    private static string? GetCommandName(string lowerCommand)
    {
        var tokens = lowerCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (t is "sudo" or "env") continue;
            if (t.StartsWith("--")) continue;
            return Path.GetFileName(t);
        }
        return null;
    }

    /// <summary>检查 rm/rmdir/del 递归删除是否指向危险目标（根/家/当前/盘符根）。返回危险目标 token 或 null</summary>
    private static string? CheckDestructiveDelete(string lowerCommand)
    {
        var tokens = lowerCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmdName = GetCommandName(lowerCommand);
        if (cmdName is not ("rm" or "rmdir" or "del")) return null;

        // 递归标志：rm/rmdir 用 -r/--recursive；del 用 /s
        var recursive = cmdName is "rm" or "rmdir"
            ? lowerCommand.Contains("-r") || lowerCommand.Contains("--recursive")
            : lowerCommand.Contains("/s");
        if (!recursive) return null;

        foreach (var t in tokens)
        {
            if (t is "sudo" or "env") continue;
            if (t == cmdName) continue;
            if (IsDestructiveTarget(t)) return t;
            if (t.StartsWith("-") || t.StartsWith("/")) continue; // 选项
        }
        return null;
    }

    /// <summary>判断是否为危险删除目标：/、~、.、盘符根（C:、C:\、C:\*、C:/ 等）。先归一化尾部路径分隔符与通配符</summary>
    private static bool IsDestructiveTarget(string target)
    {
        var normalized = target.TrimEnd('\\', '/', '*');
        // 根路径特例：全为分隔符/通配符时 TrimEnd 会得到空串，还原为根 "/"（覆盖 /、//、/* 等）
        if (normalized.Length == 0 && target.StartsWith("/")) normalized = "/";
        if (normalized is "/" or "~" or ".") return true;
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            // 盘符根：C:、C:\、C:/ 等（后面没有更多路径片段）
            var rest = normalized.Length > 2 ? normalized[2..].Trim('\\', '/') : string.Empty;
            return rest.Length == 0;
        }
        return false;
    }
}
