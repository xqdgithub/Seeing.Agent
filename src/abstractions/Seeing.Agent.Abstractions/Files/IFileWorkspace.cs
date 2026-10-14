namespace Seeing.Agent.Abstractions.Files;

/// <summary>会话级文件工作区 — 读写 + 变更订阅，路径受 IWorkspacePathGate 约束。</summary>
public interface IFileWorkspace
{
    /// <summary>所属会话 id。</summary>
    string SessionId { get; }

    /// <summary>有效根绝对路径规范化哈希；跨会话同根一致。</summary>
    string RootId { get; }

    /// <summary>有效根绝对路径。</summary>
    string RootPath { get; }

    /// <summary>把绝对/相对路径转为资源标识（不做存在性检查）。</summary>
    FileResourceId GetResourceId(string path);

    /// <summary>把资源标识转回绝对路径。</summary>
    string GetAbsolutePath(FileResourceId id);

    /// <summary>获取单条目元信息。</summary>
    Task<FileEntry> StatAsync(string path, CancellationToken ct = default);

    /// <summary>列出单层目录条目。</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>结构化读取；Display 截断展示、Full 完整用于编辑。</summary>
    Task<FileReadResult> ReadAsync(string path, FileReadMode mode = FileReadMode.Display, CancellationToken ct = default);

    /// <summary>全量写入；expectedRevision 非空时做乐观并发检查。</summary>
    Task<FileWriteResult> WriteAsync(string path, string content, FileRevision? expectedRevision = null, CancellationToken ct = default);

    /// <summary>字符串替换写入（语义同 EditTool）。</summary>
    Task<FileWriteResult> EditAsync(string path, string oldString, string newString, bool replaceAll = false, FileRevision? expectedRevision = null, CancellationToken ct = default);

    /// <summary>删除文件或目录。</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    /// <summary>按需生成统一行 diff（LCS + hunk header）：baselineContent → 当前磁盘。</summary>
    Task<string> GetDiffAsync(string path, string baselineContent, CancellationToken ct = default);

    /// <summary>订阅磁盘变更（仅 OS watcher）。</summary>
    IAsyncEnumerable<FileChangeEvent> SubscribeChangesAsync(CancellationToken ct = default);
}
