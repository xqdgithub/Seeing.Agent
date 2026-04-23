namespace Seeing.Agent.Memory.Configuration
{
    /// <summary>
    /// Memory 评分权重配置
    /// </summary>
    public class MemoryScoreOptions
    {
        /// <summary>
        /// 重要性权重
        /// </summary>
        public double ImportanceWeight { get; set; } = 1.0;

        /// <summary>
        /// 访问频次权重
        /// </summary>
        public double AccessFreqWeight { get; set; } = 1.0;

        /// <summary>
        /// 年龄权重（越新条目越高分）
        /// </summary>
        public double AgeWeight { get; set; } = 1.0;

        /// <summary>
        /// 忘记阈值，用于淘汰/降权的界限
        /// </summary>
        public double ForgettingThreshold { get; set; } = 0.5;
    }
}
