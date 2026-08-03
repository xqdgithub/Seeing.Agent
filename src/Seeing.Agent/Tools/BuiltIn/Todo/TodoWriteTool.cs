using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Todo;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Todo
{
    /// <summary>
    /// Todo 写入工具 - 更新会话的 Todo 列表
    /// </summary>
    public class TodoWriteTool : ToolBase
    {
        internal const string TodoContextKey = "todos";
        private readonly Seeing.Session.Core.ISessionManager _sessionManager;

        /// <summary>
        /// 创建 TodoWriteTool 实例
        /// </summary>
        public TodoWriteTool(ILogger<TodoWriteTool> logger, Seeing.Session.Core.ISessionManager sessionManager) : base(logger)
        {
            _sessionManager = sessionManager;
        }

        /// <inheritdoc/>
        public override string Id => "todowrite";

        /// <inheritdoc/>
        public override string Description =>
            "使用此工具创建和管理结构化任务列表，帮助跟踪进度、组织复杂任务。" +
            "## 何时使用" +
            "- 复杂多步任务（3 步以上）" +
            "- 用户提供多个任务（编号或逗号分隔）" +
            "- 收到新指令后立即捕获为 todo" +
            "## 何时不用" +
            "- 单个简单任务" +
            "- 纯问答 / 信息性请求" +
            "- 可在 3 步内完成的小任务" +
            "## 任务状态" +
            "- pending: 未开始" +
            "- in_progress: 当前进行中（一次只能一个）" +
            "- completed: 已完成" +
            "- cancelled: 不需要了" +
            "- paused: 等待用户回复，暂停执行" +
            "## 规则" +
            "- 完成后立即标记 completed，不要批量" +
            "- 一次只保持一个 in_progress" +
            "- 需要等待用户时标记 paused" +
            "- 发现新任务立即添加" +
            "有疑问时，使用此工具。主动管理任务体现你的专注度。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
        {
            ["todos"] = ("array", "更新的 Todo 列表", true, null)
        });

        /// <inheritdoc/>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            if (!arguments.TryGetProperty("todos", out var todosElement) ||
                todosElement.ValueKind != JsonValueKind.Array)
            {
                return Failure("todos 参数必须是数组");
            }

            var todos = ParseTodos(todosElement);

            // 更新会话中的 Todo 列表
            var session = _sessionManager.Get(context.SessionId);
            if (session == null)
            {
                return Failure($"会话不存在: {context.SessionId}");
            }

            // 直接设置 SessionData 的 Context
            session.SetContext(TodoContextKey, todos);

            var pendingCount = todos.Count(t => t.Status == TodoStatus.Pending || t.Status == TodoStatus.InProgress);
            var output = JsonSerializer.Serialize(todos, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            _logger.LogInformation("更新 Todo 列表: {PendingCount} 个待处理任务", pendingCount);

            return Success($"{pendingCount} 个待处理任务", output, new Dictionary<string, object>
            {
                ["todos"] = todos,
                ["pendingCount"] = pendingCount
            });
        }

        /// <summary>
        /// 解析 Todo 数组
        /// </summary>
        private List<Core.Todo.TodoItem> ParseTodos(JsonElement todosElement)
        {
            var todos = new List<Core.Todo.TodoItem>();

            foreach (var item in todosElement.EnumerateArray())
            {
                var todo = new Core.Todo.TodoItem();

                if (item.TryGetProperty("content", out var contentProp))
                {
                    todo.Content = contentProp.GetString() ?? "";
                }

                if (item.TryGetProperty("status", out var statusProp))
                {
                    var statusStr = statusProp.GetString() ?? "pending";
                    todo.Status = ParseStatus(statusStr);
                }

                if (item.TryGetProperty("priority", out var priorityProp))
                {
                    var priorityStr = priorityProp.GetString() ?? "medium";
                    todo.Priority = ParsePriority(priorityStr);
                }

                todos.Add(todo);
            }

            return todos;
        }

        /// <summary>
        /// 解析状态字符串
        /// </summary>
        private static TodoStatus ParseStatus(string status)
        {
            return status.ToLowerInvariant() switch
            {
                "pending" => TodoStatus.Pending,
                "in_progress" => TodoStatus.InProgress,
                "completed" => TodoStatus.Completed,
                "cancelled" => TodoStatus.Cancelled,
                "paused" => TodoStatus.Paused,
                _ => TodoStatus.Pending
            };
        }

        /// <summary>
        /// 解析优先级字符串
        /// </summary>
        private static Core.Todo.TodoPriority ParsePriority(string priority)
        {
            return priority.ToLowerInvariant() switch
            {
                "low" => Core.Todo.TodoPriority.Low,
                "high" => Core.Todo.TodoPriority.High,
                _ => Core.Todo.TodoPriority.Medium
            };
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

                // 为 todos 数组添加 items schema
                if (kvp.Key == "todos")
                {
                    prop["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["content"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["description"] = "任务内容描述"
                            },
                            ["status"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "pending", "in_progress", "completed", "cancelled", "paused" },
                                ["description"] = "任务状态"
                            },
                            ["priority"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "low", "medium", "high" },
                                ["description"] = "任务优先级"
                            }
                        },
                        ["required"] = new[] { "content" }
                    };
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
    /// Todo 读取工具 - 获取会话的 Todo 列表
    /// </summary>
    public class TodoReadTool : ToolBase
    {
        private const string TodoContextKey = "todos";
        private readonly Seeing.Session.Core.ISessionManager _sessionManager;

        /// <summary>
        /// 创建 TodoReadTool 实例
        /// </summary>
        public TodoReadTool(ILogger<TodoReadTool> logger, Seeing.Session.Core.ISessionManager sessionManager) : base(logger)
        {
            _sessionManager = sessionManager;
        }

        /// <inheritdoc/>
        public override string Id => "todoread";

        /// <inheritdoc/>
        public override string Description => "使用此工具读取当前的 Todo 列表";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new Dictionary<string, object>()
        });

        /// <inheritdoc/>
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var session = _sessionManager.Get(context.SessionId);
            var todos = session?.GetContext<List<TodoItem>>(TodoContextKey) ?? new List<TodoItem>();

            var pendingCount = todos.Count(t => t.Status == TodoStatus.Pending || t.Status == TodoStatus.InProgress);
            var output = JsonSerializer.Serialize(todos, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            return Success($"{pendingCount} 个待处理任务", output, new Dictionary<string, object>
            {
                ["todos"] = todos
            });
        }
    }
}
