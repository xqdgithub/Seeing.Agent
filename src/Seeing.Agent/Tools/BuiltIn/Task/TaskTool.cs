using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using SessionData = Seeing.Agent.Sessions.SessionData;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.SubTask
{
    /// <summary>
    /// 子任务执行工具 - 创建子 Session 并调用 Agent 执行任务
    /// </summary>
    public class TaskTool : ToolBase
    {
        private readonly ISessionManager _sessionManager;
        private readonly IAgentRegistry _agentRegistry;
        private readonly ILlmService _llmService;

        /// <summary>
        /// 创建 TaskTool 实例
        /// </summary>
        public TaskTool(
            ILogger<TaskTool> logger,
            ISessionManager sessionManager,
            IAgentRegistry agentRegistry,
            ILlmService llmService) : base(logger)
        {
            _sessionManager = sessionManager;
            _agentRegistry = agentRegistry;
            _llmService = llmService;
        }

        /// <inheritdoc/>
        public override string Id => "task";

        /// <inheritdoc/>
        public override string Description =>
            "创建子任务并使用专用 Agent 执行。" +
            "支持传递任务 ID 以继续之前的子任务。" +
            "子 Agent 可以是探索型、执行型或其他专用类型。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
        {
            ["description"] = ("string", "任务简短描述（3-5 个词）", true, null),
            ["prompt"] = ("string", "Agent 要执行的任务内容", true, null),
            ["subagent_type"] = ("string", "专用 Agent 类型", true, null),
            ["task_id"] = ("string", "任务 ID，用于继续之前的子任务（可选）", false, null)
        });

        /// <inheritdoc/>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var description = GetStringArgument(arguments, "description");
            var prompt = GetStringArgument(arguments, "prompt");
            var subagentType = GetStringArgument(arguments, "subagent_type");
            var taskId = GetStringArgument(arguments, "task_id");

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

            // 获取子 Agent
            var agent = await _agentRegistry.GetAgentAsync(subagentType);
            if (agent == null)
            {
                return Failure($"未知的 Agent 类型: {subagentType}");
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
                        ["prompt"] = prompt
                    }
                });
            }

            try
            {
                // 获取或创建子 Session
                SessionData session;
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
                        session = await CreateSubSession(context.SessionId, description, agent);
                        _logger.LogInformation("创建新的子 Session（未找到现有）: {SessionId}", session.SessionId);
                    }
                }
                else
                {
                    session = await CreateSubSession(context.SessionId, description, agent);
                    _logger.LogInformation("创建新的子 Session: {SessionId}", session.SessionId);
                }

                // 设置元数据
                if (context.SetMetadata != null)
                {
                    context.SetMetadata(description, new Dictionary<string, object>
                    {
                        ["sessionId"] = session.SessionId,
                        ["agent"] = agent.Name
                    });
                }

                // 构建输入消息
                var inputMessage = new ChatMessage
                {
                    Role = "user",
                    Content = prompt
                };

                // 执行 Agent
                var agentContext = new AgentContext
                {
                    SessionId = session.SessionId,
                    MessageId = $"msg_{Guid.NewGuid():N}",
                    CancellationToken = context.CancellationToken,
                    Metadata = new Dictionary<string, object>
                    {
                        ["parentSessionId"] = context.SessionId,
                        ["description"] = description
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
                output.AppendLine($"task_id: {session.SessionId}（用于继续此任务）");
                output.AppendLine();
                output.AppendLine("<task_result>");
                output.AppendLine(outputText);
                output.AppendLine("</task_result>");

                return Success(description, output.ToString(), new Dictionary<string, object>
                {
                    ["sessionId"] = session.SessionId,
                    ["agent"] = agent.Name,
                    ["messages"] = messages.Count
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
        /// 创建子 Session
        /// </summary>
        private async Task<SessionData> CreateSubSession(string parentSessionId, string description, IAgent agent)
        {
            var session = await _sessionManager.CreateSessionAsync(agent);

            // 设置父 Session 关系
            await _sessionManager.SetContextAsync(session.SessionId, "parentSessionId", parentSessionId);
            await _sessionManager.SetContextAsync(session.SessionId, "taskDescription", description);

            // 禁止子 Session 中的某些工具
            await _sessionManager.SetContextAsync(session.SessionId, "disabledTools", new[] { "todowrite", "todoread" });

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

    /// <summary>
    /// Agent 注册表接口
    /// </summary>
    public interface IAgentRegistry
    {
        /// <summary>获取所有 Agent</summary>
        Task<IReadOnlyList<IAgent>> GetAgentsAsync();

        /// <summary>获取指定 Agent</summary>
        Task<IAgent?> GetAgentAsync(string name);

        /// <summary>获取非主 Agent 列表</summary>
        Task<IReadOnlyList<IAgent>> GetSubAgentsAsync();
    }

    /// <summary>
    /// LLM 服务接口
    /// </summary>
    public interface ILlmService
    {
        /// <summary>执行对话</summary>
        Task<string> ChatAsync(
            string sessionId,
            ChatMessage message,
            CancellationToken cancellationToken = default);
    }
}