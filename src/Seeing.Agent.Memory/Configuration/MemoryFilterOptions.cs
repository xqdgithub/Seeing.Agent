using System.Collections.Generic;

namespace Seeing.Agent.Memory.Configuration
{
    /// <summary>
    /// Memory 筛选配置
    /// </summary>
    public class MemoryFilterOptions
    {
        /// <summary>
        /// 最小文本长度，低于该长度的条目将被过滤
        /// </summary>
        public int MinLength { get; set; } = 0;

        /// <summary>
        /// 实体关键词触发的关键词集合
        /// </summary>
        public List<string> EntityKeywords { get; set; } = new List<string>();

        /// <summary>
        /// 触发性关键词集合
        /// </summary>
        public List<string> TriggerKeywords { get; set; } = new List<string>();
    }
}
