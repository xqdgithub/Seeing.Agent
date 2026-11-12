using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Modules;
using Seeing.Agent.Abstractions.Ui;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Integration;

namespace Seeing.Agent.Reflection;

/// <summary>
/// 复盘能力模块 — id=<c>reflection</c>。
/// <para>
/// 职责：启停门控（<see cref="ReflectionModuleActivity"/>）+ 注册隐藏的 <c>reflection</c> agent
/// + 提供只读的复盘记录页导航。
/// </para>
/// <para>
/// <b>本模块不挂载任何工具</b>：复盘 agent 是纯只读的，技能写入由
/// <see cref="Core.ReflectionSuggestionsWriter"/> 在执行器内完成。
/// 给 agent 一个写工具会带来三重问题：与「输出 JSON 建议清单」的提示词互斥、
/// 写入结果对执行器不可见（journal 的 <c>CreatedSkills</c> 只能是空）、
/// 以及让只读 agent 需要写文件权限。
/// </para>
/// </summary>
public sealed class ReflectionModule : ISeeingModule, IUiContributor
{
    /// <summary>模块 id。</summary>
    public const string ModuleId = "reflection";

    private readonly ReflectionModuleActivity? _activity;
    private readonly ILogger<ReflectionModule>? _logger;
    private readonly IAgentStore? _agentStore;
    private readonly IOptionsMonitor<ReflectionOptions>? _options;

    /// <summary>
    /// 本模块注册的 agent 定义实例（用于 Deactivate 时按身份注销）。
    /// <para>
    /// 存实例而不是名字：用户可能在 MD 配置里覆盖同名定义，只有「仍是我注册的那个」
    /// 才该被注销，否则会把用户的覆盖删掉。
    /// </para>
    /// </summary>
    private AgentDefinition? _registeredAgent;

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

    /// <summary>
    /// 复盘能力的 UI 声明：记录页 + 设置卡。
    /// <para>
    /// 两条都以 <c>Requires = [reflection]</c> 归属自身模块，因此
    /// <b>模块停用后 UI 自动消失</b>（由 <c>UiVisibility</c> 判定），无需在 Deactivate 里撤销声明。
    /// 但「agent 定义」与「后台服务」不是声明，那两者必须对称处理——
    /// 见 <see cref="ActivateAsync"/> 与 <see cref="DeactivateAsync"/>。
    /// </para>
    /// </summary>
    public IReadOnlyList<UiContribution> DescribeUi() =>
    [
        new PageContribution
        {
            Point = "shell.page",
            Id = "page.reflection",
            Route = "/reflection",
            Title = "复盘记录",
            ViewKey = "page.reflection",
            Requires = [ModuleId],
            Order = 60,
            Nav = new NavPlacement
            {
                Icon = "bulb",
                Group = NavGroups.Workspace,
                GroupIcon = NavGroups.WorkspaceIcon,
                GroupOrder = 20,
            },
        },

        // 配置卡：模块自己声明（不走宿主代声明）。理由是这个能力的每一项配置都只在
        // 「打开/关闭复盘」这个语境里可读，挂在宿主表里会让宿主变成能力配置的堆放点。
        // Requires = 自身 → 模块停用后卡片随页面一起消失，不会留下一个点开就报错的空壳。
        new SettingsCardContribution
        {
            Point = "shell.settings.card",
            Id = "settings.reflection",
            Title = "复盘",
            ViewKey = "settings.reflection",
            Requires = [ModuleId],
            Order = 60,
        },
    ];

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

        // Agent 注册：Activate 注册、Deactivate 对称注销（见 DeactivateAsync）。
        // 保留实例引用是为了「只注销自己注册的那个」——用户可能在 MD 配置里覆盖同名定义，
        // 无条件 Unregister 会误删用户的覆盖（与 AgentsBuiltInModule 的守卫同形）。
        var options = _options?.CurrentValue;
        if (_agentStore is not null && options is not null)
        {
            try
            {
                var definition = ReflectionAgentDefinition.Create(options);
                await _agentStore.RegisterAsync(definition).ConfigureAwait(false);
                _registeredAgent = definition;
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
    public async Task DeactivateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (_activity is null)
            return;

        _activity.IsActive = false;

        // 允许注入 Worker 以便取消在途复盘；缺失时（独立装配）只能等它自然结束。
        services.GetService<Background.ReflectionWorker>()?.CancelInFlight();

        // Agent 对称注销：模块禁用后不应留下一个可被 AgentStore.Get("reflection") 取到的定义。
        // 「注册后永不注销」看似能避免影响在途会话，但结算热重载默认就是在**在途结束后**才 Deactivate，
        // 且在途轮次已在 BuildExecutionContextAsync 里解析过定义——注销不影响它。
        // 守卫 ReferenceEquals：用户可能在 MD 里覆盖同名定义，那就不该由我们删掉。
        if (_agentStore is not null && _registeredAgent is { } registered)
        {
            try
            {
                var current = await _agentStore.GetAsync(registered.Name).ConfigureAwait(false);
                if (ReferenceEquals(current, registered))
                    _agentStore.Unregister(registered.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "复盘 Agent 注销失败（agent={AgentName}）", registered.Name);
            }
            finally
            {
                _registeredAgent = null;
            }
        }

        _logger?.LogInformation("复盘模块已停用（id={ModuleId}）", ModuleId);
    }
}
