using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Permission
{
    /// <summary>
    /// 权限缓存条目
    /// </summary>
    internal class PermissionCacheEntry
    {
        public PermissionAction Action { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    /// <summary>
    /// 权限缓存选项
    /// </summary>
    public class PermissionCacheOptions
    {
        /// <summary>缓存 TTL（默认 5 分钟）</summary>
        public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// 权限缓存 - 提供 TTL 缓存和线程安全访问
    /// 注意：此服务为 Singleton，不直接依赖 scoped 服务
    /// </summary>
    public class PermissionCache : IPermissionCache
    {
        private readonly ConcurrentDictionary<PermissionCacheKey, PermissionCacheEntry> _cache = new();
        private readonly ILogger<PermissionCache>? _logger;
        private readonly PermissionCacheOptions _options;

        /// <summary>
        /// 创建权限缓存实例
        /// </summary>
        public PermissionCache(
            PermissionCacheOptions? options = null,
            ILogger<PermissionCache>? logger = null)
        {
            _options = options ?? new PermissionCacheOptions();
            _logger = logger;
        }

        /// <summary>
        /// 获取缓存的权限决策
        /// 注意：此方法不再自动评估权限，仅返回缓存结果
        /// 如需评估，请使用 IPermissionService.EvaluateAsync
        /// </summary>
        public PermissionAction Get(PermissionCacheKey key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.Now)
                {
                    _logger?.LogDebug("权限缓存命中: {Key}", key);
                    return entry.Action;
                }

                // TTL 过期，移除缓存
                _cache.TryRemove(key, out _);
                _logger?.LogDebug("权限缓存过期: {Key}", key);
            }

            // 缓存未命中，返回默认值 Deny
            // 调用方应通过 IPermissionService 进行权限评估
            _logger?.LogDebug("权限缓存未命中: {Key}, 返回 Deny", key);
            return PermissionAction.Deny;
        }

        /// <summary>
        /// 尝试获取缓存的权限决策（不触发评估）
        /// </summary>
        public bool TryGet(PermissionCacheKey key, out PermissionAction action)
        {
            action = PermissionAction.Deny;

            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.Now)
                {
                    _logger?.LogDebug("权限缓存命中（TryGet）: {Key}", key);
                    action = entry.Action;
                    return true;
                }

                // TTL 过期，移除缓存
                _cache.TryRemove(key, out _);
                _logger?.LogDebug("权限缓存过期（TryGet）: {Key}", key);
            }

            return false;
        }

        /// <summary>
        /// 设置缓存条目
        /// </summary>
        public void Set(PermissionCacheKey key, PermissionAction action, TimeSpan? ttl = null)
        {
            var effectiveTtl = ttl ?? _options.Ttl;
            var entry = new PermissionCacheEntry
            {
                Action = action,
                ExpiresAt = DateTimeOffset.Now.Add(effectiveTtl)
            };

            _cache[key] = entry;
            _logger?.LogDebug("权限缓存设置: {Key} = {Action}, TTL: {Ttl}", key, action, effectiveTtl);
        }

        /// <summary>
        /// 使指定键的缓存失效
        /// </summary>
        public void Invalidate(PermissionCacheKey key)
        {
            _cache.TryRemove(key, out _);
            _logger?.LogDebug("权限缓存失效: {Key}", key);
        }

        /// <summary>
        /// 使所有包含指定权限的缓存失效
        /// </summary>
        public void InvalidateByPermission(string permission)
        {
            var keysToRemove = _cache.Keys
                .Where(k => k.Permission == permission)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }

            _logger?.LogDebug("权限缓存按权限失效: {Permission}, 移除 {Count} 条", permission, keysToRemove.Count);
        }

        /// <summary>
        /// 使所有包含指定 Agent 的缓存失效
        /// </summary>
        public void InvalidateByAgent(string agentName)
        {
            var keysToRemove = _cache.Keys
                .Where(k => k.AgentName == agentName)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }

            _logger?.LogDebug("权限缓存按 Agent 失效: {AgentName}, 移除 {Count} 条", agentName, keysToRemove.Count);
        }

        /// <summary>
        /// 清空所有缓存
        /// </summary>
        public void Clear()
        {
            var count = _cache.Count;
            _cache.Clear();
            _logger?.LogDebug("权限缓存清空: 移除 {Count} 条", count);
        }

        /// <summary>
        /// 获取缓存统计信息
        /// </summary>
        public (int TotalEntries, int ExpiredEntries) GetStats()
        {
            var now = DateTimeOffset.Now;
            var total = _cache.Count;
            var expired = _cache.Values.Count(e => e.ExpiresAt <= now);
            return (total, expired);
        }
    }
}