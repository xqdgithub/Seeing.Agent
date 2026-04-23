using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Seeing.Agent.Memory.Core
{
    // MemoryEntry type is defined elsewhere in the project; reuse it here.

    // Heat-memory cache for recently accessed memories
    public class MemoryCache
    {
        private readonly global::Microsoft.Extensions.Caching.Memory.MemoryCache _cache;
        private readonly TimeSpan _defaultExpiration;
        private readonly ConcurrentDictionary<string, byte> _keys = new ConcurrentDictionary<string, byte>();

        // Configure cache expiration via constructor
        public MemoryCache(TimeSpan defaultExpiration)
        {
            _defaultExpiration = defaultExpiration;
            _cache = new global::Microsoft.Extensions.Caching.Memory.MemoryCache(new MemoryCacheOptions());
        }

        // Convenience ctor with 5 minutes default
        public MemoryCache() : this(TimeSpan.FromMinutes(5))
        {
        }

        // Get a memory entry by id
        public MemoryEntry? Get(string id)
        {
            if (id == null) return null;
            if (_cache.TryGetValue(id, out object value) && value is MemoryEntry me)
            {
                return me;
            }
            return null;
        }

        // Set a memory entry with configured expiration
        public void Set(string id, MemoryEntry memory)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _defaultExpiration
            };
            _cache.Set(id, memory, options);
            _keys.TryAdd(id, 0);
        }

        // Remove a memory entry
        public void Remove(string id)
        {
            if (id == null) return;
            _cache.Remove(id);
            _keys.TryRemove(id, out _);
        }

        // Clear all cached entries
        public void Clear()
        {
            foreach (var key in _keys.Keys)
            {
                _cache.Remove(key);
            }
            _keys.Clear();
        }
    }
}
