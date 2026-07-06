using System.Net;
using System.Text;
using Seeing.Gateway.Models;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Core;

/// <summary>
/// 将 <see cref="GatewayRequest.Input"/> 与可选 <see cref="GatewayQuoteContext"/> 合成为 Session user turn。
/// </summary>
internal static class GatewayUserMessageComposer
{
    public static SessionMessage? Compose(
        IReadOnlyList<GatewayContentPart>? input,
        GatewayQuoteContext? quote)
    {
        if (quote?.Content is not { Count: > 0 })
            return ConvertInputOnly(input);

        var quoteText = ExtractTextContent(quote.Content);
        var userText = ExtractTextContent(input);
        var quoteNonText = ConvertNonTextParts(quote.Content!);
        var userNonText = ConvertNonTextParts(input ?? []);

        SessionMessage message;
        if (quoteNonText.Count == 0 && userNonText.Count == 0)
        {
            message = SessionMessage.UserMessage(BuildXmlTextOnly(quote, quoteText, userText));
        }
        else
        {
            var parts = new List<SessionContentPart>
            {
                SessionContentPart.CreateText(
                    $"{BuildQuotedMessageOpen(quote)}{EscapeXml(quoteText)}</quoted_message>")
            };
            parts.AddRange(quoteNonText);
            parts.Add(SessionContentPart.CreateText(
                $"\n\n{BuildUserMessageOpen()}{EscapeXml(userText)}</user_message>"));
            parts.AddRange(userNonText);
            message = SessionMessage.UserMessageWithParts(parts);
        }

        message.Metadata = BuildQuoteMetadata(quote);
        return message;
    }

    private static SessionMessage? ConvertInputOnly(IReadOnlyList<GatewayContentPart>? input)
    {
        if (input == null || input.Count == 0)
            return null;

        var parts = new List<SessionContentPart>();

        foreach (var part in input)
        {
            switch (part)
            {
                case GatewayTextContentPart text:
                    parts.Add(SessionContentPart.CreateText(text.Text));
                    break;

                case GatewayImageContentPart image:
                    parts.Add(ConvertImagePart(image));
                    break;

                case GatewayFileContentPart file:
                    parts.Add(ConvertFilePart(file));
                    break;

                case GatewayAudioContentPart audio:
                    parts.Add(ConvertAudioPart(audio));
                    break;
            }
        }

        if (parts.Count == 0)
            return null;

        if (parts.Count == 1 && parts[0].Type == ContentPartType.Text && !string.IsNullOrEmpty(parts[0].Text))
            return SessionMessage.UserMessage(parts[0].Text!);

        return SessionMessage.UserMessageWithParts(parts);
    }

    private static string BuildXmlTextOnly(GatewayQuoteContext quote, string quoteText, string userText)
    {
        var builder = new StringBuilder();
        builder.Append(BuildQuotedMessageOpen(quote));
        builder.Append(EscapeXml(quoteText));
        builder.Append("</quoted_message>\n\n");
        builder.Append(BuildUserMessageOpen());
        builder.Append(EscapeXml(userText));
        builder.Append("</user_message>");
        return builder.ToString();
    }

    private static string BuildQuotedMessageOpen(GatewayQuoteContext quote)
    {
        var type = WebUtility.HtmlEncode(quote.MsgType ?? "text");
        var source = string.IsNullOrWhiteSpace(quote.SourceChannel)
            ? string.Empty
            : $" source=\"{WebUtility.HtmlEncode(quote.SourceChannel)}\"";
        return $"<quoted_message type=\"{type}\"{source}>\n";
    }

    private static string BuildUserMessageOpen() => "<user_message>\n";

    private static Dictionary<string, object> BuildQuoteMetadata(GatewayQuoteContext quote) =>
        new()
        {
            ["has_quote"] = true,
            ["quote_msgtype"] = quote.MsgType ?? "text",
            ["quote_source"] = quote.SourceChannel ?? string.Empty
        };

    private static string ExtractTextContent(IReadOnlyList<GatewayContentPart>? parts)
    {
        if (parts == null || parts.Count == 0)
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

    private static List<SessionContentPart> ConvertNonTextParts(IReadOnlyList<GatewayContentPart> parts)
    {
        var result = new List<SessionContentPart>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case GatewayImageContentPart image:
                    result.Add(ConvertImagePart(image));
                    break;

                case GatewayFileContentPart file:
                    result.Add(ConvertFilePart(file));
                    break;

                case GatewayAudioContentPart audio:
                    result.Add(ConvertAudioPart(audio));
                    break;
            }
        }

        return result;
    }

    private static SessionContentPart ConvertImagePart(GatewayImageContentPart image)
    {
        if (TryParseDataUrl(image.Url, out var base64, out var mime))
            return SessionContentPart.CreateImageFromBase64(base64, mime ?? image.MimeType ?? "image/png");

        return SessionContentPart.CreateImageFromUrl(image.Url);
    }

    private static SessionContentPart ConvertFilePart(GatewayFileContentPart file)
    {
        if (TryParseDataUrl(file.Url, out var base64, out var mime))
            return SessionContentPart.CreateFileFromBase64(
                base64,
                mime ?? file.MimeType ?? "application/octet-stream",
                file.Name);

        return new SessionContentPart
        {
            Type = ContentPartType.File,
            Url = file.Url,
            MimeType = file.MimeType,
            FileName = file.Name
        };
    }

    private static SessionContentPart ConvertAudioPart(GatewayAudioContentPart audio)
    {
        if (TryParseDataUrl(audio.Url, out var base64, out var mime))
            return SessionContentPart.CreateAudioFromBase64(base64, mime ?? audio.MimeType ?? "audio/wav");

        return new SessionContentPart
        {
            Type = ContentPartType.Audio,
            Url = audio.Url,
            MimeType = audio.MimeType
        };
    }

    private static bool TryParseDataUrl(string url, out string base64, out string? mimeType)
    {
        base64 = string.Empty;
        mimeType = null;

        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var separator = url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (separator < 0)
            return false;

        mimeType = url["data:".Length..separator];
        base64 = url[(separator + ";base64,".Length)..];
        return !string.IsNullOrEmpty(base64);
    }

    private static string EscapeXml(string text) =>
        WebUtility.HtmlEncode(text);
}
