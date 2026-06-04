using Microsoft.AspNetCore.Components;

namespace Seeing.Agent.WebUI.Components.Messaging;

/// <summary>
/// 消息组件接口 - 定义消息渲染组件的契约
/// </summary>
/// <typeparam name="TData">组件数据类型</typeparam>
public interface IMessageComponent<in TData> where TData : class
{
    /// <summary>
    /// 渲染组件
    /// </summary>
    /// <param name="data">组件数据</param>
    /// <returns>渲染片段</returns>
    RenderFragment Render(TData data);
}

/// <summary>
/// 消息组件基类 - 提供通用功能
/// </summary>
public abstract class MessageComponentBase
{
    /// <summary>
    /// Markdown 渲染管道（共享实例）
    /// </summary>
    protected static readonly Markdig.MarkdownPipeline MarkdownPipeline =
        new Markdig.MarkdownPipelineBuilder().Build();

    /// <summary>
    /// 渲染 Markdown 为 HTML
    /// </summary>
    protected static string RenderMarkdown(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;
        return Markdig.Markdown.ToHtml(content, MarkdownPipeline);
    }

    /// <summary>
    /// 截断文本
    /// </summary>
    protected static string TruncateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    /// <summary>
    /// 获取预览文本（第一行）
    /// </summary>
    protected static string GetPreviewText(string? text, int maxLength = 50)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var firstLine = text.Split('\n')[0].Trim();
        return TruncateText(firstLine, maxLength);
    }
}
