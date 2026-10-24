namespace Seeing.Agent.Abstractions.Context;

/// <summary>
/// 可选能力：为 <see cref="ContextSourceTarget.Message"/> 落点的消息提供强类型元数据。
/// <para>存在的唯一理由：消息需要携带「谁注入的、为什么注入、涉及哪些路径」这类结构化事实，
/// 而这些事实正是源的渲染结果本身的属性（<c>&lt;project-instructions&gt;</c> 信封同理）。
/// 装配器只负责落库，不解析任何源的信封；元数据由源自己给出。</para>
/// <para>未实现本接口的源（如 <see cref="ContextSourceTarget.System"/> 源）不附加元数据。</para>
/// </summary>
public interface IContextSourceMessageMetadata
{
    /// <summary>
    /// 为即将落库的消息计算元数据；返回 null 表示不附加。
    /// </summary>
    /// <param name="renderedText">装配器实际要落库的文本（初始渲染或重放文本）。</param>
    /// <param name="value">该文本对应的读取值（不可读时为 <see cref="ContextValueState.Unavailable"/>）。</param>
    IReadOnlyDictionary<string, object>? GetMessageMetadata(string renderedText, ContextSourceValue value);
}
