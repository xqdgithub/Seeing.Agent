using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Abstractions.Agents;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Core.Decorators;
using Seeing.Agent.Abstractions.Components;
using System.Collections.Concurrent;
using System.Text.Json;

using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Core;
namespace Seeing.Agent.Core.Tools
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
    public class ToolManager : IToolManager
    {
        private readonly ILogger<ToolManager> _logger;
        private readonly Abstractions.Hooks.IHookManager _hookManager;
        private readonly IRuleEvaluator? _ruleEvaluator;
        private readonly ConcurrentDictionary<string, ITool> _tools = new();
        private readonly IServiceProvider? _serviceProvider;
        private readonly IToolDecoratorRegistry? _decoratorRegistry;
        private readonly IWorkspaceProvider? _workspace;
        private readonly IToolPermissionPolicy? _permissionPolicy;
        private readonly object _toolStateLock = new();
        // 串行化 tool-state.json 的“快照 + 写盘”段，避免并发写同一文件抛出 IOException
        private readonly SemaphoreSlim _toolStateWriteGate = new(1, 1);
        // 收窄“ID 冲突检查 + 赋值”为原子段，避免并发注册同 ID 时的检查-写入竞态
        private readonly object _registrationLock = new();
        private HashSet<string> _userDisabledTools = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _projectDisabledTools = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>初始化工具管理器，注入日志器、Hook 管理器及装饰器/规则/权限等可选依赖。</summary>
        public ToolManager(
            ILogger<ToolManager> logger,
            Abstractions.Hooks.IHookManager hookManager,
            IServiceProvider? serviceProvider = null,
            IToolDecoratorRegistry? decoratorRegistry = null,
            IRuleEvaluator? ruleEvaluator = null,
            IWorkspaceProvider? workspace = null,
            IToolPermissionPolicy? permissionPolicy = null)
        {
            _logger = logger;
            _hookManager = hookManager;
            _serviceProvider = serviceProvider;
            _decoratorRegistry = decoratorRegistry;
            _ruleEvaluator = ruleEvaluator;
            _workspace = workspace;
            _permissionPolicy = permissionPolicy;
        }

        /// <summary>
        /// 获取全部已注册工具 schema。Mode 参数保留兼容，不再过滤；请用 Agent Allowed/Denied。
        /// </summary>
        public Task<List<FunctionToolSchema>> GetToolSchemasForModeAsync(AgentMode mode = AgentMode.Primary)
        {
            _ = mode;
            return GetToolSchemasAsync();
        }

        /// <summary>
        /// 按 Agent 的 Allowed/Denied 工具列表筛选 Schema。Mode 不再硬编码过滤工具集。
        /// 诊断/测试用；执行主路径须用 <see cref="GetToolSchemasAsync(IReadOnlyCollection{string}, AgentDefinition, CancellationToken)"/>。
        /// </summary>
        public async Task<List<FunctionToolSchema>> GetToolSchemasForAgentAsync(AgentDefinition agent)
        {
            if (agent == null)
                throw new ArgumentNullException(nameof(agent));

            var baseList = await GetToolSchemasAsync().ConfigureAwait(false);
            return FilterSchemasByAgentToolLists(baseList, agent.AllowedTools, agent.DeniedTools);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<FunctionToolSchema>> GetToolSchemasAsync(
            IReadOnlyCollection<string> settledToolIds,
            AgentDefinition agent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settledToolIds);
            ArgumentNullException.ThrowIfNull(agent);
            cancellationToken.ThrowIfCancellationRequested();

            var settled = new HashSet<string>(settledToolIds, StringComparer.OrdinalIgnoreCase);
            var all = await GetToolSchemasAsync().ConfigureAwait(false);
            var layer12 = all.Where(s =>
                s.Function != null &&
                settled.Contains(s.Function.Name)).ToList();
            return FilterSchemasByAgentToolLists(layer12, agent.AllowedTools, agent.DeniedTools);
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

        /// <summary>合并后的禁用工具集合（用户级 + 项目级）</summary>
        public IReadOnlySet<string> DisabledTools
        {
            get
            {
                lock (_toolStateLock)
                {
                    return _userDisabledTools
                        .Union(_projectDisabledTools)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>状态变更事件</summary>
        public event Action? OnToolStateChanged;

        /// <summary>检查工具是否启用</summary>
        public bool IsToolEnabled(string toolId)
        {
            lock (_toolStateLock)
            {
                return !_userDisabledTools.Contains(toolId) && !_projectDisabledTools.Contains(toolId);
            }
        }

        /// <summary>获取所有工具（包括禁用的，供 UI 使用）</summary>
        public IReadOnlyCollection<ITool> GetToolsIncludingDisabled() => _tools.Values.ToList().AsReadOnly();

        /// <summary>加载工具禁用状态（双层级）</summary>
        public async Task LoadToolStateAsync(CancellationToken ct = default)
        {
            if (_workspace == null) return;

            var userPath = Path.Combine(_workspace.UserSeeingDirectory, "tool-state.json");
            var projectPath = Path.Combine(_workspace.ProjectSeeingDirectory, "tool-state.json");

            var userDisabled = await LoadDisabledSetAsync(userPath, ct).ConfigureAwait(false);
            var projectDisabled = await LoadDisabledSetAsync(projectPath, ct).ConfigureAwait(false);

            lock (_toolStateLock)
            {
                _userDisabledTools = userDisabled;
                _projectDisabledTools = projectDisabled;
            }

            _logger.LogInformation("工具禁用状态已加载，用户级: {UserCount} 个，项目级: {ProjectCount} 个",
                _userDisabledTools.Count, _projectDisabledTools.Count);
        }

        /// <summary>设置工具启用/禁用状态</summary>
        public async Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default)
        {
            if (_workspace == null) return;

            ConfigLevel level;
            lock (_toolStateLock)
            {
                level = _projectDisabledTools.Contains(toolId) ? ConfigLevel.Project : ConfigLevel.User;
            }

            await SaveToolStateAsync(toolId, enabled, level, ct).ConfigureAwait(false);
            OnToolStateChanged?.Invoke();
        }

        /// <summary>保存工具禁用状态到指定级别</summary>
        private async Task SaveToolStateAsync(string toolId, bool enabled, ConfigLevel level, CancellationToken ct)
        {
            if (_workspace == null) return;

            var filePath = level == ConfigLevel.User
                ? Path.Combine(_workspace.UserSeeingDirectory, "tool-state.json")
                : Path.Combine(_workspace.ProjectSeeingDirectory, "tool-state.json");

            // 快照与写盘整体串行，保证落盘内容与内存最终状态一致，且同文件不并发写
            await _toolStateWriteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                List<string> snapshot;
                lock (_toolStateLock)
                {
                    var targetSet = level == ConfigLevel.User ? _userDisabledTools : _projectDisabledTools;

                    if (enabled)
                        targetSet.Remove(toolId);
                    else
                        targetSet.Add(toolId);

                    // 快照在锁内生成，写盘使用快照，避免集合在序列化期间被并发修改
                    snapshot = targetSet.ToList();
                }

                await SaveDisabledSetAsync(filePath, snapshot, ct).ConfigureAwait(false);
            }
            finally
            {
                _toolStateWriteGate.Release();
            }
        }

        /// <summary>从文件加载禁用 ID 集合</summary>
        private static async Task<HashSet<string>> LoadDisabledSetAsync(string filePath, CancellationToken ct)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(filePath))
                return result;

            try
            {
                var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<DisabledToolsData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (data?.DisabledTools != null)
                {
                    foreach (var id in data.DisabledTools)
                    {
                        result.Add(id);
                    }
                }
            }
            catch (Exception)
            {
                // 加载失败时返回空集合
            }

            return result;
        }

        /// <summary>保存禁用 ID 快照到文件</summary>
        private static async Task SaveDisabledSetAsync(string filePath, IReadOnlyList<string> disabledTools, CancellationToken ct)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var data = new DisabledToolsData { DisabledTools = disabledTools.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
        }

        /// <summary>禁用工具数据模型</summary>
        private sealed class DisabledToolsData
        {
            public List<string>? DisabledTools { get; set; }
        }

        /// <summary>
        /// 获取工具 Schema 列表（用于 LLM function calling，带 Hook 支持）。
        /// 诊断/枚举用；写入 ChatRequest.Tools 须经 ExecutionJobService 结算后的
        /// <see cref="GetToolSchemasAsync(IReadOnlyCollection{string}, AgentDefinition, CancellationToken)"/>。
        /// </summary>
        public async Task<List<FunctionToolSchema>> GetToolSchemasAsync()
        {
            var schemas = new List<FunctionToolSchema>();

            foreach (var tool in _tools.Values)
            {
                // 过滤禁用工具
                if (!IsToolEnabled(tool.Id))
                    continue;

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
                    mutable).ConfigureAwait(false);

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
        /// 注册工具（带 Hook 支持，自动应用装饰器链；异步）。
        /// </summary>
        public async Task RegisterToolAsync(ITool tool, CancellationToken cancellationToken = default)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Id))
            {
                _logger.LogWarning("尝试注册无效工具，已跳过");
                return;
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
                cancellationToken).ConfigureAwait(false);

            if (!beforeResult.Continue)
            {
                _logger.LogWarning("工具注册被 Hook 拒绝: {ToolId}", tool.Id);
                return;
            }

            // 应用装饰器
            var finalTool = _decoratorRegistry?.Apply(tool) ?? tool;

            // 冲突检查 + 赋值收窄为同一原子段，避免并发注册同 ID 时检查-写入竞态
            lock (_registrationLock)
            {
                if (_tools.ContainsKey(tool.Id))
                {
                    _logger.LogWarning("工具 ID 冲突：'{ToolId}' 已存在，将被新工具覆盖。请检查是否重复注册或命名冲突。",
                        tool.Id);
                }

                _tools[tool.Id] = finalTool;
            }

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
        /// 批量注册工具（异步）
        /// </summary>
        public async Task RegisterToolsAsync(IEnumerable<ITool> tools, CancellationToken cancellationToken = default)
        {
            foreach (var tool in tools)
            {
                await RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 从类型注册工具（使用注解发现，异步）
        /// </summary>
        public async Task RegisterToolsFromTypeAsync(Type type, CancellationToken cancellationToken = default)
        {
            var tools = Discovery.ToolWrapperFactory.CreateTools(type, null, _serviceProvider);
            await RegisterToolsAsync(tools, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 从类型注册工具（使用注解发现，异步）
        /// </summary>
        public Task RegisterToolsFromTypeAsync<T>(CancellationToken cancellationToken = default)
            => RegisterToolsFromTypeAsync(typeof(T), cancellationToken);

        /// <summary>
        /// 注销工具；未注册时返回 false，并通知工具集变更。
        /// </summary>
        public bool UnregisterTool(string toolId)
        {
            if (string.IsNullOrEmpty(toolId))
                return false;

            if (!_tools.TryRemove(toolId, out _))
                return false;

            _logger.LogDebug("注销工具: {ToolId}", toolId);
            OnToolStateChanged?.Invoke();
            return true;
        }

        /// <inheritdoc />
        public Task<bool> UnregisterToolAsync(string toolId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UnregisterTool(toolId));
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
        public Task<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            string sessionId = "",
            CancellationToken cancellationToken = default,
            Func<Seeing.Agent.Abstractions.Events.IMessageEvent, ValueTask>? emitAsync = null,
            IPermissionAuthorizer? permissionAuthorizer = null)
            => ExecuteAsync(toolCall, sessionId, cancellationToken, emitAsync, permissionAuthorizer, agentName: null);

        /// <summary>
        /// 执行工具调用（带重试支持）。
        /// <paramref name="agentName"/> 为当前执行所属 Agent，用于资源门 Agent 规则（spec §5.1 步骤 3）。
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            string sessionId,
            CancellationToken cancellationToken,
            Func<Seeing.Agent.Abstractions.Events.IMessageEvent, ValueTask>? emitAsync,
            IPermissionAuthorizer? permissionAuthorizer,
            string? agentName)
        {
            var toolId = toolCall.Name;

            // 检查工具是否启用
            if (!IsToolEnabled(toolId))
            {
                _logger.LogWarning("尝试执行已禁用的工具: {ToolId}", toolId);
                return new ToolResult
                {
                    Success = false,
                    ToolCallId = toolCall.Id,
                    Title = "工具已禁用",
                    Error = $"Tool '{toolId}' is disabled. Enable it in the Tools settings."
                };
            }

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
                cancellationToken).ConfigureAwait(false);

            if (!hookResult.Continue)
            {
                return new ToolResult
                {
                    Success = false,
                    ToolCallId = toolCall.Id,
                    Error = "工具调用被 Hook 中断"
                };
            }

            // Resolve args from hook-mutable (moved up for permission check access)
            JsonElement resolvedArgs;
            if (argsMutable["args"] is JsonElement je)
                resolvedArgs = je;
            else if (argsMutable["args"] != null)
                resolvedArgs = JsonSerializer.SerializeToElement(argsMutable["args"]);
            else
                resolvedArgs = toolCall.Arguments is JsonElement je2 ? je2 : new JsonElement();

            // ========== Resource-level permission check ==========
            if (permissionAuthorizer != null && _permissionPolicy != null)
            {
                var check = _permissionPolicy.Evaluate(toolId, resolvedArgs);
                if (check != null)
                {
                    var resolution = await permissionAuthorizer.AuthorizeAsync(new PermissionRequest
                    {
                        SessionId = string.IsNullOrEmpty(sessionId) ? permissionAuthorizer.SessionId : sessionId,
                        CallId = toolCall.Id,
                        AgentName = agentName,
                        PermissionKind = check.PermissionKind,
                        Resource = check.Resource,
                        Patterns = check.Patterns ?? new List<string>(),
                        Metadata = check.Metadata ?? new Dictionary<string, object>()
                    }, cancellationToken).ConfigureAwait(false);

                    if (resolution.Decision != PermissionEffect.Allow)
                    {
                        return new ToolResult
                        {
                            Success = false,
                            ToolCallId = toolCall.Id,
                            Error = resolution.Reason ?? "Permission denied"
                        };
                    }
                }
            }

            // 执行工具（装饰器链已在 RegisterToolAsync 时 Apply，处理重试/超时/缓存）
            var startTime = DateTime.Now;

            try
            {
                // EventSink：进度/流式走 ToolCallEvent。MetadataSink 未接线（setMetadata=null），勿依赖。
                var sink = emitAsync is null ? null : new ToolSinkAdapter(emitAsync, setMetadata: null);
                var context = new ToolContext
                {
                    SessionId = sessionId,
                    CallId = toolCall.Id,
                    CancellationToken = cancellationToken,
                    EventSink = sink,
                    MetadataSink = sink,
                    Services = _serviceProvider,
                    PermissionAuthorizer = permissionAuthorizer
                };

                var toolResult = await tool.ExecuteAsync(resolvedArgs, context).ConfigureAwait(false);

                // ========== Hook: tool.execute.after ==========
                _hookManager.TriggerFireAndForget(
                    HookRegistry.ToolExecuteAfter,
                    sessionId,
                    new Dictionary<string, object?>
                    {
                        ["toolId"] = toolId,
                        ["callId"] = toolCall.Id,
                        ["args"] = resolvedArgs
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
                cancellationToken).ConfigureAwait(false);

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
                var result = await ExecuteAsync(toolCall, sessionId, cancellationToken).ConfigureAwait(false);
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

            return await ExecuteAsync(toolCall, sessionId, cancellationToken).ConfigureAwait(false);
        }
    }
}
