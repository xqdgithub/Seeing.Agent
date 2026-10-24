namespace Seeing.Agent.Core.Context;

/// <summary>
/// 增量叙述消息的信封：格式的唯一实现（禁止在装配器 / 标题服务 / UI 各自硬编码标签）。
/// <para>信封只包裹源自己渲染的文本，装配器不添加任何措辞。</para>
/// <para>「这条消息是上下文增量」由消息元数据（<see cref="Seeing.Session.Core.SessionMetadataKeys.ContextUpdate"/>）
/// 判定，<b>不</b>由内容前缀判定：内容渲染与语义判定必须解耦。</para>
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
}
