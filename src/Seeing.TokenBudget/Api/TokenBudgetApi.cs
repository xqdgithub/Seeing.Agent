using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Seeing.TokenBudget.Api.Responses;
using Seeing.TokenEstimation;

namespace Seeing.TokenBudget.Api;

/// <summary>
/// Default implementation of the token budget API.
/// Provides access to token budget management operations for sessions.
/// </summary>
public class TokenBudgetApi : ITokenBudgetApi
{
    private readonly ISessionStore _sessionStore;
    private readonly ITokenBudgetManager _budgetManager;
    private readonly ITokenBudgetConfigResolver _configResolver;
    private readonly ICompressionStrategy _compressionStrategy;
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    /// Creates a new TokenBudgetApi instance.
    /// </summary>
    /// <param name="sessionStore">The session store for loading session data.</param>
    /// <param name="budgetManager">The budget manager for token calculations.</param>
    /// <param name="configResolver">The config resolver for determining effective configuration.</param>
    /// <param name="compressionStrategy">The compression strategy for compaction operations.</param>
    /// <param name="tokenCounter">The token counter for estimating token counts.</param>
    public TokenBudgetApi(
        ISessionStore sessionStore,
        ITokenBudgetManager budgetManager,
        ITokenBudgetConfigResolver configResolver,
        ICompressionStrategy compressionStrategy,
        ITokenCounter tokenCounter)
    {
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _budgetManager = budgetManager ?? throw new ArgumentNullException(nameof(budgetManager));
        _configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
        _compressionStrategy = compressionStrategy ?? throw new ArgumentNullException(nameof(compressionStrategy));
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
    }

    /// <inheritdoc />
    public async Task<TokenBreakdownResponse> GetBreakdownAsync(string sessionId)
    {
        var session = await GetSessionOrThrowAsync(sessionId);
        var breakdown = _budgetManager.CalculateBreakdown(session);

        return MapToResponse(sessionId, breakdown, session.UpdatedAt);
    }

    /// <inheritdoc />
    public async Task<BudgetStatusResponse> GetBudgetStatusAsync(string sessionId)
    {
        var session = await GetSessionOrThrowAsync(sessionId);
        var config = await GetEffectiveConfigInternalAsync(session);
        var breakdown = _budgetManager.CalculateBreakdown(session);
        
        var status = _budgetManager.CheckBudget(session, config, breakdown.Total);
        
        return MapToStatusResponse(sessionId, status);
    }

    /// <inheritdoc />
    public async Task<CompactionResponse> TriggerCompactionAsync(string sessionId, CompactionStrategyType? strategy = null)
    {
        var session = await GetSessionOrThrowAsync(sessionId);
        var config = await GetEffectiveConfigInternalAsync(session);
        
        // Use provided strategy or fall back to configured strategy
        var effectiveStrategy = strategy ?? config.CompactionStrategy;
        
        // Calculate tokens before
        var breakdownBefore = _budgetManager.CalculateBreakdown(session);
        var tokensBefore = breakdownBefore.Total;
        
        try
        {
            // Execute compression
            var result = _compressionStrategy.CompressByTokenBudget(
                session.Messages,
                config,
                _tokenCounter);
            
            // Update session messages if compression was successful
            if (result.Success)
            {
                session.Messages.Clear();
                session.Messages.AddRange(result.CompressedMessages);
                session.UpdatedAt = DateTime.Now;
                await _sessionStore.SaveAsync(session);
            }
            
            return new CompactionResponse
            {
                Success = result.Success,
                TokensBefore = tokensBefore,
                TokensAfter = result.TokensAfter,
                MessagesRemoved = result.MessagesRemoved,
                StrategyUsed = effectiveStrategy.ToString(),
                ErrorMessage = result.ErrorMessage
            };
        }
        catch (Exception ex)
        {
            return new CompactionResponse
            {
                Success = false,
                TokensBefore = tokensBefore,
                TokensAfter = tokensBefore,
                MessagesRemoved = 0,
                StrategyUsed = effectiveStrategy.ToString(),
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task UpdateConfigAsync(string sessionId, TokenBudgetConfig config)
    {
        var session = await GetSessionOrThrowAsync(sessionId);
        session.BudgetConfig = config ?? throw new ArgumentNullException(nameof(config));
        session.UpdatedAt = DateTime.Now;
        await _sessionStore.SaveAsync(session);
    }

    /// <inheritdoc />
    public async Task<TokenBudgetConfig> GetEffectiveConfigAsync(string sessionId)
    {
        var session = await GetSessionOrThrowAsync(sessionId);
        return await GetEffectiveConfigInternalAsync(session);
    }

    private async Task<SessionData> GetSessionOrThrowAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            throw new ArgumentException("Session ID cannot be null or empty.", nameof(sessionId));
        }

        var session = await _sessionStore.LoadAsync(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session not found: {sessionId}");
        }

        return session;
    }

    private Task<TokenBudgetConfig> GetEffectiveConfigInternalAsync(SessionData session)
    {
        // For now, return the session's budget config or a default
        // In a full implementation, this would resolve against agent and global configs
        var config = _configResolver.Resolve(
            session.BudgetConfig,
            agentConfig: null,
            globalConfig: null);
        
        return Task.FromResult(config);
    }

    private static TokenBreakdownResponse MapToResponse(string sessionId, TokenBreakdown breakdown, DateTime updatedAt)
    {
        var total = breakdown.Total;
        
        return new TokenBreakdownResponse
        {
            SessionId = sessionId,
            TotalTokens = total,
            UpdatedAt = updatedAt,
            BySource = new SourceBreakdownData
            {
                SystemPrompt = CreateCategoryInfo(breakdown.BySource.SystemPrompt, total, 0),
                ToolDefinitions = CreateCategoryInfo(breakdown.BySource.ToolDefinitions, total, 0),
                UserMessages = CreateCategoryInfo(breakdown.BySource.UserMessages, total, 0),
                AssistantMessages = CreateCategoryInfo(breakdown.BySource.AssistantMessages, total, 0),
                ToolResults = CreateCategoryInfo(breakdown.BySource.ToolResults, total, 0)
            },
            ByRole = new RoleBreakdownData
            {
                System = CreateCategoryInfo(breakdown.ByRole.System, total, 0),
                User = CreateCategoryInfo(breakdown.ByRole.User, total, 0),
                Assistant = CreateCategoryInfo(breakdown.ByRole.Assistant, total, 0),
                Tool = CreateCategoryInfo(breakdown.ByRole.Tool, total, 0)
            }
        };
    }

    private static BudgetStatusResponse MapToStatusResponse(string sessionId, BudgetStatus status)
    {
        TokenBreakdownResponse? breakdownResponse = null;
        
        if (status.Breakdown != null)
        {
            breakdownResponse = MapToResponse(sessionId, status.Breakdown, DateTime.Now);
        }

        return new BudgetStatusResponse
        {
            SessionId = sessionId,
            CurrentTokens = status.CurrentTokens,
            MaxTokens = status.MaxTokens,
            UsagePercentage = status.UsagePercentage,
            AvailableTokens = status.AvailableTokens,
            Level = status.Level.ToString().ToLowerInvariant(),
            NeedsCompaction = status.Level is BudgetLevel.Critical or BudgetLevel.Overflow,
            Message = GetBudgetMessage(status),
            Breakdown = breakdownResponse
        };
    }

    private static CategoryInfo CreateCategoryInfo(int tokens, int total, int messageCount)
    {
        return new CategoryInfo
        {
            Tokens = tokens,
            Percentage = total > 0 ? (tokens / (double)total) * 100 : 0,
            MessageCount = messageCount
        };
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
