using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using System.Runtime.InteropServices;

namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// 默认 Shell 服务实现。可执行文件发现经 <see cref="IExecutionWorld.Subprocess"/>，不直接 Process.Start。
/// </summary>
public sealed class DefaultShellService : IShellService
{
    private readonly ILogger<DefaultShellService> _logger;
    private readonly IOptionsMonitor<ShellOptions> _options;
    private readonly IExecutionWorld _world;
    private string? _acceptableShell;

    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase) { "fish", "nu" };

    public DefaultShellService(
        ILogger<DefaultShellService> logger,
        IOptionsMonitor<ShellOptions> options,
        IExecutionWorld world)
    {
        _logger = logger;
        _options = options;
        _world = world;
    }

    public string SelectShell()
    {
        if (_acceptableShell != null) return _acceptableShell;

        var shellEnv = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shellEnv) && !Blacklist.Contains(GetShellName(shellEnv)))
        {
            _acceptableShell = shellEnv;
            return _acceptableShell;
        }

        _acceptableShell = GetFallbackShell();
        return _acceptableShell;
    }

    public string PrepareCommand(string shell, string command)
    {
        var shellName = GetShellName(shell);
        if (shellName is "powershell" or "pwsh")
        {
            // PlainText：禁止 Get-ChildItem 等把 ANSI 颜色码写进重定向输出（WebUI 会当乱码展示）
            return "[Console]::InputEncoding = [System.Text.Encoding]::UTF8;" +
                   "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;" +
                   "$OutputEncoding = [System.Text.Encoding]::UTF8;" +
                   "if (Get-Variable -Name PSStyle -ErrorAction SilentlyContinue) { $PSStyle.OutputRendering = 'PlainText' };" +
                   command;
        }

        // cmd 默认系统代码页；切到 UTF-8，与 BashTool 的 UTF-8 解码一致
        if (shellName == "cmd")
            return "chcp 65001 >nul & " + command;

        return command;
    }

    public string BuildArguments(string shell, string preparedCommand)
    {
        return GetShellName(shell) switch
        {
            "cmd" => $"/c \"{EscapeDoubleQuotes(preparedCommand)}\"",
            "powershell" or "pwsh" => $"-Command \"{EscapeDoubleQuotes(preparedCommand)}\"",
            _ => $"-c '{EscapeSingleQuotes(preparedCommand)}'"
        };
    }

    public string GetShellName(string shellPath)
    {
        try { return Path.GetFileNameWithoutExtension(shellPath).ToLowerInvariant(); }
        catch { return shellPath.ToLowerInvariant(); }
    }

    private string GetFallbackShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var name in _options.CurrentValue.PreferredShells)
            {
                var found = name.ToLowerInvariant() switch
                {
                    "pwsh" or "powershell" => FindExecutable(name),
                    "bash" => GetGitBashPath() ?? FindExecutable("bash"),
                    "cmd" => Environment.GetEnvironmentVariable("COMSPEC") ?? FindExecutable("cmd"),
                    _ => FindExecutable(name),
                };
                if (!string.IsNullOrEmpty(found)) return found;
            }

            return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "/bin/zsh";
        return FindExecutable("bash") ?? "/bin/sh";
    }

    private string? GetGitBashPath()
    {
        var gitBashEnv = Environment.GetEnvironmentVariable("OPENCODE_GIT_BASH_PATH");
        if (!string.IsNullOrEmpty(gitBashEnv) && File.Exists(gitBashEnv)) return gitBashEnv;

        var gitPath = FindExecutable("git");
        if (string.IsNullOrEmpty(gitPath)) return null;

        try
        {
            var gitDir = Path.GetDirectoryName(gitPath);
            var parentDir = gitDir != null ? Path.GetDirectoryName(gitDir) : null;
            if (parentDir != null)
            {
                var bashPath = Path.Combine(parentDir, "bin", "bash.exe");
                if (File.Exists(bashPath)) return bashPath;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "查找 Git Bash 路径时出错"); }

        return null;
    }

    private string? FindExecutable(string name)
    {
        try
        {
            var finder = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            using var process = _world.Subprocess.Start(new SubprocessSpec
            {
                FileName = finder,
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            var output = process.StandardOutput.ReadToEnd();
            // SelectShell 为同步接口（BashTool.Description/Schema getter 依赖），无法异步化；
            // 此处读取 ExitCode 按其契约在进程未退出时阻塞等待，避免 WaitForExitAsync().GetAwaiter().GetResult() 同步包装。
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                return output.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "查找可执行文件 {Name} 时出错", name); }

        return null;
    }

    private static string EscapeDoubleQuotes(string value) => value.Replace("\"", "\\\"");
    private static string EscapeSingleQuotes(string value) => value.Replace("'", "'\\''");
}
