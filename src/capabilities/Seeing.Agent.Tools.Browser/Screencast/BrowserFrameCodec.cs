using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Seeing.Agent.Tools.Browser;

/// <summary>画面帧的种类。</summary>
public enum BrowserFrameKind : byte
{
    /// <summary>动态流帧（screencast 持续推流）。</summary>
    Flow = 1
}

/// <summary>
/// 二进制帧编解码器：18 字节小端头 + JPEG 载荷。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 base64 + JSON</b>：一帧 JPEG 转 base64 会膨胀 33%，外面再包 JSON 字符串，
/// 宿主与前端各要做一次编解码；画面流对带宽和延迟都敏感，直接传二进制省掉整条链路。
/// </para>
/// <para>
/// <b>头布局</b>（全部小端，共 18 字节）：
/// <code>
/// offset 0   kind    (u8)   1=Flow
/// offset 1   version (u8)   固定 1
/// offset 2   epoch   (u32)  浏览器会话代次（换页面/重启后递增，用于丢弃旧代次的帧）
/// offset 6   seq     (u32)  帧序号（同代次内单调递增）
/// offset 10  width   (u16)  像素
/// offset 12  height  (u16)  像素
/// offset 14  length  (u32)  JPEG 载荷字节数（不含头）
/// </code>
/// </para>
/// <para>
/// <b>为什么静帧复用 seq</b>：静帧没有「流序号」语义，只有「这是第几版画面」；
/// 另开一个字段会让前端两套读取路径，复用 seq 保持头定长、读取逻辑唯一。
/// </para>
/// </remarks>
public static class BrowserFrameCodec
{
    /// <summary>头长度（字节）。</summary>
    public const int HeaderSize = 18;

    /// <summary>协议版本。解码时不认识的版本一律拒绝（避免误读未来布局）。</summary>
    public const byte Version = 1;

    /// <summary>把一帧编码为可直接发送的字节数组。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> 或其 <c>Data</c> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/> 的 <see cref="BrowserFrame.Kind"/> 未知。</exception>
    /// <exception cref="OverflowException">序号/尺寸超出头字段可表达的位宽。</exception>
    public static byte[] Encode(BrowserFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var data = frame.Data ?? throw new ArgumentNullException(nameof(frame), "帧数据不能为 null");

        // 编码侧也校验种类：否则产出的长度/版本都合法、只有 kind 字节是垃圾，
        // 接收方要等到 TryDecode 才拒收——契约应当对称，越早失败越容易定位。
        if (!IsKnownKind(frame.Kind))
            throw new ArgumentOutOfRangeException(nameof(frame), frame.Kind, "帧种类未知：只允许 Flow");

        // 长度字段由 Data.Length 推出，因此「头里的 length 与真实载荷一致」在编码侧天然成立；
        // 解码侧再校验一次，防止截断帧被当成完整帧渲染（半张图比报错更糟）。
        var buffer = new byte[HeaderSize + data.Length];

        buffer[0] = (byte)frame.Kind;
        buffer[1] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(2), frame.Epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6), checked((uint)frame.Seq));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)frame.Width));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)frame.Height));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(14), checked((uint)data.Length));

        data.CopyTo(buffer, HeaderSize);

        return buffer;
    }

    /// <summary>尝试解码一帧；头不完整、版本/种类未知、载荷长度不符时返回 <c>false</c>。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out BrowserFrame? frame)
    {
        frame = null;

        if (payload.Length < HeaderSize)
            return false;

        var kind = (BrowserFrameKind)payload[0];

        if (!IsKnownKind(kind))
            return false;

        if (payload[1] != Version)
            return false;

        var epoch = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(payload[14..]);

        // 长度必须精确相等：多出来的字节说明发送方与接收方的分帧边界已经错位，
        // 少则说明帧被截断。两种都不该渲染。
        if (payload.Length - HeaderSize != length)
            return false;

        frame = new BrowserFrame(
            payload[HeaderSize..].ToArray(),
            seq,
            width,
            height,
            kind,
            epoch);

        return true;
    }

    private static bool IsKnownKind(BrowserFrameKind kind)
        => kind is BrowserFrameKind.Flow;
}
