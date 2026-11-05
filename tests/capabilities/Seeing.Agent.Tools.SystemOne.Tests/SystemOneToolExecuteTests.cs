using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Llm;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tools.SystemOne.Tests;

/// <summary>
/// <see cref="SystemOneTool"/> 经 <see cref="ILlmRouter"/> 调用 <see cref="IDecisionClient"/> 的行为，
/// 以及 spec §7.9 的模型解析四步链（工具参数 → DefaultModels.decision → 目录唯一 Decision → 报错列候选）。
/// </summary>
public class SystemOneToolExecuteTests
{
    [Fact]
    public async Task ask_执行_映射并返回定界JSON()
    {
        var client = new FakeDecisionClient();
        var (tool, context, router) = Create(SystemOneToolKind.Ask, client);
        var args = JsonDocument.Parse("""
        {"state":{"text":"text"},"model":"typesafe/jev-latest","questions":[
          {"id":"n","type":"noul","instructions":"q1","criteriaTrue":"y","criteriaFalse":"n"},
          {"id":"c","type":"choice","instructions":"q2","options":[{"label":"a","description":"da"},{"label":"b"}]},
          {"id":"s","type":"score","instructions":"q3","levels":["lo","hi"]}
        ]}
        """).RootElement;

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        result.Output.Should().Contain("<<<SYSTEMONE_ANSWERS_BEGIN>>>").And.Contain("<<<SYSTEMONE_ANSWERS_END>>>");
        router.LastModelId.Should().Be("typesafe/jev-latest");
        client.LastRequest!.Questions.Should().HaveCount(3);
        client.LastRequest.Questions["c"].Criteria.Should().BeOfType<Dictionary<string, object?>>();
        client.LastRequest.Questions["s"].Criteria.Should().BeOfType<string[]>();
        client.LastRequest.Questions["n"].Criteria.Should().BeOfType<Dictionary<string, object?>>();
    }

    [Fact]
    public async Task 同类noul_缺id_请求键应为自动id()
    {
        var client = new FakeDecisionClient();
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client);
        var args = Args("""{"state":{"text":"s"},"model":"m","questions":[{"instructions":"x"}]}""");

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeTrue(result.Error);
        client.LastRequest!.Questions.Keys.Should().Equal("q0");
        client.LastRequest.Questions["q0"].Type.Should().Be(DecisionQuestionTypes.Noul);
    }

    [Fact]
    public async Task 同类noul_无criteria_请求Criteria应为null()
    {
        var client = new FakeDecisionClient();
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client);
        var args = Args("""{"state":{"text":"s"},"model":"m","questions":[{"id":"a","instructions":"x"}]}""");

        await tool.ExecuteAsync(args, context);

        client.LastRequest!.Questions["a"].Criteria.Should().BeNull();
    }

    [Fact]
    public async Task state为对象_应作为结构化值透传()
    {
        var client = new FakeDecisionClient();
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client);
        var args = Args("""{"state":{"ticket":"x"},"model":"m","questions":[{"instructions":"x"}]}""");

        await tool.ExecuteAsync(args, context);

        client.LastRequest!.State.Should().BeOfType<JsonElement>();
    }

    [Fact]
    public async Task 输出字段名保持原小写wire形状()
    {
        var client = new FakeDecisionClient
        {
            Response = new DecisionResponse
            {
                Model = "jev-latest",
                Answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
                {
                    ["n"] = new DecisionAnswer { Type = "noul", Noul = 0.5, Confidence = 0.9 }
                },
                Usage = new TokenUsage { InputTokens = 3, OutputTokens = 1 }
            }
        };
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client);

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","model":"m","questions":[{"instructions":"x"}]}"""), context);

        var start = result.Output!.IndexOf('{');
        var end = result.Output.LastIndexOf('}');
        var json = result.Output[start..(end + 1)];
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("model").GetString().Should().Be("jev-latest");
        doc.RootElement.GetProperty("answers").GetProperty("n").GetProperty("noul")
            .GetDouble().Should().BeApproximately(0.5, 1e-9);
        doc.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Tool_UsesDefaultModelsDecision_WhenPresent()
    {
        var client = new FakeDecisionClient();
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns("typesafe/jev-default");
        var (tool, context, router) = Create(SystemOneToolKind.Noul, client, models.Object);

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","questions":[{"instructions":"x"}]}"""), context);

        result.Success.Should().BeTrue(result.Error);
        router.LastModelId.Should().Be("typesafe/jev-default");
        client.LastRequest!.Model.Should().Be("typesafe/jev-default");
        // 第 2 步命中即止：不再翻目录
        models.Verify(m => m.GetModelsByType(ModelType.Decision, null), Times.Never);
    }

    [Fact]
    public async Task 目录唯一Decision模型_第3步命中()
    {
        var client = new FakeDecisionClient();
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns((string?)null);
        models.Setup(m => m.GetModelsByType(ModelType.Decision, null))
            .Returns(new Dictionary<string, ModelConfig> { ["typesafe/jev-only"] = new() { Id = "jev-only" } });
        var (tool, context, router) = Create(SystemOneToolKind.Noul, client, models.Object);

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","questions":[{"instructions":"x"}]}"""), context);

        result.Success.Should().BeTrue(result.Error);
        router.LastModelId.Should().Be("typesafe/jev-only");
    }

    [Fact]
    public async Task Tool_NoModelNoDefaultMultipleCandidates_ShouldErrorWithCandidates()
    {
        var client = new FakeDecisionClient();
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns((string?)null);
        models.Setup(m => m.GetModelsByType(ModelType.Decision, null))
            .Returns(new Dictionary<string, ModelConfig>
            {
                ["typesafe/jev-b"] = new() { Id = "jev-b" },
                ["typesafe/jev-a"] = new() { Id = "jev-a" }
            });
        var (tool, context, router) = Create(SystemOneToolKind.Noul, client, models.Object);

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","questions":[{"instructions":"x"}]}"""), context);

        // 不静默选：报错并列出候选
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("2 个候选").And.Contain("typesafe/jev-a").And.Contain("typesafe/jev-b");
        router.LastModelId.Should().BeNull("解析失败时不应发起任何 LLM 调用");
    }

    [Fact]
    public async Task 目录无Decision模型_报未配置()
    {
        var client = new FakeDecisionClient();
        var models = new Mock<IModelConfigManager>();
        models.Setup(m => m.GetDefaultModel(LlmCapabilityKind.Decision)).Returns((string?)null);
        models.Setup(m => m.GetModelsByType(ModelType.Decision, null))
            .Returns(new Dictionary<string, ModelConfig>());
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client, models.Object);

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","questions":[{"instructions":"x"}]}"""), context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未配置 Decision 模型");
    }

    [Fact]
    public async Task 未接入路由_应失败()
    {
        var tool = new SystemOneTool(SystemOneToolKind.Score, NullLogger<SystemOneTool>.Instance);
        var context = new ToolContext { Services = new ServiceCollection().BuildServiceProvider() };
        var args = Args("""{"state":{"text":"s"},"questions":[{"instructions":"x","levels":["a","b"]}]}""");

        var result = await tool.ExecuteAsync(args, context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SystemOne").And.Contain("llm.typesafe");
    }

    [Fact]
    public async Task DecisionProtocolException_应失败带状态码()
    {
        var client = new FakeDecisionClient { Throw = new DecisionProtocolException(429, "rate", "限流") };
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client);

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","model":"m","questions":[{"instructions":"x"}]}"""), context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    [Fact]
    public async Task 路由能力错配_应失败并给出原因()
    {
        // Invoker 内部走 Router 五层校验；这里让 Invoker 直接抛能力错配异常，
        // 验证工具把它翻译成可读失败而非崩溃。
        var invoker = new Mock<ILlmInvoker>();
        invoker
            .Setup(i => i.InvokeAsync<IDecisionClient, DecisionResponse>(
                It.IsAny<string>(),
                It.IsAny<Func<IDecisionClient, LlmCallContext, CancellationToken, Task<DecisionResponse>>>(),
                It.IsAny<LlmInvocationOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LlmCapabilityMismatchException(
                "typesafe/jev-latest", LlmCapabilityKind.Decision, "Provider 未声明 decision 能力"));

        var services = new ServiceCollection()
            .AddSingleton(invoker.Object)
            .AddSingleton(new Mock<IModelConfigManager>().Object)
            .BuildServiceProvider();
        var tool = new SystemOneTool(SystemOneToolKind.Noul, NullLogger<SystemOneTool>.Instance);
        var context = new ToolContext
        {
            Services = services,
            CancellationToken = CancellationToken.None
        };

        var result = await tool.ExecuteAsync(
            Args("""{"state":"s","model":"typesafe/jev-latest","questions":[{"instructions":"x"}]}"""), context);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("路由失败").And.Contain("decision");
    }

    [Fact]
    public async Task 取消_应向外抛()
    {
        var client = new FakeDecisionClient { Throw = new OperationCanceledException() };
        var (tool, context, _) = Create(SystemOneToolKind.Noul, client);

        var act = async () => await tool.ExecuteAsync(
            Args("""{"state":"s","model":"m","questions":[{"instructions":"x"}]}"""), context);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 会话身份_随调用上下文透传()
    {
        var client = new FakeDecisionClient();
        var invoker = new StubLlmInvoker(client);
        var services = new ServiceCollection()
            .AddSingleton<ILlmInvoker>(invoker)
            .AddSingleton(new Mock<IModelConfigManager>().Object)
            .BuildServiceProvider();
        var tool = new SystemOneTool(SystemOneToolKind.Noul, NullLogger<SystemOneTool>.Instance);
        var context = new ToolContext
        {
            Services = services,
            SessionId = "session-1",
            CancellationToken = CancellationToken.None
        };

        await tool.ExecuteAsync(
            Args("""{"state":"s","model":"m","questions":[{"instructions":"x"}]}"""), context);

        // 工具经 LlmInvocationOptions 传会话身份，Invoker 再落到 LlmCallContext
        invoker.LastSessionId.Should().Be("session-1");
        client.LastCall!.SessionId.Should().Be("session-1");
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>
    /// 最小可用的 <see cref="ILlmInvoker"/> 替身：直接调用传入的委托，并如实把
    /// <see cref="LlmInvocationOptions.SessionId"/> 透传成 <see cref="LlmCallContext.SessionId"/>。
    /// <para>工具已改为经 Invoker 调用（决议 27），故测试不再 mock Router——那样会让
    /// 「Hook/出站头/耗时是否生效」这类真实行为无法被覆盖。</para>
    /// </summary>
    private sealed class StubLlmInvoker : ILlmInvoker
    {
        private readonly IDecisionClient _client;

        public StubLlmInvoker(IDecisionClient client) => _client = client;

        /// <summary>本次调用收到的模型 ID（null = 未被调用）。用于断言模型解析链的结果。</summary>
        public string? LastModelId { get; private set; }

        /// <summary>本次调用收到的会话 ID。</summary>
        public string? LastSessionId { get; private set; }

        public Task<TResponse> InvokeAsync<TClient, TResponse>(
            string modelId,
            Func<TClient, LlmCallContext, CancellationToken, Task<TResponse>> call,
            LlmInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
            where TClient : class, ILlmCapabilityClient
        {
            LastModelId = modelId;
            LastSessionId = options?.SessionId;

            var ctx = new LlmCallContext
            {
                SessionId = string.IsNullOrWhiteSpace(options?.SessionId) ? null : options.SessionId
            };
            return call((TClient)(object)_client, ctx, cancellationToken);
        }

        public IAsyncEnumerable<TChunk> StreamAsync<TClient, TChunk>(
            string modelId,
            Func<TClient, LlmCallContext, CancellationToken, IAsyncEnumerable<TChunk>> stream,
            LlmInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
            where TClient : class, ILlmCapabilityClient
            => throw new NotSupportedException("SystemOne 工具不使用流式调用");
    }

    private static (SystemOneTool Tool, ToolContext Context, StubLlmInvoker Invoker) Create(
        SystemOneToolKind kind,
        FakeDecisionClient client,
        IModelConfigManager? models = null)
    {
        var invoker = new StubLlmInvoker(client);

        var services = new ServiceCollection();
        services.AddSingleton<ILlmInvoker>(invoker);
        services.AddSingleton(models ?? new Mock<IModelConfigManager>().Object);

        var context = new ToolContext
        {
            Services = services.BuildServiceProvider(),
            CancellationToken = CancellationToken.None
        };
        return (new SystemOneTool(kind, NullLogger<SystemOneTool>.Instance), context, invoker);
    }

    /// <summary>记录请求与调用上下文、可回放固定应答或抛异常的 Decision 客户端替身。</summary>
    internal sealed class FakeDecisionClient : IDecisionClient
    {
        public DecisionRequest? LastRequest { get; private set; }
        public LlmCallContext? LastCall { get; private set; }
        public Exception? Throw { get; init; }
        public DecisionResponse Response { get; init; } = new()
        {
            Model = "jev-latest",
            Answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
        };

        public string ProviderId => "typesafe";
        public string ProviderType => ProviderTypes.TypeSafe;

        public Task<DecisionResponse> EvaluateAsync(
            DecisionRequest request, LlmCallContext? call = null, CancellationToken ct = default)
        {
            LastRequest = request;
            LastCall = call;
            return Throw is null
                ? Task.FromResult(Response)
                : Task.FromException<DecisionResponse>(Throw);
        }

        public Task<IReadOnlyList<DecisionModel>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DecisionModel>>(Array.Empty<DecisionModel>());

        public Task<bool> TestConnectionAsync(
            string modelId, LlmCapabilityKind capability, LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
