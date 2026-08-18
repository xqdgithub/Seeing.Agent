using Seeing.Agent.Abstractions.Commands;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供命令的扩展
/// </summary>
public interface ICommandExtension
{
    /// <summary>提供的命令</summary>
    IEnumerable<ICommand> GetCommands();
}