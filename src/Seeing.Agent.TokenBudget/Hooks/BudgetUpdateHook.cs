using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Events;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Agent.TokenBudget.Api.Responses;
using Seeing.TokenEstimation;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 预算更新 Hook - 在 LLM 响应后更新预算状态
/// </summary>
public class BudgetUpdateHook : IHookHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ITokenCounter _tokenCounter;
    private readonly IBudgetStatusNotifier? _notifier;
    private readonly ILogger<BudgetUpdateHook>? _logger;

    /// <summary>
    /// Hook 规格 - Chat 完成后
    /// </summary>
    public HookSpec Spec => HookRegistry.ChatAfterComplete;

    /// <summary>
    /// 优先级 - 高优先级
    /// </summary>
    public int Priority => 100;

    public BudgetUpdateHook(
        IServiceProvider serviceProvider,
        ITokenCounter tokenCounter,
        IBudgetStatusNotifier? notifier = null,
        ILogger<BudgetUpdateHook>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _tokenCounter = tokenCounter;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        // 统一通过 ISessionManager 获取 Session（确保缓存一致性）
        SessionData? session = null;
        
        if (!string.IsNullOrEmpty(payload.SessionId))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var sessionManager = scope.ServiceProvider.GetRequiredService<ISessionManager>();
                session = await sessionManager.GetOrLoadAsync(payload.SessionId);
            }
            catch (InvalidOperationException)
            {
                // Session 不存在，跳过
                _logger?.LogDebug("BudgetUpdateHook: Session not found: {SessionId}", payload.SessionId);
            }
        }

        if (session == null)
        {
            _logger?.LogDebug("BudgetUpdateHook: No session available, skipping");
            return HookResult.Success;
        }

        try
        {
            using var innerScope = _serviceProvider.CreateScope();
            var budgetManager = innerScope.ServiceProvider.GetRequiredService<ITokenBudgetManager>();
            var sessionManager = innerScope.ServiceProvider.GetRequiredService<ISessionManager>();

            // 获取 Provider Usage（优先使用 Provider 准确统计）
            var providerUsage = GetProviderUsage(payload);

            // 计算/获取 totalInputTokens
            bool shouldSaveSession = false;

            if (providerUsage != null && providerUsage.InputTokens > 0)
            {
                // 使用 Provider 准确统计
                // 缓存到 Session
                session.CachedInputTokens = providerUsage.InputTokens;
                session.CachedOutputTokens = providerUsage.OutputTokens;
                session.CachedUsageUpdatedAt = DateTime.Now;
                shouldSaveSession = true;

                _logger?.LogDebug("BudgetUpdateHook: Using Provider Usage - Input={InputTokens}, Output={OutputTokens}",
                    providerUsage.InputTokens, providerUsage.OutputTokens);
            }

            // 使用新的 GetBudgetStatus 方法，自动处理模型配置
            var status = budgetManager.GetBudgetStatus(session, session.SelectedModel);

            // 根据状态设置 PendingCompaction（Critical 或 Overflow 时设置）
            if (status.Level >= BudgetLevel.Critical)
            {
                session.PendingCompaction = true;
            }

            // 构建 Budget 状态响应
            var budgetStatusResponse = new BudgetStatusResponse
            {
                SessionId = payload.SessionId,
                CurrentTokens = status.CurrentTokens,
                MaxTokens = status.MaxTokens,
                AvailableTokens = status.AvailableTokens,
                UsagePercentage = status.UsagePercentage,
                Level = status.Level.ToString().ToLowerInvariant(),
                Message = GetBudgetMessage(status),
                NeedsCompaction = status.Level is BudgetLevel.Critical or BudgetLevel.Overflow,
                Breakdown = null
            };

            // 通过通知器发布状态更新（单例模式，通知所有订阅者）
            _notifier?.Publish(payload.SessionId, budgetStatusResponse);

            _logger?.LogInformation("Budget updated: {CurrentTokens}/{MaxTokens} tokens ({Percentage:F1}%)",
                status.CurrentTokens, status.MaxTokens, status.UsagePercentage);

            // 保存 Session（通过 SessionManager 确保缓存一致性）
            if (shouldSaveSession)
            {
                await sessionManager.SaveAndNotifyAsync(session.Id, persist: true);
                _logger?.LogDebug("BudgetUpdateHook: Session saved with cached usage");
            }

            // 构建事件并存入 Mutable，供外部事件发射器使用
            var budgetEvent = new BudgetStatusEvent
            {
                SessionId = payload.SessionId,
                CurrentTokens = status.CurrentTokens,
                MaxTokens = status.MaxTokens,
                UsagePercentage = status.UsagePercentage,
                Level = status.Level,
                Breakdown = null
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
                // 获取压缩策略
                var options = innerScope.ServiceProvider.GetRequiredService<IOptions<SeeingAgentOptions>>();
                var strategy = options.Value.TokenBudget.CompactionStrategy.ToString();

                var compactionEvent = new CompactionEvent
                {
                    SessionId = payload.SessionId,
                    Strategy = strategy,
                    TokensBefore = compactionResult.TokensBefore,
                    TokensAfter = compactionResult.TokensAfter,
                    MessagesRemoved = compactionResult.MessagesRemoved,
                    Success = compactionResult.Success
                };
                payload.SetMutable("compactionEvent", compactionEvent);
            }

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Budget update failed");
            // 预算检查失败不阻止执行
            return HookResult.Success;
        }
    }

    /// <summary>
    /// 从 Hook payload 获取 Provider Usage
    /// </summary>
    private TokenUsage? GetProviderUsage(HookPayload payload)
    {
        // 非流式：response.Usage
        var response = payload.GetResult<ChatResponse>("response");
        if (response?.Usage != null && response.Usage.InputTokens > 0)
            return response.Usage;

        // 流式：usage 直接在 result 中
        var usage = payload.GetResult<TokenUsage>("usage");
        if (usage != null && usage.InputTokens > 0)
            return usage;

        return null;
    }

    private static string? GetBudgetMessage(BudgetStatus status)
    {
        return status.Level switch
        {
            BudgetLevel.Normal => null,
            BudgetLevel.Warning => $"Approaching token limit: {status.UsagePercentage:F0}% used",
            BudgetLevel.Critical => $"Critical token usage: {status.UsagePercentage:F0}% used. Compression recommended.",
            BudgetLevel.Overflow => $"Token budget exceeded: {status.CurrentTokens}/{status.MaxTokens} tokens. Immediate compression required.",
            _ => null
        };
    }
}
