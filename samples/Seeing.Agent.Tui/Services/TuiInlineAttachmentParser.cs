using Seeing.Agent.Tools.Support;

namespace Seeing.Agent.Tui.Services;

/// <summary>`@path` 解析结果。</summary>
public sealed record TuiInlineAttachmentPlan(
    string RemainingText,
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> PromptPaths);

/// <summary>路径列表分类结果。</summary>
public sealed record TuiPathAttachmentPlan(
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> PromptPaths);

/// <summary>`@path` / `/attach` 的统一分类：图片进附件，非图片进路径。</summary>
public static class TuiInlineAttachmentParser
{
    /// <summary>解析输入首部的 `@path` 并分类。</summary>
    public static TuiInlineAttachmentPlan ParseInline(
        TuiAttachmentResolver resolver,
        string text,
        string projectRoot)
    {
        if (!resolver.TryParseInline(text, out var remaining, out var paths))
            return new TuiInlineAttachmentPlan(text, [], []);

        var classified = Classify(resolver, paths, projectRoot);
        return new TuiInlineAttachmentPlan(remaining, classified.ImagePaths, classified.PromptPaths);
    }

    /// <summary>把路径列表分为图片与普通文件路径。</summary>
    public static TuiPathAttachmentPlan Classify(
        TuiAttachmentResolver resolver,
        IEnumerable<string> paths,
        string projectRoot)
    {
        _ = resolver;
        var images = new List<string>();
        var promptPaths = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (TuiAttachmentResolver.IsImagePath(path))
                images.Add(path);
            else
                promptPaths.Add(PastedFileLayout.ToPromptPath(projectRoot, Path.GetFullPath(path)));
        }

        return new TuiPathAttachmentPlan(images, promptPaths);
    }
}
