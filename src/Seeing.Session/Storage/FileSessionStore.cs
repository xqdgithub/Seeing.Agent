using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Seeing.Session.Core;

namespace Seeing.Session.Storage
{
    /// <summary>
    /// 基于文件系统的会话存储实现
    /// 使用 JSON 文件存储会话数据，兼容 WebUI 的 ~/.seeing/sessions/*.json 格式
    /// </summary>
    public class FileSessionStore : ISessionStore
    {
        private readonly string _baseDirectory;
        private readonly ILogger<FileSessionStore>? _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        
        // 文件锁超时时间
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
        
        // 文件锁字典，用于跨进程文件锁定
        private static readonly Dictionary<string, SemaphoreSlim> _fileLocks = new();
        private static readonly object _lockDictLock = new();

        /// <summary>
        /// 创建 FileSessionStore 实例
        /// </summary>
        /// <param name="baseDirectory">基础目录路径，默认为 ~/.seeing/sessions</param>
        /// <param name="logger">日志记录器</param>
        public FileSessionStore(string? baseDirectory = null, ILogger<FileSessionStore>? logger = null)
        {
            _logger = logger;
            _baseDirectory = baseDirectory ?? GetDefaultSessionDirectory();
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            
            EnsureDirectoryExists();
        }

        /// <summary>
        /// 获取默认会话目录路径 (~/.seeing/sessions)
        /// </summary>
        private static string GetDefaultSessionDirectory()
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".seeing", "sessions");
        }

        /// <summary>
        /// 确保目录存在
        /// </summary>
        private void EnsureDirectoryExists()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
                _logger?.LogInformation("创建会话存储目录: {Directory}", _baseDirectory);
            }
        }

        /// <summary>
        /// 获取会话文件路径
        /// </summary>
        private string GetSessionFilePath(string sessionId)
        {
            ValidateSessionId(sessionId);
            return Path.Combine(_baseDirectory, $"{sessionId}.json");
        }

        /// <summary>
        /// 验证会话ID，防止路径遍历攻击
        /// </summary>
        private static void ValidateSessionId(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new ArgumentException("会话ID不能为空", nameof(sessionId));
            }
            
            // 检查路径遍历攻击（防止访问上级目录）
            if (sessionId.Contains("..") || sessionId.Contains("/") || sessionId.Contains("\\"))
            {
                throw new ArgumentException("会话ID包含非法路径字符", nameof(sessionId));
            }
            
            // 检查非法文件名字符
            var invalidChars = Path.GetInvalidFileNameChars();
            if (sessionId.IndexOfAny(invalidChars) >= 0)
            {
                throw new ArgumentException("会话ID包含非法字符", nameof(sessionId));
            }
        }

        /// <summary>
        /// 获取文件锁
        /// </summary>
        private SemaphoreSlim GetFileLock(string filePath)
        {
            lock (_lockDictLock)
            {
                if (!_fileLocks.TryGetValue(filePath, out var fileLock))
                {
                    fileLock = new SemaphoreSlim(1, 1);
                    _fileLocks[filePath] = fileLock;
                }
                return fileLock;
            }
        }

        /// <summary>
        /// 保存单个会话
        /// </summary>
        public async Task SaveAsync(SessionData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            ValidateSessionId(data.Id);
            
            var filePath = GetSessionFilePath(data.Id);
            var fileLock = GetFileLock(filePath);

            if (!await fileLock.WaitAsync(LockTimeout))
            {
                _logger?.LogWarning("获取文件锁超时: {SessionId}", data.Id);
                throw new TimeoutException("获取文件锁超时，请稍后重试");
            }

            try
            {
                // 更新时间戳
                data.UpdatedAt = DateTime.Now;
                if (data.CreatedAt == default)
                {
                    data.CreatedAt = DateTime.Now;
                }

                var json = JsonSerializer.Serialize(data, _jsonOptions);
                
                // 使用临时文件 + 原子替换确保写入完整性
                var tempPath = filePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8);
                
                // 原子替换
                File.Move(tempPath, filePath, overwrite: true);
                
                _logger?.LogDebug("保存会话成功: {SessionId}", data.Id);
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <summary>
        /// 加载单个会话
        /// </summary>
        public async Task<SessionData?> LoadAsync(string sessionId)
        {
            ValidateSessionId(sessionId);
            
            var filePath = GetSessionFilePath(sessionId);
            
            if (!File.Exists(filePath))
            {
                _logger?.LogDebug("会话文件不存在: {FilePath}", filePath);
                return null;
            }

            var fileLock = GetFileLock(filePath);

            if (!await fileLock.WaitAsync(LockTimeout))
            {
                _logger?.LogWarning("获取文件锁超时: {SessionId}", sessionId);
                throw new TimeoutException("获取文件锁超时，请稍后重试");
            }

            try
            {
                return await ReadSessionFileAsync(filePath, sessionId);
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <summary>
        /// 读取会话文件（内部方法）
        /// </summary>
        private async Task<SessionData?> ReadSessionFileAsync(string filePath, string sessionId)
        {
            try
            {
                // 使用 FileStream 读取，支持文件共享读取
                using var stream = new FileStream(
                    filePath, 
                    FileMode.Open, 
                    FileAccess.Read, 
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync();
                
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger?.LogWarning("会话文件为空: {SessionId}", sessionId);
                    throw new SessionLoadException(
                        sessionId, 
                        filePath, 
                        "会话文件为空");
                }

                var data = JsonSerializer.Deserialize<SessionData>(json, _jsonOptions);
                
                if (data == null)
                {
                    _logger?.LogWarning("会话数据反序列化失败: {SessionId}", sessionId);
                    throw new SessionLoadException(
                        sessionId, 
                        filePath, 
                        "会话数据反序列化失败");
                }

                return data;
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "会话文件格式错误: {SessionId}", sessionId);
                throw new SessionLoadException(
                    sessionId, 
                    filePath, 
                    "会话文件格式错误，可能已损坏", 
                    ex);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "读取会话文件失败: {SessionId}", sessionId);
                throw new SessionLoadException(
                    sessionId, 
                    filePath, 
                    "读取会话文件失败", 
                    ex);
            }
        }

        /// <summary>
        /// 删除会话
        /// </summary>
        public async Task DeleteAsync(string sessionId)
        {
            ValidateSessionId(sessionId);
            
            var filePath = GetSessionFilePath(sessionId);
            
            if (!File.Exists(filePath))
            {
                _logger?.LogDebug("会话文件不存在，无需删除: {FilePath}", filePath);
                return;
            }

            var fileLock = GetFileLock(filePath);

            if (!await fileLock.WaitAsync(LockTimeout))
            {
                _logger?.LogWarning("获取文件锁超时: {SessionId}", sessionId);
                throw new TimeoutException("获取文件锁超时，请稍后重试");
            }

            try
            {
                File.Delete(filePath);
                _logger?.LogDebug("删除会话成功: {SessionId}", sessionId);
            }
            finally
            {
                fileLock.Release();
            }
        }

        /// <summary>
        /// 列出所有会话
        /// </summary>
        public async Task<IAsyncEnumerable<SessionData>> ListAsync()
        {
            return await Task.FromResult(EnumerateSessions());
        }

        /// <summary>
        /// 枚举所有会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessions([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_baseDirectory))
            {
                yield break;
            }

            foreach (var filePath in Directory.GetFiles(_baseDirectory, "*.json"))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                SessionData? data = null;
                
                try
                {
                    var fileLock = GetFileLock(filePath);
                    if (await fileLock.WaitAsync(1000, cancellationToken))
                    {
                        try
                        {
                            data = await ReadSessionFileAsync(filePath, fileName);
                        }
                        finally
                        {
                            fileLock.Release();
                        }
                    }
                }
                catch (SessionLoadException ex)
                {
                    _logger?.LogWarning(ex, "跳过损坏的会话文件: {FilePath}", filePath);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "读取会话文件失败: {FilePath}", filePath);
                    continue;
                }

                if (data != null)
                {
                    yield return data;
                }
            }
        }

        /// <summary>
        /// 按分区和代理查询会话
        /// </summary>
        public async Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId)
        {
            return await Task.FromResult(EnumerateSessionsFiltered(partitionId, agentId));
        }

        /// <summary>
        /// 枚举过滤后的会话
        /// </summary>
        private async IAsyncEnumerable<SessionData> EnumerateSessionsFiltered(
            string partitionId, 
            string agentId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var session in EnumerateSessions(cancellationToken))
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
        public async Task<IAsyncEnumerable<SessionData>> LoadAllAsync()
        {
            return await ListAsync();
        }
    }
}