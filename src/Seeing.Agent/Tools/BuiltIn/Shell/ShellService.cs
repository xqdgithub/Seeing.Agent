using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Seeing.Agent.Tools.BuiltIn.Shell
{
    /// <summary>
    /// Shell 服务接口 - 提供跨平台 Shell 选择和进程管理
    /// </summary>
    public interface IShellService
    {
        /// <summary>
        /// 获取首选 Shell（优先使用环境变量 SHELL）
        /// </summary>
        string GetPreferredShell();

        /// <summary>
        /// 获取可接受的 Shell（排除不兼容的 Shell 如 fish）
        /// </summary>
        string GetAcceptableShell();

        /// <summary>
        /// 获取 Shell 执行命令的参数格式
        /// </summary>
        /// <param name="shell">Shell 路径</param>
        /// <returns>参数格式字符串，{0} 会被替换为命令</returns>
        string GetShellArgumentFormat(string shell);

        /// <summary>
        /// 终止进程树（包括所有子进程）
        /// </summary>
        Task KillProcessTreeAsync(Process process, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Shell 服务实现 - 跨平台 Shell 选择和进程管理
    /// </summary>
    public class ShellService : IShellService
    {
        private readonly ILogger<ShellService> _logger;
        private string? _preferredShell;
        private string? _acceptableShell;

        /// <summary>
        /// 不兼容的 Shell 黑名单
        /// </summary>
        private static readonly HashSet<string> Blacklist = new HashSet<string>
        {
            "fish", "nu"  // fish 和 nushell 与某些命令格式不兼容
        };

        public ShellService(ILogger<ShellService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 获取首选 Shell
        /// </summary>
        public string GetPreferredShell()
        {
            if (_preferredShell != null)
            {
                return _preferredShell;
            }

            // 优先使用环境变量 SHELL
            var shellEnv = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(shellEnv))
            {
                _preferredShell = shellEnv;
                _logger.LogDebug("使用环境变量 SHELL: {Shell}", shellEnv);
                return _preferredShell;
            }

            // 使用回退 Shell
            _preferredShell = GetFallbackShell();
            _logger.LogDebug("使用回退 Shell: {Shell}", _preferredShell);
            return _preferredShell;
        }

        /// <summary>
        /// 获取可接受的 Shell（排除黑名单中的 Shell）
        /// </summary>
        public string GetAcceptableShell()
        {
            if (_acceptableShell != null)
            {
                return _acceptableShell;
            }

            // 检查环境变量 SHELL 是否在黑名单中
            var shellEnv = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(shellEnv))
            {
                var shellName = GetShellName(shellEnv);
                if (!Blacklist.Contains(shellName))
                {
                    _acceptableShell = shellEnv;
                    _logger.LogDebug("使用可接受的 Shell: {Shell}", shellEnv);
                    return _acceptableShell;
                }

                _logger.LogDebug("Shell {Shell} 在黑名单中，使用回退 Shell", shellEnv);
            }

            // 使用回退 Shell
            _acceptableShell = GetFallbackShell();
            _logger.LogDebug("使用回退 Shell: {Shell}", _acceptableShell);
            return _acceptableShell;
        }

        /// <summary>
        /// 终止进程树
        /// </summary>
        public async Task KillProcessTreeAsync(Process process, CancellationToken cancellationToken = default)
        {
            if (process == null || process.HasExited)
            {
                return;
            }

            _logger.LogDebug("终止进程树: PID {Pid}", process.Id);
            await process.KillTreeAsync(cancellationToken);
        }

        /// <summary>
        /// 获取回退 Shell（平台特定）
        /// </summary>
        private string GetFallbackShell()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows 平台：尝试 Git Bash，回退到 CMD 或 PowerShell
                var gitBashPath = GetGitBashPath();
                if (!string.IsNullOrEmpty(gitBashPath))
                {
                    return gitBashPath;
                }

                // 回退到 COMSPEC（通常是 cmd.exe）或 PowerShell
                var comspec = Environment.GetEnvironmentVariable("COMSPEC");
                if (!string.IsNullOrEmpty(comspec))
                {
                    return comspec;
                }

                // 尝试 PowerShell
                if (File.Exists("powershell.exe"))
                {
                    return "powershell.exe";
                }

                return "cmd.exe";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS: 默认 zsh
                return "/bin/zsh";
            }

            // Linux/Unix: 尝试 bash，回退到 sh
            var bashPath = FindExecutable("bash");
            if (!string.IsNullOrEmpty(bashPath))
            {
                return bashPath;
            }

            return "/bin/sh";
        }

        /// <summary>
        /// 获取 Shell 执行命令的参数格式
        /// </summary>
        public string GetShellArgumentFormat(string shell)
        {
            var shellName = GetShellName(shell);

            return shellName switch
            {
                "cmd" => "/c \"{0}\"",
                "powershell" or "pwsh" => "-Command \"{0}\"",
                "bash" or "sh" or "zsh" or "dash" => "-c \"{0}\"",
                "fish" => "-c \"{0}\"",
                _ => "-c \"{0}\""  // 默认使用 Unix 风格
            };
        }

        /// <summary>
        /// 获取 Git Bash 路径（Windows）
        /// </summary>
        private string? GetGitBashPath()
        {
            // 检查环境变量
            var gitBashEnv = Environment.GetEnvironmentVariable("OPENCODE_GIT_BASH_PATH");
            if (!string.IsNullOrEmpty(gitBashEnv) && File.Exists(gitBashEnv))
            {
                return gitBashEnv;
            }

            // 尝试查找 git.exe 并推断 bash.exe 位置
            var gitPath = FindExecutable("git");
            if (!string.IsNullOrEmpty(gitPath))
            {
                // git.exe 通常在: C:\Program Files\Git\cmd\git.exe
                // bash.exe 在: C:\Program Files\Git\bin\bash.exe
                try
                {
                    var gitDir = Path.GetDirectoryName(gitPath);
                    if (gitDir != null)
                    {
                        // cmd 目录的父目录通常有 bin 目录
                        var parentDir = Path.GetDirectoryName(gitDir);
                        if (parentDir != null)
                        {
                            var bashPath = Path.Combine(parentDir, "bin", "bash.exe");
                            if (File.Exists(bashPath))
                            {
                                return bashPath;
                            }
                        }

                        // 也可能在 usr/bin
                        var usrBinBash = Path.Combine(parentDir ?? gitDir, "usr", "bin", "bash.exe");
                        if (File.Exists(usrBinBash))
                        {
                            return usrBinBash;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "查找 Git Bash 路径时出错");
                }
            }

            return null;
        }

        /// <summary>
        /// 查找可执行文件路径
        /// </summary>
        private string? FindExecutable(string name)
        {
            try
            {
                // 使用 where（Windows）或 which（Unix）查找
                var finder = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = finder,
                    Arguments = name,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                });

                if (process == null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    // 返回第一个匹配的路径
                    var lines = output.Split('\n', '\r');
                    return lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "查找可执行文件 {Name} 时出错", name);
            }

            return null;
        }

        /// <summary>
        /// 获取 Shell 名称（从路径中提取）
        /// </summary>
        private static string GetShellName(string shellPath)
        {
            try
            {
                return Path.GetFileNameWithoutExtension(shellPath).ToLowerInvariant();
            }
            catch
            {
                return shellPath.ToLowerInvariant();
            }
        }
    }
}