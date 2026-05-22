using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Tools.BuiltIn.Plan
{
    /// <summary>
    /// 进入计划模式工具
    /// </summary>
    public class PlanEnterTool : ITool
    {
        public string Id => "plan_enter";
        public string Description => "Enter plan mode to create or edit an execution plan";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new
                {
                    type = "string",
                    description = "Plan name"
                },
                description = new
                {
                    type = "string",
                    description = "Plan description"
                }
            },
            required = new[] { "name" }
        });

        private readonly PlanManager _planManager;

        public PlanEnterTool(PlanManager planManager)
        {
            _planManager = planManager;
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var name = arguments.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? "Untitled Plan"
                : "Untitled Plan";
            var description = arguments.TryGetProperty("description", out var descProp)
                ? descProp.GetString()
                : null;

            var plan = await _planManager.CreatePlanAsync(name, description ?? "", context.SessionId, context.CancellationToken);

            return new ToolResult
            {
                Success = true,
                Title = $"Entered Plan Mode: {name}",
                Output = $"Plan created with ID: {plan.Id}\n\nUse plan_add_task to add tasks to this plan.",
                Metadata = new Dictionary<string, object>
                {
                    ["planId"] = plan.Id,
                    ["planName"] = name
                }
            };
        }
    }

    /// <summary>
    /// 退出计划模式工具
    /// </summary>
    public class PlanExitTool : ITool
    {
        public string Id => "plan_exit";
        public string Description => "Exit plan mode and optionally start execution";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                planId = new
                {
                    type = "string",
                    description = "Plan ID to exit"
                },
                startExecution = new
                {
                    type = "boolean",
                    description = "Whether to start executing the plan"
                }
            },
            required = new[] { "planId" }
        });

        private readonly PlanManager _planManager;

        public PlanExitTool(PlanManager planManager)
        {
            _planManager = planManager;
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var planId = arguments.TryGetProperty("planId", out var idProp)
                ? idProp.GetString()
                : null;
            var startExecution = arguments.TryGetProperty("startExecution", out var startProp)
                && startProp.GetBoolean();

            if (string.IsNullOrEmpty(planId))
            {
                return new ToolResult
                {
                    Success = false,
                    Title = "Plan Exit Failed",
                    Error = "Plan ID is required"
                };
            }

            var plan = await _planManager.GetPlanAsync(planId, context.CancellationToken);
            if (plan == null)
            {
                return new ToolResult
                {
                    Success = false,
                    Title = "Plan Exit Failed",
                    Error = $"Plan not found: {planId}"
                };
            }

            plan.Status = startExecution ? PlanStatus.Active : PlanStatus.Draft;
            await _planManager.UpdatePlanAsync(plan, context.CancellationToken);

            var output = $"Exited plan mode for: {plan.Name}\n";
            output += $"Tasks: {plan.Tasks.Count}\n";
            output += $"Status: {plan.Status}\n";

            if (startExecution)
            {
                var nextTask = await _planManager.GetNextTaskAsync(planId, context.CancellationToken);
                if (nextTask != null)
                {
                    output += $"\nNext task: {nextTask.Title}";
                }
            }

            return new ToolResult
            {
                Success = true,
                Title = $"Exited Plan: {plan.Name}",
                Output = output
            };
        }
    }

    /// <summary>
    /// 添加计划任务工具
    /// </summary>
    public class PlanAddTaskTool : ITool
    {
        public string Id => "plan_add_task";
        public string Description => "Add a task to an execution plan";

        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                planId = new { type = "string", description = "Plan ID" },
                title = new { type = "string", description = "Task title" },
                description = new { type = "string", description = "Task description" },
                priority = new { type = "integer", description = "Task priority (higher = more important)" },
                dependencies = new { type = "array", items = new { type = "string" }, description = "Task IDs this depends on" }
            },
            required = new[] { "planId", "title" }
        });

        private readonly PlanManager _planManager;

        public PlanAddTaskTool(PlanManager planManager)
        {
            _planManager = planManager;
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var planId = arguments.TryGetProperty("planId", out var idProp) ? idProp.GetString() : null;
            var title = arguments.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
            var description = arguments.TryGetProperty("description", out var descProp) ? descProp.GetString() : null;
            var priority = arguments.TryGetProperty("priority", out var priProp) ? priProp.GetInt32() : 0;

            var dependencies = new List<string>();
            if (arguments.TryGetProperty("dependencies", out var depsProp) && depsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in depsProp.EnumerateArray())
                {
                    if (dep.GetString() is string d)
                        dependencies.Add(d);
                }
            }

            if (string.IsNullOrEmpty(planId) || string.IsNullOrEmpty(title))
            {
                return new ToolResult
                {
                    Success = false,
                    Error = "planId and title are required"
                };
            }

            try
            {
                var task = await _planManager.AddTaskAsync(planId, title, description, priority, dependencies, context.CancellationToken);

                return new ToolResult
                {
                    Success = true,
                    Title = $"Added Task: {title}",
                    Output = $"Task added with ID: {task.Id}\nPriority: {priority}\nDependencies: {dependencies.Count}",
                    Metadata = new Dictionary<string, object>
                    {
                        ["taskId"] = task.Id,
                        ["planId"] = planId
                    }
                };
            }
            catch (System.InvalidOperationException ex)
            {
                return new ToolResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }
}
