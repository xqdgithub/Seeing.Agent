namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 元数据存储接口 - 线程安全的 Key-Value 存储
    /// </summary>
    public interface IMetadataStore
    {
        /// <summary>设置元数据值</summary>
        void Set(string key, object value);

        /// <summary>获取元数据值</summary>
        T? Get<T>(string key);

        /// <summary>尝试获取元数据值</summary>
        bool TryGet<T>(string key, out T? value);

        /// <summary>移除元数据</summary>
        bool Remove(string key);

        /// <summary>检查键是否存在</summary>
        bool ContainsKey(string key);

        /// <summary>获取所有元数据快照</summary>
        IReadOnlyDictionary<string, object> GetAll();

        /// <summary>清空所有元数据</summary>
        void Clear();
    }
}