using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Middlewares
{
    /// <summary>
    /// 重试中间件 - 失败时自动重试
    /// </summary>
    public class RetryMiddleware : IExecutionMiddleware
    {
        private readonly ILogger<RetryMiddleware> _logger;
        private readonly int _maxRetries;
        private readonly TimeSpan _delay;
        private readonly Func<Exception, bool>? _isRetryable;

        /// <inheritdoc />
        public string Name => "Retry";

        /// <inheritdoc />
        public int Order => 200; // 在其他中间件之后

        /// <summary>
        /// 创建重试中间件
        /// </summary>
        public RetryMiddleware(
            ILogger<RetryMiddleware> logger,
            int maxRetries = 3,
            TimeSpan? delay = null,
            Func<Exception, bool>? isRetryable = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _maxRetries = Math.Max(1, maxRetries);
            _delay = delay ?? TimeSpan.FromSeconds(1);
            _isRetryable = isRetryable ?? DefaultIsRetryable;
        }

        /// <inheritdoc />
        public async Task<TResult> InvokeAsync<TContext, TResult>(
            ExecutionDelegate<TContext, TResult> next,
            TContext context)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                try
                {
                    return await next(context);
                }
                catch (Exception ex) when (attempt < _maxRetries - 1 && _isRetryable(ex))
                {
                    lastException = ex;
                    var delay = _delay * (attempt + 1); // 指数退避

                    _logger.LogWarning(
                        "[Retry] 执行失败，准备重试: Attempt={Attempt}/{Max}, Delay={Delay}ms, Error={Error}",
                        attempt + 1, _maxRetries, delay.TotalMilliseconds, ex.Message);

                    await Task.Delay(delay);
                }
            }

            // 所有重试都失败了
            if (lastException != null)
            {
                _logger.LogError(
                    "[Retry] 重试耗尽: MaxRetries={Max}, Error={Error}",
                    _maxRetries, lastException.Message);
                throw new MaxRetriesExceededException(_maxRetries, lastException);
            }

            throw new InvalidOperationException("不应到达此处");
        }

        /// <summary>
        /// 默认可重试异常判断
        /// </summary>
        private static bool DefaultIsRetryable(Exception ex)
        {
            return ex is TimeoutException
                || ex is HttpRequestException
                || ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested;
        }
    }

    /// <summary>
    /// 最大重试次数超限异常
    /// </summary>
    public class MaxRetriesExceededException : Exception
    {
        /// <summary>最大重试次数</summary>
        public int MaxRetries { get; }

        public MaxRetriesExceededException(int maxRetries, Exception innerException)
            : base($"操作在 {maxRetries} 次尝试后仍然失败", innerException)
        {
            MaxRetries = maxRetries;
        }
    }
}