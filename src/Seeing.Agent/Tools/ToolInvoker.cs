using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Decorators;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Agent.Tools
{
    /// <summary>
    /// 统一工具调用器 - 支持本地工具和 MCP 工具的统一调用
    /// <para>
    /// 功能：
    /// - 工具注册与发现
    /// - Hook 钩子支持
    /// - 重试策略（针对可重试异常）
    /// </para>
    /// <para>
    /// 注意：权限检查由 AgentExecutor.EvaluatePermissionAsync() 统一处理，
    /// 此类不重复检查以避免双重验证。
    /// </para>
    /// </summary>
    public class ToolInvoker
    {
        private readonly ILogger<ToolInvoker> _logger;
        private readonly Core.Hooks.IHookManager _hookManager;
        private readonly IRuleEvaluator? _ruleEvaluator;
        private readonly ConcurrentDictionary<string, ITool> _tools = new();
        private readonly IServiceProvider? _serviceProvider;
        private readonly IToolDecoratorRegistry? _decoratorRegistry;
        private readonly int _maxRetries;
        private readonly TimeSpan _retryDelay;

        public ToolInvoker(
            ILogger<ToolInvoker> logger,
            Core.Hooks.IHookManager hookManager,
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
                var mutable = new Dictionary<string, object?>
                {
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParametersSchema
                };

                await _hookManager.TriggerBlockingAsync(
                    HookRegistry.ToolDefinition,
                    string.Empty,
                    new Dictionary<string, object?> { ["toolId"] = tool.Id },
                    mutable);

                schemas.Add(new FunctionToolSchema
                {
                    Function = new FunctionSchema
                    {
                        Name = tool.Id,
                        Description = mutable["description"]?.ToString() ?? tool.Description,
                        Parameters = mutable["parameters"] is JsonElement je ? je : tool.ParametersSchema
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
            var beforeMutable = new Dictionary<string, object?>
            {
                ["toolId"] = tool.Id,
                ["description"] = tool.Description,
                ["category"] = tool.Category.ToString(),
                ["tags"] = string.Join(",", tool.Tags)
            };

            var beforeResult = await _hookManager.TriggerBlockingAsync(
                HookRegistry.ToolBeforeRegister,
                string.Empty,
                new Dictionary<string, object?>
                {
                    ["toolId"] = tool.Id,
                    ["tool"] = tool
                },
                beforeMutable,
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
            _hookManager.TriggerFireAndForget(
                HookRegistry.ToolAfterRegister,
                string.Empty,
                new Dictionary<string, object?>
                {
                    ["toolId"] = tool.Id,
                    ["tool"] = finalTool
                });
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
        /// 执行工具调用（带重试支持）
        /// <para>
        /// 注意：权限检查由 AgentExecutor.EvaluatePermissionAsync() 统一处理，
        /// 调用此方法前应确保权限已通过验证。
        /// </para>
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            string sessionId = "",
            CancellationToken cancellationToken = default)
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

            // ========== Hook: tool.execute.before ==========
            var argsMutable = new Dictionary<string, object?>
            {
                ["args"] = toolCall.Arguments ?? new JsonElement()
            };

            var hookResult = await _hookManager.TriggerBlockingAsync(
                HookRegistry.ToolExecuteBefore,
                sessionId,
                new Dictionary<string, object?>
                {
                    ["toolId"] = toolId,
                    ["sessionId"] = sessionId,
                    ["callId"] = toolCall.Id
                },
                argsMutable,
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
            var startTime = DateTime.Now;

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
                    if (argsMutable["args"] is JsonElement je)
                    {
                        args = je;
                    }
                    else if (argsMutable["args"] != null)
                    {
                        args = JsonSerializer.SerializeToElement(argsMutable["args"]);
                    }
                    else
                    {
                        args = toolCall.Arguments is JsonElement je2 ? je2 : new JsonElement();
                    }

                    var toolResult = await tool.ExecuteAsync(args, context);

                    // ========== Hook: tool.execute.after ==========
                    _hookManager.TriggerFireAndForget(
                        HookRegistry.ToolExecuteAfter,
                        sessionId,
                        new Dictionary<string, object?>
                        {
                            ["toolId"] = toolId,
                            ["callId"] = toolCall.Id,
                            ["args"] = args
                        },
                        new Dictionary<string, object?>
                        {
                            ["success"] = toolResult.Success,
                            ["output"] = toolResult.Output,
                            ["error"] = toolResult.Error,
                            ["metadata"] = toolResult.Metadata,
                            ["duration"] = DateTime.Now - startTime
                        });

                    toolResult.ToolCallId = toolCall.Id;
                    toolResult.Duration = DateTime.Now - startTime;

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
                    _hookManager.TriggerFireAndForget(
                        HookRegistry.ToolOnError,
                        sessionId,
                        new Dictionary<string, object?>
                        {
                            ["toolId"] = toolId,
                            ["callId"] = toolCall.Id,
                            ["error"] = ex
                        });

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
                Duration = DateTime.Now - startTime
            };
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
            // ========== Hook: tool.batch.before ==========
            var batchMutable = new Dictionary<string, object?>
            {
                ["toolCalls"] = toolCalls.Select(tc => tc.Name).ToList()
            };

            var beforeResult = await _hookManager.TriggerBlockingAsync(
                HookRegistry.ToolBatchBefore,
                sessionId,
                new Dictionary<string, object?>
                {
                    ["count"] = toolCalls.Count,
                    ["toolIds"] = toolCalls.Select(tc => tc.Name).Distinct().ToList()
                },
                batchMutable,
                cancellationToken);

            if (!beforeResult.Continue)
            {
                _logger.LogWarning("批量工具调用被 Hook 中断");
                return toolCalls.Select(tc => new ToolResult
                {
                    Success = false,
                    ToolCallId = tc.Id,
                    Error = "批量工具调用被 Hook 中断"
                }).ToList();
            }

            var results = new List<ToolResult>();

            foreach (var toolCall in toolCalls)
            {
                var result = await ExecuteAsync(toolCall, sessionId, cancellationToken);
                results.Add(result);
            }

            // ========== Hook: tool.batch.after ==========
            _hookManager.TriggerFireAndForget(
                HookRegistry.ToolBatchAfter,
                sessionId,
                new Dictionary<string, object?>
                {
                    ["count"] = toolCalls.Count
                },
                new Dictionary<string, object?>
                {
                    ["results"] = results.Select(r => new Dictionary<string, object?>
                    {
                        ["toolCallId"] = r.ToolCallId,
                        ["success"] = r.Success,
                        ["error"] = r.Error
                    }).ToList()
                });

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
}
