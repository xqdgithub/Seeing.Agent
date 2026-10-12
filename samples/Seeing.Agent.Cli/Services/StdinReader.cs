namespace Seeing.Agent.Cli.Services;

/// <summary>`-p -` 时从 stdin 读取提示词全文；可注入 <see cref="TextReader"/> 便于测试。</summary>
internal static class StdinReader
{
    /// <summary>读取 <paramref name="reader"/> 至结尾；无内容返回 null。</summary>
    public static async Task<string?> ReadAllAsync(TextReader reader, CancellationToken ct = default)
    {
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
