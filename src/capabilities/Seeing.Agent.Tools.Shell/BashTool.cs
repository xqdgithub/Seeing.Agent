using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Core.Tools.Support;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Core.Tools.Shell;

/// <summary>
/// 执行 Shell 命令的 bash 工具。
/// </summary>
public class BashTool : ToolBase
{
    private const int DefaultTimeoutMs = 120_000;
    private const int MaxMetadataLength = 30_000;
    private const int StreamEmitMinIntervalMs = 100;

    private readonly IExecutionWorld _world;
    private readonly IShellService _shellService;
    private readonly IShellEnvironmentService _shellEnvService;
    private readonly IOptionsMonitor<ShellOptions> _options;

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
        IOptionsMonitor<ShellOptions> options)
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
        var dangerCheck = DangerousCommandGuard.Check(command, _options.CurrentValue);
        if (dangerCheck != null) return Failure($"命令被拒绝执行: {dangerCheck}");

        var shell = _shellService.SelectShell();
        var envVars = await _shellEnvService.GetEnvironmentAsync(
            workdir, context.SessionId, context.CallId, context.CancellationToken);

        var prepared = _shellService.PrepareCommand(shell, command);
        var args = _shellService.BuildArguments(shell, prepared);
        var environment = EnsureUtf8OutputEnvironment(envVars);

        var spec = new SubprocessSpec
        {
            FileName = shell,
            Arguments = args,
            WorkingDirectory = workdir,
            Environment = environment,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Encoding = Encoding.UTF8,
        };

        using var subprocess = _world.Subprocess.Start(spec);
        var outputBuilder = new StringBuilder();
        var gate = new StreamEmitGate();
        var timedOut = false;
        var aborted = false;

        using var timeoutCts = new CancellationTokenSource(timeout + 100);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken, timeoutCts.Token);

        var stdoutTask = PumpStreamAsync(subprocess.StandardOutput, outputBuilder, gate, context, description, linkedCts.Token);
        var stderrTask = PumpStreamAsync(subprocess.StandardError, outputBuilder, gate, context, description, linkedCts.Token);
        await PublishProgressAsync(context, description, outputBuilder, gate, force: true);

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

        await PublishProgressAsync(context, description, outputBuilder, gate, force: true);

        var output = AnsiEscape.Strip(outputBuilder.ToString());
        var metadataLines = new List<string>();
        if (timedOut) metadataLines.Add($"命令在超过超时时间 {timeout} 毫秒后被终止");
        if (aborted) metadataLines.Add("用户取消了命令");
        // <bash_metadata> 仅面向 LLM 可读附录；UI 以 ToolResult.Metadata 为真源并 strip 该块
        if (metadataLines.Count > 0)
            output += "\n\n<bash_metadata>\n" + string.Join("\n", metadataLines) + "\n</bash_metadata>";

        var metadata = new Dictionary<string, object>
        {
            ["exit"] = subprocess.ExitCode,
            ["description"] = description,
            ["timedOut"] = timedOut,
            ["aborted"] = aborted
        };

        if (aborted)
        {
            return new ToolResult
            {
                Success = false,
                Title = description,
                Output = output,
                Error = "已取消",
                Metadata = metadata
            };
        }

        return Success(description, output, metadata);
    }

    private async Task PumpStreamAsync(
        TextReader reader,
        StringBuilder output,
        StreamEmitGate gate,
        ToolContext context,
        string description,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (line == null) break;

            lock (gate.Sync)
            {
                output.AppendLine(AnsiEscape.Strip(line));
            }

            await PublishProgressAsync(context, description, output, gate, force: false);
        }
    }

    private async Task PublishProgressAsync(
        ToolContext context,
        string description,
        StringBuilder output,
        StreamEmitGate gate,
        bool force)
    {
        string snapshot;
        lock (gate.Sync)
        {
            snapshot = output.ToString();
            if (!force && !gate.ShouldEmitUnlocked())
                return;
            gate.MarkEmittedUnlocked();
        }

        var truncated = snapshot.Length > MaxMetadataLength
            ? snapshot[..MaxMetadataLength] + "\n\n..."
            : snapshot;

        if (context.EventSink is null)
            return;

        try
        {
            await context.EventSink.EmitAsync(new ToolCallEvent
            {
                SessionId = context.SessionId,
                ToolCallId = context.CallId ?? "",
                ToolName = "bash",
                Status = ToolCallStatus.Running,
                Type = MessageEventType.ToolCallRunning,
                Output = truncated,
                Title = description
            });
        }
        catch (Exception ex)
        {
            // 流式进度失败不阻断命令执行；打日志便于发现 EventSink 接线问题
            _logger.LogDebug(ex, "bash 流式进度 EventSink 推送失败 CallId={CallId}", context.CallId);
        }
    }

    private static Dictionary<string, string?> EnsureUtf8OutputEnvironment(
        IReadOnlyDictionary<string, string> envVars)
    {
        var env = envVars.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        env.TryAdd("PYTHONIOENCODING", "utf-8");
        env.TryAdd("PYTHONUTF8", "1");
        env.TryAdd("LANG", "C.UTF-8");
        env.TryAdd("LC_ALL", "C.UTF-8");
        env.TryAdd("NO_COLOR", "1");
        return env;
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

    private sealed class StreamEmitGate
    {
        public object Sync { get; } = new();
        private long _lastEmitMs = -StreamEmitMinIntervalMs;

        public bool ShouldEmitUnlocked()
        {
            var now = Environment.TickCount64;
            return now - _lastEmitMs >= StreamEmitMinIntervalMs;
        }

        public void MarkEmittedUnlocked() => _lastEmitMs = Environment.TickCount64;
    }
}
