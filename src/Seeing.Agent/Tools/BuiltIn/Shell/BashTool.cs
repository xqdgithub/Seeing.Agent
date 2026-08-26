using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Shell;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Shell
{
    /// <summary>
    /// Bash 工具 - Shell 命令执行工具
    /// </summary>
    /// <remarks>
    /// 执行 Shell 命令，支持跨平台（Windows: cmd/powershell/git-bash, Unix: bash/zsh/sh）。
    /// 提供超时控制、取消支持、进程树终止和流式输出收集。
    /// </remarks>
    public class BashTool : ToolBase
    {
        private const int DefaultTimeoutMs = 120_000; // 2 分钟
        private const int MaxMetadataLength = 30_000;

        private readonly IShellService _shellService;
        private readonly IShellEnvironmentService _shellEnvService;
        private readonly IWorkspaceProvider _workspace;
        private readonly IOptionsMonitor<SeeingAgentOptions> _options;

        /// <summary>工具 ID</summary>
        public override string Id => "bash";

        /// <summary>工具描述</summary>
        public override string Description =>
            "执行 Shell 命令。支持跨平台执行，提供超时控制和取消支持。" +
            $"当前运行环境：{BuildPlatformHint()}。" +
            "请使用与当前 Shell 语法匹配的命令。";

        /// <summary>参数 Schema</summary>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    description = "要执行的命令。" + $"当前环境：{BuildPlatformHint()}"
                },
                timeout = new
                {
                    type = "number",
                    description = "可选的超时时间（毫秒），默认 120000（2 分钟）"
                },
                workdir = new
                {
                    type = "string",
                    description = "工作目录。默认使用当前工作目录。请使用此参数而不是 'cd' 命令。"
                },
                description = new
                {
                    type = "string",
                    description = "命令用途的简明描述（5-10 字）。\n" +
                                  "示例:\n" +
                                  "输入: ls\n" +
                                  "输出: 列出当前目录文件\n\n" +
                                  "输入: git status\n" +
                                  "输出: 显示工作树状态\n\n" +
                                  "输入: npm install\n" +
                                  "输出: 安装包依赖\n\n" +
                                  "输入: mkdir foo\n" +
                                  "输出: 创建目录 'foo'"
                }
            },
            required = new[] { "command", "description" }
        });

        /// <summary>工具分类</summary>
        public override ToolCategory Category => ToolCategory.ExternalService;

        public BashTool(
            ILogger<BashTool> logger,
            IShellService shellService,
            IShellEnvironmentService shellEnvService,
            IWorkspaceProvider workspace,
            IOptionsMonitor<SeeingAgentOptions> options)
            : base(logger)
        {
            _shellService = shellService;
            _shellEnvService = shellEnvService;
            _workspace = workspace;
            _options = options;
        }

        /// <summary>
        /// 执行 Bash 命令
        /// </summary>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 解析参数
            var command = GetStringArgument(arguments, "command");
            var description = GetStringArgument(arguments, "description");
            var timeout = GetIntArgument(arguments, "timeout") ?? DefaultTimeoutMs;
            var workdir = GetStringArgument(arguments, "workdir") ?? _workspace.WorkspaceRoot;

            if (string.IsNullOrEmpty(command))
            {
                return Failure("command 参数是必需的");
            }

            if (string.IsNullOrEmpty(description))
            {
                description = command.Length > 50 ? command.Substring(0, 50) + "..." : command;
            }

            if (timeout < 0)
            {
                return Failure($"无效的超时值: {timeout}。超时必须是正数。");
            }

            _logger.LogInformation("执行命令: {Command}", command);
            _logger.LogDebug("工作目录: {Workdir}, 超时: {Timeout}ms", workdir, timeout);

            try
            {
                return await ExecuteCommandAsync(command, description, workdir, timeout, context);
            }
            catch (OperationCanceledException)
            {
                return Failure($"命令被取消: {command}");
            }
            catch (Exception ex)
            {
                return Failure($"{description}: {ex.Message}");
            }
        }

        /// <summary>
        /// 执行命令
        /// </summary>
        private async Task<ToolResult> ExecuteCommandAsync(
            string command,
            string description,
            string workdir,
            int timeout,
            ToolContext context)
        {
            var dangerCheck = DangerousCommandGuard.Check(command, _options.CurrentValue.Shell);
            if (dangerCheck != null)
            {
                _logger.LogWarning("拒绝危险命令: {Reason}, 命令: {Command}", dangerCheck, command);
                return Failure($"命令被拒绝执行: {dangerCheck}");
            }

            // 获取 Shell
            var shell = _shellService.GetAcceptableShell();
            _logger.LogInformation("使用 Shell: {Shell}", shell);

            // 触发 shell.env Hook 获取环境变量
            var envVars = await _shellEnvService.GetEnvironmentAsync(
                workdir,
                context.SessionId,
                context.CallId,
                context.CancellationToken);

            // 创建进程
            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = workdir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                // 统一按 UTF-8 编解码子进程 stdin/stdout，避免中文乱码
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // 设置环境变量
            foreach (var (key, value) in envVars)
            {
                startInfo.Environment[key] = value;
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var outputBuilder = new StringBuilder();
var timedOut = false;
            var aborted = false;

            // 设置超时计时器
            using var timeoutCts = new CancellationTokenSource(timeout + 100);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken,
                timeoutCts.Token);

            // 输出收集
            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    UpdateMetadata(context, outputBuilder, description);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    UpdateMetadata(context, outputBuilder, description);
                }
            };

            // 启动进程
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 对 PowerShell 统一 UTF-8 编解码：使其以 UTF-8 读取 stdin 命令、以 UTF-8 解码子进程（如 python）
            // 输出并编码自身输出，与 .NET 端 Standard*Encoding=UTF8 对齐，避免中文乱码
            var shellName = _shellService.GetShellName(shell);
            var commandToRun = command;
            if (shellName is "powershell" or "pwsh")
            {
                commandToRun = "[Console]::InputEncoding = [System.Text.Encoding]::UTF8;" +
                               "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8;" + command;
            }

            await process.StandardInput.WriteLineAsync(commandToRun);
            process.StandardInput.Close();

            // 初始元数据
            UpdateMetadata(context, outputBuilder, description);

            // 检查是否已取消
            if (linkedCts.Token.IsCancellationRequested)
            {
                aborted = true;
                await _shellService.KillProcessTreeAsync(process, CancellationToken.None);
            }

            // 等待进程退出
            try
            {
                await WaitForExitAsync(process, linkedCts.Token);

// 确保所有输出都已读取
                process.WaitForExit();  // 二次确认，等待异步输出完成

            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.Token.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
                {
                    timedOut = true;
                }
                else if (context.CancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                }

                // 终止进程树
                await _shellService.KillProcessTreeAsync(process, CancellationToken.None);

                // 等待进程真正退出
                try
                {
await WaitForExitAsync(process, CancellationToken.None);
                }
                catch
                {
                    // 忽略
                }
            }

            // 构建输出
            var output = outputBuilder.ToString();

            // 添加元数据信息
            var metadataLines = new List<string>();
            if (timedOut)
            {
                metadataLines.Add($"命令在超过超时时间 {timeout} 毫秒后被终止");
            }
            if (aborted)
            {
                metadataLines.Add("用户取消了命令");
            }

            if (metadataLines.Count > 0)
            {
                output += "\n\n<bash_metadata>\n" + string.Join("\n", metadataLines) + "\n</bash_metadata>";
            }

            // 截断输出以避免过长的数据
            var truncatedOutput = output.Length > MaxMetadataLength
                ? output.Substring(0, MaxMetadataLength) + "\n\n..."
                : output;

            _logger.LogInformation("命令执行完成，退出码: {ExitCode}", process.ExitCode);

            return Success(description, output, new Dictionary<string, object>
            {
                ["output"] = truncatedOutput,
                ["exit"] = process.ExitCode,
                ["description"] = description,
                ["timedOut"] = timedOut,
                ["aborted"] = aborted
            });
        }

        /// <summary>
        /// 更新元数据
        /// </summary>
        private void UpdateMetadata(ToolContext context, StringBuilder output, string description)
        {
            if (context.MetadataSink is not null)
            {
                var outputStr = output.ToString();
                var truncatedOutput = outputStr.Length > MaxMetadataLength
                    ? outputStr.Substring(0, MaxMetadataLength) + "\n\n..."
                    : outputStr;

                context.MetadataSink.SetMetadata("bash_output", new Dictionary<string, object>
                {
                    ["output"] = truncatedOutput,
                    ["description"] = description
                });
            }
        }

        /// <summary>
        /// 构建平台提示：操作系统 + 实际使用的 Shell
        /// </summary>
        private string BuildPlatformHint()
        {
            var platform = DescribePlatform();
            var shell = DescribeShell();
            return $"{platform}，Shell: {shell}";
        }

        private static string DescribePlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
            return RuntimeInformation.OSDescription;
        }

        private string DescribeShell()
        {
            try
            {
                var shell = _shellService.GetAcceptableShell();
                if (string.IsNullOrWhiteSpace(shell)) return "未知";
                var name = _shellService.GetShellName(shell);
                return $"{name}（{shell}）";
            }
            catch
            {
                return "未知";
            }
        }

        /// <summary>
        /// 异步等待进程退出
        /// </summary>
        private static Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();

            process.Exited += (s, e) => tcs.TrySetResult(true);

            if (process.HasExited)
            {
                tcs.TrySetResult(true);
            }

            // 注册取消回调
            cancellationToken.Register(() =>
            {
                tcs.TrySetCanceled(cancellationToken);
            });

            return tcs.Task;
        }
    }
}