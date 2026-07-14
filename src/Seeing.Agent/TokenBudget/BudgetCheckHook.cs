using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.TokenBudget;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 预算检查 Hook - 在 LLM 请求前检查并执行待处理的压缩
/// </summary>
public class BudgetCheckHook : IHookHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITokenBudgetConfigResolver _configResolver;

    /// <summary>
    /// Hook 规格 - Chat 开始前
    /// </summary>
    public HookSpec Spec => HookRegistry.ChatBeforeStart;

    /// <summary>
    /// 优先级 - 高优先级，最先执行
    /// </summary>
    public int Priority => 100;

    public BudgetCheckHook(
        IServiceProvider serviceProvider,
        ITokenBudgetConfigResolver configResolver)
    {
        _serviceProvider = serviceProvider;
        _configResolver = configResolver;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        var session = payload.GetInput<SessionData>("session");
        var agent = payload.GetInput<AgentDefinition>("agent");

        if (session == null || agent == null)
        {
            return HookResult.Success;
        }

        // 检查是否需要压缩
        if (!session.PendingCompaction)
        {
            return HookResult.Success;
        }

        // 检查是否启用自动压缩
        var config = _configResolver.Resolve(
            session.BudgetConfig,
            agent.BudgetConfig,
            null);

        if (!config.AutoCompactionEnabled)
        {
            session.PendingCompaction = false;
            return HookResult.Success;
        }

        try
        {
            // Resolve scoped service
            using var scope = _serviceProvider.CreateScope();
            var compressionService = scope.ServiceProvider.GetRequiredService<ICompressionService>();

            // 执行压缩
            var result = await compressionService.CompressAsync(
                session,
                session.BudgetConfig,
                agent.BudgetConfig,
                payload.CancellationToken);

            // 清除标记
            session.PendingCompaction = false;

            // 将压缩结果存入 Mutable，供后续事件使用
            payload.SetMutable("compactionResult", result);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            // 压缩失败不阻止执行
            payload.SetMutable("compactionError", ex.Message);
            return HookResult.Success;
        }
    }
}