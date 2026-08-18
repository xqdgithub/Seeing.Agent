using Seeing.Agent.Abstractions.Components;

namespace Seeing.Agent.Core.Models;

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
