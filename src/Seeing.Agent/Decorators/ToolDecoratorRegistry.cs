using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 工具装饰器注册表接口 - 管理装饰器的注册和应用
    /// </summary>
    public interface IToolDecoratorRegistry
    {
        /// <summary>注册装饰器工厂</summary>
        void Register(Func<ITool, ITool> factory);

        /// <summary>注册类型化装饰器</summary>
        void Register<TDecorator>() where TDecorator : ITool;

        /// <summary>应用所有装饰器到工具</summary>
        ITool Apply(ITool tool);

        /// <summary>获取已注册的装饰器数量</summary>
        int Count { get; }
    }

    /// <summary>
    /// 工具装饰器注册表实现
    /// </summary>
    public class ToolDecoratorRegistry : IToolDecoratorRegistry
    {
        private readonly List<Func<ITool, ITool>> _factories = new();
        private readonly IServiceProvider? _serviceProvider;

        /// <summary>
        /// 创建装饰器注册表
        /// </summary>
        public ToolDecoratorRegistry(IServiceProvider? serviceProvider = null)
        {
            _serviceProvider = serviceProvider;
        }

        /// <inheritdoc />
        public int Count => _factories.Count;

        /// <inheritdoc />
        public void Register(Func<ITool, ITool> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            _factories.Add(factory);
        }

        /// <inheritdoc />
        public void Register<TDecorator>() where TDecorator : ITool
        {
            // 类型化注册需要 DI 支持
            if (_serviceProvider == null)
            {
                throw new InvalidOperationException(
                    "类型化装饰器注册需要 IServiceProvider。请在构造时传入。");
            }

            _factories.Add(tool =>
            {
                // 尝试从 DI 获取装饰器实例
                var decorator = _serviceProvider.GetService(typeof(TDecorator));
                if (decorator is ToolDecorator td)
                {
                    // 使用反射重新构造，传入内部工具
                    var constructor = typeof(TDecorator).GetConstructor(new[] { typeof(ITool) });
                    if (constructor != null)
                    {
                        return (ITool)constructor.Invoke(new object[] { tool });
                    }
                }
                return tool;
            });
        }

        /// <inheritdoc />
        public ITool Apply(ITool tool)
        {
            if (tool == null)
                throw new ArgumentNullException(nameof(tool));

            var current = tool;
            foreach (var factory in _factories)
            {
                current = factory(current);
            }
            return current;
        }

        /// <summary>
        /// 创建预配置的注册表
        /// </summary>
        public static ToolDecoratorRegistry CreateDefault(
            IServiceProvider? serviceProvider = null,
            TimeSpan? cacheExpiration = null,
            int maxRetries = 3,
            TimeSpan? retryDelay = null,
            TimeSpan? timeout = null)
        {
            var registry = new ToolDecoratorRegistry(serviceProvider);

            // 超时装饰器（最先应用，最外层）
            if (timeout.HasValue)
            {
                registry.Register(tool => new TimeoutToolDecorator(tool, timeout.Value));
            }

            // 重试装饰器
            if (maxRetries > 1)
            {
                registry.Register(tool => new RetryToolDecorator(
                    tool, maxRetries, retryDelay));
            }

            // 缓存装饰器（最后应用，最内层）
            if (serviceProvider != null)
            {
                var cache = serviceProvider.GetService(typeof(Microsoft.Extensions.Caching.Memory.IMemoryCache))
                    as Microsoft.Extensions.Caching.Memory.IMemoryCache;
                if (cache != null)
                {
                    registry.Register(tool => new CachedToolDecorator(
                        tool, cache, cacheExpiration ?? TimeSpan.FromMinutes(5)));
                }
            }

            return registry;
        }
    }
}