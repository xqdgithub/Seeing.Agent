namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 能力客户端基座：只含身份与连通性测试，<b>不含任何能力自述</b>。
/// <para>刻意没有 <c>Capabilities</c> 属性（spec §7.2 / 决议 15）：
/// <see cref="ProviderConfig.Capabilities"/> 是唯一真相，
/// <see cref="ILlmProvider.Capabilities"/> 是它的运行时投影；客户端再自述一份就是第三份真相，
/// 必然与用户配置漂移。诊断请读 <see cref="ProviderInfo.Capabilities"/>。</para>
/// <para>各能力的真实请求/响应形状保留在窄端口里
/// （<see cref="IChatCompletionClient"/> 等），由 <see cref="LlmCapabilityMap.ClientTypes"/> 索引。</para>
/// </summary>
public interface ILlmCapabilityClient
{
    /// <summary>所属 Provider 的 ID。</summary>
    string ProviderId { get; }

    /// <summary>所使用的协议（<c>openai</c> / <c>openai-responses</c> / <c>anthropic</c> / <c>typesafe</c>）。</summary>
    string ProviderType { get; }

    /// <summary>
    /// 在指定能力上测试连通性。实现应发最小真实请求（决议 25）——
    /// 仅探端点会把"模型不存在"误判为连通，真实探针会产生少量费用，这是唯一能验证
    /// "模型本身可用"的方式。
    /// </summary>
    /// <param name="modelId">待测模型 ID。</param>
    /// <param name="capability">待测能力；实现据此选择探针形态。</param>
    /// <param name="call">调用期上下文（出站头、scratch 袋），可为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> TestConnectionAsync(
        string modelId,
        LlmCapabilityKind capability,
        LlmCallContext? call = null,
        CancellationToken cancellationToken = default);
}
