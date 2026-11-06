using System.Text.Encodings.Web;
using System.Text.Json;

namespace Seeing.Serialization;

/// <summary>
/// 全项目统一 JSON 序列化选项。
/// <para><b>可读选项</b>：非 ASCII 字符（中文、日文、韩文、拉丁扩展等）原样输出，只转义 JSON 规范
/// 要求的字符（<c>"</c> 写成 <c>\"</c>、<c>\</c>、控制字符、U+2028/U+2029）。<c>&lt;</c> <c>&gt;</c> <c>&amp;</c>
/// 同样原样输出——本项目的 JSON 不内联进 HTML 或 <c>&lt;script&gt;</c>，无注入面。</para>
/// <para><b>签名选项</b>：保持 CLR 默认编码器，仅用于 HMAC / 内容哈希的字节稳定输入。
/// 改动它会让既有签名与哈希全部失效。</para>
/// <para>禁止在业务代码里裸写 <c>new JsonSerializerOptions</c>；需要额外配置请用
/// <see cref="Create"/>，它只预置编码器，其余设置由调用方按原样传入。</para>
/// </summary>
public static class SeeingJson
{
    /// <summary>
    /// 可读编码器（非 ASCII 原样）。供自定义写出器（如规范化 JSON）复用。
    /// </summary>
    public static JavaScriptEncoder ReadableEncoder { get; } = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// 可读、无缩进。
    /// </summary>
    public static JsonSerializerOptions Readable { get; } = new() { Encoder = ReadableEncoder };

    /// <summary>
    /// 可读、缩进。用于落盘文件与界面展示。
    /// </summary>
    public static JsonSerializerOptions ReadableIndented { get; } = new()
    {
        Encoder = ReadableEncoder,
        WriteIndented = true
    };

    /// <summary>
    /// 签名 / 哈希专用：默认编码器，字节稳定。
    /// <para><b>勿用于普通输出</b>；仅限参与 HMAC 或内容哈希的序列化。</para>
    /// </summary>
    public static JsonSerializerOptions Signature { get; } = new();

    /// <summary>
    /// 派生可读选项：<paramref name="indented"/> 决定缩进，其余设置由
    /// <paramref name="configure"/> 追加。调用方迁移既有配置时逐项照搬，避免行为漂移。
    /// </summary>
    /// <param name="indented">是否缩进输出</param>
    /// <param name="configure">追加配置（命名策略、转换器、忽略条件等）</param>
    public static JsonSerializerOptions Create(
        bool indented,
        Action<JsonSerializerOptions>? configure)
    {
        var options = new JsonSerializerOptions
        {
            Encoder = ReadableEncoder,
            WriteIndented = indented
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// 派生可读选项（无缩进），仅追加 <paramref name="configure"/>。
    /// </summary>
    /// <param name="configure">追加配置（命名策略、转换器、忽略条件等）</param>
    public static JsonSerializerOptions Create(Action<JsonSerializerOptions> configure)
        => Create(indented: false, configure);
}
