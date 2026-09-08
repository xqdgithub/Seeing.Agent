namespace Seeing.Agent.Abstractions.Events;

/// <summary>
/// 压缩进度事件出口（Sink）- 单向只写，供压缩实现方（主库）向宿主发布进度事件，不反向依赖宿主。
/// <para>宿主（App/Gateway 等）实现该接口并适配自有事件发布器；未配置时压缩正常执行但不推送进度。</para>
/// </summary>
public interface ICompactionEventSink
{
/// <summary>
    /// 发布压缩进度增量。
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="stage">进度阶段（summarizing/trimming/其他）</param>
    /// <param name="contentDelta">摘要正文增量（纯文本），为 null 表示仅阶段变化</param>
    /// <param name="reasoningDelta">摘要推理增量（纯文本），与正文一并实时展示</param>
    void PublishDelta(string sessionId, string stage, string? contentDelta = null, string? reasoningDelta = null);
}