using System.Buffers.Binary;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 二进制帧协议：18 字节小端头 + JPEG 载荷。
/// </summary>
/// <remarks>
/// 为什么不用 base64+JSON：一帧 1280×720 的 JPEG 转 base64 会膨胀 33%，
/// 再包一层 JSON 字符串还要在宿主与服务端各做一次编解码；画面流的带宽和延迟都在这上面。
/// 定长头让前端可以用一个 <c>DataView</c> 直接读，不需要 JSON 解析。
/// </remarks>
public class BrowserFrameProtocolTests
{
    [Fact]
    public void Encode_ShouldWriteLittleEndianHeader()
    {
        var b = BrowserFrameCodec.Encode(new BrowserFrame([1, 2, 3], 7, 1000, 750, BrowserFrameKind.Still, 9));

        b.Length.Should().Be(21);
        b[0].Should().Be(2);
        b[1].Should().Be(1);

        // 用 BinaryPrimitives 而不是 BitConverter：BitConverter 跟随机器字节序，
        // 在大端机器上会把一个正确的实现判成红。
        BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(2)).Should().Be(9u);
        BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(6)).Should().Be(7u);
        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(10)).Should().Be(1000);
        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12)).Should().Be(750);
        BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(14)).Should().Be(3u);
    }

    [Fact]
    public void TryDecode_ShouldRoundTrip()
    {
        var f = new BrowserFrame([9, 8], 1, 10, 20, BrowserFrameKind.Flow, 3);

        BrowserFrameCodec.TryDecode(BrowserFrameCodec.Encode(f), out var back).Should().BeTrue();

        back.Should().Be(f);
    }

    [Fact]
    public void TryDecode_WhenTruncated_ShouldReturnFalse()
        => BrowserFrameCodec.TryDecode(new byte[10], out _).Should().BeFalse();

    [Fact]
    public void Encode_WhenSeqExceedsUInt32_ShouldThrow()
    {
        // 头里 seq 是 u32；超范围必须显式失败，不能静默截断——
        // 截断后的序号会与旧帧撞车，前端去重/丢帧判定就失效。
        var frame = new BrowserFrame([1], (long)uint.MaxValue + 1, 10, 20);

        var act = () => BrowserFrameCodec.Encode(frame);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void TryDecode_WhenVersionUnknown_ShouldReturnFalseAndNullFrame()
    {
        var payload = BrowserFrameCodec.Encode(new BrowserFrame([1], 1, 10, 20));
        payload[1] = 2;

        BrowserFrameCodec.TryDecode(payload, out var frame).Should().BeFalse();

        frame.Should().BeNull();
    }
}
