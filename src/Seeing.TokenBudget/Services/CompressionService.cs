using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.TokenEstimation;

// Alias to disambiguate from Seeing.TokenBudget.CompressionResult
using SessionCompressionResult = Seeing.Session.Core.CompressionResult;

namespace Seeing.TokenBudget;

/// <summary>
/// 压缩服务实现
/// </summary>
public class CompressionService : ICompressionService
{
    private readonly ICompressionStrategyFactory _strategyFactory;
    private readonly ITokenBudgetConfigResolver _configResolver;
    private readonly ITokenCounter _tokenCounter;

    public CompressionService(
        ICompressionStrategyFactory strategyFactory,
        ITokenBudgetConfigResolver configResolver,
        ITokenCounter tokenCounter)
    {
        _strategyFactory = strategyFactory;
        _configResolver = configResolver;
        _tokenCounter = tokenCounter;
    }

    public async Task<SessionCompressionResult> CompressAsync(
        SessionData session,
        TokenBudgetConfig? sessionConfig = null,
        TokenBudgetConfig? agentConfig = null,
        CancellationToken cancellationToken = default)
    {
        // 获取有效配置
        var config = _configResolver.Resolve(
            sessionConfig ?? session.BudgetConfig,
            agentConfig,
            null); // 全局配置由 resolver 内部处理

        // 检查是否启用自动压缩
        if (!config.AutoCompactionEnabled)
        {
            return SessionCompressionResult.Succeeded(0, 0, 0, session.Messages.ToList());
        }

        // 获取策略
        var strategy = _strategyFactory.GetStrategy(config.CompactionStrategy);

        // 执行压缩
        var result = strategy.CompressByTokenBudget(session.Messages, config, _tokenCounter);

        // 如果压缩成功，更新会话消息
        if (result.Success && result.CompressedMessages != null)
        {
            session.Messages.Clear();
            foreach (var message in result.CompressedMessages)
            {
                session.Messages.Add(message);
            }
        }

        return result;
    }
}
