using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Configuration;

namespace Seeing.Agent.Memory.Tests;

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }
    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => new Noop();

    public void Update(T value) => CurrentValue = value;

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}

internal static class MemoryTestOptions
{
    public static IOptionsMonitor<MemoryOptions> Monitor(MemoryOptions? options = null) =>
        new StaticOptionsMonitor<MemoryOptions>(options ?? new MemoryOptions());

    /// <summary>
    /// 登记 Memory 依赖的统一 LLM 基础设施替身（向量调用走 <see cref="ILlmInvoker"/>，
    /// 模型引用校验走 <see cref="IModelManager"/>）。测试用最小容器时需要它们才能解析服务链。
    /// </summary>
    public static IServiceCollection AddLlmInfraDoubles(
        this IServiceCollection services,
        ILlmInvoker? invoker = null,
        IModelManager? models = null)
    {
        var manager = models ?? Mock.Of<IModelManager>();
        services.TryAdd(new ServiceDescriptor(typeof(IModelManager), manager));
        services.TryAdd(new ServiceDescriptor(typeof(IModelConfigManager), manager));
        services.TryAdd(new ServiceDescriptor(typeof(ILlmInvoker), invoker ?? Mock.Of<ILlmInvoker>()));
        return services;
    }
}
