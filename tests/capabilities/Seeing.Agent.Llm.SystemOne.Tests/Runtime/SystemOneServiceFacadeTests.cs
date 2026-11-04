using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.SystemOne;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.SystemOne.Runtime;
using Xunit;

namespace Seeing.Agent.Llm.SystemOne.Tests.Runtime;

/// <summary>
/// <see cref="SystemOneService"/> 作为 <see cref="IDecisionClient"/> 门面的行为（spec §7.9）：
/// 模型解析走四步链，客户端一律经 <see cref="ILlmRouter"/> 取得，DTO 与旧 SystemOne 形状互转。
/// </summary>
public class SystemOneServiceFacadeTests
{
    [Fact]
    public async Task EvaluateAsync_UsesDefaultDecisionModelAndMapsBothWays()
    {
        var client = new FakeDecisionClient();
        var router = Router(client);
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns("typesafe/jev-latest");
        var service = new SystemOneService(router.Object, models.Object);
        var criteria = new[] { "t", "f" };

        var response = await service.EvaluateAsync(new SystemOneRequest
        {
            State = "s",
            Model = string.Empty,
            Questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal)
            {
                ["n"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Noul,
                    Instructions = "q",
                    Criteria = criteria
                }
            }
        });

        router.Verify(r => r.Resolve<IDecisionClient>("typesafe/jev-latest"), Times.Once);
        client.LastRequest!.Model.Should().Be("typesafe/jev-latest");
        client.LastRequest.Questions["n"].Type.Should().Be("noul");
        client.LastRequest.Questions["n"].Instructions.Should().Be("q");
        client.LastRequest.Questions["n"].Criteria.Should().BeSameAs(criteria);
        response.Model.Should().Be("jev-latest");
        response.Answers["n"].Noul.Should().BeApproximately(0.42, 1e-9);
        response.Usage!.InputTokens.Should().Be(11);
    }

    [Fact]
    public async Task EvaluateAsync_KeepsNoulCriteriaWhenMapped()
    {
        var client = new FakeDecisionClient();
        var router = Router(client);
        var models = SingleDecisionModelCatalog();
        var service = new SystemOneService(router.Object, models.Object);

        await service.EvaluateAsync(new SystemOneRequest
        {
            State = "s",
            Questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal)
            {
                ["n"] = new SystemOneQuestion
                {
                    Type = SystemOneQuestionTypes.Noul,
                    Instructions = "q",
                    Criteria = new Dictionary<string, object?> { ["true"] = "y", ["false"] = "n" }
                }
            }
        });

        client.LastRequest!.Questions["n"].Criteria.Should().NotBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_UsesRequestModelWhenExplicit()
    {
        var client = new FakeDecisionClient();
        var router = Router(client);
        var models = new Mock<IModelConfigManager>();

        var service = new SystemOneService(router.Object, models.Object);
        await service.EvaluateAsync(new SystemOneRequest { State = "s", Model = "typesafe/jev-mini" });

        router.Verify(r => r.Resolve<IDecisionClient>("typesafe/jev-mini"), Times.Once);
        models.Verify(m => m.GetModelsByType(ModelType.Decision, null), Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_NoModelConfigured_ErrsWithoutFallingBack()
    {
        var client = new FakeDecisionClient();
        var router = Router(client);
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns((string?)null);
        models.Setup(m => m.GetModelsByType(ModelType.Decision, null))
            .Returns(new Dictionary<string, ModelConfig>());

        var service = new SystemOneService(router.Object, models.Object);

        var act = () => service.EvaluateAsync(new SystemOneRequest { State = "s" });

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("未配置 Decision 模型");
        router.Verify(r => r.Resolve<IDecisionClient>(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ListModelsAsync_ByProviderId_ResolvesThatProvidersClient()
    {
        var client = new FakeDecisionClient
        {
            Models = [new DecisionModel { Name = "jev-latest", Description = "d", ReleaseDate = "2026-01-01" }]
        };
        var router = Router(client);
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModelsByType(ModelType.Decision, "typesafe"))
            .Returns(new Dictionary<string, ModelConfig> { ["typesafe/jev-latest"] = new() { Id = "jev-latest" } });

        var service = new SystemOneService(router.Object, models.Object);
        var result = await service.ListModelsAsync("typesafe");

        router.Verify(r => r.Resolve<IDecisionClient>("typesafe/jev-latest"), Times.Once);
        result.Should().ContainSingle().Which.Name.Should().Be("jev-latest");
        result[0].Description.Should().Be("d");
        result[0].ReleaseDate.Should().Be("2026-01-01");
    }

    [Fact]
    public async Task ListModelsAsync_UnknownProvider_Errs()
    {
        var router = Router(new FakeDecisionClient());
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetModelsByType(ModelType.Decision, "nope"))
            .Returns(new Dictionary<string, ModelConfig>());

        var service = new SystemOneService(router.Object, models.Object);

        var act = () => service.ListModelsAsync("nope");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("nope");
    }

    /// <summary>目录中恰好一个 Decision 模型 → 第 3 步命中。</summary>
    private static Mock<IModelConfigManager> SingleDecisionModelCatalog()
    {
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns((string?)null);
        models.Setup(m => m.GetModelsByType(ModelType.Decision, null))
            .Returns(new Dictionary<string, ModelConfig> { ["typesafe/jev-latest"] = new() { Id = "jev-latest" } });
        return models;
    }

    private static Mock<ILlmRouter> Router(IDecisionClient client)
    {
        var router = new Mock<ILlmRouter>();
        router.Setup(r => r.Resolve<IDecisionClient>(It.IsAny<string>())).Returns(client);
        return router;
    }

    /// <summary>记录请求并回放固定应答的 Decision 客户端替身。</summary>
    private sealed class FakeDecisionClient : IDecisionClient
    {
        public DecisionRequest? LastRequest { get; private set; }
        public List<DecisionModel> Models { get; init; } = new();

        public Task<DecisionResponse> EvaluateAsync(
            DecisionRequest request, LlmCallContext? call = null, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new DecisionResponse
            {
                Model = "jev-latest",
                Answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
                {
                    ["n"] = new DecisionAnswer { Type = "noul", Noul = 0.42 }
                },
                Usage = new TokenUsage { InputTokens = 11, OutputTokens = 5 }
            });
        }

        public Task<IReadOnlyList<DecisionModel>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DecisionModel>>(Models);

        public Task<bool> TestConnectionAsync(
            string modelId, LlmCapabilityKind capability, LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public string ProviderId => "typesafe";
        public string ProviderType => ProviderTypes.TypeSafe;
    }
}
