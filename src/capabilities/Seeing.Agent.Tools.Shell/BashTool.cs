using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.Shell;

/// <summary>
/// 执行 Shell 命令的 bash 工具。
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
public class BashTool : ToolBase
{
    private const int DefaultTimeoutMs = 120_000;
    private const int DefaultActivityTimeoutMs = 1_800_000;
    private const int MaxMetadataLength = 30_000;
    private const int StreamEmitMinIntervalMs = 100;

    /// <summary>后台活动能力包模块 id（<c>background=true</c> 需要它提供 <c>activity_*</c> 工具）。</summary>
    private const string BackgroundModuleId = "background";

    private readonly IExecutionWorld _world;
    private readonly IShellService _shellService;
    private readonly IShellEnvironmentService _shellEnvService;
    private readonly IOptionsMonitor<ShellOptions> _options;
    private readonly IWorkspacePathGate _pathGate;
    private readonly IBackgroundActivityManager? _background;
    private readonly IModuleCatalog? _modules;

    public override string Id => "bash";

    public override string Description =>
        "执行 Shell 命令（构建、测试、脚本）；文件读写搜索优先用专用工具。\n\n" +
        "- 优先：文件读写与搜索用 `read`/`write`/`edit`/`grep`/`glob`，不要用 shell 代替。\n" +
        "- 超时：默认 120000 毫秒，可用 timeout 覆盖；超时会终止进程。\n" +
        "- 工作目录：workdir 默认当前工作目录。\n" +
        "- 后台：`background=true` 把命令转交后台活动并立即返回活动 id（此时 timeout 被忽略，改用 activity_timeout）；" +
        "用 `activity_status`/`activity_output`/`activity_wait`/`activity_cancel` 跟踪。\n" +
        $"当前环境：{BuildPlatformHint()}；请使用与当前 Shell 语法匹配的命令。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "要执行的命令。" + $"当前环境：{BuildPlatformHint()}" },
            timeout = new { type = "number", minimum = 1, description = "超时时间（毫秒），默认 120000（2 分钟）；background=true 时忽略" },
            background = new { type = "boolean", @default = false, description = "为 true 时把命令转交后台活动并立即返回活动 id（不等待退出）" },
            activity_timeout = new { type = "number", minimum = 1, @default = DefaultActivityTimeoutMs, description = "后台活动超时（毫秒），默认 1800000（30 分钟）；仅 background=true 时生效，取代 timeout" },
            workdir = new { type = "string", description = "工作目录，默认当前工作目录" },
            description = new { type = "string", description = "命令用途的简明描述（5-10 字）；可省略，缺省时自动取命令前 50 字符" }
        },
        required = new[] { "command" }
    });

    public override ToolCategory Category => ToolCategory.ExternalService;

    /// <param name="background">
    /// 后台活动会话面（可选）：宿主未接线时不支持 <c>background=true</c>，普通执行不受影响。
    /// </param>
    /// <param name="modules">
    /// 模块目录（可选）：用于判定 <c>background</c> 模块是否启用。
    /// </param>
    public BashTool(
        ILogger<BashTool> logger,
        IExecutionWorld world,
        IShellService shellService,
        IShellEnvironmentService shellEnvService,
        IOptionsMonitor<ShellOptions> options,
        IWorkspacePathGate pathGate,
        IBackgroundActivityManager? background = null,
        IModuleCatalog? modules = null)
        : base(logger)
    {
        _world = world;
        _shellService = shellService;
        _shellEnvService = shellEnvService;
        _options = options;
        _pathGate = pathGate;
        _background = background;
        _modules = modules;
    }

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var command = GetStringArgument(arguments, "command");
        var description = GetStringArgument(arguments, "description");
        var timeout = GetIntArgument(arguments, "timeout") ?? DefaultTimeoutMs;
        var background = GetBoolArgument(arguments, "background") ?? false;
        var workdir = GetStringArgument(arguments, "workdir") ?? context.WorkingDirectory;

        if (string.IsNullOrEmpty(command)) return Failure("command 参数是必需的");
        if (string.IsNullOrWhiteSpace(workdir))
            return Failure("缺少执行工作目录，无法确定命令工作目录。");
        if (string.IsNullOrEmpty(description)) description = command.Length > 50 ? command[..50] + "..." : command;
        // background=true 时 timeout 完全被 activity_timeout 取代，不再校验它。
        if (!background && timeout <= 0) return Failure($"无效的超时值: {timeout}。超时必须是正整数毫秒（timeout=0 无意义）。");

        // 工作目录必须在授权根内：shell 不能靠"命令级审批"绕过路径边界。
        var gateError = _pathGate.ResolveAllowed(
            context.SessionId ?? string.Empty, workdir, context.WorkingDirectory, out var fullWorkdir);
        if (gateError is not null)
            return Failure($"工作目录不可用: {gateError}");
        workdir = fullWorkdir;

        try
        {
            if (background)
                return await StartBackgroundActivityAsync(arguments, command, description, workdir, context);

            return await ExecuteCommandAsync(command, description, workdir, timeout, context);
        }
        catch (OperationCanceledException) { return Failure($"命令被取消: {command}"); }
        catch (Exception ex) { return Failure($"{description}: {ex.Message}"); }
    }

    /// <summary>
    /// 把本次命令转交后台活动：只做前置校验与请求装配，立即返回活动句柄（不等命令退出）。
    /// <para>
    /// 活动级超时的权威是请求的 <see cref="BackgroundActivityStartRequest.Timeout"/>；
    /// payload 里的 <c>timeoutMs</c> 供 launcher 约束子进程（见 <see cref="ProcessBackgroundActivityLauncher"/>）。
    /// </para>
    /// </summary>
    private async Task<ToolResult> StartBackgroundActivityAsync(
        JsonElement arguments, string command, string description, string workdir, ToolContext context)
    {
        if (_background is null)
            return Failure("后台活动不可用：宿主未注册后台活动管理器（IBackgroundActivityManager）。");

        if (_modules is null || !_modules.IsEnabled(BackgroundModuleId))
            return Failure($"后台活动不可用：模块 '{BackgroundModuleId}' 未启用（缺少 activity_* 工具无法跟踪结果）。");

        if (string.IsNullOrWhiteSpace(context.ExecutionId))
            return Failure("缺少本轮执行 id（ExecutionId），无法把命令转交后台活动。");

        var activityTimeout = GetIntArgument(arguments, "activity_timeout") ?? DefaultActivityTimeoutMs;
        if (activityTimeout <= 0)
            return Failure($"无效的后台活动超时值: {activityTimeout}。activity_timeout 必须是正整数毫秒。");

        // 后台执行同样受危险命令拦截：转后台不是绕过命令级安全检查的通道。
        var dangerCheck = DangerousCommandGuard.Check(command, _options.CurrentValue);
        if (dangerCheck != null) return Failure($"命令被拒绝执行: {dangerCheck}");

        var request = new BackgroundActivityStartRequest
        {
            Kind = ProcessBackgroundActivityLauncher.ProcessKind,
            Title = description,
            OwnerSessionId = context.SessionId,
            OwnerExecutionId = context.ExecutionId,
            OriginToolCallId = context.CallId,
            WorkingDirectory = workdir,
            Payload = JsonSerializer.SerializeToElement(new
            {
                command,
                workdir,
                timeoutMs = activityTimeout
            }),
            Timeout = TimeSpan.FromMilliseconds(activityTimeout),
            DedupeKey = ComputeDedupeKey(command, workdir),
            DedupeStrategy = BackgroundActivityDedupeStrategy.Reject
        };

        try
        {
            var handle = await _background.StartAsync(request, context.SessionId, context.CancellationToken);
            return new ToolResult
            {
                Success = true,
                Title = description,
                Output =
                    $"命令已转交后台活动执行（不等待退出）。\n" +
                    $"activity_id: {handle.ActivityId}\n" +
                    "后续用 activity_status / activity_output / activity_wait / activity_cancel 跟踪或取消该活动。",
                BackgroundActivityId = handle.ActivityId
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure($"后台活动启动被拒绝: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Failure($"后台活动启动失败: {ex.Message}");
        }
    }

    /// <summary>去重键：同一 <c>(command, workdir)</c> 只允许一个在途活动（manager 只比字符串）。</summary>
    private static string ComputeDedupeKey(string command, string workdir)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command + "\n" + workdir)));

    private async Task<ToolResult> ExecuteCommandAsync(
        string command, string description, string workdir, int timeout, ToolContext context)
    {
        var dangerCheck = DangerousCommandGuard.Check(command, _options.CurrentValue);
        if (dangerCheck != null) return Failure($"命令被拒绝执行: {dangerCheck}");

        var shell = _shellService.SelectShell();
        var envVars = await _shellEnvService.GetEnvironmentAsync(
            workdir, context.SessionId, context.CallId, context.CancellationToken);

        var prepared = _shellService.PrepareCommand(shell, command);
        var argumentList = _shellService.BuildArgumentList(shell, prepared);
        var environment = EnsureUtf8OutputEnvironment(envVars);

        var spec = new SubprocessSpec
        {
            FileName = shell,
            // POSIX：argv 直传，避免 Linux 把 -c '…' 按空白拆开。cmd/powershell 仍走原始命令行。
            Arguments = argumentList is null ? _shellService.BuildArguments(shell, prepared) : "",
            ArgumentList = argumentList,
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

        // 用户取消后不再 emit Running 事件：终态由上层统一渲染为"已取消"
        if (!context.CancellationToken.IsCancellationRequested)
        {
            await PublishProgressAsync(context, description, outputBuilder, gate, force: true);
        }

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
