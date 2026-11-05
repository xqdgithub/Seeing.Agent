using Seeing.Agent.Abstractions.Models;
using Seeing.Gateway.Models;
using System.Net;
using System.Text;

namespace Seeing.Gateway.Mapping;

/// <summary>
/// 网关线格式 <see cref="GatewayContentPart"/> → runtime <see cref="ChatInput"/> 的<b>唯一</b>映射实现。
/// <para>
/// <b>为什么必须只有一份</b>：这段转换历史上被复制了四份
/// （<c>GatewayUserMessageComposer</c>、<c>GatewayOrchestratorV2.BuildChatInput</c>、
/// <c>GatewayOrchestratorV2.BuildChatInputWithQuote</c>、<c>InProcessSessionClient</c>），
/// 其中三份把 data URL 原样塞进要求纯 base64 的 <c>ChatAttachment.Base64Data</c>，
/// 导致 provider 出口二次拼接成 <c>data:image/png;base64,data:image/png;base64,...</c> 并返回 400。
/// </para>
/// <para>
/// <b>不变量</b>：本方法产出的每个 <see cref="ChatAttachment.Base64Data"/> 都是纯 base64
/// （data URL 的前缀在这里、且只在这里被剥掉），<see cref="ChatAttachment.MimeType"/> 始终非空。
/// </para>
/// </summary>
public static class GatewayContentPartMapper
{
    private const string DefaultImageMime = "image/png";
    private const string DefaultFileMime = "application/octet-stream";

    /// <summary>
    /// 把请求的 <paramref name="input"/> 与可选 <paramref name="quote"/> 合成为一次 user turn。
    /// 引用存在时，正文包成 <c>&lt;quoted_message&gt;</c> / <c>&lt;user_message&gt;</c> 信封。
    /// </summary>
    public static ChatInput ToChatInput(
        IReadOnlyList<GatewayContentPart>? input,
        GatewayQuoteContext? quote)
    {
        if (quote?.Content is not { Count: > 0 })
            return FromParts(input);

        var quoteText = ExtractText(quote.Content);
        var userText = ExtractText(input);
        var text = new StringBuilder()
            .Append(BuildQuotedMessageOpen(quote))
            .Append(EscapeXml(quoteText))
            .Append("</quoted_message>\n\n")
            .Append("<user_message>\n")
            .Append(EscapeXml(userText))
            .Append("</user_message>")
            .ToString();

        var attachments = new List<ChatAttachment>();
        attachments.AddRange(CollectAttachments(quote.Content));
        attachments.AddRange(CollectAttachments(input));

        return new ChatInput
        {
            Text = text,
            Attachments = attachments.Count == 0 ? null : attachments
        };
    }

    private static ChatInput FromParts(IReadOnlyList<GatewayContentPart>? input)
    {
        if (input is not { Count: > 0 })
            return new ChatInput();

        var text = ExtractText(input);
        var attachments = CollectAttachments(input);

        return new ChatInput
        {
            Text = string.IsNullOrEmpty(text) ? null : text,
            Attachments = attachments.Count == 0 ? null : attachments
        };
    }

    private static string ExtractText(IReadOnlyList<GatewayContentPart>? parts)
    {
        if (parts is not { Count: > 0 })
            return string.Empty;

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is GatewayTextContentPart text && !string.IsNullOrWhiteSpace(text.Text))
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(text.Text.Trim());
            }
        }

        return builder.ToString();
    }

    private static List<ChatAttachment> CollectAttachments(IReadOnlyList<GatewayContentPart>? parts)
    {
        var attachments = new List<ChatAttachment>();
        if (parts is not { Count: > 0 })
            return attachments;

        foreach (var part in parts)
        {
            switch (part)
            {
                case GatewayImageContentPart image:
                    attachments.Add(ToAttachment(
                        image.Url, image.MimeType, DefaultImageMime, fileName: string.Empty));
                    break;

                case GatewayFileContentPart file:
                    // 视频等无专用 part 的媒体走 File，保留原始文件名
                    attachments.Add(ToAttachment(
                        file.Url, file.MimeType, DefaultFileMime, file.Name ?? string.Empty));
                    break;

                case GatewayAudioContentPart audio:
                    attachments.Add(ToAttachment(
                        audio.Url, audio.MimeType, "audio/wav", string.Empty));
                    break;
            }
        }

        return attachments;
    }

    /// <summary>
    /// 单个媒体段 → 附件：**在这里剥掉 data URL 前缀**，保证 <see cref="ChatAttachment.Base64Data"/> 是纯 base64。
    /// <para>
    /// MIME 优先级：段上显式声明 → data URL 自带 → 该类型的兜底值。
    /// https URL 原样保留（当前 runtime 无 URL 形态附件字段，属已知边界）。
    /// </para>
    /// </summary>
    private static ChatAttachment ToAttachment(
        string url,
        string? declaredMime,
        string fallbackMime,
        string fileName)
    {
        var isDataUrl = MediaDataUrl.TryParse(url, out var base64, out var urlMime);

        return new ChatAttachment
        {
            Base64Data = isDataUrl ? base64 : url,
            MimeType = string.IsNullOrWhiteSpace(declaredMime)
                ? urlMime ?? fallbackMime
                : declaredMime,
            FileName = fileName
        };
    }

    private static string BuildQuotedMessageOpen(GatewayQuoteContext quote)
    {
        var type = WebUtility.HtmlEncode(quote.MsgType ?? "text");
        var source = string.IsNullOrWhiteSpace(quote.SourceChannel)
            ? string.Empty
            : $" source=\"{WebUtility.HtmlEncode(quote.SourceChannel)}\"";
        return $"<quoted_message type=\"{type}\"{source}>\n";
    }

    private static string EscapeXml(string text) => WebUtility.HtmlEncode(text);
}
