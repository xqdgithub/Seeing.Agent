using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Shell;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Shell
{
    public class BashTool : ToolBase
    {
        private const int DefaultTimeoutMs = 120_000;
        private const int MaxMetadataLength = 30_000;

        private readonly IExecutionWorld _world;
        private readonly IShellService _shellService;
        private readonly IShellEnvironmentService _shellEnvService;
        private readonly IOptionsMonitor<SeeingAgentOptions> _options;

        public override string Id => "bash";

        public override string Description =>
            "执行 Shell 命令。支持跨平台执行，提供超时控制和取消支持。" +
            $"当前运行环境：{BuildPlatformHint()}。" +
            "请使用与当前 Shell 语法匹配的命令。";

        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "要执行的命令。" + $"当前环境：{BuildPlatformHint()}" },
                timeout = new { type = "number", description = "可选超时时间（正整数毫秒），默认 120000（2 分钟）" },
                workdir = new { type = "string", description = "工作目录。默认使用当前工作目录。" },
                description = new { type = "string", description = "命令用途的简明描述（5-10 字）。" }
            },
            required = new[] { "command", "description" }
        });

        public override ToolCategory Category => ToolCategory.ExternalService;

        public BashTool(
            ILogger<BashTool> logger,
            IExecutionWorld world,
            IShellService shellService,
            IShellEnvironmentService shellEnvService,
            IOptionsMonitor<SeeingAgentOptions> options)
            : base(logger)
        {
            _world = world;
            _shellService = shellService;
            _shellEnvService = shellEnvService;
            _options = options;
        }

        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var command = GetStringArgument(arguments, "command");
            var description = GetStringArgument(arguments, "description");
            var timeout = GetIntArgument(arguments, "timeout") ?? DefaultTimeoutMs;
            var workdir = GetStringArgument(arguments, "workdir") ?? _world.Cwd;

            if (string.IsNullOrEmpty(command)) return Failure("command 参数是必需的");
            if (string.IsNullOrEmpty(description)) description = command.Length > 50 ? command[..50] + "..." : command;
            if (timeout <= 0) return Failure($"无效的超时值: {timeout}。超时必须是正整数毫秒（timeout=0 无意义）。");

            try { return await ExecuteCommandAsync(command, description, workdir, timeout, context); }
            catch (OperationCanceledException) { return Failure($"命令被取消: {command}"); }
            catch (Exception ex) { return Failure($"{description}: {ex.Message}"); }
        }

        private async Task<ToolResult> ExecuteCommandAsync(
            string command, string description, string workdir, int timeout, ToolContext context)
        {
            var dangerCheck = DangerousCommandGuard.Check(command, _options.CurrentValue.Shell);
            if (dangerCheck != null) return Failure($"命令被拒绝执行: {dangerCheck}");

            var shell = _shellService.SelectShell();
            var envVars = await _shellEnvService.GetEnvironmentAsync(
                workdir, context.SessionId, context.CallId, context.CancellationToken);

            var prepared = _shellService.PrepareCommand(shell, command);
            var args = _shellService.BuildArguments(shell, prepared);

            var spec = new SubprocessSpec
            {
                FileName = shell,
                Arguments = args,
                WorkingDirectory = workdir,
                Environment = envVars.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Encoding = Encoding.UTF8,
            };

            using var subprocess = _world.Subprocess.Start(spec);
            var outputBuilder = new StringBuilder();
            var timedOut = false;
            var aborted = false;

            using var timeoutCts = new CancellationTokenSource(timeout + 100);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken, timeoutCts.Token);

            var stdoutTask = PumpStreamAsync(subprocess.StandardOutput, outputBuilder, context, description, linkedCts.Token);
            var stderrTask = PumpStreamAsync(subprocess.StandardError, outputBuilder, context, description, linkedCts.Token);
            UpdateMetadata(context, outputBuilder, description);

            if (linkedCts.Token.IsCancellationRequested)
            {
                aborted = true;
                subprocess.Kill();
            }
            else
            {
                try { await subprocess.WaitForExitAsync(linkedCts.Token); }
                catch (OperationCanceledException)
                {
                    timedOut = timeoutCts.Token.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested;
                    aborted = context.CancellationToken.IsCancellationRequested;
                    subprocess.Kill();
                    try { await subprocess.WaitForExitAsync(CancellationToken.None); } catch { }
                }
            }

            linkedCts.Cancel();
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch (OperationCanceledException) { }

            var output = outputBuilder.ToString();
            var metadataLines = new List<string>();
            if (timedOut) metadataLines.Add($"命令在超过超时时间 {timeout} 毫秒后被终止");
            if (aborted) metadataLines.Add("用户取消了命令");
            if (metadataLines.Count > 0)
                output += "\n\n<bash_metadata>\n" + string.Join("\n", metadataLines) + "\n</bash_metadata>";

            return Success(description, output, new Dictionary<string, object>
            {
                ["exit"] = subprocess.ExitCode,
                ["description"] = description,
                ["timedOut"] = timedOut,
                ["aborted"] = aborted
            });
        }

        private static async Task PumpStreamAsync(
            TextReader reader, StringBuilder output, ToolContext context, string description, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (line == null) break;
                output.AppendLine(line);
                UpdateMetadataStatic(context, output, description);
            }
        }

        private void UpdateMetadata(ToolContext context, StringBuilder output, string description) =>
            UpdateMetadataStatic(context, output, description);

        private static void UpdateMetadataStatic(ToolContext context, StringBuilder output, string description)
        {
            if (context.MetadataSink is null) return;
            var s = output.ToString();
            context.MetadataSink.SetMetadata("bash_output", new Dictionary<string, object>
            {
                ["output"] = s.Length > MaxMetadataLength ? s[..MaxMetadataLength] + "\n\n..." : s,
                ["description"] = description
            });
        }

        private string BuildPlatformHint() => $"{DescribePlatform()}，Shell: {DescribeShell()}";

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
                var shell = _shellService.SelectShell();
                return string.IsNullOrWhiteSpace(shell) ? "未知" : $"{_shellService.GetShellName(shell)}（{shell}）";
            }
            catch { return "未知"; }
        }
    }
}
