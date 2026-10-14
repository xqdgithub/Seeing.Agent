namespace Seeing.Agent.Abstractions.Files;

/// <summary>读取模式：Display=截断用于展示；Full=完整用于编辑。</summary>
public enum FileReadMode { Display, Full }

/// <summary>文件内容分类。</summary>
public enum FileContentKind { Text, Markdown, Image, Binary }

/// <summary>磁盘变更种类。</summary>
public enum FileChangeKind { Created, Modified, Deleted, Renamed }
