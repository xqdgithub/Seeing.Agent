using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seeing.Agent.Tools.BuiltIn.Shell;

/// <summary>
/// 默认 Shell 服务实现。
/// </summary>
public sealed class DefaultShellService : IShellService
{
    private readonly ILogger<DefaultShellService> _logger;
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private string? _acceptableShell;

    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase) { "fish", "nu" };

    public DefaultShellService(ILogger<DefaultShellService> logger, IOptionsMonitor<SeeingAgentOptions> options)
    {
        _logger = logger;
        _options = options;
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
            return "[Console]::InputEncoding = [System.Text.Encoding]::UTF8;" +
                   "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;" + command;
        }

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
            foreach (var name in _options.CurrentValue.Shell.PreferredShells)
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
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = finder,
                Arguments = name,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            });
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
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
