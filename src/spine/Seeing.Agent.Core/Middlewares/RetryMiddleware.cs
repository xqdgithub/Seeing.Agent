using Microsoft.Extensions.Logging;

using Seeing.Agent.Abstractions.Components;
namespace Seeing.Agent.Core.Middlewares
{
    /// <summary>
    /// 重试中间件 - 失败时自动重试
    /// </summary>
    public class RetryMiddleware : IExecutionMiddleware
    {
        /// <summary>退避上限（10 秒），防止指数增长导致长等待。</summary>
        private const double MaxBackoffMilliseconds = 10_000d;

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
        /// <param name="logger">日志器</param>
        /// <param name="maxRetries">总尝试次数（含首次），至少为 1</param>
        /// <param name="delay">首次重试间隔，后续按指数退避（上限 10 秒）</param>
        /// <param name="isRetryable">判断异常是否可重试</param>
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
            // 上下文若实现 IExecutionContext，则取其取消令牌（用于退避等待的取消与重试守卫）
            var cancellationToken = context is IExecutionContext execCtx
                ? execCtx.CancellationToken
                : CancellationToken.None;

            Exception? lastException = null;

            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                try
                {
                    return await next(context);
                }
                catch (Exception ex) when (_isRetryable!(ex) && !cancellationToken.IsCancellationRequested)
                {
                    lastException = ex;

                    // 最后一次尝试不再等待，直接落入“重试耗尽”抛出块
                    if (attempt >= _maxRetries - 1)
                        break;

                    // 指数退避：delay × 2^attempt，并设上限防长等待
                    var delay = TimeSpan.FromMilliseconds(
                        Math.Min(_delay.TotalMilliseconds * Math.Pow(2, attempt), MaxBackoffMilliseconds));

                    _logger.LogWarning(
                        "[Retry] 执行失败，准备重试: Attempt={Attempt}/{Max}, Delay={Delay}ms, Error={Error}",
                        attempt + 1, _maxRetries, delay.TotalMilliseconds, ex.Message);

                    // 传入取消令牌：调用方取消时立即抛出，避免取消后空等
                    await Task.Delay(delay, cancellationToken);
                }
            }

            if (lastException is null)
                throw new InvalidOperationException("重试循环异常终止：未捕获到可重试异常");

            // 可重试异常耗尽。中间件位于泛型执行管道中间，无法构造泛型 TResult 的降级结果，
            // 故以 MaxRetriesExceededException 上抛作为“重试耗尽”的唯一表达（包装最后一次异常）。
            // 注意：这与 RetryToolDecorator 不同——后者是具体工具包装，知道结果类型，故返回 Failure。
            _logger.LogError(
                "[Retry] 重试耗尽: MaxRetries={Max}, Error={Error}",
                _maxRetries, lastException.Message);

            throw new MaxRetriesExceededException(_maxRetries, lastException);
        }

        /// <summary>
        /// 默认可重试异常判断
        /// </summary>
        private static bool DefaultIsRetryable(Exception ex)
        {
            return ex is TimeoutException
                || ex is HttpRequestException
                || ex is IOException
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

        /// <summary>初始化最大重试次数超限异常，记录重试上限并保留内部异常。</summary>
        public MaxRetriesExceededException(int maxRetries, Exception innerException)
            : base($"操作在 {maxRetries} 次尝试后仍然失败", innerException)
        {
            MaxRetries = maxRetries;
        }
    }
}