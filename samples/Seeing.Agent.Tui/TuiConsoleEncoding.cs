using System.Text;

namespace Seeing.Agent.Tui;

/// <summary>
/// 控制台编码初始化：统一置为无 BOM 的 UTF-8（代码页 65001）。
/// </summary>
internal static class TuiConsoleEncoding
{
    private static Encoding? _originalInput;
    private static Encoding? _originalOutput;

    /// <summary>
    /// 将标准输入/输出编码置为 UTF-8。
    /// Windows 下该 setter 会调用 <c>SetConsoleCP/SetConsoleOutputCP(65001)</c>；
    /// 系统默认 936(GBK) 时中文输入按 GBK 送字节，而 <c>RawInputReader</c> 按 UTF-8 解码，
    /// 故必须显式置 65001，否则中文被当作非法 UTF-8 解析为 <c>?</c>。
    /// 无控制台或输出被重定向时 setter 会抛异常，此处吞掉，绝不因此中断启动。
    /// 原始编码会被记录，退出时由 <see cref="Restore"/> 还原。
    /// </summary>
    public static void EnsureUtf8()
    {
        try
        {
            _originalInput ??= Console.InputEncoding;
            _originalOutput ??= Console.OutputEncoding;
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 无控制台 / 重定向场景无法设置编码，忽略即可。
        }
    }

    /// <summary>还原 <see cref="EnsureUtf8"/> 之前的控制台编码（代码页），避免退出后残留 65001。</summary>
    public static void Restore()
    {
        try
        {
            if (_originalInput is not null)
                Console.InputEncoding = _originalInput;

            if (_originalOutput is not null)
                Console.OutputEncoding = _originalOutput;
        }
        catch
        {
            // 控制台已关闭 / 重定向时无法还原，忽略即可。
        }
    }
}
