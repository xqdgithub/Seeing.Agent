using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Abstractions
{
    /// <summary>
    /// Memory management core interface.
    /// 提供记忆的 CRUD 操作和检索功能。
    /// </summary>
    public interface IMemoryManager
    {
        /// <summary>
        /// 初始化记忆管理系统
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 创建新的记忆条目
        /// </summary>
        /// <param name="memory">记忆条目</param>
        /// <returns>创建成功的记忆 ID</returns>
        Task<string> CreateMemoryAsync(MemoryEntry memory);

        /// <summary>
        /// 根据 ID 获取记忆条目
        /// </summary>
        /// <param name="id">记忆 ID</param>
        /// <returns>记忆条目，不存在时返回 null</returns>
        Task<MemoryEntry?> GetMemoryAsync(string id);

        /// <summary>
        /// 更新记忆条目（部分更新）
        /// </summary>
        /// <param name="id">记忆 ID</param>
        /// <param name="update">更新数据</param>
        /// <returns>更新后的记忆条目</returns>
        Task<MemoryEntry> UpdateMemoryAsync(string id, MemoryUpdate update);

        /// <summary>
        /// 删除记忆条目
        /// </summary>
        /// <param name="id">记忆 ID</param>
        Task DeleteMemoryAsync(string id);

        /// <summary>
        /// 搜索记忆（基于查询字符串和过滤条件）
        /// </summary>
        /// <param name="query">查询字符串</param>
        /// <param name="filter">过滤条件</param>
        /// <returns>搜索结果</returns>
        Task<MemorySearchResult> SearchMemoriesAsync(string query, MemoryFilter? filter = null);

        /// <summary>
        /// 列出所有记忆（保留兼容性）
        /// </summary>
        /// <returns>所有记忆条目</returns>
        Task<IEnumerable<object>> ListMemoriesAsync();
    }
}
