namespace Seeing.Agent.Abstractions.Files;

/// <summary>磁盘变更事实（仅 OS watcher，无来源标注、无 diff）。</summary>
public sealed record FileChangeEvent(
    FileResourceId Id,
    FileChangeKind Kind,
    FileRevision? NewRevision,
    string? OldPath,
    DateTime Timestamp);
