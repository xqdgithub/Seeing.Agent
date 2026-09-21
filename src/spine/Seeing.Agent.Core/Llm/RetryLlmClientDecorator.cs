using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 重试装饰器：承接原 LlmService 内嵌重试；流式仅在未 yield 任何 chunk 时重试。
/// </summary>
public sealed class RetryLlmClientDecorator : ILlmClientDecorator
{
    private readonly ILoggerFactory _loggerFactory;

    public RetryLlmClientDecorator(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public int Order => 100;

    public ILlmClient Wrap(ILlmClient inner, ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(config);

        var settings = new LlmRetrySettings(
            BaseDelay: TimeSpan.FromMilliseconds(config.RetryBaseDelayMs > 0 ? config.RetryBaseDelayMs : 500),
            MaxDelay: TimeSpan.FromMilliseconds(config.RetryMaxDelayMs > 0 ? config.RetryMaxDelayMs : 10_000),
            Budget: TimeSpan.FromMilliseconds(config.RetryTotalBudgetMs > 0 ? config.RetryTotalBudgetMs : 120_000),
            MaxRetries: config.MaxRetries);

        return new RetryingLlmClient(
            inner,
            settings,
            _loggerFactory.CreateLogger<RetryingLlmClient>());
    }

    private readonly record struct LlmRetrySettings(
        TimeSpan BaseDelay,
        TimeSpan MaxDelay,
        TimeSpan Budget,
        int MaxRetries);

    private sealed class RetryingLlmClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly LlmRetrySettings _settings;
        private readonly ILogger _logger;

        public RetryingLlmClient(ILlmClient inner, LlmRetrySettings settings, ILogger logger)
        {
            _inner = inner;
            _settings = settings;
            _logger = logger;
        }

        public string ProviderId => _inner.ProviderId;
        public string ProviderType => _inner.ProviderType;

        public async Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
        {
            var attempt = 0;
            var elapsed = TimeSpan.Zero;
            while (true)
            {
                attempt++;
                WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
                try
                {
                    return await _inner.CompleteAsync(request, call, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (LlmRetryPolicy.IsRetryable(ex, cancellationToken))
                {
                    var nextDelay = LlmRetryPolicy.ComputeDelay(attempt, _settings.BaseDelay, _settings.MaxDelay);
                    if (!LlmRetryPolicy.ShouldRetry(
                            attempt, elapsed, nextDelay, _settings.MaxRetries, _settings.Budget))
                    {
                        throw;
                    }

                    elapsed += nextDelay;
                    WriteRetryItems(call, attempt, willRetry: true, nextDelay);
                    _logger.LogWarning(ex,
                        "[RetryLlmClient] 非流式重试: Provider={Provider}, Attempt={Attempt}/{Max}, NextDelayMs={NextDelayMs}, ElapsedMs={ElapsedMs}, BudgetMs={BudgetMs}",
                        ProviderId, attempt, FormatMax(_settings.MaxRetries),
                        nextDelay.TotalMilliseconds, elapsed.TotalMilliseconds, _settings.Budget.TotalMilliseconds);
                    await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = 0;
            var elapsed = TimeSpan.Zero;
            while (true)
            {
                attempt++;
                WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
                var yielded = false;
                Exception? captured = null;

                await foreach (var update in StreamOnceAsync(
                    request, call, cancellationToken, onYield: () => yielded = true, onError: ex => captured = ex))
                {
                    yield return update;
                }

                if (captured is null)
                    yield break;

                // 传输层边界：一旦向调用方交付过 chunk，绝不在此重试（无法回滚已交付内容）。
                // 已产出后的重开由应用层（AgentExecutor + ILlmTurnRetryPolicy）负责。
                if (yielded || !LlmRetryPolicy.IsRetryable(captured, cancellationToken))
                    throw captured;

                var nextDelay = LlmRetryPolicy.ComputeDelay(attempt, _settings.BaseDelay, _settings.MaxDelay);
                if (!LlmRetryPolicy.ShouldRetry(
                        attempt, elapsed, nextDelay, _settings.MaxRetries, _settings.Budget))
                {
                    WriteRetryItems(call, attempt, willRetry: false, nextDelay: null);
                    throw captured;
                }

                elapsed += nextDelay;
                WriteRetryItems(call, attempt, willRetry: true, nextDelay);
                _logger.LogWarning(captured,
                    "[RetryLlmClient] 流式重试: Provider={Provider}, Attempt={Attempt}/{Max}, NextDelayMs={NextDelayMs}, ElapsedMs={ElapsedMs}, BudgetMs={BudgetMs}",
                    ProviderId, attempt, FormatMax(_settings.MaxRetries),
                    nextDelay.TotalMilliseconds, elapsed.TotalMilliseconds, _settings.Budget.TotalMilliseconds);
                await Task.Delay(nextDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        private async IAsyncEnumerable<StreamUpdate> StreamOnceAsync(
            ChatRequest request,
            LlmCallContext? call,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            Action onYield,
            Action<Exception> onError)
        {
            var enumerator = _inner.CompleteStreamAsync(request, call, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        onError(ex);
                        yield break;
                    }

                    if (!moved)
                        yield break;

                    onYield();
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => _inner.TestConnectionAsync(modelId, call, cancellationToken);

        private void WriteRetryItems(LlmCallContext? call, int attempt, bool willRetry, TimeSpan? nextDelay)
        {
            if (call is null)
                return;

            call.Items[LlmRetryPolicy.AttemptItemKey] = attempt;
            call.Items[LlmRetryPolicy.WillRetryItemKey] = willRetry;
            call.Items[LlmRetryPolicy.MaxRetriesItemKey] = _settings.MaxRetries;
            if (nextDelay is { } delay)
                call.Items[LlmRetryPolicy.NextDelayItemKey] = delay.TotalMilliseconds;
        }

        private static string FormatMax(int maxRetries) => maxRetries > 0 ? maxRetries.ToString() : "∞";
    }
}
