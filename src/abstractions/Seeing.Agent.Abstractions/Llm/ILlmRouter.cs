namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 模型与客户端的配对出口：把「显式 modelId + 请求的能力端口」解析成一个可用客户端。
/// <para><b>只接受显式 modelId</b>：本接口不提供"未指定时取目录首个"的重载，Core 也不内置任何
/// 回落策略（spec §13 原则 1）。默认值的选择归调用方（能力包）。</para>
/// <para>校验顺序固定五层，失败抛 <see cref="LlmCapabilityMismatchException"/>（spec §7.4）：
/// ①模型存在 → ②类型声明含请求能力 → ③Speech 按 <c>Modalities</c> 消歧 →
/// ④Provider 声明该能力 → ⑤工厂确实产出了客户端。</para>
/// </summary>
public interface ILlmRouter
{
    /// <summary>
    /// 解析指定模型上承担 <typeparamref name="TClient"/> 能力的客户端。
    /// </summary>
    /// <exception cref="LlmCapabilityMismatchException">五层校验任一层不通过。</exception>
    TClient Resolve<TClient>(string modelId)
        where TClient : class, ILlmCapabilityClient;

    /// <summary>
    /// 不抛异常版解析：失败时返回 <c>false</c> 并给出可读原因（供 UI 与启动诊断使用）。
    /// </summary>
    bool TryResolve<TClient>(string modelId, out TClient? client, out string? reason)
        where TClient : class, ILlmCapabilityClient;
}
