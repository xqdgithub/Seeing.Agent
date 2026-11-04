using System.Linq;
using System.Reflection;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

/// <summary>
/// Provider / 工厂 / 装饰器 / Router / Invoker / 载荷适配器 的契约测试（spec §7.3 / §7.4 / §7.5 / §7.13）。
/// </summary>
public class LlmProviderContractTests
{
    // ---------- ProviderConfig 与协议常量 ----------

    [Fact]
    public void ProviderConfig_ShouldHaveCapabilitiesAndListModels_ButNoDefaultModel()
    {
        typeof(ProviderConfig).GetProperty("Capabilities").Should().NotBeNull();
        typeof(ProviderConfig).GetProperty("ListModels").Should().NotBeNull();
        typeof(ProviderConfig).GetProperty("DefaultModel").Should().BeNull();
    }

    [Fact]
    public void ProviderConfig_Capabilities_ShouldBeNullableStringList_AndListModels_ShouldBeBool()
    {
        // 配置端是字符串列表（小驼峰投影），缺省为 null 表示"未声明"，由协议缺省表兜底
        typeof(ProviderConfig).GetProperty("Capabilities")!.PropertyType
            .Should().Be(typeof(List<string>));
        typeof(ProviderConfig).GetProperty("ListModels")!.PropertyType
            .Should().Be(typeof(bool));

        var config = new ProviderConfig();
        config.Capabilities.Should().BeNull("未写 capabilities 时必须为 null，不能伪造为 Chat");
        config.ListModels.Should().BeFalse();
    }

    [Fact]
    public void ProviderConfig_Capabilities_JsonNameShouldBeCamelCaseCapabilities()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new ProviderConfig { Capabilities = ["chat", "speechSynthesis"], ListModels = true });

        json.Should().Contain("\"capabilities\"");
        json.Should().Contain("\"listModels\"");
        json.Should().NotContain("default_model");
    }

    [Fact]
    public void ProviderTypes_Normalize_ShouldHandleTypeSafeAndResponses()
    {
        ProviderTypes.Normalize("TypeSafe").Should().Be("typesafe");
        ProviderTypes.Normalize("OpenAI-Responses").Should().Be("openai-responses");
    }

    [Fact]
    public void ProviderTypes_ShouldExposeNewProtocolConstants()
    {
        ProviderTypes.TypeSafe.Should().Be("typesafe");
        ProviderTypes.OpenAiResponses.Should().Be("openai-responses");
        ProviderTypes.OpenAi.Should().Be("openai");
        ProviderTypes.Anthropic.Should().Be("anthropic");
    }

    [Fact]
    public void ProviderTypes_Normalize_ShouldKeepLegacyBehaviour()
    {
        ProviderTypes.Normalize(null).Should().BeEmpty();
        ProviderTypes.Normalize("  ").Should().BeEmpty();
        ProviderTypes.Normalize("OpenAI").Should().Be("openai");
        ProviderTypes.Normalize("anthropic").Should().Be("anthropic");
        ProviderTypes.Normalize("  TypeSafe  ").Should().Be("typesafe");
        ProviderTypes.Normalize("SomeOpaqueVendor").Should().Be("SomeOpaqueVendor");
    }

    // ---------- ILlmProvider ----------

    [Fact]
    public void ILlmProvider_ShouldTakeCapabilityOnGetClientAndTestConnection()
    {
        typeof(ILlmProvider).GetMethod("GetClient")!.GetParameters()[0].ParameterType
            .Should().Be(typeof(LlmCapabilityKind));
        typeof(ILlmProvider).GetMethod("TestConnectionAsync")!.GetParameters()[1].ParameterType
            .Should().Be(typeof(LlmCapabilityKind));
    }

    [Fact]
    public void ILlmProvider_GetClient_ShouldReturnNullableCapabilityClient_AndNoParameterlessOverload()
    {
        var overloads = typeof(ILlmProvider).GetMethods().Where(m => m.Name == "GetClient").ToArray();

        overloads.Should().ContainSingle("只允许按能力取客户端，旧的无参 GetClient() 必须消失");
        overloads[0].ReturnType.Should().Be(typeof(ILlmCapabilityClient));
        overloads[0].GetParameters().Should().ContainSingle().Which.ParameterType
            .Should().Be(typeof(LlmCapabilityKind));
    }

    [Fact]
    public void ILlmProvider_ShouldExposeProtocolAndCapabilities()
    {
        typeof(ILlmProvider).GetProperty("Protocol")!.PropertyType.Should().Be(typeof(string));
        typeof(ILlmProvider).GetProperty("Capabilities")!.PropertyType
            .Should().Be(typeof(LlmCapabilityKind));
        typeof(ILlmProvider).GetProperty("MaxRetries")!.PropertyType.Should().Be(typeof(int));
        typeof(ILlmProvider).GetProperty("Id")!.PropertyType.Should().Be(typeof(string));
        typeof(ILlmProvider).GetProperty("Name")!.PropertyType.Should().Be(typeof(string));
    }

    [Fact]
    public void ILlmProvider_TestConnectionAsync_ShouldHaveThreeParametersInSpecifiedOrder()
    {
        var method = typeof(ILlmProvider).GetMethod("TestConnectionAsync")!;
        var parameters = method.GetParameters();

        method.ReturnType.Should().Be(typeof(Task<bool>));
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(LlmCapabilityKind));
        parameters[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void ILlmProvider_GetModelsAsync_ShouldStillReturnModelConfigs()
        => typeof(ILlmProvider).GetMethod("GetModelsAsync")!.ReturnType
            .Should().Be(typeof(Task<IReadOnlyList<ModelConfig>>));

    // ---------- ILlmClientFactory ----------

    [Fact]
    public void ILlmClientFactory_ShouldCreateByProtocolAndCapability()
    {
        var create = typeof(ILlmClientFactory).GetMethod("Create")!;
        var parameters = create.GetParameters();

        parameters.Should().HaveCount(2);
        parameters[0].ParameterType.Should().Be(typeof(ProviderConfig));
        parameters[1].ParameterType.Should().Be(typeof(LlmCapabilityKind));
        create.ReturnType.Should().Be(typeof(ILlmCapabilityClient));
    }

    [Fact]
    public void ILlmClientFactory_ShouldSpeakProtocolsNotTypes()
    {
        var factory = typeof(ILlmClientFactory);

        factory.GetProperty("SupportedProtocols")!.PropertyType.Should().Be(typeof(IReadOnlySet<string>));
        factory.GetMethod("SupportsProtocol")!.GetParameters()[0].ParameterType.Should().Be(typeof(string));
        factory.GetMethod("SupportsType").Should().BeNull("旧的 SupportsType 已改名");
        factory.GetProperty("SupportedTypes").Should().BeNull("旧的 SupportedTypes 已改名");
    }

    [Fact]
    public void LlmClientFactoryResolver_Require_ShouldResolveByProtocol()
    {
        var factory = new StubFactory();
        var factories = new ILlmClientFactory[] { factory };

        LlmClientFactoryResolver.Require(factories, "TypeSafe").Should().BeSameAs(factory);

        var act = () => LlmClientFactoryResolver.Require(factories, "openai");
        act.Should().Throw<NotSupportedException>();
    }

    // ---------- 装饰器 ----------

    [Fact]
    public void ILlmClientDecorator_ShouldWrapCapabilityClientsNotChatOnly()
    {
        var wrap = typeof(ILlmClientDecorator).GetMethod("Wrap")!;
        var parameters = wrap.GetParameters();

        parameters.Should().HaveCount(2);
        parameters[0].ParameterType.Should().Be(typeof(ILlmCapabilityClient));
        parameters[1].ParameterType.Should().Be(typeof(ProviderConfig));
        wrap.ReturnType.Should().Be(typeof(ILlmCapabilityClient));
        typeof(ILlmClientDecorator).GetProperty("Order")!.PropertyType.Should().Be(typeof(int));
    }

    [Fact]
    public void ILlmClientDecoratorRegistry_Apply_ShouldCarryConfigAndReturnClient()
    {
        var apply = typeof(ILlmClientDecoratorRegistry).GetMethod("Apply")!;
        var parameters = apply.GetParameters();

        parameters.Should().HaveCount(2);
        parameters[0].ParameterType.Should().Be(typeof(ILlmCapabilityClient));
        parameters[1].ParameterType.Should().Be(typeof(ProviderConfig));
        apply.ReturnType.Should().Be(typeof(ILlmCapabilityClient));
    }

    // ---------- ProviderInfo ----------

    [Fact]
    public void ProviderInfo_ShouldCarryProtocolAndCapabilitiesForDiagnostics()
    {
        var info = new ProviderInfo
        {
            Id = "openai",
            Protocol = "openai",
            Capabilities = LlmCapabilityKind.Chat | LlmCapabilityKind.Embedding,
            MaxRetries = 3,
        };

        info.Id.Should().Be("openai");
        info.Protocol.Should().Be("openai");
        info.Capabilities.Should().HaveFlag(LlmCapabilityKind.Embedding);

        typeof(ProviderInfo).GetProperty("Source")!.PropertyType.Should().Be(typeof(ProviderSource));
        typeof(ProviderInfo).GetProperty("OwnerExtensionId")!.PropertyType.Should().Be(typeof(string));
    }

    // ---------- Router ----------

    [Fact]
    public void ILlmRouter_Resolve_ShouldRequireExplicitModelId_AndHaveNoParameterlessOverload()
    {
        var router = typeof(ILlmRouter);

        router.GetMethods().Select(m => m.Name).Should().BeEquivalentTo(new[] { "Resolve", "TryResolve" });

        var resolve = router.GetMethod("Resolve")!;
        resolve.IsGenericMethodDefinition.Should().BeTrue();
        resolve.GetParameters().Should().ContainSingle().Which.ParameterType.Should().Be(typeof(string));
        resolve.GetGenericArguments()[0].GetGenericParameterConstraints()
            .Should().Contain(typeof(ILlmCapabilityClient));
    }

    [Fact]
    public void ILlmRouter_TryResolve_ShouldGiveReasonWithoutThrowing()
    {
        var tryResolve = typeof(ILlmRouter).GetMethod("TryResolve")!;
        var parameters = tryResolve.GetParameters();

        tryResolve.ReturnType.Should().Be(typeof(bool));
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].IsOut.Should().BeTrue();
        parameters[2].IsOut.Should().BeTrue();
        parameters[2].ParameterType.Should().Be(typeof(string).MakeByRefType());
    }

    [Fact]
    public void LlmCapabilityMismatchException_ShouldCarryDiagnostics()
    {
        var ex = new LlmCapabilityMismatchException(
            "openai/gpt-4o", LlmCapabilityKind.Embedding, "模型声明类型为 [Text]，不含 Embedding");

        ex.Should().BeAssignableTo<Exception>();
        ex.ModelId.Should().Be("openai/gpt-4o");
        ex.RequestedCapability.Should().Be(LlmCapabilityKind.Embedding);
        ex.Reason.Should().Be("模型声明类型为 [Text]，不含 Embedding");

        // 消息必须让运维一眼看出是哪一层校验挂了：modelId + 请求能力（点分投影）+ 原因
        ex.Message.Should().Contain("openai/gpt-4o");
        ex.Message.Should().Contain("embedding");
        ex.Message.Should().Contain("模型声明类型");
    }

    // ---------- Invoker 与载荷适配器 ----------

    [Fact]
    public void ILlmInvoker_ShouldExposeInvokeAndStream()
    {
        var invoker = typeof(ILlmInvoker);

        invoker.GetMethods().Select(m => m.Name).Should()
            .BeEquivalentTo(new[] { nameof(ILlmInvoker.InvokeAsync), nameof(ILlmInvoker.StreamAsync) });

        var invoke = invoker.GetMethod("InvokeAsync")!;
        invoke.IsGenericMethodDefinition.Should().BeTrue();
        invoke.ReturnType.IsGenericType.Should().BeTrue();
        invoke.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(Task<>));
        invoke.ReturnType.GenericTypeArguments[0].Name.Should().Be("TResponse");

        var stream = invoker.GetMethod("StreamAsync")!;
        stream.IsGenericMethodDefinition.Should().BeTrue();
        stream.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(IAsyncEnumerable<>));
        stream.ReturnType.GenericTypeArguments[0].Name.Should().Be("TChunk");
    }

    [Fact]
    public void ILlmInvoker_GenericClient_ShouldBeConstrainedToCapabilityClient()
    {
        foreach (var method in new[] { "InvokeAsync", "StreamAsync" })
        {
            var client = typeof(ILlmInvoker).GetMethod(method)!
                .GetGenericArguments().Single(a => a.Name == "TClient");

            client.GetGenericParameterConstraints().Should().Contain(typeof(ILlmCapabilityClient));
            (client.GenericParameterAttributes & GenericParameterAttributes.ReferenceTypeConstraint)
                .Should().Be(GenericParameterAttributes.ReferenceTypeConstraint, "class 约束");
        }
    }

    [Fact]
    public void LlmInvocationOptions_EmitHooks_ShouldDefaultTrue()
    {
        var options = new LlmInvocationOptions();

        options.EmitHooks.Should().BeTrue("旁路调用才显式置 false");
        options.SessionId.Should().BeNull();
        options.ParentSessionId.Should().BeNull();
        new LlmInvocationOptions { EmitHooks = false }.EmitHooks.Should().BeFalse();
    }

    [Fact]
    public void ILlmHookPayloadAdapter_ShouldDeclareSingleCapabilityAndBuildResultDictionary()
    {
        var adapter = typeof(ILlmHookPayloadAdapter);

        adapter.GetProperty("Capability")!.PropertyType.Should().Be(typeof(LlmCapabilityKind));
        adapter.GetMethod("Build")!.ReturnType.Should().Be(typeof(IReadOnlyDictionary<string, object?>));
        adapter.GetMethod("Build")!.GetParameters()[0].ParameterType.Should().Be(typeof(LlmPayloadContext));
    }

    [Fact]
    public void LlmPayloadContext_ShouldCarryResponseUsageAndDuration()
    {
        var context = new LlmPayloadContext
        {
            Capability = LlmCapabilityKind.Chat,
            ProviderId = "openai",
            ModelId = "openai/gpt-4o",
            Response = new ChatResponse(),
            Usage = new TokenUsage { InputTokens = 3 },
            Duration = TimeSpan.FromMilliseconds(120),
            SessionId = "s-1",
            Streaming = true,
        };

        context.Capability.Should().Be(LlmCapabilityKind.Chat);
        context.Usage!.InputTokens.Should().Be(3);
        context.Duration.Should().Be(TimeSpan.FromMilliseconds(120));
        context.Streaming.Should().BeTrue();
    }

    [Fact]
    public void ILlmHookPayloadAdapterRegistry_ShouldRegisterUnregisterAndResolve()
    {
        var registry = typeof(ILlmHookPayloadAdapterRegistry);
        var resolve = registry.GetMethod("Resolve")!;

        registry.GetMethod("Register")!.GetParameters()[0].ParameterType
            .Should().Be(typeof(ILlmHookPayloadAdapter));
        registry.GetMethod("Unregister")!.ReturnType.Should().Be(typeof(bool));
        resolve.GetParameters()[0].ParameterType.Should().Be(typeof(LlmCapabilityKind));
        resolve.ReturnType.Should().Be(typeof(ILlmHookPayloadAdapter));
    }

    private sealed class StubFactory : ILlmClientFactory
    {
        public IReadOnlySet<string> SupportedProtocols { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ProviderTypes.TypeSafe };

        public bool SupportsProtocol(string protocol) =>
            !string.IsNullOrWhiteSpace(protocol) && SupportedProtocols.Contains(protocol.Trim());

        public ILlmCapabilityClient? Create(ProviderConfig config, LlmCapabilityKind capability) => null;
    }
}
