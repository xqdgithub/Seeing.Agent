namespace Seeing.Session.Storage
{
    /// <summary>
    /// 可重定位的会话存储：支持运行时切换基础目录（用于工作区切换）
    /// </summary>
    public interface IRelocatableSessionStore : ISessionStore
    {
        /// <summary>当前会话存储基础目录（可空：内存实现可能无目录）</summary>
        string? BaseDirectory { get; }

        /// <summary>切换基础目录（切换后读写操作指向新目录）</summary>
        void SetBaseDirectory(string baseDirectory);
    }
}