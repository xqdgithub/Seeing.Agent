using System.Linq;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 能力枚举与映射表（spec §7.1 / §7.2.1 / §7.12）的契约测试。
/// </summary>
public class LlmCapabilityMapTests
{
    [Fact]
    public void ToConfigKey_And_ToHookValue_ShouldCoverEveryKind()
    {
        foreach (var kind in Enum.GetValues<LlmCapabilityKind>().Where(k => k != LlmCapabilityKind.None))
        {
            LlmCapabilityMap.ToConfigKey(kind).Should().NotBeNullOrWhiteSpace();
            LlmCapabilityMap.ToHookValue(kind).Should().NotBeNullOrWhiteSpace();
        }

        // 两套投影：配置端小驼峰、Hook 端点分（决议 18）
        LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.SpeechSynthesis).Should().Be("speechSynthesis");
        LlmCapabilityMap.ToHookValue(LlmCapabilityKind.SpeechSynthesis).Should().Be("speech.synthesis");
    }

    [Fact]
    public void ToConfigKey_ShouldUseLowerCaseCamelForEverySingleKind()
    {
        var expected = new Dictionary<LlmCapabilityKind, string>
        {
            [LlmCapabilityKind.Chat] = "chat",
            [LlmCapabilityKind.Embedding] = "embedding",
            [LlmCapabilityKind.Rerank] = "rerank",
            [LlmCapabilityKind.Decision] = "decision",
            [LlmCapabilityKind.SpeechSynthesis] = "speechSynthesis",
            [LlmCapabilityKind.SpeechTranscription] = "speechTranscription",
            [LlmCapabilityKind.ImageGeneration] = "imageGeneration",
            [LlmCapabilityKind.VideoGeneration] = "videoGeneration",
        };

        foreach (var pair in expected)
        {
            LlmCapabilityMap.ToConfigKey(pair.Key).Should().Be(pair.Value, $"配置键应为小驼峰：{pair.Key}");
        }
    }

    [Fact]
    public void ToHookValue_ShouldUseDottedForCompoundNames()
    {
        var expected = new Dictionary<LlmCapabilityKind, string>
        {
            [LlmCapabilityKind.Chat] = "chat",
            [LlmCapabilityKind.Embedding] = "embedding",
            [LlmCapabilityKind.Rerank] = "rerank",
            [LlmCapabilityKind.Decision] = "decision",
            [LlmCapabilityKind.SpeechSynthesis] = "speech.synthesis",
            [LlmCapabilityKind.SpeechTranscription] = "speech.transcription",
            [LlmCapabilityKind.ImageGeneration] = "image.generation",
            [LlmCapabilityKind.VideoGeneration] = "video.generation",
        };

        foreach (var pair in expected)
        {
            LlmCapabilityMap.ToHookValue(pair.Key).Should().Be(pair.Value, $"Hook 载荷值应为点分：{pair.Key}");
        }
    }

    [Fact]
    public void ToConfigKey_And_ToHookValue_ShouldNotBeSameProjection()
    {
        // 决议 18：这是两套投影，不能合并
        LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.SpeechSynthesis)
            .Should()
            .NotBe(LlmCapabilityMap.ToHookValue(LlmCapabilityKind.SpeechSynthesis));
    }

    [Fact]
    public void ModelTypeImage_ShouldNoLongerExist()
        => Enum.GetNames<ModelType>().Should().NotContain("Image");

    [Fact]
    public void ModelType_ShouldContainDecisionAndImageGeneration()
    {
        var names = Enum.GetNames<ModelType>();

        names.Should().Contain("Decision");
        names.Should().Contain("ImageGeneration");
    }

    [Fact]
    public void ClientTypes_ShouldMapEveryRoutableKind()
    {
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.Chat].Should().Be(typeof(IChatCompletionClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.Embedding].Should().Be(typeof(IEmbeddingClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.Rerank].Should().Be(typeof(IRerankClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.Decision].Should().Be(typeof(IDecisionClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.SpeechSynthesis].Should().Be(typeof(ISpeechSynthesisClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.SpeechTranscription].Should().Be(typeof(ISpeechTranscriptionClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.ImageGeneration].Should().Be(typeof(IImageGenerationClient));
        LlmCapabilityMap.ClientTypes[LlmCapabilityKind.VideoGeneration].Should().Be(typeof(IVideoGenerationClient));
        LlmCapabilityMap.ClientTypes.Should().HaveCount(8); // 8 个能力端口
    }

    [Fact]
    public void FromModelType_ShouldCoverEveryModelType()
    {
        var mapped = Enum.GetValues<ModelType>()
            .SelectMany(LlmCapabilityMap.FromModelType)
            .Where(k => k != LlmCapabilityKind.None)
            .Distinct();

        // 除 None（Other）之外，每个能力都应由某个 ModelType 可达
        mapped.Should().BeEquivalentTo(Enum.GetValues<LlmCapabilityKind>().Where(k => k != LlmCapabilityKind.None));
    }

    [Fact]
    public void FromModelType_Speech_ShouldYieldBothDirections()
        => LlmCapabilityMap.FromModelType(ModelType.Speech).Should()
            .BeEquivalentTo(new[] { LlmCapabilityKind.SpeechSynthesis, LlmCapabilityKind.SpeechTranscription });

    [Theory]
    [InlineData(ModelType.Text, LlmCapabilityKind.Chat)]
    [InlineData(ModelType.Embedding, LlmCapabilityKind.Embedding)]
    [InlineData(ModelType.Rerank, LlmCapabilityKind.Rerank)]
    [InlineData(ModelType.Decision, LlmCapabilityKind.Decision)]
    [InlineData(ModelType.ImageGeneration, LlmCapabilityKind.ImageGeneration)]
    [InlineData(ModelType.Video, LlmCapabilityKind.VideoGeneration)]
    [InlineData(ModelType.Other, LlmCapabilityKind.None)]
    public void FromModelType_SingleValuedTypes_ShouldYieldExpectedKind(ModelType type, LlmCapabilityKind expected)
        => LlmCapabilityMap.FromModelType(type).Should().ContainSingle().Which.Should().Be(expected);

    [Fact]
    public void DefaultCapabilities_OpenAi_ShouldBeChatOnly()
        => LlmCapabilityMap.DefaultCapabilities("openai").Should().Be(LlmCapabilityKind.Chat);

    [Fact]
    public void DefaultCapabilities_TypeSafe_ShouldBeDecision()
        => LlmCapabilityMap.DefaultCapabilities("typesafe").Should().Be(LlmCapabilityKind.Decision);

    [Theory]
    [InlineData("openai", LlmCapabilityKind.Chat)]
    [InlineData("openai-responses", LlmCapabilityKind.Chat)]
    [InlineData("anthropic", LlmCapabilityKind.Chat)]
    [InlineData("typesafe", LlmCapabilityKind.Decision)]
    [InlineData("some-unknown-protocol", LlmCapabilityKind.Chat)]
    [InlineData("", LlmCapabilityKind.Chat)]
    public void DefaultCapabilities_ShouldFollowProviderDefaultsTable(string protocol, LlmCapabilityKind expected)
        => LlmCapabilityMap.DefaultCapabilities(protocol).Should().Be(expected);

    // ---- 关卡 1 修复：字符串反向解析与类型反查（B1） ----

    [Fact]
    public void TryParseConfigKey_ShouldRoundTripEveryKind()
    {
        foreach (var kind in Enum.GetValues<LlmCapabilityKind>().Where(k => k != LlmCapabilityKind.None))
        {
            LlmCapabilityMap.TryParseConfigKey(LlmCapabilityMap.ToConfigKey(kind), out var parsed)
                .Should().BeTrue();
            parsed.Should().Be(kind);
        }
    }

    [Fact]
    public void TryParseConfigKey_UnknownOrNumeric_ShouldReturnFalse()
    {
        LlmCapabilityMap.TryParseConfigKey("notACapability", out _).Should().BeFalse();
        LlmCapabilityMap.TryParseConfigKey("8", out _).Should().BeFalse();
        LlmCapabilityMap.TryParseConfigKey(null, out _).Should().BeFalse();
        LlmCapabilityMap.TryParseConfigKey("", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseConfigKey_ShouldBeCaseInsensitive()
    {
        LlmCapabilityMap.TryParseConfigKey("SpeechSynthesis", out var kind).Should().BeTrue();
        kind.Should().Be(LlmCapabilityKind.SpeechSynthesis);
    }

    [Fact]
    public void ParseCapabilities_NullOrEmpty_ShouldFallBackToProtocolDefault()
    {
        LlmCapabilityMap.ParseCapabilities(null, "openai").Should().Be(LlmCapabilityKind.Chat);
        LlmCapabilityMap.ParseCapabilities(Array.Empty<string>(), "typesafe")
            .Should().Be(LlmCapabilityKind.Decision);
    }

    [Fact]
    public void ParseCapabilities_UnknownName_ShouldThrowNotSilentlyIgnore()
    {
        var act = () => LlmCapabilityMap.ParseCapabilities(new[] { "chat", "embeding" }, "openai");
        act.Should().Throw<ArgumentException>().WithMessage("*embeding*");
    }

    [Fact]
    public void ParseCapabilities_ShouldUnionMultipleKinds()
        => LlmCapabilityMap.ParseCapabilities(new[] { "chat", "embedding" }, "openai")
            .Should().Be(LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding);

    [Fact]
    public void TryGetKindFromClientType_ShouldMapEveryPortBack()
    {
        foreach (var (kind, type) in LlmCapabilityMap.ClientTypes)
        {
            LlmCapabilityMap.TryGetKindFromClientType(type, out var back).Should().BeTrue();
            back.Should().Be(kind);
        }

        LlmCapabilityMap.TryGetKindFromClientType(typeof(ILlmCapabilityClient), out _).Should().BeFalse();
    }

    [Fact]
    public void DefaultCapabilities_OpenAi_ShouldNotIncludeEmbedding()
    {
        // v1 的 openai → Chat, Embedding 是错的（spec §7.1）：缺省只给 Chat
        LlmCapabilityMap.DefaultCapabilities("openai").HasFlag(LlmCapabilityKind.Embedding).Should().BeFalse();
    }
}
