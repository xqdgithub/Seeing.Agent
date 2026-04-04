using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 重试装饰器 - 失败时自动重试
    /// </summary>
    public class RetryToolDecorator : ToolDecorator
    {
        private readonly int _maxRetries;
        private readonly TimeSpan _delay;
        private readonly Func<Exception, bool>? _isRetryable;
        private readonly ILogger? _logger;

        /// <summary>
        /// 创建重试装饰器
        /// </summary>
        /// <param name="inner">被包装的工具</param>
        /// <param name="maxRetries">最大重试次数</param>
        /// <param name="delay">重试间隔</param>
        /// <param name="isRetryable">判断异常是否可重试</param>
        /// <param name="logger">可选日志器</param>
        public RetryToolDecorator(
            ITool inner,
            int maxRetries = 3,
            TimeSpan? delay = null,
            Func<Exception, bool>? isRetryable = null,
            ILogger? logger = null) : base(inner)
        {
            _maxRetries = Math.Max(1, maxRetries);
            _delay = delay ?? TimeSpan.FromSeconds(1);
            _isRetryable = isRetryable ?? DefaultIsRetryable;
            _logger = logger;
        }

        /// <inheritdoc />
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt < _maxRetries; attempt++)
            {
                try
                {
                    var result = await base.ExecuteAsync(arguments, context);
                    
                    // 成功则返回
                    if (result.Success)
                        return result;
                    
                    // 如果结果标记为可重试，继续尝试
                    if (result.Metadata.TryGetValue("retryable", out var retryable) 
                        && retryable is bool canRetry && canRetry)
                    {
                        _logger?.LogWarning(
                            "[Retry] 工具返回可重试状态: ToolId={ToolId}, Attempt={Attempt}",
                            Id, attempt + 1);
                    }
                    else
                    {
                        return result;
                    }
                }
                catch (Exception ex) when (attempt < _maxRetries - 1 && _isRetryable(ex))
                {
                    lastException = ex;
                    var delay = TimeSpan.FromMilliseconds(_delay.TotalMilliseconds * (attempt + 1));
                    
                    _logger?.LogWarning(
                        "[Retry] 工具执行失败，准备重试: ToolId={ToolId}, Attempt={Attempt}/{Max}, Delay={Delay}ms, Error={Error}",
                        Id, attempt + 1, _maxRetries, delay.TotalMilliseconds, ex.Message);
                    
                    await Task.Delay(delay);
                }
            }

            // 所有重试都失败
            _logger?.LogError(
                "[Retry] 重试耗尽: ToolId={ToolId}, MaxRetries={MaxRetries}",
                Id, _maxRetries);

            return new ToolResult
            {
                Success = false,
                Title = "重试耗尽",
                Output = lastException?.Message ?? $"工具 {Id} 在 {_maxRetries} 次尝试后仍失败",
                Metadata = new Dictionary<string, object>
                {
                    ["maxRetries"] = _maxRetries,
                    ["lastError"] = lastException?.Message ?? "Unknown"
                }
            };
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
}