using Seeing.Agent.Abstractions.Models;
using Seeing.Agent.Tools.Support;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>提交路由结果：正文 + 图片附件。</summary>
public sealed record PromptAttachmentRoute(string Text, List<ChatAttachment> Attachments);

/// <summary>把 UI 附件列表分流为图片附件与正文路径。</summary>
public static class PromptAttachmentRouter
{
    /// <summary>图片进 ChatAttachment；路径引用拼进正文。</summary>
    public static PromptAttachmentRoute Route(string text, IReadOnlyList<AttachmentViewModel> attachments)
    {
        var images = new List<ChatAttachment>();
        var paths = new List<string>();

        foreach (var attachment in attachments)
        {
            if (attachment.Status != AttachmentUploadStatus.Completed)
                continue;

            if (attachment.IsPathReference)
            {
                if (!string.IsNullOrWhiteSpace(attachment.PromptPath))
                    paths.Add(attachment.PromptPath!);
                continue;
            }

            if (string.IsNullOrWhiteSpace(attachment.Base64Content))
                continue;

            images.Add(new ChatAttachment
            {
                FileName = attachment.FileName,
                MimeType = attachment.FileType,
                Base64Data = StripDataPrefix(attachment.Base64Content)
            });
        }

        return new PromptAttachmentRoute(
            PastedFileLayout.AppendPromptPaths(text, paths),
            images);
    }

    private static string StripDataPrefix(string base64Content)
    {
        if (!base64Content.StartsWith("data:", StringComparison.Ordinal))
            return base64Content;

        var marker = base64Content.IndexOf(";base64,", StringComparison.Ordinal);
        return marker >= 0 ? base64Content[(marker + 8)..] : base64Content;
    }
}
