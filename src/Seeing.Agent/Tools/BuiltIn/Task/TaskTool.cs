using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.SubTask
{
    /// <summary>
    /// 子任务执行工具 - 创建子 Session 并调用 Agent 执行任务
    /// <para>
    /// 参考 opencode 的 TaskTool 设计，实现：
    /// - 动态子代理发现与权限筛选
    /// - 子会话创建与恢复
    /// - 任务执行与结果返回
    /// </para>
    /// </summary>
    public class TaskTool : ToolBase
    {
        private readonly dynamic _sessionManager;
        private readonly IAgentRegistry _agentRegistry;

        /// <summary>
        /// 创建 TaskTool 实例
        /// </summary>
        public TaskTool(
            ILogger<TaskTool> logger,
            dynamic sessionManager,
            IAgentRegistry agentRegistry) : base(logger)
        {
            _sessionManager = sessionManager;
            _agentRegistry = agentRegistry;
        }

        /// <inheritdoc/>
        public override string Id => "task";

        /// <inheritdoc/>
        public override string Description => BuildDescription();

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
        {
            ["description"] = ("string", "任务简短描述（3-5 个词）", true, null),
            ["prompt"] = ("string", "Agent 要执行的任务内容", true, null),
            ["subagent_type"] = ("string", "专用 Agent 类型", true, null),
            ["task_id"] = ("string", "任务 ID，用于继续之前的子任务（可选）", false, null),
            ["command"] = ("string", "触发此任务的命令（可选）", false, null),
            ["run_in_background"] = ("boolean", "是否在后台运行（可选，默认 false）", false, null),
            ["session_id"] = ("string", "现有会话 ID，用于继续会话（可选）", false, null),
            ["load_skills"] = ("array", "要加载的技能列表（可选）", false, null)
        });

        /// <inheritdoc/>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var description = GetStringArgument(arguments, "description");
            var prompt = GetStringArgument(arguments, "prompt");
            var subagentType = GetStringArgument(arguments, "subagent_type");
            var taskId = GetStringArgument(arguments, "task_id");
            var command = GetStringArgument(arguments, "command");
            var runInBackground = GetBoolArgument(arguments, "run_in_background") ?? false;
            var sessionId = GetStringArgument(arguments, "session_id");
            var loadSkills = GetStringArrayArgument(arguments, "load_skills");

            if (description == null)
            {
                return Failure("description 参数是必需的");
            }

            if (prompt == null)
            {
                return Failure("prompt 参数是必需的");
            }

            if (subagentType == null)
            {
                return Failure("subagent_type 参数是必需的");
            }

            // 获取子 Agent 信息
            var agentInfo = await _agentRegistry.GetAgentAsync(subagentType);
            if (agentInfo == null)
            {
                return Failure($"未知的 Agent 类型: {subagentType}");
            }

            // 检查是否为子代理
            if (agentInfo.Mode == AgentMode.Primary)
            {
                return Failure($"Agent '{subagentType}' 是主代理，不能作为子任务执行");
            }

            // 请求权限确认（如果不是用户显式调用）
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "task",
                    Patterns = new List<string> { subagentType },
                    Metadata = new Dictionary<string, object>
                    {
                        ["description"] = description,
                        ["subagent_type"] = subagentType,
                        ["prompt"] = prompt,
                        ["command"] = command ?? string.Empty
                    }
                });
            }

            try
            {
                // 获取或创建子 Session
                Seeing.Session.Core.SessionData session;
                if (taskId != null)
                {
                    var existingSession = _sessionManager.GetSession(taskId);
                    if (existingSession != null)
                    {
                        session = existingSession;
                        _logger.LogInformation("继续子任务: {TaskId}", taskId);
                    }
                    else
                    {
                        session = await CreateSubSession(context.SessionId, description, agentInfo);
                        _logger.LogInformation("创建新的子 Session（未找到现有）: {SessionId}", session.Id);
                    }
                }
                else if (sessionId != null)
                {
                    var existingSession = _sessionManager.GetSession(sessionId);
                    if (existingSession != null)
                    {
                        session = existingSession;
                        _logger.LogInformation("使用现有会话: {SessionId}", sessionId);
                    }
                    else
                    {
                        session = await CreateSubSession(context.SessionId, description, agentInfo);
                        _logger.LogInformation("创建新的子 Session（未找到指定会话）: {SessionId}", session.Id);
                    }
                }
                else
                {
                    session = await CreateSubSession(context.SessionId, description, agentInfo);
                    _logger.LogInformation("创建新的子 Session: {SessionId}", session.Id);
                }

                // 设置元数据
                if (context.SetMetadata != null)
                {
                    context.SetMetadata(description, new Dictionary<string, object>
                    {
                        ["sessionId"] = session.Id,
                        ["agent"] = agentInfo.Name,
                        ["model"] = agentInfo.Model?.ModelId ?? "default",
                        ["runInBackground"] = runInBackground
                    });
                }

                // 加载指定技能
                if (loadSkills != null && loadSkills.Count > 0)
                {
                    await _sessionManager.SetContextAsync(session.Id, "loadSkills", loadSkills);
                }

                // 构建输入消息
                var inputMessage = new ChatMessage
                {
                    Role = "user",
                    Content = BuildPrompt(prompt, command)
                };

                // 执行 Agent
                var agent = _agentRegistry.GetOrCreateAgentInstance(subagentType);
                if (agent == null)
                {
                    return Failure($"无法创建 Agent 实例: {subagentType}");
                }

                var agentContext = new AgentContext
                {
                    SessionId = session.Id,
                    MessageId = $"msg_{Guid.NewGuid():N}",
                    CancellationToken = context.CancellationToken,
                    Metadata = new Dictionary<string, object>
                    {
                        ["parentSessionId"] = context.SessionId,
                        ["description"] = description,
                        ["command"] = command ?? string.Empty
                    }
                };

                var outputBuilder = new StringBuilder();
                var messages = new List<ChatMessage>();

                await foreach (var message in agent.ExecuteAsync(inputMessage, agentContext, context.CancellationToken))
                {
                    messages.Add(message);
                    if (message.Role == "assistant")
                    {
                        outputBuilder.AppendLine(message.Content);
                    }
                }

                var outputText = outputBuilder.ToString().Trim();
                if (string.IsNullOrEmpty(outputText))
                {
                    outputText = "子任务执行完成，无输出内容。";
                }

                // 构建返回结果
                var output = new StringBuilder();
                output.AppendLine($"task_id: {session.Id}（用于继续此任务）");
                output.AppendLine();
                output.AppendLine("<task_result>");
                output.AppendLine(outputText);
                output.AppendLine("</task_result>");

                return Success(description, output.ToString(), new Dictionary<string, object>
                {
                    ["sessionId"] = session.Id,
                    ["agent"] = agentInfo.Name,
                    ["messages"] = messages.Count,
                    ["model"] = agentInfo.Model?.ModelId ?? "default"
                });
            }
            catch (OperationCanceledException)
            {
                return Failure("子任务被取消");
            }
            catch (Exception ex)
            {
                return Failure(ex, "子任务执行失败");
            }
        }

        /// <summary>
        /// 构建工具描述（动态包含可用子代理列表）
        /// </summary>
        private string BuildDescription()
        {
            try
            {
                // 异步获取子代理列表，同步等待结果
                var subAgents = _agentRegistry.GetSubAgentsAsync().GetAwaiter().GetResult();

                var agentListText = subAgents.Count > 0
                    ? string.Join("\n", subAgents.Select(a =>
                        $"- {a.Name}: {a.Description ?? "此子代理应仅由用户手动调用"}"))
                    : "- 无可用子代理";

                return
                    "创建子任务并使用专用 Agent 执行。" +
                    "支持传递任务 ID 以继续之前的子任务。" +
                    "子 Agent 可以是探索型、执行型或其他专用类型。" +
                    "\n\n可用的子代理类型：\n" + agentListText;
            }
            catch
            {
                return
                    "创建子任务并使用专用 Agent 执行。" +
                    "支持传递任务 ID 以继续之前的子任务。" +
                    "子 Agent 可以是探索型、执行型或其他专用类型。";
            }
        }

        /// <summary>
        /// 构建提示词
        /// </summary>
        private string BuildPrompt(string prompt, string? command)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(command))
            {
                sb.AppendLine($"[命令触发: {command}]");
                sb.AppendLine();
            }

            sb.Append(prompt);

            return sb.ToString();
        }

        /// <summary>
        /// 创建子 Session
        /// </summary>
        private async Task<Seeing.Session.Core.SessionData> CreateSubSession(string parentSessionId, string description, AgentInfo agentInfo)
        {
            var agent = _agentRegistry.GetOrCreateAgentInstance(agentInfo.Name);
            var session = await _sessionManager.CreateSessionAsync(agent?.Name, agent?.Name);

            // 设置父 Session 关系
            await _sessionManager.SetContextAsync(session.Id, "parentSessionId", parentSessionId);
            await _sessionManager.SetContextAsync(session.Id, "taskDescription", description);

            // 根据 Agent 权限配置禁用工具
            var disabledTools = new List<string>();

            // 检查是否禁用 todowrite
            if (agentInfo.PermissionRules.Any(p => p.Kind == PermissionKind.Tool && p.Pattern == "todowrite" && p.Effect == PermissionEffect.Deny))
            {
                disabledTools.Add("todowrite");
            }

            // 检查是否禁用 task
            if (agentInfo.PermissionRules.Any(p => p.Kind == PermissionKind.Tool && p.Pattern == "task" && p.Effect == PermissionEffect.Deny))
            {
                disabledTools.Add("task");
            }

            if (disabledTools.Count > 0)
            {
                await _sessionManager.SetContextAsync(session.Id, "disabledTools", disabledTools.ToArray());
            }

            return session;
        }

        /// <summary>
        /// 构建带属性的对象 Schema
        /// </summary>
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
                {
                    prop["enum"] = kvp.Value.EnumValues;
                }

                props[kvp.Key] = prop;

                if (kvp.Value.Required)
                {
                    required.Add(kvp.Key);
                }
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props
            };

            if (required.Count > 0)
            {
                schema["required"] = required.ToArray();
            }

            return JsonSerializer.SerializeToElement(schema);
        }
    }
}
