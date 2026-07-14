using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Models;
using Seeing.Session.Core;
using Seeing.TokenBudget;
using Seeing.TokenBudget.Api.Responses;
using Seeing.TokenEstimation;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 预算更新 Hook - 在 LLM 响应后更新预算状态
/// </summary>
public class BudgetUpdateHook : IHookHandler
{
    private readonly ITokenBudgetManager _budgetManager;
    private readonly ITokenBudgetConfigResolver _configResolver;
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    /// Hook 规格 - Chat 完成后
    /// </summary>
    public HookSpec Spec => HookRegistry.ChatAfterComplete;

    /// <summary>
    /// 优先级 - 高优先级
    /// </summary>
    public int Priority => 100;

    public BudgetUpdateHook(
        ITokenBudgetManager budgetManager,
        ITokenBudgetConfigResolver configResolver,
        ITokenCounter tokenCounter)
    {
        _budgetManager = budgetManager;
        _configResolver = configResolver;
        _tokenCounter = tokenCounter;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        var session = payload.GetInput<SessionData>("session");
        var agent = payload.GetInput<AgentDefinition>("agent");
        var systemPrompt = payload.GetInput<string>("systemPrompt");
        var toolTokens = payload.GetInput<int?>("toolTokens");

        if (session == null || agent == null)
        {
            return HookResult.Success;
        }

        try
        {
            // 计算当前 token 分布
            var breakdown = _budgetManager.CalculateBreakdown(session, systemPrompt, toolTokens);

            // 获取有效配置
            var config = _configResolver.Resolve(
                session.BudgetConfig,
                agent.BudgetConfig,
                null);

            // 检查预算状态
            var status = _budgetManager.CheckBudget(session, config, breakdown.Total);

            // 根据状态设置 PendingCompaction（Critical 或 Overflow 时设置）
            if (status.Level >= BudgetLevel.Critical)
            {
                session.PendingCompaction = true;
            }

            // 构建事件并存入 Mutable，供外部事件发射器使用
            var budgetEvent = new BudgetStatusEvent
            {
                SessionId = payload.SessionId,
                CurrentTokens = status.CurrentTokens,
                MaxTokens = status.MaxTokens,
                UsagePercentage = status.UsagePercentage,
                Level = status.Level,
                Breakdown = MapToResponse(breakdown)
            };

            payload.SetMutable("budgetStatusEvent", budgetEvent);

            // 检查是否需要发出警告事件
            if (status.Level >= BudgetLevel.Warning)
            {
                var warningEvent = new BudgetWarningEvent
                {
                    SessionId = payload.SessionId,
                    Message = status.Level >= BudgetLevel.Critical
                        ? $"Token usage at {status.UsagePercentage:F1}%, compaction recommended"
                        : $"Token usage at {status.UsagePercentage:F1}%, approaching limit",
                    Level = status.Level
                };
                payload.SetMutable("budgetWarningEvent", warningEvent);
            }

            // 如果有压缩结果，构建压缩事件
            if (payload.Mutable.TryGetValue("compactionResult", out var compactionResultObj) &&
                compactionResultObj is SessionCompressionResult compactionResult)
            {
                var compactionEvent = new CompactionEvent
                {
                    SessionId = payload.SessionId,
                    Strategy = config.CompactionStrategy.ToString(),
                    TokensBefore = compactionResult.TokensBefore,
                    TokensAfter = compactionResult.TokensAfter,
                    MessagesRemoved = compactionResult.MessagesRemoved,
                    Success = compactionResult.Success
                };
                payload.SetMutable("compactionEvent", compactionEvent);
            }

            return HookResult.Success;
        }
        catch (Exception)
        {
            // 预算检查失败不阻止执行
            return HookResult.Success;
        }
    }

    private static TokenBreakdownResponse? MapToResponse(TokenBreakdown? breakdown)
    {
        if (breakdown == null) return null;

        return new TokenBreakdownResponse
        {
            TotalTokens = breakdown.Total,
            BySource = new SourceBreakdownData
            {
                SystemPrompt = new CategoryInfo { Tokens = breakdown.BySource.SystemPrompt },
                ToolDefinitions = new CategoryInfo { Tokens = breakdown.BySource.ToolDefinitions },
                UserMessages = new CategoryInfo { Tokens = breakdown.BySource.UserMessages },
                AssistantMessages = new CategoryInfo { Tokens = breakdown.BySource.AssistantMessages },
                ToolResults = new CategoryInfo { Tokens = breakdown.BySource.ToolResults }
            },
            ByRole = new RoleBreakdownData
            {
                System = new CategoryInfo { Tokens = breakdown.ByRole.System },
                User = new CategoryInfo { Tokens = breakdown.ByRole.User },
                Assistant = new CategoryInfo { Tokens = breakdown.ByRole.Assistant },
                Tool = new CategoryInfo { Tokens = breakdown.ByRole.Tool }
            }
        };
    }
}
