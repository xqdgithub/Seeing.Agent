using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm.Support;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// Task R-7：<c>ModelType.Image</c> 删除后推断必须整体改投 <see cref="ModelType.ImageGeneration"/>。
/// <para>含糊模式（<c>-image</c> / <c>image-</c>）也归生成：若落到 Chat，
/// <c>Resolve&lt;IChatCompletionClient&gt;("openai/dall-e-3")</c> 会通过五层校验并发出 chat 请求，
/// 制造运行期 400（spec §7.2.1 / 决议 4）。</para>
/// </summary>
public class ModelTypeInferenceTests
{
    [Theory]
    [InlineData("dall-e-3", ModelType.ImageGeneration)]
    [InlineData("DALL-E-2", ModelType.ImageGeneration)]
    [InlineData("flux-pro", ModelType.ImageGeneration)]
    [InlineData("sdxl-turbo", ModelType.ImageGeneration)]
    [InlineData("stable-diffusion-xl", ModelType.ImageGeneration)]
    [InlineData("kolors-v1", ModelType.ImageGeneration)]
    [InlineData("my-image-model", ModelType.ImageGeneration)]
    [InlineData("image-anything", ModelType.ImageGeneration)]
    public void InferTypes_ImageModels_ShouldReturnImageGeneration(string id, ModelType expected)
        => ModelTypeInference.InferTypes(id).Should().ContainSingle().Which.Should().Be(expected);

    [Fact]
    public void InferTypes_ShouldNeverReturnRemovedImageValue()
        => Enum.GetNames<ModelType>().Should().NotContain("Image");

    [Theory]
    [InlineData("text-embedding-3-small", ModelType.Embedding)]
    [InlineData("bge-m3", ModelType.Embedding)]
    [InlineData("gte-large", ModelType.Embedding)]
    [InlineData("bge-reranker-v2", ModelType.Rerank)]
    [InlineData("whisper-1", ModelType.Speech)]
    [InlineData("tts-1", ModelType.Speech)]
    [InlineData("cogvideox-2", ModelType.Video)]
    public void InferTypes_OtherStrongConventions_ShouldStayUnchanged(string id, ModelType expected)
        => ModelTypeInference.InferTypes(id).Should().ContainSingle().Which.Should().Be(expected);

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("")]
    [InlineData(null)]
    public void InferTypes_NoStrongConvention_ShouldReturnNull(string? id)
        => ModelTypeInference.InferTypes(id).Should().BeNull("未命中约定就不臆测");

    [Fact]
    public void InferTypes_ImageGeneration_ShouldMapToImageGenerationCapability()
    {
        // 推断结果必须能被路由：ImageGeneration 类型 → ImageGeneration 能力端口
        foreach (var id in new[] { "dall-e-3", "flux-pro", "sdxl-turbo" })
        {
            var types = ModelTypeInference.InferTypes(id)!;
            types.Should().OnlyContain(type =>
                LlmCapabilityMap.FromModelType(type).Contains(LlmCapabilityKind.ImageGeneration));
        }
    }
}
