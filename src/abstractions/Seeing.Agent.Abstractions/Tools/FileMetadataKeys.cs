namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 文件工具（write/edit/delete）结果元数据预定义键。
/// <para>
/// 宿主桥接（如 WebUI 文件面板）据此映射文件变更事件；
/// 各宿主共享同一权威来源，禁止魔法字符串。
/// </para>
/// </summary>
public static class FileMetadataKeys
{
    /// <summary>变更文件的绝对路径（string）。</summary>
    public const string Path = "path";

    /// <summary>unified diff 文本（string，LCS + hunk header）。</summary>
    public const string Diff = "diff";

    /// <summary>变更前的文件内容（string）。仅当内容不超过 <see cref="DiffContentMaxBytes"/> 时提供。</summary>
    public const string OldContent = "oldContent";

    /// <summary>变更后的文件内容（string）。仅当内容不超过 <see cref="DiffContentMaxBytes"/> 时提供。</summary>
    public const string NewContent = "newContent";

    /// <summary>文件语言标识（string，供 diff 编辑器着色；可空）。</summary>
    public const string Language = "language";

    /// <summary>回写全文内容对的上限（字节）。超过时仅回写 <see cref="Diff"/>，前端回退纯文本展示。</summary>
    public const int DiffContentMaxBytes = 256 * 1024;
}
