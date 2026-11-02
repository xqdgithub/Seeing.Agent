using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Integration;

namespace Seeing.Agent.Reflection;

/// <summary>
/// 复盘能力模块 — id=<c>reflection</c>。
/// <para>
/// 职责：启停门控（<see cref="ReflectionModuleActivity"/>）+ 注册隐藏的 <c>reflection</c> agent。
/// </para>
/// <para>
/// <b>本模块不挂载任何工具</b>：复盘 agent 是纯只读的，技能写入由
/// <see cref="Core.ReflectionSuggestionsWriter"/> 在执行器内完成。
/// 给 agent 一个写工具会带来三重问题：与「输出 JSON 建议清单」的提示词互斥、
/// 写入结果对执行器不可见（journal 的 <c>CreatedSkills</c> 只能是空）、
/// 以及让只读 agent 需要写文件权限。
/// </para>
/// </summary>
public sealed class ReflectionModule : ISeeingModule
{
    /// <summary>模块 id。</summary>
    public const string ModuleId = "reflection";

    private readonly ReflectionModuleActivity? _activity;
    private readonly ILogger<ReflectionModule>? _logger;
    private readonly IAgentStore? _agentStore;
    private readonly IOptionsMonitor<ReflectionOptions>? _options;

    /// <summary>无依赖实例仅用于 <see cref="ConfigureServices"/>。</summary>
    public ReflectionModule()
    {
    }

    /// <summary>DI 解析用（可选依赖缺失时对应挂载步骤跳过）。</summary>
    public ReflectionModule(
        ReflectionModuleActivity activity,
        ILogger<ReflectionModule>? logger = null,
        IAgentStore? agentStore = null,
        IOptionsMonitor<ReflectionOptions>? options = null)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger;
        _agentStore = agentStore;
        _options = options;
    }

    /// <inheritdoc />
    public string Id => ModuleId;

    /// <inheritdoc />
    /// <remarks>本模块不提供工具——复盘 agent 只读，写入在宿主侧执行器内完成。</remarks>
    public IReadOnlyList<string> ProvidedTools { get; } = Array.Empty<string>();

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
        else
        {
            // 缺 Agent 时后台仍会建子会话，然后因 "Agent not found" 失败并留下孤儿会话，
            // 这是最难排查的一类静默故障，必须在激活时就说清楚。
            _logger?.LogWarning(
                "复盘 Agent 未注册（agent={AgentName}）：AgentStore 或配置缺失，后台复盘将无法执行",
                ReflectionAgentDefinition.AgentName);
        }

        _logger?.LogInformation("复盘模块已激活（id={ModuleId}）", ModuleId);
    }

    /// <inheritdoc />
    public Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null)
            return Task.CompletedTask;

        _activity.IsActive = false;
        _logger?.LogInformation("复盘模块已停用（id={ModuleId}）", ModuleId);
        return Task.CompletedTask;
    }
}
