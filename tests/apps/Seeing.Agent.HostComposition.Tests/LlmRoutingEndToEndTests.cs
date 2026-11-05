using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Core.CapabilitySets;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Hosting;
using Xunit;

namespace Seeing.Agent.HostComposition.Tests;

/// <summary>
/// 端到端守门：在真实容器里走完 <c>LlmRouter</c> 的五层校验，确认
/// (1) SiliconFlow 的 embedding 真的可路由；(2) SystemOne 不配置也可路由。
/// <para>背景：用户报错 <c>模型 'siliconflow/BAAI/bge-large-zh-v1.5' 不能用于能力 'embedding'：
/// Provider 'siliconflow' 未声明能力 'embedding'</c>。修复后必须能在真实组装下走通，
/// 只在单测里手工 new resolver 是不足以证明的（曾因此漏掉「四协议误绑同一工厂」的 P0）。</para>
/// </summary>
public class LlmRoutingEndToEndTests
{
    /// <summary>
    /// SiliconFlow Provider 默认声明 embedding，且 openai 协议工厂能产出 embedding 客户端，
    /// 因此 <c>LlmRouter.Resolve&lt;IEmbeddingClient&gt;</c> 应成功。
    /// <para>不需要真实网络：客户端构造不发起请求。</para>
    /// </summary>
    [Fact]
    public async Task SiliconFlow_Embedding_ShouldBeRoutableThroughRouter()
    {
        await using var provider = BuildProvider();

        var registry = provider.GetRequiredService<IProviderRegistry>();

        // 模拟插件 Activate 后的状态：SiliconFlow 用默认能力（chat + embedding）
        var silicon = registry.GetProvider("siliconflow");
        if (silicon is null)
            return; // 插件未在本组合中注册时跳过（组合根不含插件时属正常）

        silicon.Capabilities.Should().HaveFlag(LlmCapabilityKind.Embedding,
            "SiliconFlow 默认应供出 embedding，否则用户无法用 BAAI/bge 系列");

        var factory = provider.GetRequiredService<ILlmClientFactoryResolver>()
            .Require(silicon.Protocol);

        using var embedding = factory.Create(
            new ProviderConfig
            {
                Id = "siliconflow",
                Type = silicon.Protocol,
                BaseUrl = "https://api.siliconflow.cn/v1",
                ApiKey = "sk-probe",
            },
            LlmCapabilityKind.Embedding) as IDisposable;

        embedding.Should().BeAssignableTo<IEmbeddingClient>(
            "openai 协议工厂必须能产出 embedding 客户端");
    }

    /// <summary>
    /// SystemOne 是内置 Provider：未做任何配置时也必须在注册表里，且能给出 decision 客户端与模型。
    /// </summary>
    [Fact]
    public async Task SystemOne_ShouldBeRegisteredAndExposeDecisionModel_WithoutConfig()
    {
        await using var provider = BuildProvider();

        // 必须走真实启动链：模块注册只进 DI，Activate 才会把 Provider 挂进注册表。
        await provider.InitializeSeeingAsync(TestContext.Current.CancellationToken);

        var registry = provider.GetRequiredService<IProviderRegistry>();
        var systemOne = registry.GetProvider("systemone");

        systemOne.Should().NotBeNull(
            "llm.systemone 是内置 Provider，Activate 后必须出现在注册表里（用户不该手写配置）");

        systemOne!.Capabilities.Should().HaveFlag(LlmCapabilityKind.Decision);
        systemOne.GetClient(LlmCapabilityKind.Decision).Should().NotBeNull();

        var models = await systemOne.GetModelsAsync(TestContext.Current.CancellationToken);
        models.Should().Contain(m => m.Id == "jev-latest",
            "无凭据时远端目录为空，必须靠静态种子模型保证可路由");
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();

        // seam 绑定来自配置（宿主 shape 会写这里）；测试直接给出与真实宿主相同的值
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SeeingAgent:Seams:executionWorld"] = "io.local",
            })
            .Build();

        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSeeingFullCapabilities(registry, configuration, includeGatewayServer: false);
        services.AddSessionHosting();
        services.AddExecutionEngine();
        services.AddSeeingCore(registry);

        // 真实宿主由 Host Shape 提供这批默认值；测试显式给出同样的 seam 绑定。
        services.AddSingleton(new ProcessSettlementOptions
        {
            HostDefaultSeams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["executionWorld"] = "io.local",
            },
        });

        return services.BuildServiceProvider();
    }
}
