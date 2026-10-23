using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Comms.Extensions;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Files;
using Seeing.Agent.Gateway;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.ModelCapabilities;
using Seeing.Agent.Llm.ModelCatalog.Builtin;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Mcp;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;
using Seeing.Agent.Skills;
using Seeing.Agent.SystemOne.Extensions;
using Seeing.Agent.TokenBudget.Extensions;
using Seeing.Agent.Tools.Basic;
using Seeing.Agent.Tools.ChannelMessaging;
using Seeing.Agent.Tools.Editor;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Agent.Tools.Git;
using Seeing.Agent.Tools.Question;
using Seeing.Agent.Tools.Session;
using Seeing.Agent.Tools.Shell;
using Seeing.Agent.Tools.SystemOne.Extensions;
using Seeing.Agent.Tools.ToolDiscovery;
using Seeing.Agent.Tools.Web;
using Seeing.Agent.Workspace;
using Seeing.IO.Local;
using Seeing.Provider.DeepSeek;
using Seeing.Provider.MiMo;
using Seeing.Provider.OpenCodeZen;
using Seeing.Provider.SiliconFlow;

namespace Seeing.Agent.HostComposition;

/// <summary>
/// 所有宿主共享的 Full 能力集组合根。
/// <para>
/// 目的：让 WebUI / Tui / CLI-Headless / Gateway.Server 的默认启用模块完全一致，
/// 且等于 <see cref="Seeing.Agent.Core.CapabilitySets.BuiltInCapabilitySets.Full"/>。
/// 新增模块时只需改本文件（以及 BuiltInCapabilitySets.Full）。
/// </para>
/// <para>
/// 该类型位于 samples 层：Hosting 层按架构规范禁止引用具体能力包。
/// </para>
/// </summary>
public static class FullCapabilityComposition
{
    /// <summary>
    /// 注册 Full 能力集的全部模块。
    /// 须在 <c>AddSeeingCore</c> 之前调用。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="registry">配置节注册表。</param>
    /// <param name="configuration">宿主配置（TokenBudget / Gateway 兼容绑定用）。</param>
    /// <param name="includeGatewayServer">
    /// true：注册 Gateway 服务端（HostedService + GatewayPermissionChannel），供 WebUI / CLI / Gateway.Server 使用；
    /// false：只登记 gateway/comms 模块而不监听端口（Tui 无远端入口需求）。
    /// 两种分支都保证模块 id 一致，避免重复登记。
    /// </param>
    public static IServiceCollection AddSeeingFullCapabilities(
        this IServiceCollection services,
        IConfigSectionRegistry registry,
        IConfiguration configuration,
        bool includeGatewayServer = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configuration);

        // Memory / SystemOne 的 Provider 走 IHttpClientFactory（幂等）。
        services.AddHttpClient();

        // ── 执行世界与模型 ──
        services.AddSeeingModule<LocalExecutionWorldModule>(registry);
        services.AddSeeingModule<AgentsBuiltInModule>(registry);
        services.AddSeeingModule<OpenAiLlmModule>(registry);
        services.AddSeeingModule<AnthropicLlmModule>(registry);
        services.AddSeeingModule<ModelCapabilitiesModule>(registry);
        services.AddSeeingModule<BuiltinCatalogModule>(registry);
        services.AddSeeingModule<DeepSeekLlmModule>(registry);
        services.AddSeeingModule<MiMoLlmModule>(registry);
        services.AddSeeingModule<OpenCodeZenLlmModule>(registry);
        services.AddSeeingModule<SiliconFlowLlmModule>(registry);

        // ── 工具 ──
        services.AddSeeingModule<BasicModule>(registry);
        services.AddSeeingModule<FileSystemModule>(registry);
        services.AddSeeingModule<FileWorkspaceModule>(registry);
        services.AddSeeingModule<EditorToolsModule>(registry);
        services.AddSeeingModule<ShellModule>(registry);
        services.AddSeeingModule<GitModule>(registry);
        services.AddSeeingModule<SessionToolsModule>(registry);
        services.AddSeeingModule<ToolDiscoveryModule>(registry);
        services.AddSeeingModule<QuestionToolsModule>(registry);
        services.AddSeeingModule<WebModule>(registry);
        services.AddSeeingModule<ChannelMessagingModule>(registry);
        services.AddSeeingModule<WorkspaceGitExcludeModule>(registry);

        // ── 能力 ──
        services.AddSeeingModule<SkillsModule>(registry);
        services.AddSeeingModule<McpModule>(registry);
        services.AddMemoryServices(registry);
        services.AddSeeingScheduler(registry);
        services.AddSeeingAcp(registry);
        services.AddSystemOne(registry);
        services.AddSystemOneTools(registry);
        services.AddTokenBudgetIntegration(registry, configuration);

        // ── Gateway / Comms ──
        // 两种分支都登记 gateway + comms 模块（保证 Full 一致），区别仅在是否挂真实服务端。
        if (includeGatewayServer)
        {
            services.AddSeeingGatewayServer(registry, configuration);
        }
        else
        {
            services.AddSeeingComms(registry);
            services.AddSingleton<ISeeingModule>(sp =>
                new GatewayModule(sp.GetService<IUiContributionRegistry>()));
        }

        return services;
    }
}
