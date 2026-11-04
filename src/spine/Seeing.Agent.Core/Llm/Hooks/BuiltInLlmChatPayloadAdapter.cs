using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// chat 能力的 <c>llm.after_complete</c> 载荷适配器：产出正文 / 推理 / 工具调用 / 消息 ID / 轮次。
/// <para>流式与非流式走同一份键：流式时正文、推理、工具调用取 Invoker 已累计好的
/// <see cref="LlmPayloadContext.StreamedContent"/> 等字段（适配器绝不重新遍历流），
/// 非流式时取 <see cref="ChatResponse.Message"/>。</para>
/// </summary>
public sealed class BuiltInLlmChatPayloadAdapter : LlmHookPayloadAdapterBase
{
    /// <summary>
    /// 构造 chat 载荷适配器。
    /// </summary>
    /// <param name="logger">可选日志。</param>
    public BuiltInLlmChatPayloadAdapter(ILogger<BuiltInLlmChatPayloadAdapter>? logger = null)
        : base(logger)
    {
    }

    /// <inheritdoc />
    public override LlmCapabilityKind Capability => LlmCapabilityKind.Chat;

    /// <inheritdoc />
    protected override void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload)
    {
        var message = (context.Response as ChatResponse)?.Message;

        payload[HookDataContract.LlmAfterComplete.Content.Name] = context.Streaming
            ? context.StreamedContent
            : message?.Content;

        payload[HookDataContract.LlmAfterComplete.Reasoning.Name] = context.Streaming
            ? context.StreamedReasoning
            : message?.ReasoningContent;

        payload[HookDataContract.LlmAfterComplete.ToolCalls.Name] = context.Streaming
            ? context.StreamedToolCalls
            : message?.ToolCalls;

        payload[HookDataContract.LlmAfterComplete.MessageId.Name] =
            FirstNonEmpty(context.MessageId, (context.Response as ChatResponse)?.Id);

        payload[HookDataContract.LlmAfterComplete.Streaming.Name] = context.Streaming;
        payload[HookDataContract.LlmAfterComplete.Step.Name] = context.Step;

        // 兼容键：既有记账方（BudgetUpdateHook）在非流式路径读原始 ChatResponse 取 usage
        payload[HookDataContract.LlmAfterComplete.Response.Name] = context.Response as ChatResponse;

        LlmHookPayloadKeys.FillUsageWhenAbsent(payload, (context.Response as ChatResponse)?.Usage);
    }

    /// <summary>取第一个非空白值；全为空时返回 null（<see cref="ChatResponse.Id"/> 缺省是空串，不该写成假 ID）。</summary>
    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
