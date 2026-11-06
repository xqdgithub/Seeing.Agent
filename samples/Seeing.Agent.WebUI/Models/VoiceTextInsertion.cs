using System.Text;

namespace Seeing.Agent.WebUI.Models;

/// <summary>语音转写文本插入输入框的纯函数（不依赖组件状态）。</summary>
public static class VoiceTextInsertion
{
    /// <summary>
    /// 在 <paramref name="selectionStart"/>..<paramref name="selectionEnd"/> 之间插入文本。
    /// 边界越界自动夹紧；插入内容为空白时原样返回。
    /// </summary>
    /// <returns>替换后的全文与建议光标位置（紧随插入内容之后）。</returns>
    public static (string Text, int CaretIndex) Insert(
        string original,
        int selectionStart,
        int selectionEnd,
        string insert)
    {
        original ??= string.Empty;
        insert ??= string.Empty;

        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, original.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, original.Length);

        if (string.IsNullOrWhiteSpace(insert))
            return (original, start);

        // 只在「ASCII 词字符 ↔ 非 ASCII（中文等）」交界处补空格：
        // 中文与中文之间不需要空格（"你好"+"语音" 应是 "你好语音"），
        // 而中文紧贴英文单词时补一个空格才读得通（"abc"+"语音" → "abc 语音"）。
        var needLeft = start > 0
            && IsAsciiWordChar(original[start - 1])
            && !IsAsciiWordChar(insert[0]);
        var needRight = end < original.Length
            && IsAsciiWordChar(original[end])
            && !IsAsciiWordChar(insert[^1]);

        var builder = new StringBuilder(original.Length + insert.Length + 2);
        builder.Append(original, 0, start);
        if (needLeft)
            builder.Append(' ');
        builder.Append(insert);
        if (needRight)
            builder.Append(' ');
        builder.Append(original, end, original.Length - end);

        var caret = start + (needLeft ? 1 : 0) + insert.Length;
        return (builder.ToString(), caret);
    }

    private static bool IsAsciiWordChar(char ch)
        => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
