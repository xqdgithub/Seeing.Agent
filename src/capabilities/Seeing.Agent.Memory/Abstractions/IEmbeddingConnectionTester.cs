namespace Seeing.Agent.Memory.Abstractions;

public interface IEmbeddingConnectionTester
{
    /// <summary>
    /// 对指定模型引用（形如 <c>provider/model</c>）发一条真实向量探针并返回维度。
    /// <para>留空则使用当前配置的 <c>Embedding.ModelId</c>；配置也为空时返回失败——
    /// <b>不会自动挑选模型</b>（spec §0 决议 10 / §13 原则 1）。</para>
    /// </summary>
    /// <param name="modelId">模型引用；null/空表示用配置值。</param>
    /// <param name="ct">取消。</param>
    Task<EmbeddingConnectionTestResult> TestAsync(
        string? modelId = null,
        CancellationToken ct = default);
}

public sealed record EmbeddingConnectionTestResult(
    bool Success,
    string Message,
    int? Dimensions = null);
