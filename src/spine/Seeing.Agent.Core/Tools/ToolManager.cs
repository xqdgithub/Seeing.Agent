using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Components;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Decorators;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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
    public class ToolManager : IToolManager, IDisposable
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

        // 键：目标 id 集（排序后拼接）+ allowed + denied + 禁用集版本 + Hook 版本
        private readonly ConcurrentDictionary<string, IReadOnlyList<FunctionToolSchema>> _schemaCache = new(StringComparer.Ordinal);
        private const int SchemaCacheCapacity = 32;
        private long _toolStateVersion;

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

            // 先求交集（settled ∩ enabled ∩ allowed − denied），再为存活 id 生成 schema
            var targetIds = new HashSet<string>(settledToolIds, StringComparer.OrdinalIgnoreCase);
            if (agent.AllowedTools is { Count: > 0 } allowed)
            {
                var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
                targetIds.IntersectWith(allowedSet);
            }

            if (agent.DeniedTools is { Count: > 0 } denied)
            {
                targetIds.ExceptWith(new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase));
            }

            var cacheKey = BuildSchemaCacheKey(targetIds, agent);
            if (_schemaCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var schemas = new List<FunctionToolSchema>(targetIds.Count);
            var cacheable = true;

            foreach (var id in targetIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetToolIgnoreCase(id, out var tool))
                    continue;

                if (!IsSchemaCacheable(tool))
                    cacheable = false;

                var schema = await BuildSchemaAsync(tool, cancellationToken).ConfigureAwait(false);
                if (schema != null)
                    schemas.Add(schema);
            }

            var result = schemas
                .OrderBy(x => x.Function!.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

            if (cacheable)
                StoreSchemaCache(cacheKey, result);

            return result;
        }

        /// <summary>构造 schema 缓存键：目标 id 集（排序后拼接）+ allowed + denied + 禁用集版本 + Hook 版本。</summary>
        private string BuildSchemaCacheKey(HashSet<string> targetIds, AgentDefinition agent)
        {
            static string JoinSorted(IEnumerable<string>? ids)
                => string.Join(",", (ids ?? Enumerable.Empty<string>()).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

            var sb = new StringBuilder()
                .Append(Interlocked.Read(ref _toolStateVersion))
                .Append('|')
                .Append(_hookManager.Version)
                .Append('|')
                .Append(JoinSorted(targetIds))
                .Append('|')
                .Append(JoinSorted(agent.AllowedTools))
                .Append('|')
                .Append(JoinSorted(agent.DeniedTools));

            return sb.ToString();
        }

        private void StoreSchemaCache(string cacheKey, IReadOnlyList<FunctionToolSchema> schemas)
        {
            _schemaCache[cacheKey] = schemas;

            // 超限淘汰：移除第一个键（不实现 LRU）
            if (_schemaCache.Count > SchemaCacheCapacity)
            {
                foreach (var key in _schemaCache.Keys)
                {
                    if (key == cacheKey)
                        continue;

                    _schemaCache.TryRemove(key, out _);
                    break;
                }
            }
        }

        /// <summary>按 id 取工具（大小写不敏感，保持原 settled ∩ schema 名称比较语义）。</summary>
        private bool TryGetToolIgnoreCase(string toolId, out ITool tool)
        {
            if (_tools.TryGetValue(toolId, out var found))
            {
                tool = found;
                return true;
            }

            foreach (var pair in _tools)
            {
                if (string.Equals(pair.Key, toolId, StringComparison.OrdinalIgnoreCase))
                {
                    tool = pair.Value;
                    return true;
                }
            }

            tool = null!;
            return false;
        }

        /// <summary>工具能力是否允许其 schema 进入缓存（缺省 true；<c>cacheable=false</c> 时不缓存）。</summary>
        private static bool IsSchemaCacheable(ITool tool)
        {
            var capabilities = tool.Capabilities;
            if (capabilities == null || !capabilities.TryGetValue(ToolCapabilityKeys.Cacheable, out var raw))
                return true;

            return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
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
        public IReadOnlyCollection<ITool> GetRegisteredTools() => _tools.Values.ToList().AsReadOnly();

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

            Interlocked.Increment(ref _toolStateVersion);

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
            Interlocked.Increment(ref _toolStateVersion);
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
        /// 诊断/枚举用；写入 ChatRequest.Tools 须经 ExecutionRunner 结算后的
        /// <see cref="GetToolSchemasAsync(IReadOnlyCollection{string}, AgentDefinition, CancellationToken)"/>。
        /// </summary>
        public async Task<List<FunctionToolSchema>> GetToolSchemasAsync()
        {
            var schemas = new List<FunctionToolSchema>();

            foreach (var toolId in _tools.Keys.ToList())
            {
                if (!_tools.TryGetValue(toolId, out var tool))
                    continue;

                var schema = await BuildSchemaAsync(tool, CancellationToken.None).ConfigureAwait(false);
                if (schema != null)
                    schemas.Add(schema);
            }

            return schemas
                .OrderBy(x => x.Function!.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 为单个工具生成 schema（触发 <c>tool.definition</c> Hook）；工具被禁用时返回 null。
        /// </summary>
        private async Task<FunctionToolSchema?> BuildSchemaAsync(ITool tool, CancellationToken ct)
        {
            // 过滤禁用工具
            if (!IsToolEnabled(tool.Id))
                return null;

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
                mutable,
                ct).ConfigureAwait(false);

            return new FunctionToolSchema
            {
                Function = new FunctionSchema
                {
                    Name = tool.Id,
                    Description = mutable["description"]?.ToString() ?? tool.Description,
                    Parameters = mutable["parameters"] is JsonElement je ? je : tool.ParametersSchema
                }
            };
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

            Interlocked.Increment(ref _toolStateVersion);

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

            Interlocked.Increment(ref _toolStateVersion);
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
        /// 执行工具调用（带重试支持）。
        /// <paramref name="agent"/> 为当前执行所属 Agent，用于资源门 Agent 规则（spec §5.1 步骤 3）
        /// 及 <see cref="ToolContext.Agent"/> 透传。
        /// <paramref name="toolScope"/> 为执行级可见性作用域（可空）；缺失时工具拿不到“同执行放行”能力。
        /// <paramref name="skillSnapshot"/> 为本轮执行钉住的技能快照（可空），透传到 <see cref="ToolContext.SkillSnapshot"/>。
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(
            ToolCall toolCall,
            string sessionId = "",
            CancellationToken cancellationToken = default,
            Func<Seeing.Agent.Abstractions.Events.IMessageEvent, ValueTask>? emitAsync = null,
            IPermissionAuthorizer? permissionAuthorizer = null,
            AgentDefinition? agent = null,
            IExecutionToolScope? toolScope = null,
            Seeing.Agent.Abstractions.Skills.SkillCatalogSnapshot? skillSnapshot = null)
        {
            var toolId = toolCall.Name;
            var agentName = agent?.Name;

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
                    Agent = agent,
                    CancellationToken = cancellationToken,
                    EventSink = sink,
                    MetadataSink = sink,
                    Services = _serviceProvider,
                    PermissionAuthorizer = permissionAuthorizer,
                    ToolScope = toolScope,
                    SkillSnapshot = skillSnapshot
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
        /// 释放工具管理器持有的资源：写盘门（SemaphoreSlim）含系统句柄，须随宿主关闭显式释放。
        /// <para>
        /// 其余字段（<c>_toolStateLock</c>/<c>_registrationLock</c> 为 object，<c>_tools</c> 为
        /// ConcurrentDictionary）均不持有需释放的资源。
        /// </para>
        /// </summary>
        public void Dispose()
        {
            _toolStateWriteGate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
