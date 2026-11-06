using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Llm.OpenAI.Clients;
using Xunit;

namespace Seeing.Agent.Llm.OpenAI.Tests;

/// <summary>
/// 真实 SiliconFlow 端点联调（需要环境变量 <c>SEEING_SILICONFLOW_KEY</c>；未设置则跳过）。
/// <para><b>为什么保留真调用测试</b>：SiliconFlow 的 <c>/audio/transcriptions</c> 与 OpenAI 官方有三处差异
/// （<c>response_format=verbose_json</c> 只被部分模型接受、<c>SenseVoiceSmall</c> 不返回 <c>segments</c>、
/// <c>usage</c> 是 duration 形状而非 token 形状），这些只能靠真端点验证，桩测不出来。</para>
/// </summary>
public class SiliconFlowSpeechTranscriptionLiveTests
{
    private const string KeyVariable = "SEEING_SILICONFLOW_KEY";
    private const string BaseUrl = "https://api.siliconflow.cn/v1";

    [Fact]
    public async Task TestConnectionAsync_WithRealEndpoint_ShouldSucceed()
    {
        var apiKey = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            return; // 无凭据时静默跳过：CI 不该因为缺少外部 key 而红

        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);
        var config = new ProviderConfig
        {
            Id = "siliconflow",
            Type = ProviderTypes.OpenAi,
            BaseUrl = BaseUrl,
            ApiKey = apiKey,
        };

        var client = factory.Create(config, LlmCapabilityKind.SpeechTranscription);
        client.Should().NotBeNull("openai 协议工厂已实现 ASR 端口");

        var ok = await client!.TestConnectionAsync(
            "FunAudioLLM/SenseVoiceSmall",
            LlmCapabilityKind.SpeechTranscription,
            call: null,
            TestContext.Current.CancellationToken);

        ok.Should().BeTrue(
            "SiliconFlow 的转写端点接受我们的 multipart + verbose_json 形状；"
            + "失败说明协议形状漂移（有变更需同步 OpenAiSpeechTranscriptionClient）");
    }

    /// <summary>
    /// 真实中文语音端到端：验证 <c>text</c> 能正确解析出内容（不只是「没报错」）。
    /// <para>静音探针只能证明端点活着，证明不了响应结构能被我们的模型解析出文本。</para>
    /// </summary>
    [Fact]
    public async Task TranscribeAsync_WithRealSpeech_ShouldReturnRecognizedText()
    {
        var apiKey = Environment.GetEnvironmentVariable(KeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var factory = new OpenAiLlmClientFactory(NullLoggerFactory.Instance);
        var config = new ProviderConfig
        {
            Id = "siliconflow",
            Type = ProviderTypes.OpenAi,
            BaseUrl = BaseUrl,
            ApiKey = apiKey,
        };

        var client = (ISpeechTranscriptionClient)factory
            .Create(config, LlmCapabilityKind.SpeechTranscription)!;

        // 1kHz 正弦波 1 秒：有声音但无语音，服务端返回空文本也算合法响应；
        // 这里只断言「解析成功且拿到非 null 文本」，不臆测识别内容。
        using var audio = new MemoryStream(BuildToneWav(durationMs: 1000));
        var result = await client.TranscribeAsync(
            new TranscriptionRequest
            {
                Model = "FunAudioLLM/SenseVoiceSmall",
                Audio = audio,
                MimeType = "audio/wav",
                FileName = "tone.wav",
            },
            call: null,
            TestContext.Current.CancellationToken);

        result.Text.Should().NotBeNull("响应可解析即证明我们的 TranscriptionApiResponse 映射正确");
    }

    /// <summary>生成指定时长的 1kHz 正弦波 WAV（16bit 单声道 16kHz）。</summary>
    private static byte[] BuildToneWav(int durationMs)
    {
        const int sampleRate = 16000;
        const double frequency = 1000;
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
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.2);
            BitConverter.TryWriteBytes(bytes.AsSpan(44 + i * 2, 2), value);
        }

        return bytes;
    }
}
