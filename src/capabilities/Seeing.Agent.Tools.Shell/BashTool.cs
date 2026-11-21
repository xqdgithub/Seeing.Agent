using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Configuration;
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
/// <para>
/// <b>统一执行路径</b>：前台与后台都经 <see cref="IBackgroundActivityManager"/>（<c>kind=process</c> launcher）执行，
/// 工具自身不再管理进程、超时与输出泵：
/// </para>
/// <code>
/// StartAsync ──┬─ background=true → 立即返回活动 id
///              └─ 前台           → WaitAsync → 读活动输出 → ToolResult
///                                   └─ 用户「转后台」→ Wait 以 Detached 返回 → Success + 活动 id
/// </code>
/// <para>依靠这一点，前台命令的进程生命周期、输出落盘、超时与取消全部只有一份真相。</para>
/// </summary>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
public class BashTool : ToolBase
{
    private const int DefaultTimeoutMs = 120_000;
    private const int DefaultActivityTimeoutMs = 1_800_000;

    /// <summary>工具结果中保留的正文上限（超出即截断并加省略号）。</summary>
    private const int MaxMetadataLength = 30_000;

    /// <summary>活动输出分页读取的单页上限（与服务端契约一致）。</summary>
    private const int OutputPageBytes = 32 * 1024;

    /// <summary>一次工具调用最多读回的活动输出字节数。</summary>
    private const int MaxOutputBytes = 256 * 1024;

    /// <summary>等待终态时比命令超时多给的余量：超时裁决以 manager 为准。</summary>
    private const int WaitMarginMs = 500;

    /// <summary>后台活动能力包模块 id（<c>background=true</c> 需要它提供 <c>activity_*</c> 工具）。</summary>
    private const string BackgroundModuleId = "background";

    private readonly IShellService _shellService;
    private readonly IOptionsMonitor<ShellOptions> _options;
    private readonly IWorkspacePathGate _pathGate;
    private readonly IBackgroundActivityManager? _background;
    private readonly IModuleCatalog? _modules;

    public override string Id => "bash";

    public override string Description =>
        "执行 Shell 命令（构建、测试、脚本）；文件读写搜索优先用专用工具。\n\n" +
        "- 优先：文件读写与搜索用 `read`/`write`/`edit`/`grep`/`glob`，不要用 shell 代替。\n" +
        "- 前台（默认）：命令在 timeout 内跑完后才返回，输出即结果；默认 120000 毫秒，可用 timeout 覆盖；" +
        "超过 timeout 会终止命令并返回已产生的输出。\n" +
        "- 后台：`background=true` 立即返回活动 id 而不等待（此时忽略 timeout，改用 activity_timeout）；" +
        "用 `activity_status`/`activity_output`/`activity_wait`/`activity_cancel` 跟踪。\n" +
        "- 转后台：前台命令运行中可被用户转为后台，此时工具立刻返回活动 id，命令继续执行。\n" +
        "- 工作目录：workdir 默认当前工作目录。\n" +
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
    /// 后台活动会话面（<b>必需</b>，却仍按可空实现）：前台与后台都经它执行，
    /// 缺失时工具直接失败——不再有「无 manager 也能跑」的回退路径。
    /// </param>
    /// <param name="modules">
    /// 模块目录（可选）：用于判定 <c>background</c> 模块是否启用（<c>background=true</c> 需要 <c>activity_*</c> 工具）。
    /// </param>
    public BashTool(
        ILogger<BashTool> logger,
        IShellService shellService,
        IOptionsMonitor<ShellOptions> options,
        IWorkspacePathGate pathGate,
        IBackgroundActivityManager background,
        IModuleCatalog? modules = null)
        : base(logger)
    {
        _shellService = shellService;
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

        if (_background is null)
            return Failure("后台活动不可用：宿主未注册后台活动管理器（IBackgroundActivityManager），bash 无法执行命令。");

        try
        {
            if (background)
                return await StartBackgroundActivityAsync(arguments, command, description, workdir, context);

            return await StartForegroundAsync(command, description, workdir, timeout, context);
        }
        catch (OperationCanceledException) { return Failure($"命令被取消: {command}"); }
        catch (Exception ex) { return Failure($"{description}: {ex.Message}"); }
    }

    /// <summary>
    /// 前台执行：启动活动后等待终态，读回活动输出作为工具结果。
    /// <para>前台活动 <c>NotifyPolicy=None</c>（自身就在等，不需要完成唤醒）；超时与取消由本方法裁决后取消活动。</para>
    /// </summary>
    private async Task<ToolResult> StartForegroundAsync(
        string command, string description, string workdir, int timeout, ToolContext context)
    {
        var dangerCheck = DangerousCommandGuard.Check(command, _options.CurrentValue);
        if (dangerCheck != null) return Failure($"命令被拒绝执行: {dangerCheck}");

        var manager = _background!;
        var sessionId = context.SessionId ?? string.Empty;

        var request = new BackgroundActivityStartRequest
        {
            Kind = ProcessBackgroundActivityLauncher.ProcessKind,
            Title = description,
            OwnerSessionId = sessionId,
            OwnerExecutionId = context.ExecutionId ?? string.Empty,
            OriginToolCallId = context.CallId,
            WorkingDirectory = workdir,
            Origin = BackgroundActivityOrigin.ToolForeground,
            NotifyPolicy = BackgroundActivityNotifyPolicy.None,
            Timeout = TimeSpan.FromMilliseconds(timeout),
            Payload = JsonSerializer.SerializeToElement(new { command, workdir })
        };

        BackgroundActivityHandle handle;
        try
        {
            handle = await manager.StartAsync(request, sessionId, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 调用方在启动阶段就取消了：活动可能已登记，交给会话级取消收敛。
            return Failure("已取消");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure($"后台活动启动被拒绝: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Failure($"后台活动启动失败: {ex.Message}");
        }

        var wait = await manager.WaitAsync(
            handle.ActivityId,
            TimeSpan.FromMilliseconds(timeout + WaitMarginMs),
            sessionId,
            context.CancellationToken);

        // 越权与不存在都返回空快照（越权在 manager 侧表现为 TimedOut）。
        if (wait.Snapshot is null)
            return Failure("后台活动不存在或不属于当前会话");

        switch (wait.Outcome)
        {
            case BackgroundActivityWaitOutcome.Detached:
                return new ToolResult
                {
                    Success = true,
                    Title = description,
                    Output = $"命令已转入后台，使用 activity_* 跟踪（不等待退出）。\n" +
                             $"activity_id: {handle.ActivityId}\n" +
                             "后续用 activity_status / activity_output / activity_wait / activity_cancel 跟踪或取消该活动。",
                    BackgroundActivityId = handle.ActivityId,
                    Metadata = new Dictionary<string, object> { ["description"] = description }
                };

            case BackgroundActivityWaitOutcome.TimedOut:
                await manager.CancelAsync(
                    handle.ActivityId, BackgroundActivityCancelReason.Timeout, sessionId, CancellationToken.None)
                    .ConfigureAwait(false);
                return await FailureAfterTerminatingAsync(
                    handle.ActivityId, sessionId, description, timeout, timedOut: true).ConfigureAwait(false);

            case BackgroundActivityWaitOutcome.CallerCancelled:
                await manager.CancelAsync(
                    handle.ActivityId, BackgroundActivityCancelReason.User, sessionId, CancellationToken.None)
                    .ConfigureAwait(false);
                return await FailureAfterTerminatingAsync(
                    handle.ActivityId, sessionId, description, timeout, timedOut: false).ConfigureAwait(false);

            default:
                return await FromTerminalSnapshotAsync(
                    wait.Snapshot, sessionId, description, timeout).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 终态快照 → 工具结果：成功读输出；失败/取消同样把已产生的输出带回（先读再返回失败）。
    /// <para>非零退出码在 launcher 侧是 <c>Succeeded</c> + <c>ExitCode</c>，不构成工具失败。</para>
    /// </summary>
    private async Task<ToolResult> FromTerminalSnapshotAsync(
        BackgroundActivitySnapshot snapshot, string sessionId, string description, int timeout)
    {
        switch (snapshot.Status)
        {
            case BackgroundActivityStatus.Succeeded:
                {
                    var output = await ReadActivityOutputAsync(snapshot.Id, sessionId).ConfigureAwait(false);
                    return Success(description, output, BuildMetadata(snapshot.Result?.ExitCode, timedOut: false, aborted: false, description));
                }

            case BackgroundActivityStatus.Cancelled:
                return await FailureAfterTerminatingAsync(
                        snapshot.Id,
                        sessionId,
                        description,
                        timeout,
                        timedOut: snapshot.CancelReason is BackgroundActivityCancelReason.Timeout,
                        exitCode: snapshot.Result?.ExitCode)
                    .ConfigureAwait(false);

            case BackgroundActivityStatus.Failed:
                {
                    var output = await ReadActivityOutputAsync(snapshot.Id, sessionId).ConfigureAwait(false);
                    var errorCode = snapshot.Result?.ErrorCode;
                    var isTimeout = string.Equals(
                        errorCode, ProcessBackgroundActivityLauncher.TimeoutErrorCode, StringComparison.Ordinal);
                    var metadata = BuildMetadata(
                        snapshot.Result?.ExitCode, timedOut: isTimeout, aborted: false, description);
                    var error = isTimeout
                        ? TimeoutMessage(timeout)
                        : snapshot.Result?.ErrorMessage ?? errorCode ?? "命令执行失败";
                    return new ToolResult
                    {
                        Success = false,
                        Title = description,
                        Output = AppendMetadataBlock(output, isTimeout ? [TimeoutMessage(timeout)] : []),
                        Error = error,
                        Metadata = metadata
                    };
                }

            case BackgroundActivityStatus.Interrupted:
                return new ToolResult
                {
                    Success = false,
                    Title = description,
                    Output = await ReadActivityOutputAsync(snapshot.Id, sessionId).ConfigureAwait(false),
                    Error = "命令被中断（宿主关闭或活动已被回收）",
                    Metadata = BuildMetadata(snapshot.Result?.ExitCode, timedOut: false, aborted: true, description)
                };

            default:
                return new ToolResult
                {
                    Success = false,
                    Title = description,
                    Output = await ReadActivityOutputAsync(snapshot.Id, sessionId).ConfigureAwait(false),
                    Error = $"命令未进入终态（当前状态: {snapshot.Status}）",
                    Metadata = BuildMetadata(snapshot.Result?.ExitCode, timedOut: false, aborted: false, description)
                };
        }
    }

    /// <summary>
    /// 超时 / 取消后的失败结果：先读回已产生的输出，再返回失败。
    /// </summary>
    private async Task<ToolResult> FailureAfterTerminatingAsync(
        string activityId,
        string sessionId,
        string description,
        int timeout,
        bool timedOut,
        int? exitCode = null)
    {
        var aborted = !timedOut;

        var output = await ReadActivityOutputAsync(activityId, sessionId).ConfigureAwait(false);
        var notes = new List<string>();
        if (timedOut) notes.Add(TimeoutMessage(timeout));
        if (aborted) notes.Add("命令被取消");

        return new ToolResult
        {
            Success = false,
            Title = description,
            Output = AppendMetadataBlock(output, notes),
            Error = timedOut ? TimeoutMessage(timeout) : "已取消",
            Metadata = BuildMetadata(exitCode, timedOut, aborted, description)
        };
    }

    /// <summary>
    /// 分页读回活动输出（单页 32KB，上限 256KB），剥离 ANSI 后截断到 <see cref="MaxMetadataLength"/>。
    /// <para>读盘不受调用方取消影响：终态已到，取消令牌反而会丢掉用户想看的输出。</para>
    /// </summary>
    private async Task<string> ReadActivityOutputAsync(string activityId, string sessionId)
    {
        var manager = _background!;
        var builder = new StringBuilder();
        long offset = 0;
        var readBytes = 0;

        while (readBytes < MaxOutputBytes)
        {
            var page = await manager.ReadOutputAsync(
                    activityId, offset, Math.Min(OutputPageBytes, MaxOutputBytes - readBytes), sessionId, CancellationToken.None)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(page.Content))
            {
                builder.Append(page.Content);
                readBytes += Encoding.UTF8.GetByteCount(page.Content);
            }

            // 空页或游标不前进都必须停：否则越权/异常分页会变成死循环。
            if (page.EndOfStream || page.NextOffset <= offset)
                break;

            offset = page.NextOffset;
        }

        var text = AnsiEscape.Strip(builder.ToString());
        return text.Length > MaxMetadataLength
            ? text[..MaxMetadataLength] + "\n\n..."
            : text;
    }

    private static Dictionary<string, object> BuildMetadata(
        int? exitCode, bool timedOut, bool aborted, string description)
    {
        var metadata = new Dictionary<string, object>
        {
            ["description"] = description,
            ["timedOut"] = timedOut,
            ["aborted"] = aborted
        };

        if (exitCode.HasValue)
            metadata["exit"] = exitCode.Value;

        return metadata;
    }

    /// <summary>
    /// <c>&lt;bash_metadata&gt;</c> 仅面向 LLM 可读附录；UI 以 <see cref="ToolResult.Metadata"/> 为真源并 strip 该块。
    /// </summary>
    private static string AppendMetadataBlock(string output, IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
            return output;

        return output + "\n\n<bash_metadata>\n" + string.Join("\n", notes) + "\n</bash_metadata>";
    }

    private static string TimeoutMessage(int timeout) => $"命令在超过超时时间 {timeout} 毫秒后被终止";

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
            Origin = BackgroundActivityOrigin.ToolBackground,
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
            var handle = await _background!.StartAsync(request, context.SessionId, context.CancellationToken);

            // 去重命中：命令没有重新执行，回执不能讲成「已转交执行」。
            var output = handle.IsExisting
                ? $"已存在同命令后台活动，复用活动 {handle.ActivityId}（本次未重复执行命令）。\n" +
                  $"activity_id: {handle.ActivityId}\n" +
                  "后续用 activity_status / activity_output / activity_wait / activity_cancel 跟踪或取消该活动。"
                : $"命令已转交后台活动执行（不等待退出）。\n" +
                  $"activity_id: {handle.ActivityId}\n" +
                  "后续用 activity_status / activity_output / activity_wait / activity_cancel 跟踪或取消该活动。";

            return new ToolResult
            {
                Success = true,
                Title = description,
                Output = output,
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
