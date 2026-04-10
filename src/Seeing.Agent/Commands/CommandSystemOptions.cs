using Microsoft.Extensions.Logging;
using Seeing.Agent.Commands.Discovery;
using System.Reflection;

namespace Seeing.Agent.Commands
{
    /// <summary>
    /// 命令系统配置选项
    /// </summary>
    public class CommandSystemOptions
    {
        /// <summary>需要自动发现命令的程序集列表</summary>
        public List<Assembly> DiscoveryAssemblies { get; set; } = new();

        /// <summary>是否自动注册扩展提供的命令</summary>
        public bool RegisterExtensionCommands { get; set; } = true;

        /// <summary>添加程序集用于命令发现</summary>
        public CommandSystemOptions AddAssembly(Assembly assembly)
        {
            DiscoveryAssemblies.Add(assembly);
            return this;
        }

        /// <summary>添加类型所在的程序集</summary>
        public CommandSystemOptions AddAssemblyFromType<T>()
        {
            DiscoveryAssemblies.Add(typeof(T).Assembly);
            return this;
        }
    }

    /// <summary>
    /// 命令发现初始化器接口
    /// </summary>
    public interface ICommandDiscoveryInitializer
    {
        /// <summary>执行初始化</summary>
        void Initialize();
    }

    /// <summary>
    /// 命令发现初始化器实现
    /// </summary>
    internal class CommandDiscoveryInitializer : ICommandDiscoveryInitializer
    {
        private readonly ICommandRegistry _registry;
        private readonly CommandDiscovery _discovery;
        private readonly IEnumerable<Assembly> _assemblies;
        private readonly IServiceProvider? _services;
        private readonly ILogger? _logger;

        public CommandDiscoveryInitializer(
            ICommandRegistry registry,
            CommandDiscovery discovery,
            IEnumerable<Assembly> assemblies,
            IServiceProvider? services = null,
            ILogger? logger = null)
        {
            _registry = registry;
            _discovery = discovery;
            _assemblies = assemblies;
            _services = services;
            _logger = logger;
        }

        public void Initialize()
        {
            var commands = _discovery.DiscoverFromAssemblies(_assemblies, _services);
            _registry.RegisterAll(commands);

            _logger?.LogInformation("命令系统初始化完成，已注册 {Count} 个命令", _registry.Count);
        }
    }
}