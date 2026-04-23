using System.Collections.Generic;
using System.Threading.Tasks;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// 遗忘管理器接口，负责主动遗忘和被动衰减
    /// </summary>
    public interface IMemoryForgetter
    {
        /// <summary>
        /// 归档低于阈值的记忆（主动遗忘）
        /// </summary>
        /// <param name="threshold">遗忘阈值，低于此值的记忆将被归档</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>归档的记忆数量</returns>
        Task<int> ArchiveLowScoreMemoriesAsync(double threshold, CancellationToken cancellationToken = default);

        /// <summary>
        /// 应用被动衰减，降低长期未访问记忆的重要性
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>被衰减的记忆数量</returns>
        Task<int> ApplyDecayAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取候选遗忘记忆列表
        /// </summary>
        /// <param name="threshold">遗忘阈值（可选，使用配置默认值）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>候选遗忘记忆列表</returns>
        Task<IReadOnlyList<MemoryEntry>> GetForgettingCandidatesAsync(double? threshold = null, CancellationToken cancellationToken = default);
    }
}