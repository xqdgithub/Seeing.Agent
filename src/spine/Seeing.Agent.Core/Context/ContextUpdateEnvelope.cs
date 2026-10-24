namespace Seeing.Agent.Core.Context;

/// <summary>
/// 增量叙述消息的信封：格式与识别的唯一实现（禁止在装配器 / 标题服务 / UI 各自硬编码标签）。
/// <para>信封只包裹源自己渲染的文本，装配器不添加任何措辞。</para>
/// </summary>
public static class ContextUpdateEnvelope
{
    /// <summary>信封标签。</summary>
    public const string Tag = "context-update";

    /// <summary>包裹一条源叙述。</summary>
    /// <param name="sourceKey">源键（写入 <c>source</c> 属性，便于诊断）。</param>
    /// <param name="text">源渲染文本（原样放入）。</param>
    public static string Wrap(string sourceKey, string text) =>
        $"<{Tag} source=\"{sourceKey}\">\n{text}\n</{Tag}>";

    /// <summary>
    /// 判断消息内容是否为增量叙述信封（用于「机器注入 ≠ 用户输入」的判定）。
    /// </summary>
    public static bool IsContextUpdate(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return content.TrimStart().StartsWith($"<{Tag}", StringComparison.Ordinal);
    }
}
