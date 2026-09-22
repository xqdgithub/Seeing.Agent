using FluentAssertions;
using Seeing.Agent.Tui.Input;

namespace Seeing.Agent.Tui.Tests.Input;

public class RawInputReaderPasteTests
{
    private static readonly byte[] Emoji = "😀"u8.ToArray(); // 4 字节 UTF-8
    private static readonly byte[] Cjk = "中"u8.ToArray();   // 3 字节 UTF-8
    private static readonly byte[] PasteEnd = "\x1b[201~"u8.ToArray();

    [Fact]
    public void Utf8CompletePrefixLength_ShouldNotIncludeTruncatedTail()
    {
        RawInputReader.Utf8CompletePrefixLength(ReadOnlySpan<byte>.Empty).Should().Be(0);
        RawInputReader.Utf8CompletePrefixLength("abc"u8).Should().Be(3);
        RawInputReader.Utf8CompletePrefixLength(Emoji).Should().Be(4);
        RawInputReader.Utf8CompletePrefixLength(Cjk).Should().Be(3);

        RawInputReader.Utf8CompletePrefixLength(Emoji.AsSpan(0, 1)).Should().Be(0);
        RawInputReader.Utf8CompletePrefixLength(Emoji.AsSpan(0, 2)).Should().Be(0);
        RawInputReader.Utf8CompletePrefixLength(Emoji.AsSpan(0, 3)).Should().Be(0);
        RawInputReader.Utf8CompletePrefixLength(Cjk.AsSpan(0, 2)).Should().Be(0);
    }

    [Fact]
    public void Utf8CompletePrefixLength_ShouldKeepCompleteCharsBeforePartialTail()
    {
        var bytes = new byte[1 + 3];
        bytes[0] = (byte)'a';
        Emoji.AsSpan(0, 3).CopyTo(bytes.AsSpan(1));

        RawInputReader.Utf8CompletePrefixLength(bytes).Should().Be(1);
    }

    [Fact]
    public void PasteSafeEmitLength_ShouldNotDecodeEmojiSplitAcrossReads()
    {
        // 第一轮：4 字节 emoji 只到达前 3 字节 —— 不得输出（避免 U+FFFD）。
        RawInputReader.PasteSafeEmitLength(Emoji.AsSpan(0, 3)).Should().Be(0);
    }

    [Fact]
    public void PasteSafeEmitLength_ShouldEmitCompleteCharsAndReserveEndPrefix()
    {
        // 8 字节纯 ASCII：只输出前 3 字节，保留 5 字节用于识别 \x1b[201~。
        RawInputReader.PasteSafeEmitLength("abcdefgh"u8).Should().Be(3);

        // 完整 emoji + 5 字节 ASCII：emoji 作为完整字符被包含（4 字节）。
        RawInputReader.PasteSafeEmitLength(Emoji.Concat("abcde"u8.ToArray()).ToArray()).Should().Be(4);

        // emoji 残缺（仅前 3 字节）时不得输出任何字节。
        RawInputReader.PasteSafeEmitLength(Emoji.AsSpan(0, 3).ToArray().Concat("abcde"u8.ToArray()).ToArray())
            .Should().Be(0);
    }

    [Fact]
    public void IndexOfSequence_ShouldStillDetectPasteEnd()
    {
        var pending = Emoji.Concat(PasteEnd).ToArray();
        RawInputReader.IndexOfSequence(pending, PasteEnd).Should().Be(4);

        // 结束序列残缺时不得误判为已完成粘贴。
        RawInputReader.IndexOfSequence("\x1b[201"u8, PasteEnd).Should().Be(-1);
        RawInputReader.IndexOfSequence("\x1b[20"u8, PasteEnd).Should().Be(-1);
        RawInputReader.IndexOfSequence(ReadOnlySpan<byte>.Empty, PasteEnd).Should().Be(-1);
    }

    [Fact]
    public void SplitPaste_ShouldDecodeEmojiIntactAfterSecondChunk()
    {
        // 模拟按字节流切断在 4 字节字符中间：第一轮不输出，第二轮输出完整 emoji。
        var firstChunk = Emoji.AsSpan(0, 3).ToArray();
        RawInputReader.PasteSafeEmitLength(firstChunk).Should().Be(0);

        var pending = firstChunk.Concat(new byte[] { Emoji[3] }).Concat(PasteEnd).ToArray();
        var endIndex = RawInputReader.IndexOfSequence(pending, PasteEnd);
        endIndex.Should().Be(4);
        pending.AsSpan(0, endIndex).ToArray().Should().Equal(Emoji);
        System.Text.Encoding.UTF8.GetString(pending.AsSpan(0, endIndex)).Should().Be("😀");
    }
}
