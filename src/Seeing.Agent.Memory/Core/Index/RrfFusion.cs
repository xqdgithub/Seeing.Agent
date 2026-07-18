namespace Seeing.Agent.Memory.Core.Index;

/// <summary>
/// 融合结果
/// </summary>
public record FusionResult(
    string Path,
    double Score,
    double VectorScore,
    double KeywordScore,
    int? VectorRank,
    int? KeywordRank
);

/// <summary>
/// RRF (Reciprocal Rank Fusion) 融合算法
/// 用于混合向量和关键词检索结果
/// </summary>
public static class RrfFusion
{
    /// <summary>
    /// 默认平滑参数 K
    /// </summary>
    public const int DefaultK = 60;

    /// <summary>
    /// 融合向量和关键词检索结果
    /// </summary>
    /// <param name="vectorResults">向量检索结果列表</param>
    /// <param name="keywordResults">关键词检索结果列表</param>
    /// <param name="vectorWeight">向量权重 (0-1, 默认 0.5)</param>
    /// <param name="k">RRF 平滑参数 (默认 60)</param>
    /// <returns>融合后的结果列表，按总分降序排列</returns>
    public static IReadOnlyList<FusionResult> Fuse(
        IReadOnlyList<(string Path, double Score)> vectorResults,
        IReadOnlyList<(string Path, double Score)> keywordResults,
        double vectorWeight = 0.5,
        int k = DefaultK)
    {
        if (vectorResults.Count == 0 && keywordResults.Count == 0)
        {
            return Array.Empty<FusionResult>();
        }

        // 验证权重范围
        if (vectorWeight < 0 || vectorWeight > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vectorWeight), "Vector weight must be between 0 and 1");
        }

        // 验证 K 值
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), "K must be greater than 0");
        }

        var keywordWeight = 1.0 - vectorWeight;

        // 构建路径到结果的映射
        var fusionMap = new Dictionary<string, FusionResultBuilder>();

        // 处理向量结果
        for (var rank = 0; rank < vectorResults.Count; rank++)
        {
            var (path, score) = vectorResults[rank];
            var rrfScore = vectorWeight / (k + rank + 1); // rank 从 1 开始

            if (!fusionMap.TryGetValue(path, out var builder))
            {
                builder = new FusionResultBuilder(path);
                fusionMap[path] = builder;
            }

            builder.VectorScore = score;
            builder.VectorRank = rank + 1;
            builder.RrfContribution += rrfScore;
        }

        // 处理关键词结果
        for (var rank = 0; rank < keywordResults.Count; rank++)
        {
            var (path, score) = keywordResults[rank];
            var rrfScore = keywordWeight / (k + rank + 1); // rank 从 1 开始

            if (!fusionMap.TryGetValue(path, out var builder))
            {
                builder = new FusionResultBuilder(path);
                fusionMap[path] = builder;
            }

            builder.KeywordScore = score;
            builder.KeywordRank = rank + 1;
            builder.RrfContribution += rrfScore;
        }

        // 构建最终结果并按总分排序
        var results = fusionMap.Values
            .Select(b => b.Build())
            .OrderByDescending(r => r.Score)
            .ToList();

        return results;
    }

    /// <summary>
    /// 辅助类：用于构建 FusionResult
    /// </summary>
    private class FusionResultBuilder
    {
        public string Path { get; }
        public double VectorScore { get; set; }
        public double KeywordScore { get; set; }
        public int? VectorRank { get; set; }
        public int? KeywordRank { get; set; }
        public double RrfContribution { get; set; }

        public FusionResultBuilder(string path)
        {
            Path = path;
            VectorScore = 0;
            KeywordScore = 0;
        }

        public FusionResult Build()
        {
            return new FusionResult(
                Path: Path,
                Score: RrfContribution,
                VectorScore: VectorRank.HasValue ? VectorScore : 0,
                KeywordScore: KeywordRank.HasValue ? KeywordScore : 0,
                VectorRank: VectorRank,
                KeywordRank: KeywordRank
            );
        }
    }
}
