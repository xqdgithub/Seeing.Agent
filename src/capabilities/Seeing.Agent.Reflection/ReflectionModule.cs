using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Integration;
using Seeing.Agent.Reflection.Integration.Tools;

namespace Seeing.Agent.Reflection;

/// <summary>
/// 复盘能力模块 — id=<c>reflection</c>。
/// <para>
/// 职责：启停门控（<see cref="ReflectionModuleActivity"/>）+ 挂载复盘用的
/// <c>skill_create</c> 工具与隐藏的 <c>reflection</c> agent。
/// </para>
/// <para>
/// 所有可选依赖都写成可空参数：宿主未装配工具/Agent 能力时模块仍能解析并激活，
/// 只是少挂一部分东西——复盘是可选能力，不能因为它缺失导致整个宿主起不来。
/// </para>
/// </summary>
public sealed class ReflectionModule : ISeeingModule
{
    /// <summary>模块 id。</summary>
    public const string ModuleId = "reflection";

    private readonly ReflectionModuleActivity? _activity;
    private readonly ILogger<ReflectionModule>? _logger;
    private readonly IToolManager? _toolManager;
    private readonly IAgentStore? _agentStore;
    private readonly IOptionsMonitor<ReflectionOptions>? _options;
    private readonly SkillCreateTool? _skillCreateTool;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public ReflectionModule()
    {
    }

    /// <summary>DI 解析用（可选依赖缺失时对应挂载步骤跳过）。</summary>
    public ReflectionModule(
        ReflectionModuleActivity activity,
        ILogger<ReflectionModule>? logger = null,
        IToolManager? toolManager = null,
        IAgentStore? agentStore = null,
        IOptionsMonitor<ReflectionOptions>? options = null,
        SkillCreateTool? skillCreateTool = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
        _toolManager = toolManager;
        _agentStore = agentStore;
        _options = options;
        _skillCreateTool = skillCreateTool;
    }

    /// <inheritdoc />
    public string Id => ModuleId;

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedTools { get; } = [SkillCreateTool.ToolId];

    /// <inheritdoc />
    public IReadOnlyList<string> ProvidedSeams { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> DependsOn { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        // 实际 DI 登记由 AddSeeingReflection 完成。
    }

    /// <inheritdoc />
    public async Task ActivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null)
        {
            throw new InvalidOperationException(
                "ReflectionModule.ActivateAsync 需要由 DI 解析（缺少 ReflectionModuleActivity）。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _activity.IsActive = true;

        // 工具挂载：优先用构造注入的实例，其次从容器取，最后按最小依赖就地构造。
        var tool = _skillCreateTool
            ?? services.GetService<SkillCreateTool>()
            ?? TryBuildTool(services);

        if (_toolManager is not null && tool is not null)
        {
            try
            {
                await _toolManager.RegisterToolAsync(tool, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 激活失败会让整个宿主起不来；复盘是可选能力，这里只记录不抛。
                _logger?.LogWarning(ex, "复盘工具挂载失败（tool={ToolId}）", SkillCreateTool.ToolId);
            }
        }

        // Agent 注册：与内置 agent 一致，注册后不在 Deactivate 注销，避免影响进行中的会话。
        var options = _options?.CurrentValue;
        if (_agentStore is not null && options is not null)
        {
            try
            {
                await _agentStore.RegisterAsync(ReflectionAgentDefinition.Create(options)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "复盘 Agent 注册失败（agent={AgentName}）", ReflectionAgentDefinition.AgentName);
            }
        }

        _logger?.LogInformation("复盘模块已激活（id={ModuleId}）", ModuleId);
    }

    /// <inheritdoc />
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null)
            return;

        _activity.IsActive = false;

        if (_toolManager is not null)
        {
            try
            {
                await _toolManager.UnregisterToolAsync(SkillCreateTool.ToolId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "复盘工具卸载失败（tool={ToolId}）", SkillCreateTool.ToolId);
            }
        }

        _logger?.LogInformation("复盘模块已停用（id={ModuleId}）", ModuleId);
    }

    /// <summary>
    /// 容器未登记 <see cref="SkillCreateTool"/> 时就地构造（写作通道按需从容器取）。
    /// </summary>
    private SkillCreateTool? TryBuildTool(IServiceProvider services)
    {
        if (_options is null)
            return null;

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<SkillCreateTool>()
            ?? NullLogger<SkillCreateTool>.Instance;

        return new SkillCreateTool(
            logger,
            _options,
            services.GetService<ISkillWriter>(),
            services.GetService<ISkillCatalog>());
    }
}
