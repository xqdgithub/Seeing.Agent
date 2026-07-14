using Seeing.Session.Compression;
using Seeing.Session.Core;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 压缩策略工厂接口
/// </summary>
public interface ICompressionStrategyFactory
{
    /// <summary>
    /// 获取指定类型的压缩策略
    /// </summary>
    ICompressionStrategy GetStrategy(CompactionStrategyType type);
}
