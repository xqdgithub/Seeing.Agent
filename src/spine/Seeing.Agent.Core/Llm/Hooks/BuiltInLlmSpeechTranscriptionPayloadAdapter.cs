using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm.Hooks;

/// <summary>
/// 语音转写（ASR）能力的 <c>llm.after_complete</c> 载荷适配器：正文走通用 <c>content</c> 键。
/// <para>spec §7.6 把 <c>content</c> 标为「chat / transcription」共用，订阅方读正文时
/// 不必按能力分叉；段落时间轴（<see cref="TranscriptionResult.Segments"/>）不进载荷。</para>
/// </summary>
public sealed class BuiltInLlmSpeechTranscriptionPayloadAdapter : LlmHookPayloadAdapterBase
{
    /// <summary>
    /// 构造语音转写载荷适配器。
    /// </summary>
    /// <param name="logger">可选日志。</param>
    public BuiltInLlmSpeechTranscriptionPayloadAdapter(
        ILogger<BuiltInLlmSpeechTranscriptionPayloadAdapter>? logger = null)
        : base(logger)
    {
    }

    /// <inheritdoc />
    public override LlmCapabilityKind Capability => LlmCapabilityKind.SpeechTranscription;

    /// <inheritdoc />
    protected override void AppendSpecialized(LlmPayloadContext context, IDictionary<string, object?> payload)
    {
        var response = context.Response as TranscriptionResult;

        payload[HookDataContract.LlmAfterComplete.Content.Name] = response?.Text;

        LlmHookPayloadKeys.FillUsageWhenAbsent(payload, response?.Usage);
    }
}
