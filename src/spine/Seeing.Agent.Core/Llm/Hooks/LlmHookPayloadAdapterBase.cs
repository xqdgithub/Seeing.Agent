using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// 载荷适配器基类：先装配通用键，再追加能力特化键。
/// <para><b>为什么特化键要包在 try 里</b>：<see cref="Build"/> 发生在一次 LLM 调用返回之后、
/// Hook 触发之前（spec §7.5 的固定流程），它抛出去会让"调用其实成功了"变成"调用失败"。
/// 适配器只读 DTO 属性，正常路径不抛；这里兜的是"插件注册的适配器写坏了"这类事故——
/// 记录错误并退回通用键，比让整次调用炸掉更有用。</para>
/// </summary>
public abstract class LlmHookPayloadAdapterBase : ILlmHookPayloadAdapter
{
    private readonly ILogger? _logger;

    /// <inheritdoc />
    public abstract LlmCapabilityKind Capability { get; }

    /// <summary>
    /// 构造适配器基类。
    /// </summary>
    /// <param name="logger">可选日志；为 <c>null</c> 时（测试或独立装配）特化键失败不留痕。</param>
    protected LlmHookPayloadAdapterBase(ILogger? logger = null) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Build(LlmPayloadContext context)
    {
        var payload = LlmHookPayloadKeys.Common(context);

        try
        {
            AppendSpecialized(context, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "llm.after_complete 载荷特化键装配失败，退回通用键: Capability={Capability}, Adapter={Adapter}",
                Capability,
                GetType().Name);
        }

        return payload;
    }

    /// <summary>
    /// 追加本能力的特化键（spec §7.6 的 result 超集表）。
    /// <para>约定：只读 <paramref name="context"/> 已有的字段与响应 DTO 的属性，
    /// 不做解析、不做 I/O、不重新遍历流（流式累计值由 Invoker 填入）。</para>
    /// </summary>
    /// <param name="context">调用上下文。</param>
    /// <param name="payload">已含通用键的可变字典。</param>
    protected abstract void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload);
}
