using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Middlewares
{
    /// <summary>
    /// 日志中间件 - 记录执行前后的状态
    /// </summary>
    public class LoggingMiddleware : IExecutionMiddleware
    {
        private readonly ILogger<LoggingMiddleware> _logger;

        /// <inheritdoc />
        public string Name => "Logging";

        /// <inheritdoc />
        public int Order => 100; // 最先执行

        /// <summary>
        /// 创建日志中间件
        /// </summary>
        public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<TResult> InvokeAsync<TContext, TResult>(
            ExecutionDelegate<TContext, TResult> next,
            TContext context)
        {
            var contextType = typeof(TContext).Name;
            
            // 从上下文获取更多信息
            string? sessionId = null;
            string? messageId = null;
            
            if (context is IExecutionContext execCtx)
            {
                sessionId = execCtx.SessionId;
                messageId = execCtx.MessageId;
            }

            _logger.LogInformation(
                "[Pipeline] 开始执行: {ContextType}, Session={Session}, Message={Message}",
                contextType, sessionId ?? "N/A", messageId ?? "N/A");

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var result = await next(context);
                stopwatch.Stop();

                _logger.LogInformation(
                    "[Pipeline] 执行成功: {ContextType}, 耗时={Elapsed}ms",
                    contextType, stopwatch.ElapsedMilliseconds);

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                _logger.LogError(ex,
                    "[Pipeline] 执行失败: {ContextType}, 耗时={Elapsed}ms, Error={Error}",
                    contextType, stopwatch.ElapsedMilliseconds, ex.Message);

                throw;
            }
        }
    }
}