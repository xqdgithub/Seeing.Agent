using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Shell;
using System.Diagnostics;
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

        /// <summary>工具 ID</summary>
        public override string Id => "bash";

        /// <summary>工具描述</summary>
        public override string Description =>
            "执行 Shell 命令。支持跨平台执行，提供超时控制和取消支持。" +
            "重要提示：请谨慎使用危险命令（如 rm、删除文件等）。";

        /// <summary>参数 Schema</summary>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new
                {
                    type = "string",
                    description = "要执行的命令"
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
        public ToolCategory Category => ToolCategory.ExternalService;

        public BashTool(
            ILogger<BashTool> logger,
            IShellService shellService,
            IShellEnvironmentService shellEnvService)
            : base(logger)
        {
            _shellService = shellService;
            _shellEnvService = shellEnvService;
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
            var workdir = GetStringArgument(arguments, "workdir") ?? Environment.CurrentDirectory;

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
            // 权限检查 - 执行命令前需要确认
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "bash",
                    Patterns = new List<string> { command },
                    Metadata = new Dictionary<string, object>
                    {
                        ["command"] = command,
                        ["workdir"] = workdir,
                        ["timeout"] = timeout,
                        ["description"] = description
                    }
                });
            }

            // 获取 Shell
            var shell = _shellService.GetAcceptableShell();
            var argFormat = _shellService.GetShellArgumentFormat(shell);
            _logger.LogInformation("使用 Shell: {Shell}, 参数格式: {ArgFormat}", shell, argFormat);

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
                Arguments = string.Format(argFormat, command),
                WorkingDirectory = workdir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
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
            var exited = false;

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

            // 关闭标准输入，防止某些程序等待输入
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

                exited = true;
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
                    exited = true;
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
            if (context.SetMetadata != null)
            {
                var outputStr = output.ToString();
                var truncatedOutput = outputStr.Length > MaxMetadataLength
                    ? outputStr.Substring(0, MaxMetadataLength) + "\n\n..."
                    : outputStr;

                context.SetMetadata("bash_output", new Dictionary<string, object>
                {
                    ["output"] = truncatedOutput,
                    ["description"] = description
                });
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