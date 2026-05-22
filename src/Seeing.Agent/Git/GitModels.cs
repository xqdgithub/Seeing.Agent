using System;
using System.Collections.Generic;

namespace Seeing.Agent.Git
{
    /// <summary>Git 状态</summary>
    public class GitStatus
    {
        public string Branch { get; set; } = string.Empty;
        public bool IsClean { get; set; }
        public bool HasUntrackedFiles { get; set; }
        public bool HasStagedChanges { get; set; }
        public bool HasUnstagedChanges { get; set; }
        public List<GitFileStatus> Files { get; set; } = new();
        public string? Ahead { get; set; }
        public string? Behind { get; set; }
    }

    /// <summary>文件状态</summary>
    public class GitFileStatus
    {
        public string Path { get; set; } = string.Empty;
        public GitFileState State { get; set; }
        public bool IsStaged { get; set; }
    }

    /// <summary>文件状态枚举</summary>
    public enum GitFileState
    {
        Unmodified,
        Modified,
        Added,
        Deleted,
        Renamed,
        Copied,
        Untracked,
        Ignored
    }

    /// <summary>Git 差异</summary>
    public class GitDiff
    {
        public List<GitDiffFile> Files { get; set; } = new();
        public int TotalAddedLines { get; set; }
        public int TotalDeletedLines { get; set; }
        public string RawDiff { get; set; } = string.Empty;
    }

    /// <summary>文件差异</summary>
    public class GitDiffFile
    {
        public string Path { get; set; } = string.Empty;
        public string? OldPath { get; set; }
        public List<GitDiffHunk> Hunks { get; set; } = new();
        public int AddedLines { get; set; }
        public int DeletedLines { get; set; }
    }

    /// <summary>差异块</summary>
    public class GitDiffHunk
    {
        public int OldStartLine { get; set; }
        public int OldLineCount { get; set; }
        public int NewStartLine { get; set; }
        public int NewLineCount { get; set; }
        public string Header { get; set; } = string.Empty;
        public List<GitDiffLine> Lines { get; set; } = new();
    }

    /// <summary>差异行</summary>
    public class GitDiffLine
    {
        public GitDiffLineType Type { get; set; }
        public string Content { get; set; } = string.Empty;
        public int? OldLineNumber { get; set; }
        public int? NewLineNumber { get; set; }
    }

    /// <summary>差异行类型</summary>
    public enum GitDiffLineType
    {
        Context,
        Added,
        Deleted
    }

    /// <summary>Git 提交</summary>
    public class GitCommit
    {
        public string Hash { get; set; } = string.Empty;
        public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;
        public string Message { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string AuthorEmail { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; }
        public List<string> Parents { get; set; } = new();
    }

    /// <summary>Git 分支</summary>
    public class GitBranch
    {
        public string Name { get; set; } = string.Empty;
        public bool IsCurrent { get; set; }
        public bool IsRemote { get; set; }
        public string? Upstream { get; set; }
        public int? Ahead { get; set; }
        public int? Behind { get; set; }
        public DateTimeOffset? LastCommit { get; set; }
    }

    /// <summary>Git 配置</summary>
    public class GitOptions
    {
        public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;
        public string GitPath { get; set; } = "git";
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool AutoFetch { get; set; }
    }
}
