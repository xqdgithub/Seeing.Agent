using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// 语音合成（TTS）能力的 <c>llm.after_complete</c> 载荷适配器：只产出字节数。
/// <para>音频字节本身不进 Hook 载荷——一次合成的音频可达数 MB，载荷是观测数据不是缓冲区。</para>
/// <para>流式合成按契约不承诺累计语义（<see cref="ILlmInvoker.StreamAsync"/> 的注释），
/// 此时 <see cref="LlmPayloadContext.Response"/> 为 null，<c>audioBytes</c> 落到 0。</para>
/// </summary>
public sealed class BuiltInLlmSpeechSynthesisPayloadAdapter : LlmHookPayloadAdapterBase
{
    /// <summary>
    /// 构造语音合成载荷适配器。
    /// </summary>
    /// <param name="logger">可选日志。</param>
    public BuiltInLlmSpeechSynthesisPayloadAdapter(ILogger<BuiltInLlmSpeechSynthesisPayloadAdapter>? logger = null)
        : base(logger)
    {
    }

    /// <inheritdoc />
    public override LlmCapabilityKind Capability => LlmCapabilityKind.SpeechSynthesis;

    /// <inheritdoc />
    protected override void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload)
    {
        var response = context.Response as SpeechSynthesisResult;

        payload[HookDataContract.LlmAfterComplete.AudioBytes.Name] = response?.Audio.Length ?? 0;

        LlmHookPayloadKeys.FillUsageWhenAbsent(payload, response?.Usage);
    }
}
