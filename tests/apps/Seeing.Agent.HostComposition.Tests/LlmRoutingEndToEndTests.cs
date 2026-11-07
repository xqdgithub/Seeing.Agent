using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Llm;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Llm.TypeSafe;
using Seeing.Agent.Llm.TypeSafe.Clients;

namespace Seeing.Agent.HostComposition.Tests;

/// <summary>
/// 端到端守门：真实容器里走完 <see cref="ILlmRouter"/> 的五层校验。
/// <para><b>为什么必须真正调用 Router</b>：早先版本只断言了
/// <c>registry.GetProvider(...)</c> 与 <c>provider.GetModelsAsync(...)</c>，
/// 结果「Provider 有模型、但模型进不了全局目录」的缺陷完全逃过测试——
/// 而 Router 第 1 层查的正是全局目录。</para>
/// <para><b>为什么不用 <c>InitializeSeeingAsync</c></b>：它会拉起 Quartz 等进程级静态
/// 组件，其 <c>LogProvider</c> 捕获第一个容器的 <c>LoggerFactory</c>；
/// 同进程内第二个容器解析时必然撞 <see cref="ObjectDisposedException"/>。
/// 这里改为直接调用模块的 <c>ActivateAsync</c>——那正是启动链中负责注册 Provider 的一步，
/// 且不引入无关的静态状态。</para>
/// </summary>
public class LlmRoutingEndToEndTests
{
    /// <summary>
    /// TypeSafe 端点插件：不做任何配置也必须能被 Router 解析出 decision 客户端。
    /// <para>覆盖三个环节：插件 Activate 注册 Provider → 聚合进全局模型目录 → Router 五层校验。</para>
    /// <para>Provider 由插件 <c>provider.typesafe-api</c> 提供，不再由能力包 <c>llm.typesafe</c> 内置。</para>
    /// </summary>
    [Fact]
    public async Task TypeSafeApi_ShouldBeRoutableThroughRouter_WithoutAnyConfig()
    {
        await using var provider = await BuildProviderAsync();

        var registry = provider.GetRequiredService<IProviderRegistry>();
        registry.GetProvider("typesafe-api").Should().NotBeNull(
            "provider.typesafe-api 是插件 Provider，Activate 后必须出现在注册表里");

        // 目录刷新是后台单线程队列，注册与可路由之间有收敛窗口。
        // 这里显式 await 一次该 Provider 的刷新完成，而不是轮询固定短窗口：
        // 生产侧单个 Provider 的 GetModelsAsync 上限是 15s、刷新完成等待是 45s，
        // 测试若用更短的窗口，慢网络下会在刷新尚未结束时误判失败。
        var catalog = provider.GetRequiredService<IModelConfigManager>();
        await catalog.RefreshCatalogAsync("typesafe-api", TestContext.Current.CancellationToken);

        catalog.GetCatalogModels().Should().ContainKey("typesafe-api/jev-latest",
            "种子模型来自插件静态表，不依赖网络；刷新完成后必须进全局目录");

        var resolved = provider.GetRequiredService<ILlmRouter>()
            .TryResolve<IDecisionClient>("typesafe-api/jev-latest", out var client, out var reason);

        resolved.Should().BeTrue($"Router 应能解析插件提供的 decision 模型；失败原因: {reason}");
        client.Should().NotBeNull();
        reason.Should().BeNull();
    }

    /// <summary>
    /// SiliconFlow 默认供出 chat + embedding，openai 协议工厂能产出 embedding 客户端，
    /// 因此 Provider 层的 embedding 取用必须成功。
    /// <para>不需要真实网络：客户端构造不发起请求。</para>
    /// </summary>
    [Fact]
    public async Task SiliconFlow_Embedding_ShouldBeAvailable()
    {
        await using var provider = await BuildProviderAsync();

        var silicon = provider.GetRequiredService<IProviderRegistry>().GetProvider("siliconflow");
        silicon.Should().NotBeNull("插件模块在 Full 组合里默认启用");

        silicon!.Capabilities.Should().HaveFlag(LlmCapabilityKind.Embedding,
            "SiliconFlow 默认应供出 embedding，否则用户无法用 BAAI/bge 系列");

        var factory = provider.GetRequiredService<ILlmClientFactoryResolver>()
            .Require(silicon.Protocol);
        using var client = factory.Create(
            new ProviderConfig
            {
                Id = "siliconflow",
                Type = silicon.Protocol,
                BaseUrl = "https://api.siliconflow.cn/v1",
                ApiKey = "sk-probe",
            },
            LlmCapabilityKind.Embedding) as IDisposable;

        client.Should().BeAssignableTo<IEmbeddingClient>();
    }

    /// <summary>每个协议都必须解析到自己的工厂（防止「按接口取」把四个协议绑到同一个）。</summary>
    [Fact]
    public async Task Protocols_ShouldResolveToTheirOwnFactory()
    {
        await using var provider = await BuildProviderAsync();

        var resolver = provider.GetRequiredService<ILlmClientFactoryResolver>();

        resolver.Protocols.Should().BeEquivalentTo(
            ProviderTypes.OpenAi,
            ProviderTypes.OpenAiResponses,
            ProviderTypes.Anthropic,
            ProviderTypes.TypeSafe);

        resolver.Require(ProviderTypes.OpenAi).Should().BeOfType<OpenAiLlmClientFactory>();
        resolver.Require(ProviderTypes.OpenAiResponses).Should().BeOfType<OpenAiLlmClientFactory>();
        resolver.Require(ProviderTypes.Anthropic).Should().BeOfType<AnthropicLlmClientFactory>();
        resolver.Require(ProviderTypes.TypeSafe).Should().BeOfType<TypeSafeClientFactory>();

        resolver.Require(ProviderTypes.OpenAi)
            .Should().BeSameAs(resolver.Require(ProviderTypes.OpenAiResponses),
                "同一工厂服务两个协议时必须共享实例");
    }

    /// <summary>OpenAI 协议必须能产出 chat 与 embedding 客户端（误绑时两者都是 null）。</summary>
    [Fact]
    public async Task OpenAiProtocol_ShouldProduceChatAndEmbeddingClients()
    {
        await using var provider = await BuildProviderAsync();

        var factory = provider.GetRequiredService<ILlmClientFactoryResolver>()
            .Require(ProviderTypes.OpenAi);

        var config = new ProviderConfig
        {
            Id = "probe",
            Type = ProviderTypes.OpenAi,
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "sk-probe",
        };

        using var chat = factory.Create(config, LlmCapabilityKind.Chat) as IDisposable;
        using var embedding = factory.Create(config, LlmCapabilityKind.Embedding) as IDisposable;

        chat.Should().BeAssignableTo<IChatCompletionClient>("openai 协议必须产出 chat 客户端");
        embedding.Should().BeAssignableTo<IEmbeddingClient>("openai 协议必须产出 embedding 客户端");
    }

    /// <summary>
    /// 组装真实容器并执行模块 Activate（启动链中注册 Provider 的那一步）。
    /// <para>只启用与 LLM 相关的模块，避免拉起 Quartz / Gateway 等进程级静态组件。</para>
    /// </summary>
    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var workspace = new TempWorkspaceProvider();
        var services = new ServiceCollection();
        var registry = new ConfigSectionRegistry();
        var configuration = new ConfigurationBuilder().Build();

        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSingleton<IConfiguration>(configuration);

        services.AddSeeingCore(registry);

        // 依赖注入是「后注册者覆盖」，因此隔离 Provider 必须放在 AddSeeingCore **之后**，
        // 否则会被 Core 自己的 WorkspaceProvider 顶掉，测试就会写真实 ~/.seeing。
        services.AddSingleton<IWorkspaceProvider>(workspace);

        // 与 Full 组合一致的 LLM 相关模块（不引入 scheduler/gateway）
        var modules = new ISeeingModule[]
        {
            new OpenAiLlmModule(),
            new AnthropicLlmModule(),
            new TypeSafeModule(),
            new Seeing.Provider.TypeSafeApi.TypeSafeApiLlmModule(),
            new Seeing.Provider.DeepSeek.DeepSeekLlmModule(),
            new Seeing.Provider.MiMo.MiMoLlmModule(),
            new Seeing.Provider.OpenCodeZen.OpenCodeZenLlmModule(),
            new Seeing.Provider.SiliconFlow.SiliconFlowLlmModule(),
        };

        foreach (var module in modules)
        {
            module.ConfigureServices(services);
            services.AddSingleton(module);
        }

        var provider = services.BuildServiceProvider();
        foreach (var module in modules)
            await module.ActivateAsync(provider, TestContext.Current.CancellationToken);

        return provider;
    }

    /// <summary>测试独占的用户级 / 项目级目录，避免污染真实 <c>~/.seeing</c>。</summary>
    private sealed class TempWorkspaceProvider : IWorkspaceProvider
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "seeing-llm-e2e-" + Guid.NewGuid().ToString("N"));

        public TempWorkspaceProvider() => Directory.CreateDirectory(_root);

        public string WorkspaceRoot => _root;

        public string UserSeeingDirectory => Path.Combine(_root, "user", ".seeing");

        public string ProjectSeeingDirectory => Path.Combine(_root, ".seeing");

        public string GetSeeingDirectory(ConfigLevel level) => level switch
        {
            ConfigLevel.User => UserSeeingDirectory,
            ConfigLevel.Project => ProjectSeeingDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

        public string StartupDirectory => _root;

        public bool IsResolved => true;

        public string? ResolvedRoot => _root;

        public WorkspaceResolutionSource ResolutionSource => WorkspaceResolutionSource.StartupDirectory;

        public string? GlobalWorkspaceRoot => null;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged
        {
            add { }
            remove { }
        }

        public void SetInitialWorkspace(string workspaceRoot, WorkspaceResolutionSource source) { }
        public void SetWorkspaceRoot(string workspaceRoot) { }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
