using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using System.Collections.Concurrent;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 扩展管理器 - 管理 IExtension 的生命周期
    /// <para>
    /// 参考 opencode TuiPluginRuntime 设计，支持：
    /// - 初始化和加载
    /// - 激活/停用
    /// - 组件注册（Agent/Tool/Hook/MCP/Skill）
    /// - 清理资源
    /// </para>
    /// </summary>
    public class ExtensionManager
    {
        private readonly ILogger<ExtensionManager> _logger;
        private readonly ExtensionLoader _loader;
        private readonly ConcurrentDictionary<string, LoadedExtension> _extensions = new();
        private readonly ConcurrentDictionary<string, bool> _enabledStates = new();
        private readonly ConcurrentDictionary<string, List<Func<Task>>> _disposeCallbacks = new();

        private const int DISPOSE_TIMEOUT_MS = 5000;

        /// <summary>
        /// 创建扩展管理器
        /// </summary>
        public ExtensionManager(
            ILogger<ExtensionManager> logger,
            ExtensionLoader loader)
        {
            _logger = logger;
            _loader = loader;
        }

        /// <summary>
        /// 获取所有已加载的扩展
        /// </summary>
        public IReadOnlyCollection<LoadedExtension> GetAll() => _extensions.Values.ToList();

        /// <summary>
        /// 获取指定扩展
        /// </summary>
        public LoadedExtension? Get(string id) => _extensions.TryGetValue(id, out var ext) ? ext : null;

        /// <summary>
        /// 获取扩展状态列表
        /// </summary>
        public IEnumerable<ExtensionStatus> ListStatus()
        {
            return _extensions.Values.Select(e => new ExtensionStatus
            {
                Id = e.Id,
                Source = e.Source,
                Spec = e.Spec,
                Enabled = _enabledStates.GetValueOrDefault(e.Id, true),
                Active = e.Active
            });
        }

        /// <summary>
        /// 初始化并加载所有配置的扩展
        /// </summary>
        /// <param name="specs">插件规格列表</param>
        /// <param name="enabledOverrides">启用状态覆盖</param>
        /// <param name="context">扩展上下文</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task InitializeAsync(
            IEnumerable<PluginSpec> specs,
            Dictionary<string, bool>? enabledOverrides,
            ExtensionContext context,
            CancellationToken cancellationToken = default)
        {
            // 1. 应用启用状态覆盖
            if (enabledOverrides != null)
            {
                foreach (var (id, enabled) in enabledOverrides)
                {
                    _enabledStates[id] = enabled;
                }
            }

            var specList = specs.ToList();
            if (specList.Count == 0)
            {
                _logger.LogDebug("No extensions to load");
                return;
            }

            _logger.LogInformation("Loading {Count} extensions...", specList.Count);

            // 2. 加载所有插件
            var results = await _loader.LoadExternal(specList, context, cancellationToken);

            // 3. 注册所有加载成功的扩展（顺序执行以保证确定性）
            var successCount = 0;
            foreach (var result in results)
            {
                if (!result.Ok)
                {
                    _logger.LogWarning("Failed to load extension (stage: {Stage}): {Error}",
                        result.Stage, result.Error);
                    continue;
                }

                var loaded = result.Loaded!;

                // 检查是否启用
                if (!_enabledStates.GetValueOrDefault(loaded.Id, true))
                {
                    _logger.LogInformation("Extension {Id} is disabled, skipping", loaded.Id);
                    continue;
                }

                // 检查重复
                if (_extensions.ContainsKey(loaded.Id))
                {
                    _logger.LogWarning("Duplicate extension id: {Id}, skipping {Spec}",
                        loaded.Id, loaded.Spec);
                    continue;
                }

                // 注册组件
                if (await RegisterComponents(loaded, context))
                {
                    loaded.Active = true;
                    _extensions[loaded.Id] = loaded;
                    successCount++;
                    _logger.LogInformation("Loaded extension: {Id} ({Source}) v{Version}",
                        loaded.Id, loaded.Source, loaded.Instance.Version);
                }
            }

            _logger.LogInformation("Extensions loaded: {Success}/{Total}",
                successCount, specList.Count);
        }

        /// <summary>
        /// 注册扩展提供的组件
        /// </summary>
        private async Task<bool> RegisterComponents(LoadedExtension ext, ExtensionContext context)
        {
            try
            {
                // 注册 Agent
                foreach (var agent in ext.Instance.GetAgents())
                {
                    var info = CreateAgentInfo(agent, ext.Id);
                    await context.AgentRegistry.RegisterAgentAsync(info);
                    _logger.LogDebug("Registered agent: {Name} from extension {Id}",
                        agent.Name, ext.Id);
                }

                // 注册工具
                foreach (var tool in ext.Instance.GetTools())
                {
                    context.ToolInvoker.RegisterTool(tool);
                    _logger.LogDebug("Registered tool: {Id} from extension {Id}",
                        tool.Id, ext.Id);
                }

                // 注册 Hook
                foreach (var hook in ext.Instance.GetHookHandlers())
                {
                    context.HookManager.Register(hook);
                    _logger.LogDebug("Registered hook: {HookPoint} from extension {Id}",
                        hook.Spec.Point, ext.Id);
                }

                // 连接 MCP Server
                foreach (var mcpConfig in ext.Instance.GetMcpServers())
                {
                    try
                    {
                        await context.McpClientManager.ConnectAsync(mcpConfig);
                        _logger.LogDebug("Connected MCP server: {Name} from extension {Id}",
                            mcpConfig.Name, ext.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to connect MCP server {Name} from extension {Id}",
                            mcpConfig.Name, ext.Id);
                    }
                }

                // 添加 Skill 路径
                foreach (var skillPath in ext.Instance.GetSkillPaths())
                {
                    context.SkillManager.AddSearchDirectory(skillPath);
                    _logger.LogDebug("Added skill path: {Path} from extension {Id}",
                        skillPath, ext.Id);
                }

                // 注册命令
                foreach (var command in ext.Instance.GetCommands())
                {
                    context.CommandRegistry.Register(command);
                    _logger.LogDebug("Registered command: {Name} from extension {Id}",
                        command.Metadata.Name, ext.Id);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register components for extension: {Id}", ext.Id);
                return false;
            }
        }

        /// <summary>
        /// 创建 AgentInfo
        /// </summary>
        private static AgentDefinition CreateAgentInfo(IAgent agent, string extensionId)
        {
            return new AgentDefinition
            {
                Name = agent.Name,
                Description = agent.Description,
                Mode = agent.Mode,
                SystemPrompt = agent.SystemPrompt,
                MaxSteps = agent.MaxSteps,
                PermissionRules = agent.PermissionRules?.ToList() ?? new List<PermissionRuleEntry>(),
                AllowedTools = agent.AllowedTools?.ToList() ?? new List<string>(),
                DeniedTools = agent.DeniedTools?.ToList() ?? new List<string>(),
                IsNative = false,
                Tags = new List<string> { "extension", extensionId },
                AgentFactory = () => agent
            };
        }

        /// <summary>
        /// 激活扩展
        /// </summary>
        public async Task<bool> ActivateAsync(string id, ExtensionContext context)
        {
            if (!_extensions.TryGetValue(id, out var ext))
            {
                return false;
            }

            if (ext.Active)
            {
                return true;
            }

            _enabledStates[id] = true;

            if (await RegisterComponents(ext, context))
            {
                ext.Active = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 停用扩展
        /// </summary>
        public async Task<bool> DeactivateAsync(string id)
        {
            if (!_extensions.TryGetValue(id, out var ext))
            {
                return false;
            }

            _enabledStates[id] = false;

            // 执行清理回调
            if (_disposeCallbacks.TryGetValue(id, out var callbacks))
            {
                foreach (var callback in Enumerable.Reverse(callbacks))
                {
                    await RunCleanupAsync(callback, DISPOSE_TIMEOUT_MS);
                }
                _disposeCallbacks.TryRemove(id, out _);
            }

            // 调用扩展的 DisposeAsync
            try
            {
                await ext.Instance.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extension DisposeAsync failed: {Id}", id);
            }

            ext.Active = false;

            return true;
        }

        /// <summary>
        /// 动态添加扩展
        /// </summary>
        public async Task<bool> AddAsync(string spec, ExtensionContext context)
        {
            var pluginSpec = new PluginSpec { Spec = spec };
            var results = await _loader.LoadExternal(new[] { pluginSpec }, context);

            var result = results.FirstOrDefault();
            if (result == null || !result.Ok)
            {
                _logger.LogWarning("Failed to add extension: {Spec} - {Error}",
                    spec, result?.Error ?? "Unknown error");
                return false;
            }

            var loaded = result.Loaded!;
            if (_extensions.ContainsKey(loaded.Id))
            {
                _logger.LogDebug("Extension already loaded: {Id}", loaded.Id);
                return true;
            }

            if (await RegisterComponents(loaded, context))
            {
                loaded.Active = true;
                _extensions[loaded.Id] = loaded;
                _logger.LogInformation("Added extension: {Id}", loaded.Id);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理所有扩展
        /// </summary>
        public async Task DisposeAllAsync()
        {
            _logger.LogInformation("Disposing {Count} extensions...", _extensions.Count);

            foreach (var ext in _extensions.Values.Reverse())
            {
                try
                {
                    await ext.Instance.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose extension: {Id}", ext.Id);
                }
            }

            _extensions.Clear();
            _disposeCallbacks.Clear();

            _logger.LogInformation("All extensions disposed");
        }

        /// <summary>
        /// 注册清理回调
        /// </summary>
        public void RegisterDisposeCallback(string extensionId, Func<Task> callback)
        {
            var list = _disposeCallbacks.GetOrAdd(extensionId, _ => new List<Func<Task>>());
            list.Add(callback);
        }

        private async Task RunCleanupAsync(Func<Task> cleanup, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                var task = cleanup();
                var completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs, cts.Token));

                if (completedTask != task)
                {
                    _logger.LogWarning("Cleanup callback timed out after {Timeout}ms", timeoutMs);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup callback failed");
            }
        }
    }
}