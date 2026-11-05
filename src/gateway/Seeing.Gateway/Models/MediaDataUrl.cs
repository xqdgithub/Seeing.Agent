namespace Seeing.Gateway.Models;

/// <summary>
/// 媒体载荷的 data URL 编解码（wire 格式与 runtime 格式之间的唯一转换点）。
/// <para>
/// <b>背景</b>：<see cref="GatewayContentPart.Url"/> 的契约是「公开 https URL <b>或</b> data: URL」，
/// 而 runtime 侧（<c>ChatAttachment.Base64Data</c>）要求<b>纯 base64</b>。
/// 这两个表示的转换必须只有一处实现，否则会漂移成「data URL 被当成 base64 再拼一次前缀」的双前缀缺陷。
/// </para>
/// <para>纯函数、无状态，属协议层的格式转换。</para>
/// </summary>
public static class MediaDataUrl
{
    public const string Prefix = "data:";
    public const string Base64Marker = ";base64,";

    /// <summary>是否为 data URL（大小写不敏感）。</summary>
    public static bool IsDataUrl(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 拆解 data URL：成功时 <paramref name="base64"/> 为纯载荷（不含前缀），
    /// <paramref name="mimeType"/> 为 URL 自带的 MIME（可能为 null）。
    /// </summary>
    public static bool TryParse(string? value, out string base64, out string? mimeType)
    {
        base64 = string.Empty;
        mimeType = null;

        if (!IsDataUrl(value))
            return false;

        var marker = value!.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return false;

        var payload = value[(marker + Base64Marker.Length)..];
        if (payload.Length == 0)
            return false;

        var mediaType = value[Prefix.Length..marker];
        mimeType = mediaType.Length == 0 ? null : mediaType;

        base64 = payload;
        return true;
    }

    /// <summary>
    /// 归一化为「纯 base64」：data URL 剥前缀，普通字符串原样返回。
    /// <para>用于「值可能是 URL 也可能是 data URL」的线格式字段。</para>
    /// </summary>
    public static string ToBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return TryParse(value, out var base64, out _) ? base64 : value;
    }

    /// <summary>取 data URL 自带的 MIME；非 data URL 返回 null。</summary>
    public static string? TryGetMimeType(string? value) =>
        TryParse(value, out _, out var mimeType) ? mimeType : null;

    /// <summary>由纯 base64 + MIME 拼出 data URL（provider 出口专用）。</summary>
    public static string Compose(string mimeType, string base64) =>
        $"{Prefix}{mimeType}{Base64Marker}{base64}";
}
