namespace Seeing.Agent.Memory.Configuration;

public sealed class MemoryOptions
{
    public bool Enabled { get; set; } = true;
    public MemoryCaptureOptions Capture { get; set; } = new();
    public MemoryFilterOptions Filter { get; set; } = new();
    public MemoryExtractionOptions Extraction { get; set; } = new();
    public MemoryEvolutionOptions Evolution { get; set; } = new();
    public MemoryEmbeddingOptions Embedding { get; set; } = new();
    public MemoryRetrievalOptions Retrieval { get; set; } = new();
    public MemoryCostOptions Cost { get; set; } = new();

    public bool IsEmbeddingConfigured =>
        !string.IsNullOrWhiteSpace(Embedding.Provider)
        && !string.IsNullOrWhiteSpace(Embedding.Model);
}

public sealed class MemoryCaptureOptions
{
    public bool AutoCapture { get; set; } = true;
    public bool CaptureChat { get; set; } = true;
    /// <summary>默认关闭：工具输出噪声大、触发频繁，不参与记忆捕获。</summary>
    public bool CaptureTools { get; set; } = false;
    public List<string> ToolAllowlist { get; set; } = new();
    public List<string> ToolBlocklist { get; set; } = new() { "list_dir", "glob", "grep" };
    public int MaxSnippetChars { get; set; } = 4096;
    public int QueueCapacity { get; set; } = 256;
}

public sealed class MemoryFilterOptions
{
    public int MinChars { get; set; } = 20;
    public List<string> AckPatterns { get; set; } = new()
    {
        @"^(好的|嗯|ok|okay|thanks|谢谢)[\s!。．.]*$"
    };
    public int NearDuplicateWindow { get; set; } = 32;
}

public sealed class MemoryExtractionOptions
{
    public bool Enabled { get; set; } = true;
    public double MinImportance { get; set; } = 0.5;
    public string? Model { get; set; }
    /// <summary>每分钟最多 flush（批量抽取）次数。</summary>
    public int MaxCandidatesPerMinute { get; set; } = 6;
    /// <summary>每累计 N 次顶层 agent.after_invoke 触发一次 flush；≤0 关闭轮次触发。</summary>
    public int ExtractEveryNTurns { get; set; } = 10;
    /// <summary>缓冲空闲超过该分钟数则 flush；≤0 关闭空闲 flush。</summary>
    public int FlushIdleMinutes { get; set; } = 5;
    public int MaxBufferedSnippets { get; set; } = 64;
    public int MaxBatchChars { get; set; } = 16000;
}

public sealed class MemoryEvolutionOptions
{
    public bool Enabled { get; set; } = true;
    public int IdleMinutes { get; set; } = 15;
    public bool OnSessionEnd { get; set; } = true;
    public double PromoteToDigestMinImportance { get; set; } = 0.8;
}

public sealed class MemoryEmbeddingOptions
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public int? Dimensions { get; set; }
}

public enum MemoryRetrievalMode { AutoInject, ToolsOnly, Both }

public sealed class MemoryRetrievalOptions
{
    public MemoryRetrievalMode Mode { get; set; } = MemoryRetrievalMode.Both;
    public int TopK { get; set; } = 5;
    public int MaxInjectTokens { get; set; } = 800;
    public int InjectTimeoutMs { get; set; } = 150;
    public List<string> SearchTypes { get; set; } = new() { "daily", "digest" };
}

public sealed class MemoryCostOptions
{
    public long DailyTokenQuota { get; set; } = 1_000_000;
    public int RateLimitPerMinute { get; set; } = 60;
}
