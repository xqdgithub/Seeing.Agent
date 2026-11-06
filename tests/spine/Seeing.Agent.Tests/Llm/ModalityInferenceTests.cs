using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support.Speech;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 语音模态推断守门。
/// <para><b>背景</b>：Provider 的远端 <c>/models</c> 不返回模态，而 Router 第 3 层要求
/// 语音模型必须写明 <c>modalities.input/output</c> 含 <c>audio</c>（决议 24）。
/// 不补的话，刷新出来的 ASR 模型永远无法设为语音转写默认模型。</para>
/// </summary>
public class ModalityInferenceTests
{
    [Theory]
    [InlineData("FunAudioLLM/SenseVoiceSmall")]
    [InlineData("TeleAI/TeleSpeechASR")]
    [InlineData("XingChenAGI/XingChenASR-V3.2")]
    [InlineData("mimo-v2.5-asr")]
    [InlineData("whisper-1")]
    public void Infer_TranscriptionModels_ShouldBeAudioInTextOut(string id)
        => AssertInferred(id, ModelType.Speech, ["audio"], ["text"]);

    [Theory]
    [InlineData("FunAudioLLM/CosyVoice2-0.5B")]
    [InlineData("tts-1")]
    [InlineData("fnlp/MOSS-TTSD-v0.5")]
    public void Infer_SynthesisModels_ShouldBeTextInAudioOut(string id)
        => AssertInferred(id, ModelType.Speech, ["text"], ["audio"]);

    [Fact]
    public void Infer_SpeechWithoutDirectionKeyword_ShouldNotGuess()
    {
        // 标了 Speech 但看不出方向：宁可不填，也不要猜错方向
        var inferred = ModalityInference.Infer("some-speech-model", [ModelType.Speech]);

        inferred.Should().BeNull("猜错方向会让合成模型被当转写模型路由通过，比空着更坏");
    }

    [Fact]
    public void Infer_EmbeddingModel_ShouldBeTextInEmbeddingOut()
        => AssertInferred("BAAI/bge-m3", ModelType.Embedding, ["text"], ["embedding"]);

    [Fact]
    public void Infer_ImageGeneration_ShouldBeTextInImageOut()
        => AssertInferred("Qwen/Qwen-Image", ModelType.ImageGeneration, ["text"], ["image"]);

    [Fact]
    public void Infer_PlainTextModel_ShouldReturnNull()
    {
        ModalityInference.Infer("deepseek-chat", [ModelType.Text]).Should().BeNull(
            "文本模型没有特殊模态约定，留给内建目录与用户覆盖");
    }

    [Fact]
    public void Infer_NoTypes_ShouldReturnNull()
        => ModalityInference.Infer("whisper-1", null).Should().BeNull();

    [Fact]
    public void Apply_ShouldNotOverwriteExistingModalities()
    {
        var model = new ModelConfig
        {
            Id = "FunAudioLLM/SenseVoiceSmall",
            Types = [ModelType.Speech],
            Modalities = new ModelModalities { Input = ["audio", "video"], Output = ["text"] },
        };

        ModalityInference.Apply(model);

        model.Modalities.Input.Should().Equal("audio", "video");
    }

    [Fact]
    public void Apply_ShouldFillWhenModalitiesAreEmpty()
    {
        var model = new ModelConfig
        {
            Id = "mimo-v2.5-asr",
            Types = [ModelType.Speech],
        };

        ModalityInference.Apply(model);

        model.Modalities.Input.Should().Equal("audio");
        model.Modalities.Output.Should().Equal("text");
    }

    private static void AssertInferred(
        string modelId,
        ModelType type,
        string[] expectedInput,
        string[] expectedOutput)
    {
        var inferred = ModalityInference.Infer(modelId, [type]);

        inferred.Should().NotBeNull($"{modelId} 的模态可从名字确定");
        inferred!.Input.Should().Equal(expectedInput);
        inferred.Output.Should().Equal(expectedOutput);
    }
}
