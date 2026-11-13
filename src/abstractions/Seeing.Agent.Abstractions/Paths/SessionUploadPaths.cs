using System.Text;

namespace Seeing.Agent.Paths;

/// <summary>
/// 用户粘贴/拖拽附件的目录公式：<c>&lt;sessionRoot&gt;/.seeing/uploads/&lt;safeSessionId&gt;</c>。
/// <para>
/// 落盘（<c>PastedFileLayout</c>）与出站物化（<c>ImagePartResolver</c>）必须用同一公式，
/// 否则文件写进上传目录后会被当成越界路径，模型只能看到「图片不可用」。
/// </para>
/// </summary>
public static class SessionUploadPaths
{
    /// <summary>会话 id 只保留 ASCII 字母数字、下划线和连字符；空值回落 default。</summary>
    public static string SafeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "default";

        var builder = new StringBuilder(sessionId.Length);
        foreach (var ch in sessionId)
        {
            var safe = ch is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '_' or '-';
            builder.Append(safe ? ch : '_');
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }

    /// <summary>会话上传目录绝对路径（不创建目录）。</summary>
    public static string BuildDirectory(string sessionRoot, string sessionId)
        => Path.Combine(sessionRoot, ".seeing", "uploads", SafeSessionId(sessionId));
}
