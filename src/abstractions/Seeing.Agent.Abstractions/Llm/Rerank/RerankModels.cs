namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 检索重排请求（本次只出契约，spec §7.11）。
/// </summary>
public sealed record RerankRequest
{
    /// <summary>模型 ID。</summary>
    public required string Model { get; init; }

    /// <summary>用于排序的查询串。</summary>
    public required string Query { get; init; }

    /// <summary>候选文档，顺序即 <see cref="RerankHit.Index"/> 的编号基准。</summary>
    public required IReadOnlyList<string> Documents { get; init; }

    /// <summary>只回前排多少条；留空表示全量排序。</summary>
    public int? TopN { get; init; }

    /// <summary>是否回显文档正文（服务端可省）。缺省不回显。</summary>
    public bool? ReturnDocuments { get; init; }
}

/// <summary>
/// 单条重排结果。<see cref="Index"/> 指向 <see cref="RerankRequest.Documents"/> 的原下标，
/// 乱序响应以此为准排序。
/// </summary>
/// <param name="Index">候选文档原下标。</param>
/// <param name="Score">相关性得分（越大越相关）。</param>
/// <param name="Document">文档正文（仅当请求要求回显时有值）。</param>
public sealed record RerankHit(int Index, double Score, string? Document);

/// <summary>
/// 重排响应。
/// </summary>
public sealed record RerankResponse
{
    /// <summary>按 <see cref="RerankHit.Score"/> 降序的结果集合。</summary>
    public required IReadOnlyList<RerankHit> Results { get; init; }

    /// <summary>token 用量；只进 <c>llm.after_complete</c> 载荷，不写会话预算（决议 1）。</summary>
    public TokenUsage? Usage { get; init; }
}
