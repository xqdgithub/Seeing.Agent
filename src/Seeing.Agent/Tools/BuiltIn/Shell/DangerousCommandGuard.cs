using Seeing.Agent.Configuration;

namespace Seeing.Agent.Tools.BuiltIn.Shell;

/// <summary>
/// 危险命令检测器 - 基于 <see cref="ShellOptions"/> 的可配置命令安全检测。
/// 只拦截灾难性操作，普通删除与绝对路径重定向不再拦截。
/// 采用令牌化解析：剥离 sudo/env 与所有选项令牌后提取命令名，删除目标按令牌精确判断。
/// </summary>
internal static class DangerousCommandGuard
{
    /// <summary>检查命令是否危险。安全返回 null，否则返回拒绝原因。</summary>
    public static string? Check(string command, ShellOptions options)
    {
        if (string.IsNullOrWhiteSpace(command)) return "空命令";
        if (options == null) return null;
        if (!options.EnableCommandGuard) return null;

        var lower = command.Trim().ToLowerInvariant();

        // 1. 管道：对每段做命令名精确匹配（解释器名）
        if (lower.Contains("|"))
        {
            foreach (var segment in lower.Split('|'))
            {
                var segCmd = GetCommandName(segment);
                if (segCmd is "bash" or "sh" or "zsh" or "powershell" or "pwsh" or "cmd" or "cmd.exe")
                    return "禁止使用管道将输出传递给 Shell 解释器";
            }
        }

        // 2. 命令替换 $()/反引号 或 进程替换 <( 与网络请求组合
        if ((lower.Contains("$(") || lower.Contains("`") || lower.Contains("<(")) &&
            (lower.Contains("curl") || lower.Contains("wget") ||
             lower.Contains("nc ") || lower.Contains("ncat") ||
             lower.Contains("invoke-webrequest") || lower.Contains("iwr ")))
            return "禁止命令/进程替换与网络请求组合使用";

        // 3. 封禁模式（子串匹配，用于明确灾难性片段）
        foreach (var pattern in options.BlockedPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            if (lower.Contains(pattern.ToLowerInvariant()))
                return $"禁止执行危险命令模式: {pattern}";
        }

        // 4. 命令名封禁（剥离 sudo/env 与所有选项令牌）
        var cmdName = GetCommandName(lower);
        if (cmdName != null && IsBlockedCommand(cmdName, options.BlockedCommands))
            return $"禁止执行危险命令: {cmdName}";

        // 5. 删除目标检查（对管道各段分别检查）
        foreach (var segment in lower.Split('|'))
        {
            var deleteTarget = CheckDestructiveDelete(segment);
            if (deleteTarget != null)
                return $"禁止递归删除根/家/当前目录: {deleteTarget}";
        }

        return null;
    }

    /// <summary>cmd 风格选项：/ 后跟单个字母（如 /s、/q、/f）。排除 /*、/bin 等路径形态</summary>
    private static bool IsCmdOption(string t) =>
        t.Length == 2 && t[0] == '/' && char.IsLetter(t[1]) && t[1] != ':';

    /// <summary>敏感命令名集合（命令封禁 + 删除检查），用于选项参数消费前的守卫</summary>
    private static readonly HashSet<string> SensitiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "rd", "del", "erase",
        "dd", "mkfs", "format", "shutdown", "reboot", "halt", "poweroff", "init"
    };

    /// <summary>提取命令名：跳过 env 赋值（VAR=value）、sudo/env 前缀与选项令牌及其参数。
    /// 任何选项在消费参数前，先检查下一 token 是否为敏感命令，若是则停止消费，让敏感命令成为命令名。</summary>
    private static string? GetCommandName(string lowerCommand)
    {
        var tokens = lowerCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i].Trim('\'', '"');
            if (t is "sudo" or "env") continue;
            if (t.Contains('=')) continue; // 环境赋值 VAR=value / --key=value 一体式

            // 辅助：判断某 token 是否为敏感命令（含 mkfs. 子命令）
            bool IsSensitive(string tok)
            {
                if (SensitiveCommands.Contains(tok)) return true;
                foreach (var s in SensitiveCommands)
                    if (tok.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            bool NextIsSensitive() =>
                i + 1 < tokens.Length && IsSensitive(tokens[i + 1].Trim('\'', '"'));

            if (t.StartsWith("--"))
            {
                // 长选项（可选/必选参数）：仅当下一 token 非敏感命令时消费参数
                if (NextIsSensitive()) continue;
                if (i + 1 < tokens.Length) i++;
                continue;
            }
            if (t.Length > 1 && t[0] == '-')
            {
                // 必选参数短选项（-u/-g/-C/-D/-p/-T/-R/-a/-S，输入已小写化）：仅当下一 token 非敏感命令时消费参数
                if (t.StartsWith("-u") || t.StartsWith("-g") || t.StartsWith("-c") ||
                    t.StartsWith("-d") || t.StartsWith("-p") || t.StartsWith("-t") ||
                    t.StartsWith("-r") || t.StartsWith("-a") || t.StartsWith("-s"))
                {
                    if (t.Length > 2) continue; // -uroot 合并写法：本身是选项，不消费
                    if (NextIsSensitive()) continue; // 不消费敏感命令
                    if (i + 1 < tokens.Length) i++; // 消费参数
                    continue;
                }
                continue; // 其他短选项（-i、-n、-h 等）
            }
            if (IsCmdOption(t)) continue;
            return Path.GetFileName(t);
        }
        return null;
    }

    /// <summary>命令封禁匹配：支持 mkfs.ext4 等子命令前缀</summary>
    private static bool IsBlockedCommand(string cmdName, IEnumerable<string> blocked)
    {
        foreach (var bc in blocked)
        {
            if (string.IsNullOrWhiteSpace(bc)) continue;
            if (string.Equals(cmdName, bc, StringComparison.OrdinalIgnoreCase)) return true;
            if (cmdName.StartsWith(bc + ".", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>检查删除命令是否递归删除危险目标（根/家/当前/盘符根）。返回危险目标 token 或 null</summary>
    private static string? CheckDestructiveDelete(string lowerSegment)
    {
        var tokens = lowerSegment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmdName = GetCommandName(lowerSegment);
        if (cmdName is not ("rm" or "rmdir" or "rd" or "del" or "erase")) return null;

        var recursive = false;
        var targets = new List<string>();
        foreach (var raw in tokens)
        {
            var t = raw.Trim('\'', '"');
            if (t is "sudo" or "env") continue;
            if (t == cmdName) continue;
            if (t.StartsWith("--"))
            {
                if (t == "--recursive") recursive = true;
                continue;
            }
            if (t.Length > 1 && t[0] == '-')
            {
                if (t.Contains('r')) recursive = true; // 含 -fr、-irf、-fR 等组合短选项
                continue;
            }
            if (IsCmdOption(t))
            {
                if (t.Contains('s')) recursive = true; // cmd 风格 /s
                continue;
            }
            targets.Add(t);
        }

        if (!recursive) return null;
        foreach (var target in targets)
            if (IsDestructiveTarget(target)) return target;
        return null;
    }

    /// <summary>判断是否为危险删除目标：/、~、.、盘符根（C:、C:\、C:\*、C:/ 等）。先归一化尾部路径分隔符与通配符</summary>
    private static bool IsDestructiveTarget(string target)
    {
        var normalized = target.TrimEnd('\\', '/', '*');
        if (normalized.Length == 0 && target.StartsWith("/")) normalized = "/";
        if (normalized is "/" or "~" or ".") return true;
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            var rest = normalized.Length > 2 ? normalized[2..].Trim('\\', '/') : string.Empty;
            return rest.Length == 0;
        }
        return false;
    }
}
