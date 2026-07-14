using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Seeing.Session.Management;

namespace Seeing.Agent.TokenBudget;

/// <summary>
/// 模型限制自动应用到 Token Budget 的 Hook 处理器
/// </summary>
/// <remarks>
/// 监听多个 Hook 点，触发 Budget 状态更新：
/// - session.model_changed: 模型切换时
/// - session.loaded: 会话加载时（首次启动或打开会话）
/// - session.created: 会话创建时
/// 
/// MaxContextTokens 由 TokenBudgetManager.GetEffectiveMaxContextTokens() 运行时计算，
/// 不再存储到 session。
/// </remarks>
public class BudgetModelLimitHandler : IMultiHookHandler
{
    private readonly ILlmService _llmService;
    private readonly ITokenBudgetManager _budgetManager;
    private readonly IBudgetStatusNotifier? _notifier;
    private readonly ILogger<BudgetModelLimitHandler>? _logger;

    /// <summary>
    /// Hook 规格列表 - 监听模型变更和会话加载
    /// </summary>
    public IReadOnlyList<HookSpec> Specs { get; } = new[]
    {
        // 模型切换时触发（FireAndForget，不需要阻塞）
        new HookSpec(HookPolicy.FireAndForget, HookPoints.ModelChanged),
        // 会话加载时触发（FireAndForget，不需要阻塞后续流程）
        new HookSpec(HookPolicy.FireAndForget, HookPoints.Loaded),
        // 会话创建时触发
        new HookSpec(HookPolicy.FireAndForget, HookPoints.Created)
    };

    /// <summary>
    /// 优先级 - 高优先级（确保在其他处理器之前执行）
    /// </summary>
    public int Priority => 100;

    /// <summary>
    /// 创建 BudgetModelLimitHandler 实例
    /// </summary>
    public BudgetModelLimitHandler(
        ILlmService llmService,
        ITokenBudgetManager budgetManager,
        IBudgetStatusNotifier? notifier = null,
        ILogger<BudgetModelLimitHandler>? logger = null)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _budgetManager = budgetManager ?? throw new ArgumentNullException(nameof(budgetManager));
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// 处理 Hook 事件
    /// </summary>
    public async Task<HookResult> ExecuteAsync(HookPayload payload)
    {
        var input = payload.Input;
        var result = payload.Result;
        
        // 获取会话对象（根据不同 Hook 点，session 可能在 input 或 result 中）
        SessionData? session = null;
        string? modelId = null;

        // 1. 尝试从 input 获取（model_changed 事件）
        if (input != null)
        {
            if (input.TryGetValue("session", out var sessionObj) && sessionObj is SessionData s1)
                session = s1;
            if (input.TryGetValue("modelId", out var modelIdObj) && modelIdObj is string m1)
                modelId = m1;
        }

        // 2. 尝试从 result 获取（loaded/created 事件）
        if (session == null && result != null)
        {
            if (result.TryGetValue("session", out var sessionObj2) && sessionObj2 is SessionData s2)
                session = s2;
        }

        if (session == null)
        {
            _logger?.LogDebug("Hook {HookPoint} 没有 session 数据，跳过", payload.Spec.Point);
            return HookResult.Success;
        }

        // 如果没有显式传入 modelId，从 session 获取当前选中的模型
        if (string.IsNullOrEmpty(modelId))
        {
            modelId = GetFullModelId(session);
        }

        // 计算 budget 状态并通知 UI
        try
        {
            var status = _budgetManager.GetBudgetStatus(session, modelId);
            
            // 构建响应
            var response = new Api.Responses.BudgetStatusResponse
            {
                SessionId = session.Id,
                CurrentTokens = status.CurrentTokens,
                MaxTokens = status.MaxTokens,
                AvailableTokens = status.AvailableTokens,
                UsagePercentage = status.MaxTokens > 0 ? 100.0 * status.CurrentTokens / status.MaxTokens : 0,
                Level = status.Level.ToString().ToLowerInvariant(),
                Message = null,
                NeedsCompaction = status.Level >= BudgetLevel.Critical,
                Breakdown = null
            };

            // 通知 UI 更新
            _notifier?.Publish(session.Id, response);

            _logger?.LogInformation(
                "Budget 状态更新 [{HookPoint}]: Session={SessionId}, Model={Model}, MaxContext={MaxContext}",
                payload.Spec.Point,
                session.Id,
                modelId ?? "(none)",
                status.MaxTokens);

            return HookResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "更新 Budget 状态失败: Model={ModelId}", modelId);
            return HookResult.FromError(ex);
        }
    }

    /// <summary>
    /// 获取完整的模型 ID（包含 provider 前缀）
    /// </summary>
    private string? GetFullModelId(SessionData session)
    {
        if (string.IsNullOrEmpty(session.SelectedModel))
            return null;

        // 情况 1：selectedModel 已经包含 provider 前缀（如 "anthropic/GLM-5"）
        if (session.SelectedModel.Contains('/'))
        {
            return session.SelectedModel;
        }

        // 情况 2：有独立的 provider 字段
        if (!string.IsNullOrEmpty(session.SelectedModelProvider))
        {
            return $"{session.SelectedModelProvider}/{session.SelectedModel}";
        }

        // 情况 3：只有 modelId，尝试在 availableModels 中查找带前缀的版本
        var availableModels = _llmService.GetAvailableModels();
        
        // 尝试直接匹配
        if (availableModels.ContainsKey(session.SelectedModel))
            return session.SelectedModel;

        // 尝试查找带前缀的版本
        foreach (var key in availableModels.Keys)
        {
            if (key.EndsWith($"/{session.SelectedModel}"))
                return key;
        }

        // 兜底：返回原始值
        return session.SelectedModel;
    }
}
