using Microsoft.Extensions.Logging;

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

        /// <summary>获取所有命令（去重）</summary>
        IEnumerable<ICommand> GetAllCommands();

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
    /// 命令注册表实现 - 支持插件动态注册命令
    /// </summary>
    public class CommandRegistry : ICommandRegistry
    {
        private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<CommandRegistry>? _logger;

        public CommandRegistry(ILogger<CommandRegistry>? logger = null)
        {
            _logger = logger;
        }

        /// <summary>获取命令数量</summary>
        public int Count => _commands.Values.Distinct().Count();

        /// <summary>注册命令</summary>
        public void Register(ICommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            var metadata = command.Metadata;
            var name = metadata.Name;

            // 注册主名称
            if (_commands.ContainsKey(name))
            {
                _logger?.LogWarning("命令 '{Name}' 已存在，将被覆盖", name);
            }
            _commands[name] = command;

            // 注册别名
            foreach (var alias in metadata.Aliases)
            {
                if (_commands.ContainsKey(alias))
                {
                    _logger?.LogWarning("别名 '{Alias}' 已被其他命令使用", alias);
                }
                else
                {
                    _commands[alias] = command;
                }
            }

            _logger?.LogDebug("已注册命令: {Name} (别名: {Aliases})", name, string.Join(", ", metadata.Aliases));
        }

        /// <summary>批量注册命令</summary>
        public void RegisterAll(IEnumerable<ICommand> commands)
        {
            foreach (var command in commands)
            {
                Register(command);
            }
        }

        /// <summary>获取命令（通过名称或别名）</summary>
        public ICommand? GetCommand(string name)
        {
            return _commands.TryGetValue(name, out var command) ? command : null;
        }

        /// <summary>获取所有命令（去重）</summary>
        public IEnumerable<ICommand> GetAllCommands()
        {
            return _commands.Values.Distinct();
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
        public bool HasCommand(string name) => _commands.ContainsKey(name);

        /// <summary>取消注册命令</summary>
        public bool Unregister(string name)
        {
            var command = GetCommand(name);
            if (command == null) return false;

            var metadata = command.Metadata;

            // 移除主名称
            _commands.Remove(metadata.Name);

            // 移除别名
            foreach (var alias in metadata.Aliases)
            {
                _commands.Remove(alias);
            }

            _logger?.LogDebug("已取消注册命令: {Name}", metadata.Name);
            return true;
        }
    }
}