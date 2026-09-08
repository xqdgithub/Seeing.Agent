using Seeing.Agent.Abstractions.Agents;

namespace Seeing.Agent.Abstractions.Commands
{
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
}
