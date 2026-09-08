using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Helpers;
using System.Text.Json;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 缓存装饰器 - 按工具能力声明自动缓存工具执行结果
    /// </summary>
    /// <remarks>
    /// 缓存策略由工具通过 <see cref="IToolCapabilities.Capabilities"/> 声明：
    /// <list type="bullet">
    /// <item><see cref="ToolCapabilityKeys.CacheEnabled"/> = "true" 才缓存（默认不缓存，副作用工具天然安全）</item>
    /// <item><see cref="ToolCapabilityKeys.CacheScope"/> = "session"(默认) 键含 SessionId，"global" 不含</item>
    /// <item><see cref="ToolCapabilityKeys.CacheTtl"/> 毫秒值覆盖默认过期时间</item>
    /// </list>
    /// </remarks>
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
            // 能力声明：cache.enabled != "true" → 绕过缓存直接执行（默认不缓存）
            if (!ToolCapabilityReader.GetBool(this, ToolCapabilityKeys.CacheEnabled))
            {
                return await base.ExecuteAsync(arguments, context);
            }

            var scope = ToolCapabilityReader.GetCacheScope(this);
            var ttl = ToolCapabilityReader.GetDurationMs(this, ToolCapabilityKeys.CacheTtl) ?? _expiration;

            // 计算缓存键（scope=session 时含 SessionId，避免跨会话串缓存）
            var cacheKey = ComputeCacheKey(arguments, context.SessionId, scope);

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
                _cache.Set(cacheKey, result, ttl);
                _logger?.LogDebug("[Cache] 已缓存: ToolId={ToolId}, Ttl={Ttl}", Id, ttl);
            }

            return result;
        }

        /// <summary>
        /// 计算缓存键：tool:{Id}:{scope}:{sessionId}:{参数哈希}。scope=global 时省略 sessionId。
        /// </summary>
        private string ComputeCacheKey(JsonElement arguments, string sessionId, string scope)
        {
            var args = new ToolArguments(arguments);
            var hash = args.ComputeHash();
            return string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase)
                ? $"tool:{Id}:{scope}::{hash}"
                : $"tool:{Id}:{scope}:{sessionId}:{hash}";
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
