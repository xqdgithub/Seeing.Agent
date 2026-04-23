namespace Seeing.Agent.Memory.Configuration
{
    /// <summary>
    /// Memory 存储（持久化）相关配置
    /// </summary>
    public class MemoryStoreOptions
    {
        /// <summary>
        /// 存储目录，默认相对应用根路径下的 MemoryStore
        /// </summary>
        public string MemoryDirectory { get; set; } = "MemoryStore";

        /// <summary>
        /// 单个文件最大大小，单位为 KB
        /// </summary>
        public int MaxFileSizeKB { get; set; } = 1024;

        /// <summary>
        /// 是否对较大条目进行分块存储以提升写入性能和稳定性
        /// </summary>
        public bool EnableChunking { get; set; } = true;
    }
}
