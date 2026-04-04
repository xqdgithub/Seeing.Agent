using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 超时装饰器 - 限制工具执行时间
    /// </summary>
    public class TimeoutToolDecorator : ToolDecorator
    {
        private readonly TimeSpan _timeout;
        private readonly ILogger? _logger;

        /// <summary>
        /// 创建超时装饰器
        /// </summary>
        /// <param name="inner">被包装的工具</param>
        /// <param name="timeout">超时时间</param>
        /// <param name="logger">可选日志器</param>
        public TimeoutToolDecorator(
            ITool inner,
            TimeSpan timeout,
            ILogger? logger = null) : base(inner)
        {
            _timeout = timeout;
            _logger = logger;
        }

        /// <inheritdoc />
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cts.CancelAfter(_timeout);

            try
            {
                // 创建带超时的上下文
                var timeoutContext = new TimeoutToolContext(context, cts.Token);
                
                var result = await base.ExecuteAsync(arguments, timeoutContext);
                
                return result;
            }
            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    "[Timeout] 工具执行超时: ToolId={ToolId}, Timeout={Timeout}ms",
                    Id, _timeout.TotalMilliseconds);

                return new ToolResult
                {
                    Success = false,
                    Title = "执行超时",
                    Output = $"工具 {Id} 执行超过 {_timeout.TotalSeconds} 秒",
                    Metadata = new Dictionary<string, object>
                    {
                        ["timeout"] = _timeout.TotalMilliseconds,
                        ["timedOut"] = true
                    }
                };
            }
        }

        /// <summary>
        /// 带超时令牌的工具上下文
        /// </summary>
        private class TimeoutToolContext : ToolContext
        {
            public TimeoutToolContext(ToolContext original, CancellationToken timeoutToken)
            {
                SessionId = original.SessionId;
                MessageId = original.MessageId;
                CallId = original.CallId;
                Agent = original.Agent;
                CancellationToken = timeoutToken;
                SetMetadata = original.SetMetadata;
                AskPermission = original.AskPermission;
            }
        }
    }
}