namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 能力校验失败：请求的模型/Provider 组合无法满足所请求的能力端口。
/// <para>Router 的五层校验任一层不通过都抛本异常（spec §7.4），消息必须一次说清：
/// modelId、模型声明类型、Provider 声明能力、请求能力、缺失原因——
/// <b>禁止静默降级，也禁止靠运行期捕获 400 来判断错配</b>。</para>
/// </summary>
public sealed class LlmCapabilityMismatchException : Exception
{
    /// <summary>
    /// 构造一次能力错配。
    /// </summary>
    /// <param name="modelId">被请求的模型引用（可能是带 provider 前缀的 ID）。</param>
    /// <param name="requestedCapability">请求的能力（决定要解析哪个客户端端口）。</param>
    /// <param name="reason">缺失原因；必须由抛出方写成可操作的一句话。</param>
    /// <param name="declaredModelTypes">模型声明的 <see cref="ModelType"/>；尚未取到目录时可为 null。</param>
    /// <param name="providerCapabilities">Provider 声明的能力位集；尚未定位到 Provider 时可为 null。</param>
    public LlmCapabilityMismatchException(
        string modelId,
        LlmCapabilityKind requestedCapability,
        string reason,
        IReadOnlyList<ModelType>? declaredModelTypes = null,
        LlmCapabilityKind? providerCapabilities = null)
        : base(BuildMessage(modelId, requestedCapability, reason, declaredModelTypes, providerCapabilities))
    {
        ModelId = modelId;
        RequestedCapability = requestedCapability;
        Reason = reason;
        DeclaredModelTypes = declaredModelTypes;
        ProviderCapabilities = providerCapabilities;
    }

    /// <summary>被请求的模型引用。</summary>
    public string ModelId { get; }

    /// <summary>请求的能力。</summary>
    public LlmCapabilityKind RequestedCapability { get; }

    /// <summary>缺失原因（抛出方给的可操作描述）。</summary>
    public string Reason { get; }

    /// <summary>模型声明类型；未知时为 null。</summary>
    public IReadOnlyList<ModelType>? DeclaredModelTypes { get; }

    /// <summary>Provider 声明的能力；未知时为 null。</summary>
    public LlmCapabilityKind? ProviderCapabilities { get; }

    private static string BuildMessage(
        string modelId,
        LlmCapabilityKind requestedCapability,
        string reason,
        IReadOnlyList<ModelType>? declaredModelTypes,
        LlmCapabilityKind? providerCapabilities)
    {
        // 能力名一律走 LlmCapabilityMap 投影，禁止在这里手拼（决议 18）
        var capability = LlmCapabilityMap.ToHookValue(requestedCapability);
        var detail = $"模型 '{modelId}' 不能用于能力 '{capability}'：{reason}";

        if (declaredModelTypes is { Count: > 0 })
            detail += $" 模型声明类型 [{string.Join(", ", declaredModelTypes)}]。";

        if (providerCapabilities.HasValue)
            detail += $" Provider 声明能力 [{LlmCapabilityMap.ToConfigKey(providerCapabilities.Value)}]。";

        return detail;
    }
}
