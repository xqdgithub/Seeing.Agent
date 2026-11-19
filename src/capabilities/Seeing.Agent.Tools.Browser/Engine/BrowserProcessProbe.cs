using System.Diagnostics;
using System.Globalization;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 操作系统里某个进程的快照（只保留判定「谁占着 profile」需要的字段）。
/// </summary>
/// <param name="Pid">进程 PID。</param>
/// <param name="ParentPid">父进程 PID；读不到时为 0（含义是「未知」，不是「无父进程」）。</param>
/// <param name="ExecutablePath">可执行文件路径；读不到时为空字符串。</param>
/// <param name="CommandLine">完整命令行；读不到时为空字符串。</param>
public sealed record BrowserProcessInfo(int Pid, int ParentPid, string ExecutablePath, string CommandLine);

/// <summary>
/// 按 <c>--user-data-dir</c> 查「谁正在使用这个 profile」的跨平台进程探测。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：宿主启动时若 Chromium 报
/// <c>The browser is already running for ...</c>，只有两条正确出路 ——
/// 结束「父进程已经死掉的孤儿」（上次宿主崩溃留下的），或明确拒绝并告诉用户是哪个 PID 占着。
/// 两者都需要知道「哪个进程的命令行里带这个 profile 目录」。
/// </para>
/// <para>
/// <b>为什么探测失败一律返回空列表</b>：它只用于恢复与诊断。拿不到信息时宿主会走
/// 「保守拒绝」而不是误杀别人的浏览器 —— 宁可让用户看到一条明确的报错，
/// 也不能因为一次 WMI 超时就去 <c>Kill(entireProcessTree)</c> 一个他正在用的窗口。
/// </para>
/// <para>
/// <b>实现按平台分流</b>：Windows 走 WMI（<c>Win32_Process</c>，只有它能给出完整命令行），
/// Linux 读 <c>/proc</c>，macOS 调 <c>ps</c>（macOS 没有可用的 procfs，而 <c>ps</c> 恒在）。
/// </para>
/// </remarks>
public static class BrowserProcessProbe
{
    /// <summary>Chromium 指定 profile 目录的开关。</summary>
    private const string UserDataDirSwitch = "--user-data-dir";

    /// <summary>
    /// 找出命令行里使用了指定 profile 目录的进程。
    /// </summary>
    /// <param name="profileDirectory">profile 目录（相对路径按当前目录转绝对）。</param>
    /// <returns>匹配的进程；探测不可用或没有匹配时为空列表。</returns>
    public static IReadOnlyList<BrowserProcessInfo> FindByProfileDirectory(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
            return [];

        var processes = EnumerateProcesses();
        if (processes.Count == 0)
            return [];

        var matches = new List<BrowserProcessInfo>();
        foreach (var process in processes)
        {
            if (CommandLineUsesProfile(process.CommandLine, profileDirectory))
                matches.Add(process);
        }

        return matches;
    }

    /// <summary>
    /// 判断一条命令行是否以指定目录作为 <c>--user-data-dir</c>。
    /// </summary>
    /// <param name="commandLine">完整命令行（可能带引号）。</param>
    /// <param name="profileDirectory">期望的 profile 目录。</param>
    /// <remarks>
    /// <para>
    /// <b>只认 <c>--user-data-dir=</c> 这个开关，不做子串匹配</b>：子串匹配会把
    /// <c>--user-data-dir=C:\p\profile-backup</c> 也当成同一个目录，
    /// 进而误杀一个只是路径前缀相同的浏览器。
    /// </para>
    /// <para>
    /// 比较前双方都规范化（绝对路径 + 去尾分隔符）；Windows / macOS 忽略大小写，Linux 保留。
    /// </para>
    /// </remarks>
    public static bool CommandLineUsesProfile(string commandLine, string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(profileDirectory))
            return false;

        string expected;
        try
        {
            expected = BrowserHostLocator.NormalizeProfileDirectory(profileDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (var value in ExtractUserDataDirValues(commandLine))
        {
            string normalized;
            try
            {
                normalized = BrowserHostLocator.NormalizeProfileDirectory(value);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (string.Equals(normalized, expected, comparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 从命令行里逐个取出 <c>--user-data-dir=</c> 的值（支持单/双引号与裸值）。
    /// </summary>
    private static IEnumerable<string> ExtractUserDataDirValues(string commandLine)
    {
        var index = 0;
        while (index < commandLine.Length)
        {
            var switchIndex = commandLine.IndexOf(UserDataDirSwitch, index, StringComparison.OrdinalIgnoreCase);
            if (switchIndex < 0)
                yield break;

            var cursor = switchIndex + UserDataDirSwitch.Length;

            // 允许 `--user-data-dir = <path>`（虽然 Chromium 自己不会写，但手工拼的参数可能有空格）。
            while (cursor < commandLine.Length && commandLine[cursor] == ' ')
                cursor++;

            // 形如 `--user-data-dir-foo`：不是我们要的开关，继续往后找。
            if (cursor >= commandLine.Length || commandLine[cursor] != '=')
            {
                index = cursor;
                continue;
            }

            cursor++;
            while (cursor < commandLine.Length && commandLine[cursor] == ' ')
                cursor++;

            if (cursor >= commandLine.Length)
                yield break;

            var quote = commandLine[cursor];
            if (quote is '"' or '\'')
            {
                var end = commandLine.IndexOf(quote, cursor + 1);
                if (end < 0)
                    yield break;

                yield return commandLine[(cursor + 1)..end];
                index = end + 1;
                continue;
            }

            var valueEnd = commandLine.IndexOf(' ', cursor);
            if (valueEnd < 0)
            {
                yield return commandLine[cursor..];
                yield break;
            }

            yield return commandLine[cursor..valueEnd];
            index = valueEnd;
        }
    }

    /// <summary>按平台枚举全部进程；任何平台级失败都退化为空列表（见类型注释）。</summary>
    private static List<BrowserProcessInfo> EnumerateProcesses()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return EnumerateWindowsProcesses();

            if (OperatingSystem.IsLinux())
                return EnumerateLinuxProcesses();

            if (OperatingSystem.IsMacOS())
                return EnumerateMacProcesses();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 探测只是诊断辅助：WMI 服务被禁用 / procfs 不可读 / ps 不存在都不该让宿主起不来。
            Debug.WriteLine($"浏览器进程探测失败（按「没有占用者」处理）：{ex.Message}");
        }

        return [];
    }

#if BROWSER_WMI
    /// <summary>Windows：WMI <c>Win32_Process</c>（只有它能一次给出命令行与父进程）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<BrowserProcessInfo> EnumerateWindowsProcesses()
    {
        var results = new List<BrowserProcessInfo>();

        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine FROM Win32_Process");

        foreach (System.Management.ManagementBaseObject item in searcher.Get())
        {
            if (item is not System.Management.ManagementObject process)
                continue;

            try
            {
                var pid = ToInt32(process["ProcessId"]);
                if (pid <= 0)
                    continue;

                results.Add(new BrowserProcessInfo(
                    pid,
                    ToInt32(process["ParentProcessId"]),
                    process["ExecutablePath"] as string ?? string.Empty,
                    process["CommandLine"] as string ?? string.Empty));
            }
            catch (Exception ex) when (ex is System.Management.ManagementException
                                           or FormatException
                                           or InvalidCastException
                                           or InvalidOperationException)
            {
                // 受保护进程（系统服务、另一个用户）读属性会失败：跳过这一条即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    private static int ToInt32(object? value)
        => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
#endif

    /// <summary>Linux：<c>/proc/&lt;pid&gt;/{cmdline,stat,exe}</c>。</summary>
    private static List<BrowserProcessInfo> EnumerateLinuxProcesses()
    {
        var results = new List<BrowserProcessInfo>();
        if (!Directory.Exists("/proc"))
            return results;

        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                continue;

            try
            {
                // cmdline 用 \0 分隔参数；替换成空格只是为了让日志与判定可读（路径里不会有 \0）。
                var commandLine = File.ReadAllText(Path.Combine(directory, "cmdline"))
                    .Replace('\0', ' ')
                    .TrimEnd();

                if (commandLine.Length == 0)
                    continue;

                results.Add(new BrowserProcessInfo(
                    pid,
                    ReadLinuxParentPid(directory),
                    TryReadSymbolicLink(Path.Combine(directory, "exe")),
                    commandLine));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 进程在枚举期间退出 / 属于别的用户：跳过。
            }
        }

        return results;
    }

    /// <summary>
    /// 读 <c>/proc/&lt;pid&gt;/stat</c> 的父 PID。
    /// </summary>
    /// <remarks>
    /// 第二个字段是 <c>(comm)</c>，进程名里可以有空格与右括号，
    /// 所以只能从**最后一个** <c>)</c> 之后开始切分，不能按空格整体 split。
    /// </remarks>
    private static int ReadLinuxParentPid(string processDirectory)
    {
        var stat = File.ReadAllText(Path.Combine(processDirectory, "stat"));
        var close = stat.LastIndexOf(')');
        if (close < 0)
            return 0;

        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // fields[0] = state，fields[1] = ppid
        return fields.Length > 1
               && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid)
            ? parentPid
            : 0;
    }

    private static string TryReadSymbolicLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>macOS：<c>ps -axo pid=,ppid=,args=</c>（macOS 没有 procfs）。</summary>
    private static List<BrowserProcessInfo> EnumerateMacProcesses()
    {
        var results = new List<BrowserProcessInfo>();

        var startInfo = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-axo");
        startInfo.ArgumentList.Add("pid=,ppid=,args=");

        using var process = Process.Start(startInfo);
        if (process is null)
            return results;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
            {
                continue;
            }

            // ps 的 args 是完整命令行（含可执行文件本身），ExecutablePath 不单独解析：
            // 恢复逻辑只关心命令行里的 --user-data-dir。
            results.Add(new BrowserProcessInfo(pid, parentPid, string.Empty, parts[2]));
        }

        return results;
    }
}
