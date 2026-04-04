using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 缓存装饰器 - 自动缓存工具执行结果
    /// </summary>
    public class CachedToolDecorator : ToolDecorator
    {
        private readonly IMemoryCache _cache;
        private readonly TimeSpan _expiration;
        private readonly ILogger? _logger;

        /// <summary>
        /// 创建缓存装饰器
        /// </summary>
        /// <param name="inner">被包装的工具</param>
        /// <param name="cache">内存缓存</param>
        /// <param name="expiration">缓存过期时间</param>
        /// <param name="logger">可选日志器</param>
        public CachedToolDecorator(
            ITool inner,
            IMemoryCache cache,
            TimeSpan? expiration = null,
            ILogger? logger = null) : base(inner)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _expiration = expiration ?? TimeSpan.FromMinutes(5);
            _logger = logger;
        }

        /// <inheritdoc />
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 计算缓存键
            var cacheKey = ComputeCacheKey(arguments);
            
            // 尝试从缓存获取
            if (_cache.TryGetValue<ToolResult>(cacheKey, out var cachedResult))
            {
                _logger?.LogDebug("[Cache] 命中缓存: ToolId={ToolId}", Id);
                return cachedResult!;
            }

            // 执行内部工具
            var result = await base.ExecuteAsync(arguments, context);

            // 只缓存成功的结果
            if (result.Success)
            {
                _cache.Set(cacheKey, result, _expiration);
                _logger?.LogDebug("[Cache] 已缓存: ToolId={ToolId}, Expiration={Expiration}", Id, _expiration);
            }

            return result;
        }

        /// <summary>
        /// 计算缓存键
        /// </summary>
        private string ComputeCacheKey(JsonElement arguments)
        {
            // 使用工具 ID + 参数哈希作为缓存键
            var args = new ToolArguments(arguments);
            return $"tool:{Id}:{args.ComputeHash()}";
        }

        /// <summary>
        /// 清除该工具的所有缓存
        /// </summary>
        public void ClearCache(IMemoryCache cache)
        {
            // MemoryCache 不支持批量清除，这里只是标记
            _logger?.LogDebug("[Cache] 请求清除缓存: ToolId={ToolId}", Id);
        }
    }
}