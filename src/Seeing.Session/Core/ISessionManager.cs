using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Seeing.Session.Core
{
    /// <summary>
    /// Session 管理器接口 - 管理会话的创建、存储和检索
    /// </summary>
    /// <remarks>
    /// 新架构设计：
    /// - 使用 SessionData 作为核心数据结构
    /// - 所有方法使用异步模式
    /// - 支持持久化和压缩扩展
    /// </remarks>
    public interface ISessionManager
    {
        /// <summary>创建新会话</summary>
        SessionData Create(string? partitionId = null, string? selectedAgent = null);

        /// <summary>获取会话</summary>
        SessionData? Get(string id);

        /// <summary>删除会话</summary>
        bool Delete(string id);

        /// <summary>注册现有会话到缓存</summary>
        void Register(SessionData session);

        /// <summary>列出所有会话</summary>
        IReadOnlyList<SessionData> List();

        /// <summary>保存会话到存储</summary>
        Task SaveAsync(string id);

        /// <summary>从存储加载会话</summary>
        Task<SessionData?> LoadAsync(string id);

        /// <summary>压缩会话消息</summary>
        IReadOnlyList<SessionMessage> Compress(string id);
    }
}