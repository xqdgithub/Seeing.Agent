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
        BitConverter.ToUInt32(b, 2).Should().Be(9);
        BitConverter.ToUInt32(b, 6).Should().Be(7);
        BitConverter.ToUInt16(b, 10).Should().Be(1000);
        BitConverter.ToUInt16(b, 12).Should().Be(750);
        BitConverter.ToUInt32(b, 14).Should().Be(3);
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
}
