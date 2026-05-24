using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Retrieval interface for querying memories.
    /// </summary>
    public interface IMemoryRetriever
    {
        Task<IEnumerable<object>> RetrieveAsync(string query);
        Task<IEnumerable<object>> RetrieveAsync(object criteria);

        /// <summary>
        /// 根据元数据过滤条件检索记忆
        /// </summary>
        /// <param name="filter">记忆过滤条件</param>
        /// <returns>匹配的记忆条目列表</returns>
        Task<IReadOnlyList<MemoryEntry>> RetrieveByMetadataAsync(MemoryFilter filter);

        /// <summary>
        /// 根据时间范围检索记忆（基于 ValidAt 字段）
        /// </summary>
        /// <param name="from">起始时间</param>
        /// <param name="to">结束时间</param>
        /// <returns>时间范围内的记忆条目列表</returns>
        Task<IReadOnlyList<MemoryEntry>> RetrieveByTimeRangeAsync(DateTimeOffset from, DateTimeOffset to);

        /// <summary>
        /// 根据 Agent ID 检索记忆
        /// </summary>
        /// <param name="agentId">Agent 标识符</param>
        /// <returns>该 Agent 创建的记忆条目列表</returns>
        Task<IReadOnlyList<MemoryEntry>> RetrieveByAgentAsync(string agentId);

        /// <summary>
        /// 检索当前有效的记忆（valid_at &lt;= now &lt; invalid_at）
        /// </summary>
        /// <param name="now">当前时间点</param>
        /// <returns>当前有效的记忆条目列表</returns>
        Task<IReadOnlyList<MemoryEntry>> RetrieveValidAsync(DateTimeOffset now);
    }
}
