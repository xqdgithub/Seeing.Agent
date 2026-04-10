using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Sessions;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Todo
{
    /// <summary>
    /// Todo 项状态
    /// </summary>
    public enum TodoStatus
    {
        /// <summary>待处理</summary>
        Pending,
        /// <summary>进行中</summary>
        InProgress,
        /// <summary>已完成</summary>
        Completed,
        /// <summary>已取消</summary>
        Cancelled
    }

    /// <summary>
    /// Todo 项优先级
    /// </summary>
    public enum TodoPriority
    {
        /// <summary>低优先级</summary>
        Low,
        /// <summary>中等优先级</summary>
        Medium,
        /// <summary>高优先级</summary>
        High
    }

    /// <summary>
    /// Todo 项
    /// </summary>
    public class TodoItem
    {
        /// <summary>任务内容描述</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>任务状态</summary>
        public TodoStatus Status { get; set; } = TodoStatus.Pending;

        /// <summary>任务优先级</summary>
        public TodoPriority Priority { get; set; } = TodoPriority.Medium;
    }

    /// <summary>
    /// Todo 写入工具 - 更新会话的 Todo 列表
    /// </summary>
    public class TodoWriteTool : ToolBase
    {
        private const string TodoContextKey = "todos";
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// 创建 TodoWriteTool 实例
        /// </summary>
        public TodoWriteTool(ILogger<TodoWriteTool> logger, ISessionManager sessionManager) : base(logger)
        {
            _sessionManager = sessionManager;
        }

        /// <inheritdoc/>
        public override string Id => "todowrite";

        /// <inheritdoc/>
        public override string Description =>
            "使用此工具创建和管理任务列表，帮助跟踪任务进度。" +
            "支持设置任务状态（pending/in_progress/completed/cancelled）和优先级（low/medium/high）。" +
            "有助于组织复杂的多步骤任务。";

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

            // 请求权限确认
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "todowrite",
                    Patterns = new List<string> { "*" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["todos"] = todos
                    }
                });
            }

            // 更新会话中的 Todo 列表
            var session = _sessionManager.GetSession(context.SessionId);
            if (session == null)
            {
                return Failure($"会话不存在: {context.SessionId}");
            }

            await _sessionManager.SetContextAsync(context.SessionId, TodoContextKey, todos);

            var pendingCount = todos.Count(t => t.Status != TodoStatus.Completed);
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
        private List<TodoItem> ParseTodos(JsonElement todosElement)
        {
            var todos = new List<TodoItem>();

            foreach (var item in todosElement.EnumerateArray())
            {
                var todo = new TodoItem();

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
                _ => TodoStatus.Pending
            };
        }

        /// <summary>
        /// 解析优先级字符串
        /// </summary>
        private static TodoPriority ParsePriority(string priority)
        {
            return priority.ToLowerInvariant() switch
            {
                "low" => TodoPriority.Low,
                "medium" => TodoPriority.Medium,
                "high" => TodoPriority.High,
                _ => TodoPriority.Medium
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
                                ["enum"] = new[] { "pending", "in_progress", "completed", "cancelled" },
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
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// 创建 TodoReadTool 实例
        /// </summary>
        public TodoReadTool(ILogger<TodoReadTool> logger, ISessionManager sessionManager) : base(logger)
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
            // 请求权限确认
            if (context.AskPermission != null)
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = "todoread",
                    Patterns = new List<string> { "*" },
                    Metadata = new Dictionary<string, object>()
                });
            }

            var todos = _sessionManager.GetContext<List<TodoItem>>(context.SessionId, TodoContextKey) 
                ?? new List<TodoItem>();

            var pendingCount = todos.Count(t => t.Status != TodoStatus.Completed);
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
