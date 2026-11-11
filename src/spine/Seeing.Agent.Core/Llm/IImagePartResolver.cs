using Seeing.Session.Core;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// 把会话/工具侧的本地产出物化为可直接出站的载荷。
/// <para>
/// <b>为什么在框架层而不是 client 内</b>：<c>LocalPath</c> 是会话存储概念，LLM 契约
/// （<see cref="Abstractions.Llm.ChatContentPart"/>）不含路径字段。若让 client 各自读盘，
/// N 个 provider 就是 N 份重复实现且必然漂移。
/// </para>
/// <para>
/// <b>为什么 public 而不是 internal</b>：Hosting 的 <c>SessionHistoryBuilder</c> 也要用它，
/// 而 Core 的 <c>InternalsVisibleTo</c> 只对测试程序集开放（Core → Hosting 是反向依赖）。
/// </para>
/// </summary>
public interface IImagePartResolver
{
    /// <summary>
    /// 物化一个会话内容段。
    /// </summary>
    /// <param name="sessionId">用于校验路径必须落在该会话的 ref 目录内。</param>
    /// <param name="part">源内容段（可能含 <c>LocalPath</c>）。</param>
    /// <returns>
    /// 可出站的载荷。路径不可读/越界时返回**文本占位**（不抛异常），
    /// 因为一张历史图片丢失不应让整轮对话失败。
    /// </returns>
    Abstractions.Llm.ChatContentPart Resolve(string sessionId, SessionContentPart part);

    /// <summary>物化一个工具产出内容块。</summary>
    /// <param name="sessionId">用于校验路径必须落在该会话的 ref 目录内。</param>
    /// <param name="part">源内容块（可能含 <c>LocalPath</c>）。</param>
    Abstractions.Llm.ChatContentPart Resolve(string sessionId, Abstractions.Tools.ToolOutputPart part);
}
