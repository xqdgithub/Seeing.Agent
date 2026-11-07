using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// 能力端口与 DTO 的契约形状测试（spec §7.2 / §7.2.2）。
/// 锁死三条最容易被顺手写回去的规则：客户端不自述能力（决议 15）、
/// 连接测试必带 capability（决议 9）、音频流归调用方（决议 17）。
/// </summary>
public class LlmCapabilityContractTests
{
    private static readonly Type[] s_allPorts =
    [
        typeof(IChatCompletionClient),
        typeof(IEmbeddingClient),
        typeof(IRerankClient),
        typeof(IDecisionClient),
        typeof(ISpeechSynthesisClient),
        typeof(ISpeechTranscriptionClient),
        typeof(IImageGenerationClient),
        typeof(IVideoGenerationClient),
    ];

    [Fact]
    public void ILlmCapabilityClient_ShouldNotExposeCapabilitiesProperty()
        => typeof(ILlmCapabilityClient).GetProperty("Capabilities").Should().BeNull();

    [Fact]
    public void CapabilityClient_And_EveryPort_ShouldNotSelfDescribeCapabilities()
    {
        // 决议 15 / 反目标「让同一个事实存在三份数据」：
        // 基座与八个端口都不允许出现 Capabilities（位集）或 Capability（单值）属性。
        var suspects = new[] { typeof(ILlmCapabilityClient) }.Concat(s_allPorts);

        foreach (var type in suspects)
        {
            type.GetProperty("Capabilities").Should().BeNull($"{type.Name} 不得声明 Capabilities 属性");
            type.GetProperty("Capability").Should().BeNull($"{type.Name} 不得声明 Capability 属性");
        }
    }

    [Fact]
    public void TestConnectionAsync_ShouldRequireCapabilityParameter()
    {
        var p = typeof(ILlmCapabilityClient).GetMethod(nameof(ILlmCapabilityClient.TestConnectionAsync))!;

        p.GetParameters().Select(x => x.ParameterType).Should().Contain(typeof(LlmCapabilityKind));
    }

    [Fact]
    public void TestConnectionAsync_ShouldHaveSpecifiedParameterOrder()
    {
        var method = typeof(ILlmCapabilityClient).GetMethod(nameof(ILlmCapabilityClient.TestConnectionAsync))!;
        var parameters = method.GetParameters();

        method.ReturnType.Should().Be(typeof(Task<bool>));
        parameters.Should().HaveCount(4);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(LlmCapabilityKind));
        parameters[2].ParameterType.Should().Be(typeof(LlmCallContext));
        parameters[3].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void ILlmCapabilityClient_ShouldCarryIdentityOnly()
    {
        typeof(ILlmCapabilityClient).GetProperty("ProviderId")!.PropertyType.Should().Be(typeof(string));
        typeof(ILlmCapabilityClient).GetProperty("ProviderType")!.PropertyType.Should().Be(typeof(string));

        // 基座只有身份 + 连接测试：任何能力语义方法都不许下沉到基座
        typeof(ILlmCapabilityClient)
            .GetMethods()
            .Select(m => m.Name)
            .Where(n => n.StartsWith("get_", StringComparison.Ordinal) is false)
            .Should()
            .BeEquivalentTo(new[] { nameof(ILlmCapabilityClient.TestConnectionAsync) });
    }

    [Theory]
    [MemberData(nameof(AllPorts))]
    public void EveryPort_ShouldDeriveFromCapabilityClientBase(Type port)
        => typeof(ILlmCapabilityClient).IsAssignableFrom(port).Should().BeTrue($"{port.Name} 应派生于基座");

    public static TheoryData<Type> AllPorts
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var port in s_allPorts)
                data.Add(port);
            return data;
        }
    }

    [Fact]
    public void ClientTypes_ShouldOnlyContainTheEightPortInterfaces()
    {
        LlmCapabilityMap.ClientTypes.Values.Should().BeEquivalentTo(s_allPorts);

        foreach (var type in LlmCapabilityMap.ClientTypes.Values)
        {
            type.IsInterface.Should().BeTrue();
            type.IsPublic.Should().BeTrue();
        }
    }

    [Fact]
    public void ChatPort_ShouldExposeCompleteAndStreamVariants()
    {
        typeof(IChatCompletionClient).GetMethod("CompleteAsync")!.ReturnType
            .Should().Be(typeof(Task<ChatResponse>));
        typeof(IChatCompletionClient).GetMethod("CompleteStreamAsync")!.ReturnType
            .Should().Be(typeof(IAsyncEnumerable<StreamUpdate>));
    }

    [Fact]
    public void EmbeddingPort_ShouldReturnEmbeddingResponse()
    {
        var method = typeof(IEmbeddingClient).GetMethod("EmbedAsync")!;

        method.GetParameters()[0].ParameterType.Should().Be(typeof(EmbeddingRequest));
        method.ReturnType.Should().Be(typeof(Task<EmbeddingResponse>));
    }

    [Fact]
    public void DecisionPort_ShouldNotDuplicateModelCatalog()
    {
        typeof(IDecisionClient).GetMethod("EvaluateAsync")!.ReturnType
            .Should().Be(typeof(Task<DecisionResponse>));

        // 目录维度的唯一投影是 IModelCatalogClient（返回 ModelConfig，直接进目录口径）。
        // 早期版本在 IDecisionClient 上也声明过同名成员，导致同一 wire 端点被两个接口
        // 各投影一次且无消费者——与「同一事实不留三份数据」冲突，已删除。
        typeof(IDecisionClient).GetMethod("ListModelsAsync").Should().BeNull(
            "决策端口只承载评估，不重复声明模型目录");
    }

    [Fact]
    public void SpeechPorts_ShouldExposeNonStreamAndStreamVariants()
    {
        typeof(ISpeechSynthesisClient).GetMethod("SynthesizeAsync")!.ReturnType
            .Should().Be(typeof(Task<SpeechSynthesisResult>));
        typeof(ISpeechSynthesisClient).GetMethod("SynthesizeStreamAsync")!.ReturnType
            .Should().Be(typeof(IAsyncEnumerable<AudioChunk>));
        typeof(ISpeechTranscriptionClient).GetMethod("TranscribeAsync")!.ReturnType
            .Should().Be(typeof(Task<TranscriptionResult>));
        typeof(ISpeechTranscriptionClient).GetMethod("TranscribeStreamAsync")!.ReturnType
            .Should().Be(typeof(IAsyncEnumerable<TranscriptSegment>));
    }

    [Fact]
    public void VideoPort_ShouldBeJobBased()
    {
        typeof(IVideoGenerationClient).GetMethod("SubmitAsync")!.ReturnType
            .Should().Be(typeof(Task<MediaJobHandle>));
        typeof(IVideoGenerationClient).GetMethod("GetStatusAsync")!.ReturnType
            .Should().Be(typeof(Task<MediaJobStatus>));
        typeof(IVideoGenerationClient).GetMethod("CancelAsync")!.ReturnType
            .Should().Be(typeof(Task));
        typeof(IVideoGenerationClient).GetMethod("PollAsync")!.ReturnType
            .Should().Be(typeof(IAsyncEnumerable<MediaJobStatus>));
    }

    [Fact]
    public void ImageAndRerankPorts_ShouldBeSingleMethod()
    {
        typeof(IImageGenerationClient).GetMethod("GenerateAsync")!.ReturnType
            .Should().Be(typeof(Task<ImageGenerationResult>));
        typeof(IRerankClient).GetMethod("RerankAsync")!.ReturnType
            .Should().Be(typeof(Task<RerankResponse>));
    }

    [Fact]
    public void EveryPortMethod_ShouldBeTaskBasedOrAsyncEnumerable()
    {
        // 禁止 async void：每个端口方法的返回类型必须是 Task/Task<T> 或 IAsyncEnumerable<T>
        foreach (var port in s_allPorts)
        {
            foreach (var method in port.GetMethods())
            {
                var isTaskBased = typeof(Task).IsAssignableFrom(method.ReturnType);
                var isStream = method.ReturnType.IsGenericType
                               && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>);

                (isTaskBased || isStream).Should().BeTrue(
                    $"{port.Name}.{method.Name} 返回类型必须是 Task/Task<T>/IAsyncEnumerable<T>，实际 {method.ReturnType.Name}");
            }
        }
    }

    [Fact]
    public void ModelCatalogClient_ShouldBeIndependentOptionalPort()
    {
        // IModelCatalogClient 不是能力端口：由客户端自行实现，ConfiguredLlmProvider 检测
        typeof(ILlmCapabilityClient).IsAssignableFrom(typeof(IModelCatalogClient))
            .Should().BeFalse("动态目录端口不属于能力基座");
        typeof(IModelCatalogClient).GetMethod("ListModelsAsync")!.ReturnType
            .Should().Be(typeof(Task<IReadOnlyList<ModelConfig>>));
    }

    [Fact]
    public void DecisionAnswer_ShouldCarryAllThreePrimitives()
    {
        var a = new DecisionAnswer { Noul = 0.9, Choice = "billing", Score = 2, Confidence = 0.8 };

        a.Noul.Should().Be(0.9);
        a.Choice.Should().Be("billing");
        a.Score.Should().Be(2);
        a.Confidence.Should().Be(0.8);
    }

    [Fact]
    public void DecisionQuestionTypes_ShouldUseWireNames()
    {
        DecisionQuestionTypes.Noul.Should().Be("noul");
        DecisionQuestionTypes.Choice.Should().Be("choice");
        DecisionQuestionTypes.Score.Should().Be("score");
    }

    [Fact]
    public void DecisionModels_ShouldRoundTripStateAndAnswers()
    {
        var request = new DecisionRequest
        {
            State = new { ticket = "t-1" },
            Model = "jev-latest",
            Questions = { ["route"] = new DecisionQuestion { Type = DecisionQuestionTypes.Choice } }
        };
        var response = new DecisionResponse { Model = "jev-latest" };
        response.Answers["route"] = new DecisionAnswer { Type = "choice", Choice = "billing" };

        request.Model.Should().Be("jev-latest");
        request.Questions["route"].Type.Should().Be("choice");
        response.Usage.Should().BeNull();
        response.Answers["route"].Choice.Should().Be("billing");
    }

    [Fact]
    public void EmbeddingResponse_Dimensions_ShouldDeriveFromFirstVector()
    {
        var response = new EmbeddingResponse
        {
            Vectors = [new EmbeddingVector("a", new float[3], 0), new EmbeddingVector("b", new float[3], 1)]
        };
        var empty = new EmbeddingResponse { Vectors = Array.Empty<EmbeddingVector>() };

        response.Dimensions.Should().Be(3);
        empty.Dimensions.Should().Be(0);
    }

    [Fact]
    public void EmbeddingRequest_ShouldCarryInputsAndOptionalDimensionControls()
    {
        var request = new EmbeddingRequest
        {
            Model = "text-embedding-3-small",
            Inputs = ["a", "b"],
            Dimensions = 1024,
            EncodingFormat = "float"
        };

        request.Model.Should().Be("text-embedding-3-small");
        request.Inputs.Should().HaveCount(2);
        request.Dimensions.Should().Be(1024);
        request.EncodingFormat.Should().Be("float");
    }

    [Fact]
    public void TranscriptionRequest_Audio_ShouldBeCallerOwnedStream()
    {
        // 决议 17：Audio 由调用方拥有，实现不得 Dispose。
        // 这里锁定形状（可写的 Stream 属性 + 元数据字段），Dispose 行为由 O-3 的流所有权测试兜住。
        var audio = typeof(TranscriptionRequest).GetProperty("Audio")!;

        audio.PropertyType.Should().Be(typeof(Stream));
        audio.CanWrite.Should().BeTrue();
        typeof(TranscriptionRequest).GetProperty("Model")!.PropertyType.Should().Be(typeof(string));
        typeof(TranscriptionRequest).GetProperty("MimeType")!.PropertyType.Should().Be(typeof(string));
        typeof(TranscriptionRequest).GetProperty("FileName")!.PropertyType.Should().Be(typeof(string));
        typeof(TranscriptionRequest).GetProperty("Language")!.PropertyType.Should().Be(typeof(string));
    }

    [Fact]
    public void AudioPayloads_ShouldUseReadOnlyMemoryNotOwnedBuffers()
    {
        var chunk = new AudioChunk(new byte[8], "audio/mpeg");
        var result = new SpeechSynthesisResult(new byte[8], "audio/mpeg", null);

        chunk.Data.Length.Should().Be(8);
        chunk.MimeType.Should().Be("audio/mpeg");
        result.Audio.Length.Should().Be(8);
        result.Usage.Should().BeNull();

        typeof(SpeechSynthesisRequest).GetProperty("Speed")!.PropertyType.Should().Be(typeof(double?));
        typeof(TranscriptSegment).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(new[]
        {
            nameof(TranscriptSegment.Start),
            nameof(TranscriptSegment.End),
            nameof(TranscriptSegment.Text),
        });
    }

    [Fact]
    public void ImageAndVideoDtos_ShouldBeContractComplete()
    {
        var image = new GeneratedImage("https://example.com/1.png", null, "image/png", null);
        var handle = new MediaJobHandle("job-1", "openai");
        var status = new MediaJobStatus(MediaJobState.Running, null, null, 0.5);

        image.Url.Should().Be("https://example.com/1.png");
        image.Data.HasValue.Should().BeFalse();
        handle.JobId.Should().Be("job-1");
        handle.ProviderId.Should().Be("openai");
        status.State.Should().Be(MediaJobState.Running);
        status.Progress.Should().Be(0.5);

        Enum.GetNames<MediaJobState>().Should().BeEquivalentTo(new[]
        {
            "Queued", "Running", "Succeeded", "Failed", "Cancelled",
        });

        typeof(VideoGenerationRequest).GetProperty("Duration")!.PropertyType.Should().Be(typeof(TimeSpan?));
        typeof(ImageGenerationRequest).GetProperty("Count")!.PropertyType.Should().Be(typeof(int?));
    }

    [Fact]
    public void StreamingPorts_ShouldOnlyUseAsyncEnumerable()
    {
        // 禁止 async void / Task<IAsyncEnumerable<T>>：流式一律直接返回 IAsyncEnumerable<T>
        var streamMethods = s_allPorts
            .SelectMany(t => t.GetMethods())
            .Where(m => m.Name.EndsWith("StreamAsync", StringComparison.Ordinal) || m.Name == "PollAsync");

        streamMethods.Should().NotBeEmpty();
        foreach (var method in streamMethods)
        {
            method.ReturnType.IsGenericType.Should().BeTrue();
            method.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(IAsyncEnumerable<>));
        }
    }
}
