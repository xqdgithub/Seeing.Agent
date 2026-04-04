using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Decorators;
using Seeing.Agent.Hooks;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Agent.Tools
{
    /// <summary>
    /// 统一工具调用器 - 支持本地工具和 MCP 工具的统一调用
    /// </summary>
    public class ToolInvoker
    {
        private readonly ILogger<ToolInvoker> _logger;
        private readonly IHookManager _hookManager;
        private readonly ConcurrentDictionary<string, ITool> _tools = new();
        private readonly IServiceProvider? _serviceProvider;
        private readonly IToolDecoratorRegistry? _decoratorRegistry;

        public ToolInvoker(
            ILogger<ToolInvoker> logger,
            IHookManager hookManager,
            IServiceProvider? serviceProvider = null,
            IToolDecoratorRegistry? decoratorRegistry = null)
        {
            _logger = logger;
            _hookManager = hookManager;
            _serviceProvider = serviceProvider;
            _decoratorRegistry = decoratorRegistry;
        }

        /// <summary>
        /// 获取所有已注册的工具
        /// </summary>
        public IReadOnlyCollection<ITool> GetTools() => _tools.Values.ToList().AsReadOnly();

        /// <summary>
        /// 获取工具 Schema 列表（用于 LLM function calling，带 Hook 支持）
        /// </summary>
        public async Task<List<FunctionToolSchema>> GetToolSchemasAsync()
        {
            var schemas = new List<FunctionToolSchema>();

            foreach (var tool in _tools.Values)
            {
                // ========== Hook: tool.definition ==========
                var output = new Dictionary<string, object>
                {
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParametersSchema
                };

                await _hookManager.TriggerAsync(
                    HookPoints.ToolDefinition,
                    new Dictionary<string, object> { ["toolId"] = tool.Id },
                    output);

                schemas.Add(new FunctionToolSchema
                {
                    Function = new FunctionSchema
                    {
                        Name = tool.Id,
                        Description = output["description"]?.ToString() ?? tool.Description,
                        Parameters = output["parameters"] is JsonElement je ? je : tool.ParametersSchema
                    }
                });
            }

            return schemas;
        }

        /// <summary>
        /// 获取工具 Schema 列表（同步版本，向后兼容）
        /// </summary>
        public List<FunctionToolSchema> GetToolSchemas()
        {
            return GetToolSchemasAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// 注册工具（自动应用装饰器）
        /// </summary>
        public void RegisterTool(ITool tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Id))
            {
                _logger.LogWarning("尝试注册无效工具，已跳过");
                return;
            }

            // 同步版本调用异步方法
            RegisterToolAsync(tool).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 注册工具（带 Hook 支持，异步版本）
        /// </summary>
        public async Task RegisterToolAsync(ITool tool, CancellationToken cancellationToken = default)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Id))
            {
                _logger.LogWarning("尝试注册无效工具，已跳过");
                return;
            }

            // 检测工具 ID 冲突
            if (_tools.ContainsKey(tool.Id))
            {
                _logger.LogWarning("工具 ID 冲突：'{ToolId}' 已存在，将被新工具覆盖。请检查是否重复注册或命名冲突。",
                    tool.Id);
            }

            // ========== Hook: tool.before_register ==========
            var beforeOutput = new Dictionary<string, object>
            {
                ["toolId"] = tool.Id,
                ["description"] = tool.Description,
                ["category"] = tool.Category.ToString(),
                ["tags"] = string.Join(",", tool.Tags)
            };

            var beforeResult = await _hookManager.TriggerAsync(
                HookPoints.ToolBeforeRegister,
                new Dictionary<string, object>
                {
                    ["toolId"] = tool.Id,
                    ["tool"] = tool
                },
                beforeOutput,
                cancellationToken);

            if (!beforeResult.Continue)
            {
                _logger.LogWarning("工具注册被 Hook 拒绝: {ToolId}", tool.Id);
                return;
            }

            // 应用装饰器
            var finalTool = _decoratorRegistry?.Apply(tool) ?? tool;

            _tools[tool.Id] = finalTool;
            _logger.LogDebug("注册工具: {ToolId}, Tags={Tags}, Category={Category}",
                tool.Id, string.Join(",", tool.Tags), tool.Category);

            // ========== Hook: tool.after_register ==========
            await _hookManager.TriggerAsync(
                HookPoints.ToolAfterRegister,
                new Dictionary<string, object>
                {
                    ["toolId"] = tool.Id,
                    ["tool"] = finalTool
                },
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// 批量注册工具
        /// </summary>
        public void RegisterTools(IEnumerable<ITool> tools)
        {
            foreach (var tool in tools)
            {
                RegisterTool(tool);
            }
        }

        /// <summary>
        /// 从类型注册工具（使用注解发现）
        /// </summary>
        public void RegisterToolsFromType(Type type)
        {
            var tools = Discovery.ToolWrapperFactory.CreateTools(type, null, _serviceProvider);
            RegisterTools(tools);
        }

        /// <summary>
        /// 从类型注册工具（使用注解发现）
        /// </summary>
        public void RegisterToolsFromType<T>()
        {
            RegisterToolsFromType(typeof(T));
        }

        /// <summary>
        /// 注销工具
        /// </summary>
        public bool UnregisterTool(string toolId)
        {
            return _tools.TryRemove(toolId, out _);
        }

        /// <summary>
        /// 检查工具是否存在
        /// </summary>
        public bool HasTool(string toolId) => _tools.ContainsKey(toolId);

        /// <summary>
        /// 获取工具
        /// </summary>
        public ITool? GetTool(string toolId) => _tools.TryGetValue(toolId, out var tool) ? tool : null;

        /// <summary>
        /// 按标签获取工具
        /// </summary>
        public IEnumerable<ITool> GetToolsByTag(string tag)
        {
            return _tools.Values.Where(t => t.Tags.Contains(tag));
        }

        /// <summary>
        /// 按分类获取工具
        /// </summary>
        public IEnumerable<ITool> GetToolsByCategory(ToolCategory category)
        {
            return _tools.Values.Where(t => t.Category == category);
        }

        /// <summary>
        /// 执行工具调用
        /// </summary>
        public async Task<ToolCallResult> ExecuteAsync(
            ToolCall toolCall,
            string sessionId = "",
            CancellationToken cancellationToken = default)
        {
            var result = new ToolCallResult
            {
                ToolCall = toolCall
            };

            var toolId = toolCall.Name;
            if (!_tools.TryGetValue(toolId, out var tool))
            {
                result.Success = false;
                result.Message = $"工具不存在: {toolId}";
                return result;
            }

            // ========== Hook: tool.execute.before ==========
            var argsOutput = new Dictionary<string, object>
            {
                ["args"] = toolCall.Arguments ?? new JsonElement()
            };

            var hookResult = await _hookManager.TriggerAsync(
                HookPoints.ToolExecuteBefore,
                new Dictionary<string, object>
                {
                    ["toolId"] = toolId,
                    ["sessionId"] = sessionId,
                    ["callId"] = toolCall.Id
                },
                argsOutput,
                cancellationToken);

            if (!hookResult.Continue)
            {
                result.Success = false;
                result.Message = "工具调用被 Hook 中断";
                return result;
            }

            try
            {
                var context = new ToolContext
                {
                    SessionId = sessionId,
                    CallId = toolCall.Id,
                    CancellationToken = cancellationToken
                };

                // 使用可能被 Hook 修改的参数
                JsonElement args;
                if (argsOutput["args"] is JsonElement je)
                {
                    args = je;
                }
                else if (argsOutput["args"] != null)
                {
                    args = JsonSerializer.SerializeToElement(argsOutput["args"]);
                }
                else
                {
                    args = toolCall.Arguments is JsonElement je2 ? je2 : new JsonElement();
                }

                var toolResult = await tool.ExecuteAsync(args, context);

                result.Success = toolResult.Success;
                result.CallResult = toolResult.Output;
                result.Message = toolResult.Title;

                // ========== Hook: tool.execute.after ==========
                var afterOutput = new Dictionary<string, object>
                {
                    ["title"] = result.Message,
                    ["output"] = result.CallResult ?? string.Empty,
                    ["metadata"] = toolResult.Metadata
                };

                await _hookManager.TriggerAsync(
                    HookPoints.ToolExecuteAfter,
                    new Dictionary<string, object>
                    {
                        ["toolId"] = toolId,
                        ["sessionId"] = sessionId,
                        ["callId"] = toolCall.Id,
                        ["args"] = args
                    },
                    afterOutput,
                    cancellationToken);

                // 应用 Hook 修改后的输出
                result.Message = afterOutput["title"]?.ToString() ?? result.Message;
                result.CallResult = afterOutput["output"]?.ToString() ?? result.CallResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工具执行失败: {ToolId}", toolId);
                result.Success = false;
                result.Message = ex.Message;

                // ========== Hook: tool.on_error ==========
                await _hookManager.TriggerAsync(
                    HookPoints.ToolOnError,
                    new Dictionary<string, object>
                    {
                        ["toolId"] = toolId,
                        ["sessionId"] = sessionId,
                        ["callId"] = toolCall.Id,
                        ["error"] = ex
                    },
                    cancellationToken: cancellationToken);
            }

            return result;
        }

        /// <summary>
        /// 批量执行工具调用
        /// </summary>
        public async Task<List<ToolCallResult>> ExecuteAsync(
            List<ToolCall> toolCalls,
            string sessionId = "",
            CancellationToken cancellationToken = default)
        {
            var results = new List<ToolCallResult>();

            foreach (var toolCall in toolCalls)
            {
                var result = await ExecuteAsync(toolCall, sessionId, cancellationToken);
                results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// 通过字典参数执行工具
        /// </summary>
        public async Task<ToolCallResult> ExecuteAsync(
            string toolId,
            Dictionary<string, object?> args,
            string sessionId = "",
            CancellationToken cancellationToken = default)
        {
            var toolCall = new ToolCall
            {
                Name = toolId,
                Id = Guid.NewGuid().ToString("N"),
                Arguments = JsonSerializer.SerializeToElement(args)
            };

            return await ExecuteAsync(toolCall, sessionId, cancellationToken);
        }
    }
}