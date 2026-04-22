using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 基于内存的会话存储实现
    /// 使用 ConcurrentDictionary 实现线程安全的内存存储，适用于测试和临时场景
    /// </summary>
    public class InMemorySessionStore : ISessionStore
    {
        private readonly ConcurrentDictionary<string, SessionData> _sessions = new();

        /// <summary>
        /// 保存单个会话
        /// </summary>
        public Task SaveAsync(SessionData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (string.IsNullOrWhiteSpace(data.Id))
            {
                throw new ArgumentException("会话ID不能为空", nameof(data.Id));
            }

            // 更新时间戳
            data.UpdatedAt = DateTime.Now;
            if (data.CreatedAt == default)
            {
                data.CreatedAt = DateTime.Now;
            }

            _sessions[data.Id] = data;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 加载单个会话
        /// </summary>
        public Task<SessionData?> LoadAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));
            }

            _sessions.TryGetValue(sessionId, out var data);
            return Task.FromResult(data);
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        public Task DeleteAsync(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));
            }

            _sessions.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 列出所有会话
        /// </summary>
        public Task<IAsyncEnumerable<SessionData>> ListAsync()
        {
            return Task.FromResult(EnumerateSessions());
        }

        /// <summary>
        /// 枚举所有会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessions()
        {
            foreach (var session in _sessions.Values)
            {
                yield return session;
            }
        }

        /// <summary>
        /// 按分区和代理查询会话
        /// </summary>
        public Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId)
        {
            return Task.FromResult(EnumerateSessionsFiltered(partitionId, agentId));
        }

        /// <summary>
        /// 枚举过滤后的会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessionsFiltered(
            string partitionId, 
            string agentId)
        {
            await foreach (var session in EnumerateSessions())
            {
                var matchPartition = string.IsNullOrEmpty(partitionId) || 
                                     session.PartitionId == partitionId;
                
                var matchAgent = string.IsNullOrEmpty(agentId) || 
                                 (session.Agent?.AgentId == agentId);
                
                if (matchPartition && matchAgent)
                {
                    yield return session;
                }
            }
        }

        /// <summary>
        /// 批量保存会话
        /// </summary>
        public async Task SaveAllAsync(IEnumerable<SessionData> data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            foreach (var session in data)
            {
                await SaveAsync(session);
            }
        }

        /// <summary>
        /// 批量加载会话
        /// </summary>
        public Task<IAsyncEnumerable<SessionData>> LoadAllAsync()
        {
            return ListAsync();
        }

        /// <summary>
        /// 清空所有会话（用于测试）
        /// </summary>
        public void Clear()
        {
            _sessions.Clear();
        }

        /// <summary>
        /// 获取会话数量（用于测试）
        /// </summary>
        public int Count => _sessions.Count;
    }
}