using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Agents;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Acp.Backends;
using Seeing.Agent.Acp.Execution;
using Seeing.Agent.Acp.Mapping;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Session.Core;

namespace Seeing.Agent.Acp.Tools;

/// <summary>
/// ACP 工具委派（TaskTool 语义）。
/// </summary>
public sealed class AcpTool : ToolBase
{
    private readonly IAcpSessionRunner _sessionRunner;
    private readonly IAcpBackendRegistry _backendRegistry;
    private readonly ContentBlockMapper _contentMapper;
    private readonly IOptions<SeeingAgentOptions> _options;
    private readonly ISessionManager _sessionManager;
    private readonly IWorkspaceProvider _workspace;
    private readonly ConcurrentDictionary<string, BackgroundTaskState> _backgroundTasks = new();

    public AcpTool(
        ILogger<AcpTool> logger,
        IAcpSessionRunner sessionRunner,
        IAcpBackendRegistry backendRegistry,
        ContentBlockMapper contentMapper,
        IOptions<SeeingAgentOptions> options,
        ISessionManager sessionManager,
        IWorkspaceProvider workspace) : base(logger)
    {
        _sessionRunner = sessionRunner;
        _backendRegistry = backendRegistry;
        _contentMapper = contentMapper;
        _options = options;
        _sessionManager = sessionManager;
        _workspace = workspace;
    }

    public override string Id => "acp";

    public override string Description => BuildDescription();

    public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
    {
        ["description"] = ("string", "任务简短描述（3-5 个词）", true, null),
        ["prompt"] = ("string", "要发送给 ACP Agent 的任务内容", true, null),
        ["backend"] = ("string", "ACP 后端标识", true, null),
        ["task_id"] = ("string", "任务 ID，用于继续之前的 ACP 子任务（可选）", false, null),
        ["cwd"] = ("string", "工作目录（可选）", false, null),
        ["run_in_background"] = ("boolean", "是否在后台运行（可选，默认 false）", false, null)
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!_options.Value.Acp.Enabled)
            return Failure("ACP 集成未启用");

        var description = GetStringArgument(arguments, "description");
        var prompt = GetStringArgument(arguments, "prompt");
        var backend = GetStringArgument(arguments, "backend");
        var taskId = GetStringArgument(arguments, "task_id");
        var cwd = GetStringArgument(arguments, "cwd");
        var runInBackground = GetBoolArgument(arguments, "run_in_background") ?? false;

        if (description == null)
            return Failure("description 参数是必需的");
        if (prompt == null)
            return Failure("prompt 参数是必需的");
        if (backend == null)
            return Failure("backend 参数是必需的");

        try
        {
            _backendRegistry.GetBackend(backend);
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }

        if (context.PermissionChannel is not null)
        {
            var permResult = await context.PermissionChannel.RequestAsync(new PermissionRequest
            {
                PermissionKind = "tool.execute",
                Resource = "acp",
                Metadata = new Dictionary<string, object>
                {
                    ["description"] = description,
                    ["backend"] = backend,
                    ["prompt"] = prompt
                }
            });

            if (permResult.Action != PermissionChannelAction.Allow)
                return Failure(permResult.Reason ?? "权限被拒绝");
        }

        var session = await ResolveTaskSessionAsync(taskId);
        var workingDirectory = cwd ?? session.WorkingDirectory ?? _workspace.WorkspaceRoot;

        if (runInBackground)
        {
            var bgTaskId = session.Id;
            _backgroundTasks[bgTaskId] = new BackgroundTaskState
            {
                Status = "running",
                Description = description,
                Backend = backend
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await RunTaskAsync(session.Id, backend, prompt, description, workingDirectory, context, CancellationToken.None);
                    _backgroundTasks[bgTaskId] = new BackgroundTaskState
                    {
                        Status = result.Success ? "completed" : "failed",
                        Description = description,
                        Backend = backend,
                        Output = result.Text,
                        Error = result.Error
                    };
                }
                catch (Exception ex)
                {
                    _backgroundTasks[bgTaskId] = new BackgroundTaskState
                    {
                        Status = "failed",
                        Description = description,
                        Backend = backend,
                        Error = ex.Message
                    };
                }
            });

            return Success(description,
                $"task_id: {bgTaskId}（后台运行中，使用 acp_status 查询进度）",
                new Dictionary<string, object>
                {
                    ["task_id"] = bgTaskId,
                    ["backend"] = backend,
                    ["run_in_background"] = true
                });
        }

        try
        {
            var result = await RunTaskAsync(session.Id, backend, prompt, description, workingDirectory, context, context.CancellationToken);
            var output = new StringBuilder();
            output.AppendLine($"task_id: {session.Id}（用于继续此任务）");
            output.AppendLine();
            output.AppendLine("<task_result>");
            output.AppendLine(string.IsNullOrWhiteSpace(result.Text) ? "ACP 任务完成，无输出。" : result.Text);
            output.AppendLine("</task_result>");

            return Success(description, output.ToString(), new Dictionary<string, object>
            {
                ["task_id"] = session.Id,
                ["backend"] = backend,
                ["success"] = result.Success
            });
        }
        catch (OperationCanceledException)
        {
            return Failure("ACP 任务被取消");
        }
        catch (Exception ex)
        {
            return Failure(ex, "ACP 任务执行失败");
        }
    }

    internal BackgroundTaskState? GetBackgroundTask(string taskId) =>
        _backgroundTasks.TryGetValue(taskId, out var state) ? state : null;

    private async Task<AcpRunResult> RunTaskAsync(
        string taskId,
        string backend,
        string prompt,
        string description,
        string workingDirectory,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var sink = new BufferingSink();
        var request = new AcpRunRequest
        {
            Scope = "tool",
            ScopeKey = taskId,
            BackendId = backend,
            SeeingSessionId = context.SessionId,
            Prompt = _contentMapper.MapPrompt(prompt),
            WorkingDirectory = workingDirectory,
            ParentContext = new AgentContext
            {
                SessionId = context.SessionId,
                PermissionChannel = context.PermissionChannel,
                Metadata = new Dictionary<string, object>
                {
                    [AgentContextKeys.ParentSessionIdKey] = context.SessionId,
                    [AgentContextKeys.TaskDescriptionKey] = description,
                    [AgentContextKeys.AcpBackendKey] = backend
                }
            }
        };

        var result = await _sessionRunner.RunAsync(request, sink, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Text))
            result = result with { Text = sink.GetText() };

        return result;
    }

    private async Task<SessionData> ResolveTaskSessionAsync(string? taskId)
    {
        if (taskId != null)
        {
            var existing = _sessionManager.Get(taskId);
            if (existing != null)
                return existing;

            var loaded = await _sessionManager.LoadAsync(taskId);
            if (loaded != null)
                return loaded;
        }

        var session = _sessionManager.Create();
        await _sessionManager.SaveAsync(session.Id);
        return session;
    }

    private string BuildDescription()
    {
        try
        {
            var backends = _backendRegistry.GetEnabledBackends();
            var list = backends.Count > 0
                ? string.Join("\n", backends.Select(b => $"- {b.Id}: {b.Command}"))
                : "- 无可用 ACP 后端";

            return "委派任务给外部 ACP Agent 执行。支持 task_id 续接任务。\n\n可用后端：\n" + list;
        }
        catch
        {
            return "委派任务给外部 ACP Agent 执行。支持 task_id 续接任务。";
        }
    }

    private static JsonElement BuildObjectSchema(
        Dictionary<string, (string Type, string Description, bool Required, string[]? EnumValues)> properties)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var kvp in properties)
        {
            var prop = new Dictionary<string, object>
            {
                ["type"] = kvp.Value.Type,
                ["description"] = kvp.Value.Description
            };

            if (kvp.Value.EnumValues != null && kvp.Value.EnumValues.Length > 0)
                prop["enum"] = kvp.Value.EnumValues;

            props[kvp.Key] = prop;
            if (kvp.Value.Required)
                required.Add(kvp.Key);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props
        };

        if (required.Count > 0)
            schema["required"] = required.ToArray();

        return JsonSerializer.SerializeToElement(schema);
    }

    internal sealed class BackgroundTaskState
    {
        public required string Status { get; init; }
        public required string Description { get; init; }
        public required string Backend { get; init; }
        public string? Output { get; init; }
        public string? Error { get; init; }
    }
}
