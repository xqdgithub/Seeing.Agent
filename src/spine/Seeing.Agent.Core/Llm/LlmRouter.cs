using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 模型 ↔ 客户端配对路由器：按 spec §7.4 的固定五层校验解析显式 modelId + 请求端口。
/// <para>校验顺序（失败即止，逐层给出可操作原因）：
/// ①模型存在 → ②模型声明类型含请求能力 → ③Speech 按 <c>Modalities</c> 消歧（决议 24，不猜）→
/// ④Provider 声明该能力 → ⑤工厂确实产出了客户端（决议 19：以配置为准，不回退）。</para>
/// <para><b>零启发式</b>（spec §13 原则 1）：只有显式 modelId 入口，不提供"取目录首个"的重载，
/// 也不内置任何回落。默认值的选择归调用方（能力包）。</para>
/// <para><b>实例缓存不在这里</b>：缓存键是 <c>(providerId, capability)</c>，由
/// <see cref="ConfiguredLlmProvider"/> 的 <c>Lazy</c> 客户端束天然承担——
/// 同一 Provider 下的 100 个模型只应有一个 embedding 客户端实例。</para>
/// </summary>
public sealed class LlmRouter : ILlmRouter
{
    /// <summary>语音模态标识（<c>ModelConfig.Modalities</c> 的取值约定，spec §7.10）。</summary>
    private const string AudioModality = "audio";

    private readonly IModelConfigManager _models;
    private readonly IProviderRegistry _providers;
    private readonly ILogger<LlmRouter> _logger;

    /// <summary>
    /// 注入模型目录、Provider 注册表与日志构造路由器。
    /// </summary>
    public LlmRouter(
        IModelConfigManager models,
        IProviderRegistry providers,
        ILogger<LlmRouter> logger)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public TClient Resolve<TClient>(string modelId)
        where TClient : class, ILlmCapabilityClient
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // 端口 → 能力是本方法的第一道门：端口未登记属于编程错误，但同样给可读原因而非 NullReference
        var capability = CapabilityOf<TClient>(modelId);

        var (client, reason, declaredTypes, providerCapabilities) = ResolveCore(modelId, capability);
        if (client is null)
        {
            throw new LlmCapabilityMismatchException(
                modelId, capability, reason ?? "未知原因", declaredTypes, providerCapabilities);
        }

        if (client is not TClient typed)
        {
            // 配置声明了能力、工厂也给了客户端，但客户端不实现请求端口：配置/实现漂移
            throw new LlmCapabilityMismatchException(
                modelId,
                capability,
                $"工厂提供的客户端不实现请求端口 '{typeof(TClient).Name}'（实际类型 {client.GetType().Name}）",
                declaredTypes,
                providerCapabilities);
        }

        return typed;
    }

    /// <inheritdoc />
    public bool TryResolve<TClient>(string modelId, out TClient? client, out string? reason)
        where TClient : class, ILlmCapabilityClient
    {
        client = null;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            reason = "模型引用为空：路由器只接受显式 modelId，默认值解析归调用方（spec §13 原则 1）。";
            return false;
        }

        if (!TryCapabilityOf<TClient>(out var capability, out reason))
            return false;

        ILlmCapabilityClient? resolved;
        string? failureReason;
        (resolved, failureReason, _, _) = ResolveCore(modelId, capability);

        if (resolved is null)
        {
            reason = failureReason;
            return false;
        }

        if (resolved is not TClient typed)
        {
            reason = $"工厂提供的客户端不实现请求端口 '{typeof(TClient).Name}'（实际类型 {resolved.GetType().Name}）";
            return false;
        }

        client = typed;
        return true;
    }

    /// <summary>
    /// 五层校验主体：永不抛异常，失败时返回 null 客户端 + 可操作原因
    /// （供 <see cref="Resolve{TClient}"/> 包装成异常、<see cref="TryResolve{TClient}"/> 直接回报）。
    /// </summary>
    private (
        ILlmCapabilityClient? Client,
        string? Reason,
        IReadOnlyList<ModelType>? DeclaredTypes,
        LlmCapabilityKind? ProviderCapabilities) ResolveCore(
        string modelId,
        LlmCapabilityKind capability)
    {
        var capabilityName = LlmCapabilityMap.ToHookValue(capability);

        // ---- 第 1 层：模型存在 ----
        var model = _models.GetModel(modelId);
        if (model is null)
        {
            return Fail(modelId,
                $"模型目录中不存在 '{modelId}'：请检查 provider/model 引用是否拼写正确，或先在模型页添加该模型");
        }

        // ---- 第 2 层：模型声明类型映射到请求能力 ----
        var effectiveTypes = _models.GetEffectiveTypes(model) ?? [ModelType.Text];
        if (!effectiveTypes.Any(type => LlmCapabilityMap.FromModelType(type).Contains(capability)))
        {
            return Fail(modelId,
                $"模型声明类型不含能力 '{capabilityName}'：请把模型的 types 补上对应用途类型",
                effectiveTypes);
        }

        // ---- 第 3 层：Speech 双向消歧（决议 24：不猜，缺声明就要求补） ----
        if (capability is LlmCapabilityKind.SpeechSynthesis or LlmCapabilityKind.SpeechTranscription)
        {
            var modalityKey = capability == LlmCapabilityKind.SpeechSynthesis
                ? "modalities.output"
                : "modalities.input";
            var modalityValues = capability == LlmCapabilityKind.SpeechSynthesis
                ? model.Modalities?.Output
                : model.Modalities?.Input;

            if (modalityValues is null ||
                !modalityValues.Any(value => string.Equals(value, AudioModality, StringComparison.OrdinalIgnoreCase)))
            {
                return Fail(modelId,
                    $"声明不足：模型标为 Speech 但未在 {modalityKey} 写明 'audio'，"
                    + $"无法判定它承担 '{capabilityName}'。请在模型配置补 Modalities（合成看 output、转写看 input）",
                    effectiveTypes);
            }
        }

        // ---- 第 4 层：Provider 声明该能力 ----
        var providerId = model.Provider;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Fail(modelId,
                $"模型 '{modelId}' 未绑定 Provider，无法定位承担 '{capabilityName}' 的客户端",
                effectiveTypes);
        }

        var provider = _providers.GetProvider(providerId);
        if (provider is null)
        {
            return Fail(modelId,
                $"Provider '{providerId}' 未注册：模型所属 Provider 不在注册表里（配置驱动 Provider 需写在 Providers 节，"
                + "扩展 Provider 需由对应模块 Activate）",
                effectiveTypes);
        }

        var providerCapabilities = provider.Capabilities;
        if (!providerCapabilities.HasFlag(capability))
        {
            return Fail(modelId,
                $"Provider '{providerId}' 未声明能力 '{capabilityName}'："
                + $"请在该 Provider 的 capabilities 里加上 '{LlmCapabilityMap.ToConfigKey(capability)}'",
                effectiveTypes,
                providerCapabilities);
        }

        // ---- 第 5 层：实现可用性（决议 19：以配置为准，工厂返回 null 就报错，不回退） ----
        var client = provider.GetClient(capability);
        if (client is null)
        {
            return Fail(modelId,
                $"Provider '{providerId}' 配置声明了能力 '{capabilityName}'，但工厂未提供对应客户端："
                + "协议实现不支持该能力，请把 capabilities 改为实际可支持的集合，或换用支持该能力的协议",
                effectiveTypes,
                providerCapabilities);
        }

        return (client, null, effectiveTypes, providerCapabilities);

        (ILlmCapabilityClient?, string?, IReadOnlyList<ModelType>?, LlmCapabilityKind?) Fail(
            string id,
            string reason,
            IReadOnlyList<ModelType>? types = null,
            LlmCapabilityKind? caps = null)
        {
            _logger.LogDebug(
                "LLM 路由解析失败: Model={ModelId}, Capability={Capability}, Reason={Reason}",
                id,
                capabilityName,
                reason);
            return (null, reason, types, caps);
        }
    }

    /// <summary>
    /// 端口类型 → 能力；未登记时抛 <see cref="LlmCapabilityMismatchException"/>（禁止静默降级）。
    /// </summary>
    private static LlmCapabilityKind CapabilityOf<TClient>(string modelId)
    {
        if (TryCapabilityOf<TClient>(out var capability, out _))
            return capability;

        throw new LlmCapabilityMismatchException(modelId, LlmCapabilityKind.None, CapabilityFailure<TClient>());
    }

    /// <summary>
    /// 端口类型 → 能力的反查。判定规则统一在 <see cref="LlmCapabilityMap.TryResolveKind"/>
    /// （精确匹配 → Assignable 单命中 → 多命中视为歧义），本类不再自行实现。
    /// </summary>
    private static bool TryCapabilityOf<TClient>(out LlmCapabilityKind capability, out string? reason)
    {
        if (LlmCapabilityMap.TryResolveKind(typeof(TClient), out capability))
        {
            reason = null;
            return true;
        }

        reason = CapabilityFailure<TClient>();
        return false;
    }

    /// <summary>端口未登记时的可读原因（<see cref="CapabilityOf{TClient}"/> 与 Try 版共用）。</summary>
    private static string CapabilityFailure<TClient>()
        => $"请求端口 '{typeof(TClient).Name}' 未登记于 LlmCapabilityMap.ClientTypes，"
           + "无法确定要解析的能力：请把泛型实参换成八个能力端口之一"
           + $"（当前登记：{string.Join(", ", LlmCapabilityMap.ClientTypes.Keys.Select(LlmCapabilityMap.ToHookValue))}）";
}
