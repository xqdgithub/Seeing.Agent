using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Commands
{
    /// <summary>
    /// 命令注册表接口 - 管理所有可用命令
    /// </summary>
    public interface ICommandRegistry
    {
        /// <summary>注册命令</summary>
        void Register(ICommand command);

        /// <summary>批量注册命令</summary>
        void RegisterAll(IEnumerable<ICommand> commands);

        /// <summary>获取命令（通过名称或别名）</summary>
        ICommand? GetCommand(string name);

        /// <summary>获取命令（通过名称和 Runtime）</summary>
        ICommand? GetCommand(string name, AgentRuntime runtime);

        /// <summary>获取所有命令（去重）</summary>
        IEnumerable<ICommand> GetAllCommands();

        /// <summary>获取指定 Runtime 可用的所有命令</summary>
        IEnumerable<ICommand> GetCommandsByRuntime(AgentRuntime runtime);

        /// <summary>获取指定分类的命令</summary>
        IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category);

        /// <summary>获取所有命令元数据</summary>
        IEnumerable<CommandMetadata> GetAllMetadata();

        /// <summary>检查命令是否存在</summary>
        bool HasCommand(string name);

        /// <summary>取消注册命令</summary>
        bool Unregister(string name);

        /// <summary>获取命令数量</summary>
        int Count { get; }
    }

    /// <summary>
    /// 命令注册表实现 - 支持按 Runtime 隔离的命令注册
    /// </summary>
    public class CommandRegistry : ICommandRegistry
    {
        // Runtime 特定命令: (name, runtime) -> command
        private readonly Dictionary<(string Name, AgentRuntime Runtime), ICommand> _commandsByRuntime = new();
        // 默认命令（支持所有 Runtime）：name -> command
        private readonly Dictionary<string, ICommand> _defaultCommands = new(StringComparer.OrdinalIgnoreCase);
        // 别名映射：(alias, runtime) -> command (用于 Runtime 特定命令的别名)
        private readonly Dictionary<(string Alias, AgentRuntime Runtime), ICommand> _aliasByRuntime = new();
        // 默认命令别名：alias -> command
        private readonly Dictionary<string, ICommand> _defaultAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<CommandRegistry>? _logger;

        public CommandRegistry(ILogger<CommandRegistry>? logger = null)
        {
            _logger = logger;
        }

        /// <summary>获取命令数量</summary>
        public int Count => GetAllCommands().Count();

        /// <summary>注册命令</summary>
        public void Register(ICommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            var metadata = command.Metadata;
            var name = metadata.Name;

            if (metadata.SupportedRuntimes.Length == 0)
            {
                // 支持所有 Runtime，注册为默认命令
                RegisterDefaultCommand(command, name);
            }
            else
            {
                // Runtime 特定命令，按 Runtime 分别注册
                RegisterRuntimeCommand(command, name);
            }
        }

        private void RegisterDefaultCommand(ICommand command, string name)
        {
            var metadata = command.Metadata;

            if (_defaultCommands.ContainsKey(name))
            {
                _logger?.LogWarning("默认命令 '{Name}' 已存在，将被覆盖", name);
            }
            _defaultCommands[name] = command;

            // 注册别名
            foreach (var alias in metadata.Aliases)
            {
                if (_defaultAliases.ContainsKey(alias))
                {
                    _logger?.LogWarning("默认别名 '{Alias}' 已被其他命令使用", alias);
                }
                else
                {
                    _defaultAliases[alias] = command;
                }
            }

            _logger?.LogDebug("已注册默认命令: {Name} (别名: {Aliases})", name, string.Join(", ", metadata.Aliases));
        }

        private void RegisterRuntimeCommand(ICommand command, string name)
        {
            var metadata = command.Metadata;

            foreach (var runtime in metadata.SupportedRuntimes)
            {
                var key = (name, runtime);
                if (_commandsByRuntime.ContainsKey(key))
                {
                    _logger?.LogWarning("命令 '{Name}' (Runtime={Runtime}) 已存在，将被覆盖", name, runtime);
                }
                _commandsByRuntime[key] = command;

                // 注册别名
                foreach (var alias in metadata.Aliases)
                {
                    var aliasKey = (alias, runtime);
                    if (_aliasByRuntime.ContainsKey(aliasKey))
                    {
                        _logger?.LogWarning("别名 '{Alias}' (Runtime={Runtime}) 已被其他命令使用", alias, runtime);
                    }
                    else
                    {
                        _aliasByRuntime[aliasKey] = command;
                    }
                }
            }

            _logger?.LogDebug("已注册命令: {Name} (Runtime: {Runtimes}, 别名: {Aliases})",
                name,
                string.Join(", ", metadata.SupportedRuntimes),
                string.Join(", ", metadata.Aliases));
        }

        /// <summary>批量注册命令</summary>
        public void RegisterAll(IEnumerable<ICommand> commands)
        {
            foreach (var command in commands)
            {
                Register(command);
            }
        }

        /// <summary>获取命令（通过名称或别名）- 返回任意匹配的命令</summary>
        public ICommand? GetCommand(string name)
        {
            // 优先返回默认命令
            if (_defaultCommands.TryGetValue(name, out var defaultCmd))
                return defaultCmd;
            if (_defaultAliases.TryGetValue(name, out var defaultAliasCmd))
                return defaultAliasCmd;

            // 否则返回任意一个 Runtime 特定命令
            return _commandsByRuntime.Values.FirstOrDefault(c =>
                c.Metadata.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                _aliasByRuntime.Values.FirstOrDefault(c =>
                    c.Metadata.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>获取命令（通过名称和 Runtime）</summary>
        public ICommand? GetCommand(string name, AgentRuntime runtime)
        {
            // 优先查找 Runtime 特定命令
            if (_commandsByRuntime.TryGetValue((name, runtime), out var runtimeCmd))
                return runtimeCmd;

            // 查找 Runtime 特定别名
            if (_aliasByRuntime.TryGetValue((name, runtime), out var aliasCmd))
                return aliasCmd;

            // 回退到默认命令（支持所有 Runtime）
            if (_defaultCommands.TryGetValue(name, out var defaultCmd) &&
                defaultCmd.Metadata.SupportsRuntime(runtime))
                return defaultCmd;

            // 回退到默认别名
            if (_defaultAliases.TryGetValue(name, out var defaultAliasCmd) &&
                defaultAliasCmd.Metadata.SupportsRuntime(runtime))
                return defaultAliasCmd;

            return null;
        }

        /// <summary>获取所有命令（去重）</summary>
        public IEnumerable<ICommand> GetAllCommands()
        {
            return _commandsByRuntime.Values
                .Concat(_defaultCommands.Values)
                .Distinct();
        }

        /// <summary>获取指定 Runtime 可用的所有命令</summary>
        public IEnumerable<ICommand> GetCommandsByRuntime(AgentRuntime runtime)
        {
            // Runtime 特定命令
            var runtimeCommands = _commandsByRuntime
                .Where(kvp => kvp.Key.Runtime == runtime)
                .Select(kvp => kvp.Value);

            // 默认命令（支持该 Runtime）
            var defaultCommands = _defaultCommands.Values
                .Where(c => c.Metadata.SupportsRuntime(runtime));

            return runtimeCommands.Concat(defaultCommands).Distinct();
        }

        /// <summary>获取指定分类的命令</summary>
        public IEnumerable<ICommand> GetCommandsByCategory(CommandCategory category)
        {
            return GetAllCommands().Where(c => c.Metadata.Category == category);
        }

        /// <summary>获取所有命令元数据</summary>
        public IEnumerable<CommandMetadata> GetAllMetadata()
        {
            return GetAllCommands().Select(c => c.Metadata);
        }

        /// <summary>检查命令是否存在</summary>
        public bool HasCommand(string name)
        {
            return _defaultCommands.ContainsKey(name) ||
                   _defaultAliases.ContainsKey(name) ||
                   _commandsByRuntime.Any(kvp => kvp.Key.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                   _aliasByRuntime.Any(kvp => kvp.Key.Alias.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>取消注册命令</summary>
        public bool Unregister(string name)
        {
            var command = GetCommand(name);
            if (command == null) return false;

            var metadata = command.Metadata;

            // 从默认命令中移除
            if (_defaultCommands.Remove(metadata.Name))
            {
                foreach (var alias in metadata.Aliases)
                {
                    _defaultAliases.Remove(alias);
                }
            }

            // 从 Runtime 特定命令中移除
            foreach (var runtime in metadata.SupportedRuntimes)
            {
                _commandsByRuntime.Remove((metadata.Name, runtime));
                foreach (var alias in metadata.Aliases)
                {
                    _aliasByRuntime.Remove((alias, runtime));
                }
            }

            _logger?.LogDebug("已取消注册命令: {Name}", metadata.Name);
            return true;
        }
    }
}
