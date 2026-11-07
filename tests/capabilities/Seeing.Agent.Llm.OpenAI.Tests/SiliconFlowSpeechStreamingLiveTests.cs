using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// 流式（增量）转写的真实端点行为（需 <c>SEEING_SILICONFLOW_KEY</c>；未设置则跳过）。
/// <para><b>为什么单独测</b>：SiliconFlow 接受 <c>stream=true</c> 但<em>不</em>返回 SSE——
/// 它回的是普通 JSON（<c>Content-Type: application/json</c> + <c>Content-Length</c>）。
/// 因此「请求发得出去」与「拿得到增量」是两件事，必须分别确认。</para>
/// <para><b>本用例锁定的结论</b>：SiliconFlow 当前不支持流式转写，
/// <c>TranscribeStreamAsync</c> 会静默产出空序列。这是已知能力缺口，
/// 消费方（WebUI 语音输入）因此采用整段转写，不做实时字幕。</para>
/// </summary>
public class SiliconFlowSpeechStreamingLiveTests
{
    private const string KeyVariable = "SEEING_SILICONFLOW_KEY";
    private const string BaseUrl = "https://api.siliconflow.cn/v1";

    [Theory]
    [InlineData("FunAudioLLM/SenseVoiceSmall")]
    [InlineData("TeleAI/TeleSpeechASR")]
    public async Task TranscribeStreamAsync_AgainstSiliconFlow_ShouldYieldNoSegments(string modelId)
    {
        var apiKey = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);
        var client = (ISpeechTranscriptionClient)factory.Create(
            new ProviderConfig
            {
                Id = "siliconflow",
                Type = ProviderTypes.OpenAi,
                BaseUrl = BaseUrl,
                ApiKey = apiKey,
            },
            LlmCapabilityKind.SpeechTranscription)!;

        using var audio = new MemoryStream(SilentTone(1000));
        var segments = new List<TranscriptSegment>();

        await foreach (var segment in client.TranscribeStreamAsync(
            new TranscriptionRequest
            {
                Model = modelId,
                Audio = audio,
                MimeType = "audio/wav",
                FileName = "tone.wav",
            },
            call: null,
            TestContext.Current.CancellationToken))
        {
            segments.Add(segment);
        }

        segments.Should().BeEmpty(
            "SiliconFlow 忽略 stream=true 并返回普通 JSON，客户端只解析 SSE 的 data: 行，"
            + "因此产出空序列。若此断言失败，说明厂商开始支持 SSE，需同步更新语音输入的方案（可启用实时字幕）");
    }

    /// <summary>440Hz 正弦波 WAV（16bit 单声道 16kHz）。</summary>
    private static byte[] SilentTone(int durationMs)
    {
        const int sampleRate = 16000;
        var samples = sampleRate * durationMs / 1000;
        var dataSize = samples * 2;
        var bytes = new byte[44 + dataSize];

        void Ascii(int offset, string value)
            => System.Text.Encoding.ASCII.GetBytes(value).CopyTo(bytes.AsSpan(offset));
        void I32(int offset, int value)
            => BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
        void I16(int offset, short value)
            => BitConverter.TryWriteBytes(bytes.AsSpan(offset, 2), value);

        Ascii(0, "RIFF");
        I32(4, 36 + dataSize);
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        I32(16, 16);
        I16(20, 1);
        I16(22, 1);
        I32(24, sampleRate);
        I32(28, sampleRate * 2);
        I16(32, 2);
        I16(34, 16);
        Ascii(36, "data");
        I32(40, dataSize);

        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * short.MaxValue * 0.2);
            BitConverter.TryWriteBytes(bytes.AsSpan(44 + i * 2, 2), value);
        }

        return bytes;
    }
}
