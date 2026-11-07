namespace Seeing.Agent.Llm.Support.Speech;

/// <summary>
/// 语音能力连接测试用的极短静音 WAV 探针（20ms、8kHz、16bit 单声道）。
/// <para><b>为什么必须是真实音频</b>：决议 25 要求连接测试发真实探针而非「构造成功即通过」。
/// 空 body 会让服务端在解析阶段就失败，测不出模型/凭据是否可用。</para>
/// <para><b>为什么放在 Support</b>：OpenAI 兼容客户端与各专有 Provider 都需要同一份字节，
/// 各写一份会漂移（改了采样率只改一处，另一处就废）。</para>
/// </summary>
public static class SilentWavProbe
{
    /// <summary>探针采样率（Hz）。</summary>
    public const int SampleRate = 8000;

    /// <summary>
    /// 探针时长（毫秒）。
    /// <para><b>为什么不是 20ms</b>：实测 SiliconFlow 对过短音频直接返回 HTTP 500
    /// （20ms 失败、200ms 起成功），会让「端点其实可用」被误判成连接失败。
    /// 200ms 已是能探测的最小安全值，静音内容不消耗转写语义。</para>
    /// </summary>
    public const int DurationMs = 200;

    /// <summary>探针 MIME 类型。</summary>
    public const string MimeType = "audio/wav";

    /// <summary>探针文件名（部分服务端按扩展名判格式）。</summary>
    public const string FileName = "connection-probe.wav";

    /// <summary>
    /// 构造静音 WAV 字节。每次返回新数组，调用方可安全写入临时流。
    /// </summary>
    public static byte[] CreateBytes()
    {
        const int samples = SampleRate * DurationMs / 1000;
        var dataSize = samples * 2;
        var bytes = new byte[44 + dataSize];

        WriteAscii(bytes, 0, "RIFF");
        WriteInt32(bytes, 4, 36 + dataSize);
        WriteAscii(bytes, 8, "WAVE");
        WriteAscii(bytes, 12, "fmt ");
        WriteInt32(bytes, 16, 16);              // fmt 块长度
        WriteInt16(bytes, 20, 1);               // PCM
        WriteInt16(bytes, 22, 1);               // 单声道
        WriteInt32(bytes, 24, SampleRate);
        WriteInt32(bytes, 28, SampleRate * 2);  // 字节率
        WriteInt16(bytes, 32, 2);               // 块对齐
        WriteInt16(bytes, 34, 16);              // 位深
        WriteAscii(bytes, 36, "data");
        WriteInt32(bytes, 40, dataSize);
        // 44.. 保持全零 = 静音

        return bytes;
    }

    private static void WriteAscii(byte[] target, int offset, string value)
        => System.Text.Encoding.ASCII.GetBytes(value).CopyTo(target.AsSpan(offset));

    private static void WriteInt32(byte[] target, int offset, int value)
        => BitConverter.TryWriteBytes(target.AsSpan(offset, 4), value);

    private static void WriteInt16(byte[] target, int offset, short value)
        => BitConverter.TryWriteBytes(target.AsSpan(offset, 2), value);
}
