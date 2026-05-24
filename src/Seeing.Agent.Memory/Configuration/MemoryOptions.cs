namespace Seeing.Agent.Memory.Configuration
{
    /// <summary>
    /// Memory 系统主配置，聚合各子配置。
    /// 配置节名称应为 "Memory"，通过 IOptions<MemoryOptions> 注入。
    /// </summary>
    public class MemoryOptions
    {
        /// <summary>
        /// Memory 存储相关配置
        /// </summary>
        public MemoryStoreOptions MemoryStore { get; set; } = new MemoryStoreOptions();

        /// <summary>
        /// Memory 捕获钩子相关配置
        /// </summary>
        public MemoryHookOptions MemoryHook { get; set; } = new MemoryHookOptions();

        /// <summary>
        /// Memory 筛选策略配置
        /// </summary>
        public MemoryFilterOptions MemoryFilter { get; set; } = new MemoryFilterOptions();

        /// <summary>
        /// Memory 评分权重配置
        /// </summary>
        public MemoryScoreOptions MemoryScore { get; set; } = new MemoryScoreOptions();
    }
}
