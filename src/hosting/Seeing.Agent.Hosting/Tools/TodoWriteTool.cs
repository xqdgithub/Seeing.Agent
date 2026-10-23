using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.Support;
using System.Text.Json;

namespace Seeing.Agent.Hosting.Tools
{
    /// <summary>
    /// Todo 写入工具 - 更新会话的 Todo 列表
    /// </summary>
    public class TodoWriteTool : ToolBase
    {
        private readonly ITodoStore _todoStore;

        /// <summary>
        /// 创建 TodoWriteTool 实例
        /// </summary>
        public TodoWriteTool(ILogger<TodoWriteTool> logger, ITodoStore todoStore) : base(logger)
        {
            _todoStore = todoStore;
        }

        /// <inheritdoc/>
        public override string Id => "todowrite";

        /// <inheritdoc/>
        public override string Description =>
            "维护当前会话的结构化任务列表，跟踪多步任务进度。\n\n" +
            "- 何时用：3 步以上的多步任务；用户一次给出多个任务；收到新指令需要登记时。\n" +
            "- 何时不用：单步任务、纯问答、3 步内可完成的小事。\n" +
            "- 状态：pending 未开始；in_progress 进行中（一次只保留一个）；completed 已完成；cancelled 不需要了；paused 等待用户。\n" +
            "- 规则：完成立即标 completed，不要批量；发现新任务立即追加。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => BuildObjectSchema(new Dictionary<string, (string, string, bool, string[]?)>
        {
            ["todos"] = ("array", "完整任务列表（整体替换旧列表，不是增量更新）", true, null)
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

            // 保存 Todo 列表（同步请求-响应语义：写失败 → 工具 Failure 返回给 LLM）
            try
            {
                await _todoStore.SaveAsync(context.SessionId,
                    new TodoList { SessionId = context.SessionId, Items = todos });
            }
            catch (Exception ex)
            {
                return Failure($"保存 Todo 失败: {ex.Message}");
            }

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
        private List<Seeing.Agent.Abstractions.Todo.TodoItem> ParseTodos(JsonElement todosElement)
        {
            var todos = new List<Seeing.Agent.Abstractions.Todo.TodoItem>();

            foreach (var item in todosElement.EnumerateArray())
            {
                var todo = new Seeing.Agent.Abstractions.Todo.TodoItem();

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
        private static Seeing.Agent.Abstractions.Todo.TodoPriority ParsePriority(string priority)
        {
            return priority.ToLowerInvariant() switch
            {
                "low" => Seeing.Agent.Abstractions.Todo.TodoPriority.Low,
                "high" => Seeing.Agent.Abstractions.Todo.TodoPriority.High,
                _ => Seeing.Agent.Abstractions.Todo.TodoPriority.Medium
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
                                ["description"] = "任务状态：pending=未开始；in_progress=进行中；completed=已完成；cancelled=不需要了；paused=等待用户"
                            },
                            ["priority"] = new Dictionary<string, object>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "low", "medium", "high" },
                                ["description"] = "优先级：low=低；medium=中（默认）；high=高"
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
}
