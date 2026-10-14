using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Support;

/// <summary>
/// 文件变更元数据构建（write/edit 工具共用）：统一回写 path/diff/oldContent/newContent。
/// </summary>
public static class FileDiffMetadata
{
    /// <summary>
    /// 构建文件变更元数据。旧/新内容均在 <see cref="FileMetadataKeys.DiffContentMaxBytes"/> 以内时，
    /// 额外回写全文内容对（供宿主用 diff 编辑器并排/内联展示）；否则仅回写 unified diff 文本。
    /// </summary>
    public static Dictionary<string, object> Build(string filePath, string oldContent, string newContent, string diff)
    {
        var metadata = new Dictionary<string, object>
        {
            [FileMetadataKeys.Path] = filePath,
            [FileMetadataKeys.Diff] = diff,
        };

        if (oldContent.Length <= FileMetadataKeys.DiffContentMaxBytes
            && newContent.Length <= FileMetadataKeys.DiffContentMaxBytes)
        {
            metadata[FileMetadataKeys.OldContent] = oldContent;
            metadata[FileMetadataKeys.NewContent] = newContent;
        }

        return metadata;
    }
}
