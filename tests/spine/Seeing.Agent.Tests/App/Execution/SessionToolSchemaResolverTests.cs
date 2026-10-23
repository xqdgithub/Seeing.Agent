using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Modules;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Hosting.Execution;
using Seeing.Agent.Tests.Scenarios;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.App.Execution;

/// <summary>
/// 会话级 schema 解析端口（schema 单点纪律共享出口）：
/// Scheduler 直连执行与主执行链共用 <see cref="SessionToolSchemaResolver"/> 的结算结果。
/// </summary>
public class SessionToolSchemaResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithSessionAndCatalog_ShouldReturnSettledSchemas()
    {
        var session = SessionData.Create(scenario: "code");
        var services = await BuildServicesAsync(
            sessionManager: MockSessionManager(session),
            withCatalog: true);

        var resolver = CreateResolver(services);
        var agent = new AgentDefinition { Name = "build", Runtime = AgentRuntime.Native };

        var resolution = await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.InlineSchemas.Select(s => s.Function!.Name)
            .Should().Contain("git_status")
            .And.Contain("read")
            .And.Contain("current_time");
        // 会话 scenario=code：memory 模块未启用 → memory_search 不入 schema
        resolution.InlineSchemas.Select(s => s.Function!.Name)
            .Should().NotContain("memory_search");
        resolution.SectionIds.Should().Contain("tools");
    }

    [Fact]
    public async Task ResolveAsync_AgentDeniedTools_ShouldFilterSchema()
    {
        var session = SessionData.Create(scenario: "code");
        var services = await BuildServicesAsync(
            sessionManager: MockSessionManager(session),
            withCatalog: true);

        var resolver = CreateResolver(services);
        var agent = new AgentDefinition
        {
            Name = "build",
            Runtime = AgentRuntime.Native,
            DeniedTools = ["git_status"]
        };

        var resolution = await resolver.ResolveAsync(session.Id, agent, TestContext.Current.CancellationToken);

        resolution.InlineSchemas.Select(s => s.Function!.Name)
            .Should().NotContain("git_status")
            .And.Contain("read");
    }

    [Fact]
    public async Task ResolveAsync_WithoutModuleCatalog_ShouldReturnEmpty()
    {
        var session = SessionData.Create(scenario: "code");
        var services = await BuildServicesAsync(
            sessionManager: MockSessionManager(session),
            withCatalog: false);

        var resolver = CreateResolver(services);

        var resolution = await resolver.ResolveAsync(session.Id, new AgentDefinition { Name = "build" });

        resolution.InlineSchemas.Should().BeEmpty();
        resolution.SectionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_SessionMissing_ShouldReturnEmptyWithoutCreatingSession()
    {
        // 端口只读：会话不存在时返回空视图，绝不新建会话。
        var session = SessionData.Create(scenario: "code");
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.TryGetOrLoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionData?)null);

        var services = await BuildServicesAsync(sessionManager: sessionManager, withCatalog: true);
        var resolver = CreateResolver(services);

        var resolution = await resolver.ResolveAsync(session.Id, new AgentDefinition { Name = "build" });

        resolution.InlineSchemas.Should().BeEmpty();
        sessionManager.Verify(
            m => m.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    private static Mock<ISessionManager> MockSessionManager(SessionData session)
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager.Setup(m => m.TryGetOrLoadAsync(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        return sessionManager;
    }

    private static SessionToolSchemaResolver CreateResolver(IServiceProvider services)
    {
        var optionsMonitor =
            Mock.Of<IOptionsMonitor<SeeingAgentOptions>>(m => m.CurrentValue == new SeeingAgentOptions());
        return new SessionToolSchemaResolver(
            services,
            optionsMonitor,
            new Seeing.Agent.Hosting.Execution.ToolExposure.ToolExposureConfigResolver(optionsMonitor),
            new Seeing.TokenEstimation.CharBasedTokenCounter(),
            NullLogger<SessionToolSchemaResolver>.Instance);
    }

    private static async Task<IServiceProvider> BuildServicesAsync(Mock<ISessionManager> sessionManager, bool withCatalog)
    {
        var toolManager = new Seeing.Agent.Core.Tools.ToolManager(
            NullLogger<Seeing.Agent.Core.Tools.ToolManager>.Instance,
            new HookManager(NullLogger<HookManager>.Instance));
        await toolManager.RegisterToolAsync(new NamedTool("git_status"));
        await toolManager.RegisterToolAsync(new NamedTool("memory_search"));
        await toolManager.RegisterToolAsync(new NamedTool("read"));
        await toolManager.RegisterToolAsync(new NamedTool("current_time"));

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(toolManager);
        services.AddSingleton<IToolManager>(toolManager);
        if (withCatalog)
        {
            var catalog = new ModuleCatalog();
            catalog.ReplaceAvailable(
            [
                new ModuleDescriptor("git", ["git_status"], [], []),
                new ModuleDescriptor("memory", ["memory_search"], [], []),
                new ModuleDescriptor("filesystem", ["read"], [], []),
                new ModuleDescriptor("basic", ["current_time"], [], [])
            ]);
            catalog.ReplaceEnabled(["git", "memory", "filesystem", "basic"]);
            services.AddSingleton<IModuleCatalog>(catalog);
            services.AddSingleton<IScenarioCatalog>(new DelegatingScenarioCatalog(NarrowingScenarios.Resolve));
            services.AddSingleton(new ProcessSettlementOptions { HostDefaultScenario = "full" });
            services.AddSingleton<IPromptSectionContributor>(new FakeSectionContributor());
        }
        return services.BuildServiceProvider();
    }

    private sealed class NamedTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => Id;
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public System.Text.Json.JsonElement ParametersSchema =>
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });

        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement arguments, ToolContext context) =>
            Task.FromResult(ToolResult.Succeeded("ok"));
    }

    private sealed class FakeSectionContributor : IPromptSectionContributor
    {
        public string SectionName => PromptSectionNames.Tools;
        public string Heading => "## Tools";
        public PromptSectionPlacement Placement => PromptSectionPlacement.AtEnd;
        public int Order => 0;
        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("tools");
    }
}
