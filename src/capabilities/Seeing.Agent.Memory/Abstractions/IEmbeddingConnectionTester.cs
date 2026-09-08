namespace Seeing.Agent.Memory.Abstractions;

public interface IEmbeddingConnectionTester
{
    /// <summary>
    /// 使用指定（或当前配置）的 Provider/Model 请求一条测试 embedding。
    /// </summary>
    Task<EmbeddingConnectionTestResult> TestAsync(
        string? provider = null,
        string? model = null,
        CancellationToken ct = default);
}

public sealed record EmbeddingConnectionTestResult(
    bool Success,
    string Message,
    int? Dimensions = null);
