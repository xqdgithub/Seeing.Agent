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
        var maxRetries = config.MaxRetries > 0 ? config.MaxRetries : 3;
        return new RetryingLlmClient(
            inner,
            maxRetries,
            _loggerFactory.CreateLogger<RetryingLlmClient>());
    }

    private sealed class RetryingLlmClient : ILlmClient
    {
        private readonly ILlmClient _inner;
        private readonly int _maxRetries;
        private readonly ILogger _logger;
        private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(1);

        public RetryingLlmClient(ILlmClient inner, int maxRetries, ILogger logger)
        {
            _inner = inner;
            _maxRetries = Math.Max(1, maxRetries);
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
            while (true)
            {
                attempt++;
                WriteRetryItems(call, attempt, willRetry: false);
                try
                {
                    return await _inner.CompleteAsync(request, call, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    attempt < _maxRetries &&
                    LlmRetryPolicy.IsRetryable(ex, cancellationToken))
                {
                    WriteRetryItems(call, attempt, willRetry: true);
                    _logger.LogWarning(ex,
                        "[RetryLlmClient] 非流式重试: Provider={Provider}, Attempt={Attempt}/{Max}",
                        ProviderId, attempt, _maxRetries);
                    var delay = TimeSpan.FromMilliseconds(
                        s_retryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var attempt = 0;
            while (true)
            {
                attempt++;
                WriteRetryItems(call, attempt, willRetry: false);
                var yielded = false;
                Exception? captured = null;

                await foreach (var update in StreamOnceAsync(
                    request, call, cancellationToken, onYield: () => yielded = true, onError: ex => captured = ex))
                {
                    yield return update;
                }

                if (captured is null)
                    yield break;

                if (yielded ||
                    attempt >= _maxRetries ||
                    !LlmRetryPolicy.IsRetryable(captured, cancellationToken))
                {
                    WriteRetryItems(call, attempt, willRetry: false);
                    throw captured;
                }

                WriteRetryItems(call, attempt, willRetry: true);
                _logger.LogWarning(captured,
                    "[RetryLlmClient] 流式重试: Provider={Provider}, Attempt={Attempt}/{Max}",
                    ProviderId, attempt, _maxRetries);
                var delay = TimeSpan.FromMilliseconds(
                    s_retryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

        private void WriteRetryItems(LlmCallContext? call, int attempt, bool willRetry)
        {
            if (call is null)
                return;
            call.Items[LlmRetryPolicy.AttemptItemKey] = attempt;
            call.Items[LlmRetryPolicy.WillRetryItemKey] = willRetry;
            call.Items[LlmRetryPolicy.MaxRetriesItemKey] = _maxRetries;
        }
    }
}
