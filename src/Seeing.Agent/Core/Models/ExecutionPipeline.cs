using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// 执行管道实现 - 中间件链
    /// </summary>
    public class ExecutionPipeline : IExecutionPipeline
    {
        private readonly List<IExecutionMiddleware> _middlewares = new();
        private readonly ILogger<ExecutionPipeline>? _logger;

        /// <summary>
        /// 创建执行管道
        /// </summary>
        public ExecutionPipeline(ILogger<ExecutionPipeline>? logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public IExecutionPipeline Use(IExecutionMiddleware middleware)
        {
            if (middleware == null)
                throw new ArgumentNullException(nameof(middleware));
            
            _middlewares.Add(middleware);
            _middlewares.Sort((a, b) => a.Order.CompareTo(b.Order));
            
            _logger?.LogDebug("添加中间件: {Name}, Order={Order}", middleware.Name, middleware.Order);
            return this;
        }

        /// <inheritdoc />
        public IExecutionPipeline Use<TMiddleware>() where TMiddleware : IExecutionMiddleware
        {
            throw new NotSupportedException(
                "请使用 IServiceProvider 解析中间件后调用 Use(IExecutionMiddleware)");
        }

        /// <summary>
        /// 从 DI 容器解析并添加中间件
        /// </summary>
        public IExecutionPipeline Use<TMiddleware>(IServiceProvider services) 
            where TMiddleware : IExecutionMiddleware
        {
            var middleware = services.GetRequiredService<TMiddleware>();
            return Use(middleware);
        }

        /// <inheritdoc />
        public async Task<TResult> ExecuteAsync<TContext, TResult>(
            TContext context, 
            Func<TContext, Task<TResult>> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // 构建中间件链
            ExecutionDelegate<TContext, TResult> pipeline = (ctx) => handler(ctx);

            // 从后向前构建（最后的中间件最先包装）
            for (int i = _middlewares.Count - 1; i >= 0; i--)
            {
                var middleware = _middlewares[i];
                var next = pipeline;
                pipeline = (ctx) => middleware.InvokeAsync(next, ctx);
            }

            return await pipeline(context);
        }

        /// <summary>
        /// 获取中间件数量
        /// </summary>
        public int MiddlewareCount => _middlewares.Count;
    }
}