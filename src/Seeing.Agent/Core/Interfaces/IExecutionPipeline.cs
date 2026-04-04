using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 执行管道接口 - 中间件链
    /// </summary>
    public interface IExecutionPipeline
    {
        /// <summary>添加中间件</summary>
        IExecutionPipeline Use(IExecutionMiddleware middleware);
        
        /// <summary>添加中间件类型（通过 DI 解析）</summary>
        IExecutionPipeline Use<TMiddleware>() where TMiddleware : IExecutionMiddleware;
        
        /// <summary>执行管道</summary>
        Task<TResult> ExecuteAsync<TContext, TResult>(
            TContext context, 
            Func<TContext, Task<TResult>> handler);
    }

    /// <summary>
    /// 中间件接口 - 可拦截、修改、短路执行流
    /// </summary>
    public interface IExecutionMiddleware
    {
        /// <summary>中间件名称</summary>
        string Name { get; }
        
        /// <summary>执行顺序（越小越先执行）</summary>
        int Order { get; }
        
        /// <summary>执行中间件</summary>
        Task<TResult> InvokeAsync<TContext, TResult>(
            ExecutionDelegate<TContext, TResult> next,
            TContext context);
    }

    /// <summary>
    /// 执行委托 - 管道下一个节点
    /// </summary>
    public delegate Task<TResult> ExecutionDelegate<TContext, TResult>(TContext context);

    /// <summary>
    /// 管道构建器 - 用于配置中间件
    /// </summary>
    public class PipelineBuilder
    {
        private readonly List<Type> _middlewares = new();

        /// <summary>已注册的中间件类型</summary>
        public IReadOnlyList<Type> Middlewares => _middlewares.AsReadOnly();

        /// <summary>添加中间件</summary>
        public PipelineBuilder Use<TMiddleware>() where TMiddleware : IExecutionMiddleware
        {
            _middlewares.Add(typeof(TMiddleware));
            return this;
        }
    }
}