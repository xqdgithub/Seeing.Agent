using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>
/// 文件存储接口 - ReMe 格式的 Markdown 文件存储
/// </summary>
public interface IFileStore
{
    // ===== 文件操作 =====
    
    /// <summary>写入文件</summary>
    Task<FileNode> WriteAsync(string path, string content, CancellationToken ct = default);
    
    /// <summary>读取文件</summary>
    Task<FileNode?> ReadAsync(string path, CancellationToken ct = default);
    
    /// <summary>删除文件</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);
    
    /// <summary>检查文件是否存在</summary>
    Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    
    // ===== 列表操作 =====
    
    /// <summary>列出所有文件</summary>
    Task<IReadOnlyList<FileNode>> ListAsync(string? pattern = null, CancellationToken ct = default);
    
    /// <summary>按前缀列出文件</summary>
    Task<IReadOnlyList<FileNode>> ListByPrefixAsync(string prefix, CancellationToken ct = default);
    
    // ===== 批量操作 =====
    
    /// <summary>批量写入</summary>
    Task WriteBatchAsync(IEnumerable<(string path, string content)> items, CancellationToken ct = default);
    
    // ===== 变更监听 =====
    
    /// <summary>文件变更事件流</summary>
    IObservable<FileChangeEventArgs> Changes { get; }
}
