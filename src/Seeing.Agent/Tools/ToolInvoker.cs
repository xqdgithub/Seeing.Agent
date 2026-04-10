using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Decorators;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using Seeing.Agent.Middlewares;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace Seeing.Agent.Tools
{
    /// <summary>
    /// 统一工具调用器 - 支持本地工具和 MCP 工具的统一调用
    /// <para>
    /// Oracle 评审建议：
    /// - 工具级权限检查在 ToolInvoker 内部实现
    /// - 重试策略针对工具执行失败
    /// </para>
    /// </summary>
    public class ToolInvoker
    {
        private readonly ILogger<ToolInvoker> _logger;
        private readonly IHookManager _hookManager;
        private readonly IRuleEvaluator? _ruleEvaluator;
        private readonly ConcurrentDictionary<string, ITool> _tools = new();
        private readonly IServiceProvider? _serviceProvider;
        private readonly IToolDecoratorRegistry? _decoratorRegistry;
        private readonly int _maxRetries;
        private readonly TimeSpan _retryDelay;

        public ToolInvoker(
            ILogger<ToolInvoker> logger,
            IHookManager hookManager,
            IServiceProvider? serviceProvider = null,
            IToolDecoratorRegistry? decoratorRegistry = null,
            IRuleEvaluator? ruleEvaluator = null,
            int maxRetries = 3,
            TimeSpan? retryDelay = null)
        {
            _logger = logger;
            _hookManager = hookManager;
            _serviceProvider = serviceProvider;
            _decoratorRegistry = decoratorRegistry;
            _ruleEvaluator = ruleEvaluator;
            _maxRetries = maxRetries;
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        }

        // Primary tools that are allowed in Primary mode
        private static readonly HashSet<string> PrimaryTools = new HashSet<string>(new[]
        {
            "write", "edit", "bash", "question", "plan_enter"
        }, System.StringComparer.OrdinalIgnoreCase);

        // SubAgent specific tools
        private static readonly HashSet<string> SubAgentTools = new HashSet<string>(new[]
        {
            "read", "grep", "glob", "webfetch", "websearch"
        }, System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Get tool schemas filtered by AgentMode (async)
        /// </summary>
        public async Task<List<FunctionToolSchema>> GetToolSchemasForModeAsync(AgentMode mode)
        {
            // Retrieve all schemas first, including hooks processing
            var allSchemas = await GetToolSchemasAsync();

            // Build allowed set based on mode
            HashSet<string> allowed = mode switch
            {
                AgentMode.Primary => new HashSet<string>(PrimaryTools.Union(SubAgentTools), StringComparer.OrdinalIgnoreCase),
                AgentMode.SubAgent => new HashSet<string>(SubAgentTools, StringComparer.OrdinalIgnoreCase),
                AgentMode.All => new HashSet<string>(allSchemas.Select(s => s.Function.Name), StringComparer.OrdinalIgnoreCase),
                _ => new HashSet<string>(PrimaryTools.Union(SubAgentTools), StringComparer.OrdinalIgnoreCase)
            };

            // Filter schemas by allowed tool IDs
            return allSchemas.Where(s => allowed.Contains(s.Function.Name)).ToList();
        }

        /// <summary>
        /// Get tool schemas filtered by AgentMode (sync version)
        /// Default mode is Primary
        /// </summary>
        public List<FunctionToolSchema> GetToolSchemasForMode(AgentMode mode)
        {
            return GetToolSchemasForModeAsync(mode).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Overload: default to Primary mode
        /// </summary>
        public List<FunctionToolSchema> GetToolSchemasForMode()
        {
            return GetToolSchemasForMode(AgentMode.Primary);
        }

        /// <summary>
        /// 按 Agent 的 <see cref="AgentInfo.Mode"/> 与 Allowed/Denied 工具列表筛选 Schema（异步）
        /// </summary>
        public async Task<List<FunctionToolSchema>> GetToolSchemasForAgentAsync(AgentInfo agent)
        {
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));

            var baseList = await GetToolSchemasForModeAsync(agent.Mode);
            return FilterSchemasByAgentToolLists(baseList, agent.AllowedTools, agent.DeniedTools);
        }

        /// <summary>
        /// 按 Agent 的 Mode 与 Allowed/Denied 工具列表筛选 Schema（同步）
        /// </summary>
        public List<FunctionToolSchema> GetToolSchemasForAgent(AgentInfo agent)
        {
            return GetToolSchemasForAgentAsync(agent).GetAwaiter().GetResult();
        }

        private static List<FunctionToolSchema> FilterSchemasByAgentToolLists(
            List<FunctionToolSchema> schemas,
            IList<string>? allowedNullable,
            IList<string>? deniedNullable)
        {
            var deniedSet = new HashSet<string>(deniedNullable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            IEnumerable<FunctionToolSchema> q = schemas.Where(s =>
                s.Function != null &&
                !deniedSet.Contains(s.Function.Name));

            if (allowedNullable is { Count: > 0 } allowed)
            {
                var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
                q = q.Where(s => allowedSet.Contains(s.Function!.Name));
            }

            return q.ToList();
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
        /// 执行工具调用（带权限检查和重试）
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            string sessionId = "",
            CancellationToken cancellationToken = default,
            AgentPermissionConfig? permissionConfig = null)
        {
            var toolId = toolCall.Name;
            
            if (!_tools.TryGetValue(toolId, out var tool))
            {
                return new ToolResult
                {
                    Success = false,
                    ToolCallId = toolCall.Id,
                    Error = $"工具不存在: {toolId}"
                };
            }

            // ========== Oracle 建议：工具级权限检查 ==========
            if (!await CheckToolPermissionAsync(toolId, permissionConfig, cancellationToken))
            {
                return new ToolResult
                {
                    Success = false,
                    ToolCallId = toolCall.Id,
                    Error = $"[权限拒绝] 工具 '{toolId}' 不在允许列表中或被禁止"
                };
            }

            // 规则引擎评估
            if (_ruleEvaluator != null)
            {
                var decision = _ruleEvaluator.EvaluateTool(toolId);
                if (decision.Action == PermissionAction.Deny)
                {
                    return new ToolResult
                    {
                        Success = false,
                        ToolCallId = toolCall.Id,
                        Error = $"[规则拒绝] {decision.Reason ?? "权限规则拒绝"}"
                    };
                }
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
                return new ToolResult
                {
                    Success = false,
                    ToolCallId = toolCall.Id,
                    Error = "工具调用被 Hook 中断"
                };
            }

            // ========== Oracle 建议：工具级重试策略 ==========
            Exception? lastException = null;
            var startTime = DateTime.UtcNow;
            
            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
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

                    // ========== Hook: tool.execute.after ==========
                    var afterOutput = new Dictionary<string, object>
                    {
                        ["output"] = toolResult.Output,
                        ["error"] = toolResult.Error,
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
                    toolResult.Output = afterOutput["output"]?.ToString() ?? toolResult.Output;
                    toolResult.Error = afterOutput["error"]?.ToString() ?? toolResult.Error;
                    toolResult.ToolCallId = toolCall.Id;
                    toolResult.Duration = DateTime.UtcNow - startTime;

                    return toolResult;
                }
                catch (Exception ex) when (attempt < _maxRetries - 1 && IsRetryableException(ex))
                {
                    lastException = ex;
                    var delay = _retryDelay * (attempt + 1); // 指数退避

                    _logger.LogWarning(
                        "[ToolRetry] 工具 {ToolId} 执行失败，准备重试: Attempt={Attempt}/{Max}, Delay={Delay}ms",
                        toolId, attempt + 1, _maxRetries, delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "工具执行失败: {ToolId}", toolId);

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

                    throw;
                }
            }

            // 所有重试都失败了
            _logger.LogError(
                "[ToolRetry] 工具 {ToolId} 重试耗尽: MaxRetries={Max}",
                toolId, _maxRetries);

            return new ToolResult
            {
                Success = false,
                ToolCallId = toolCall.Id,
                Error = $"[重试耗尽] {lastException?.Message}",
                Duration = DateTime.UtcNow - startTime
            };
        }

        /// <summary>
        /// 检查工具权限
        /// </summary>
        private async Task<bool> CheckToolPermissionAsync(
            string toolId,
            AgentPermissionConfig? config,
            CancellationToken cancellationToken)
        {
            if (config == null)
            {
                // 无权限配置，默认允许
                return true;
            }

            // 检查黑名单
            if (config.DeniedTools.Contains(toolId, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[Permission] 工具 {ToolId} 在黑名单中", toolId);
                return false;
            }

            // 检查白名单（如果有）
            if (config.AllowedTools.Count > 0 &&
                !config.AllowedTools.Contains(toolId, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[Permission] 工具 {ToolId} 不在白名单中", toolId);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断异常是否可重试
        /// </summary>
        private static bool IsRetryableException(Exception ex)
        {
            return ex is TimeoutException
                || ex is HttpRequestException
                || ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested
                || ex is IOException;
        }

        /// <summary>
        /// 批量执行工具调用
        /// </summary>
        public async Task<List<ToolResult>> ExecuteAsync(
            List<ToolCall> toolCalls,
            string sessionId = "",
            CancellationToken cancellationToken = default)
        {
            var results = new List<ToolResult>();

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
        public async Task<ToolResult> ExecuteAsync(
            string toolId,
            Dictionary<string, object?> args,
            string sessionId = "",
            CancellationToken cancellationToken = default)
        {
            var toolCall = new ToolCall
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = "function",
                Function = new FunctionCall
                {
                    Name = toolId,
                    Arguments = JsonSerializer.Serialize(args)
                }
            };

            return await ExecuteAsync(toolCall, sessionId, cancellationToken);
        }
    }

    /// <summary>
    /// Agent 权限配置 - 用于工具调用的权限检查
    /// </summary>
    public class AgentPermissionConfig
    {
        /// <summary>允许的工具（白名单）</summary>
        public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

        /// <summary>禁止的工具（黑名单）</summary>
        public IReadOnlyList<string> DeniedTools { get; init; } = Array.Empty<string>();

        /// <summary>权限规则</summary>
        public IReadOnlyList<PermissionRule> PermissionRules { get; init; } = Array.Empty<PermissionRule>();
    }
}
