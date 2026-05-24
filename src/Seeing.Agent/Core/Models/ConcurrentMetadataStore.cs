using Seeing.Agent.Core.Interfaces;
using System.Collections.Concurrent;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// 线程安全的元数据存储实现
    /// </summary>
    public class ConcurrentMetadataStore : IMetadataStore
    {
        private readonly ConcurrentDictionary<string, object> _data = new();

        /// <inheritdoc />
        public void Set(string key, object value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            _data[key] = value;
        }

        /// <inheritdoc />
        public T? Get<T>(string key)
        {
            if (TryGet<T>(key, out var value))
                return value;
            return default;
        }

        /// <inheritdoc />
        public bool TryGet<T>(string key, out T? value)
        {
            if (string.IsNullOrEmpty(key))
            {
                value = default;
                return false;
            }

            if (_data.TryGetValue(key, out var obj))
            {
                if (obj is T typedValue)
                {
                    value = typedValue;
                    return true;
                }

                // 尝试转换
                try
                {
                    value = (T)Convert.ChangeType(obj, typeof(T));
                    return true;
                }
                catch
                {
                    value = default;
                    return false;
                }
            }

            value = default;
            return false;
        }

        /// <inheritdoc />
        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;

            return _data.TryRemove(key, out _);
        }

        /// <inheritdoc />
        public bool ContainsKey(string key)
        {
            return !string.IsNullOrEmpty(key) && _data.ContainsKey(key);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object> GetAll()
        {
            return new Dictionary<string, object>(_data);
        }

        /// <inheritdoc />
        public void Clear()
        {
            _data.Clear();
        }

        /// <summary>
        /// 获取存储的项数
        /// </summary>
        public int Count => _data.Count;
    }
}