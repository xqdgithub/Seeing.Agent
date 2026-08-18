using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Abstractions.Extensions;

/// <summary>
/// 提供工具的扩展
/// </summary>
public interface IToolExtension
{
    /// <summary>提供的工具</summary>
    IEnumerable<ITool> GetTools();
}